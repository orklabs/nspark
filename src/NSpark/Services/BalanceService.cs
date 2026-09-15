using Google.Protobuf;
using NSpark.Models;
using NSpark.Proto;

namespace NSpark.Services;

/// <summary>
/// Extension methods on <see cref="SparkWallet"/> for reading the wallet's
/// balance and the underlying leaf inventory.
/// </summary>
public static class BalanceService
{
    /// <summary>
    /// Locked statuses that count toward <c>SatsBalance.Owned</c> but not
    /// toward <c>SatsBalance.Available</c>. Mirrors the Swift / Kotlin / TS
    /// Spark SDKs so balances agree across language clients.
    /// </summary>
    private static readonly HashSet<string> LockedStatuses = new(StringComparer.Ordinal)
    {
        "TRANSFER_LOCKED",
        "SPLIT_LOCKED",
        "AGGREGATE_LOCK",
        "RENEW_LOCKED",
    };

    /// <summary>The balance figures a set of coordinator nodes classify into.</summary>
    /// <param name="Available">Sats in <c>AVAILABLE</c> leaves that can be sent right now.</param>
    /// <param name="Owned"><see cref="Available"/> + <see cref="Frozen"/> + sats in locked leaves.</param>
    /// <param name="Frozen">Sats in <c>AVAILABLE</c> leaves at the timelock floor.</param>
    /// <param name="Creating">Sats in deposit leaves still being created.</param>
    /// <param name="Leaves">Every <c>AVAILABLE</c> leaf, spendable or frozen.</param>
    internal sealed record NodeSummary(
        long Available,
        long Owned,
        long Frozen,
        long Creating,
        IReadOnlyList<SparkLeaf> Leaves);

    /// <summary>
    /// Pure classification of the coordinator's nodes into the balance figures. Owned =
    /// AVAILABLE + locked (transfer, split, aggregate, renew). Available excludes AVAILABLE
    /// leaves at the timelock floor, which are reported as frozen instead: the coordinator will
    /// neither move nor renew them, so counting them as available would make "send everything"
    /// fail.
    /// </summary>
    internal static NodeSummary SummarizeNodes(IEnumerable<KeyValuePair<string, TreeNode>> nodes)
    {
        long available = 0;
        long owned = 0;
        long frozen = 0;
        long creating = 0;
        var leaves = new List<SparkLeaf>();

        foreach (var (id, node) in nodes)
        {
            var value = (long)node.Value;
            if (node.Status == "AVAILABLE")
            {
                var leaf = new SparkLeaf(id, node.TreeId, value, node.Status) { Node = node };
                owned += value;
                if (leaf.IsSpendable)
                {
                    available += value;
                }
                else
                {
                    frozen += value;
                }
                leaves.Add(leaf);
            }
            else if (LockedStatuses.Contains(node.Status))
            {
                owned += value;
            }
            else if (node.Status == "CREATING")
            {
                creating += value;
            }
        }

        return new NodeSummary(available, owned, frozen, creating, leaves);
    }

    /// <summary>
    /// Query the wallet's balance from the coordinator Signing Operator and
    /// aggregate it into <see cref="WalletBalance"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Performs two RPCs against the coordinator: <c>query_nodes</c> for the
    /// leaf inventory and <c>query_pending_transfers</c> for inbound
    /// transfers that have not yet been claimed. The satoshi totals
    /// in <see cref="SatsBalance"/> are computed as follows:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>Available</b>: sum of leaves with status <c>AVAILABLE</c> whose
    ///   refund timelock is above the floor, i.e. what a send can spend right now.</description></item>
    ///   <item><description><b>Frozen</b>: sum of <c>AVAILABLE</c> leaves at the timelock floor,
    ///   which only a unilateral exit can recover.</description></item>
    ///   <item><description><b>Owned</b>: <i>Available</i> + <i>Frozen</i> + leaves in any of the locked statuses
    ///   (<c>TRANSFER_LOCKED</c>, <c>SPLIT_LOCKED</c>, <c>AGGREGATE_LOCK</c>, <c>RENEW_LOCKED</c>).</description></item>
    ///   <item><description><b>Incoming</b>: total value of pending inbound transfers + leaves
    ///   in the <c>CREATING</c> state (in-flight deposits).</description></item>
    /// </list>
    /// <para>
    /// Token balances are best-effort: a failing token round-trip yields an empty list rather
    /// than failing the whole balance read. Call <c>GetTokenBalancesAsync</c> directly to see
    /// the failure.
    /// </para>
    /// </remarks>
    public static async Task<WalletBalance> GetBalanceAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);

        var soAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var client = wallet.Pool.GetSparkClient(soAddress);
        var headers = await wallet.GetAuthMetadataAsync(soAddress, ct).ConfigureAwait(false);

        var network = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Network.Mainnet
            : Network.Regtest;

        var nodesResponse = await client.query_nodesAsync(
            new QueryNodesRequest
            {
                OwnerIdentityPubkey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                Network = network,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);

        var summary = SummarizeNodes(nodesResponse.Nodes);

        var pendingTransfers = await client.query_pending_transfersAsync(
            new TransferFilter
            {
                ReceiverIdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                Network = network,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);

        long incomingFromTransfers = 0;
        foreach (var transfer in pendingTransfers.Transfers)
        {
            incomingFromTransfers += (long)transfer.TotalValue;
        }

        var satsBalance = new SatsBalance(
            Available: summary.Available,
            Owned: summary.Owned,
            Incoming: summary.Creating + incomingFromTransfers,
            Frozen: summary.Frozen);

        // Token balances are best-effort: if the token RPC fails we fall back
        // to an empty list so a single bad token-service round-trip doesn't
        // break the entire balance read. Callers that need failures surfaced
        // explicitly should call wallet.GetTokenBalancesAsync() directly.
        IReadOnlyList<TokenBalance> tokenBalances;
        try
        {
            tokenBalances = await wallet.GetTokenBalancesAsync(ct).ConfigureAwait(false);
        }
        catch (Grpc.Core.RpcException)
        {
            tokenBalances = Array.Empty<TokenBalance>();
        }

        return new WalletBalance(satsBalance, tokenBalances, summary.Leaves);
    }

    /// <summary>
    /// Query all leaf nodes currently owned by the wallet that are in the
    /// <c>AVAILABLE</c> state, spendable or not. Prefer
    /// <see cref="GetSpendableLeavesAsync"/> as the basis for a "send everything" amount.
    /// </summary>
    public static async Task<IReadOnlyList<SparkLeaf>> GetLeavesAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);

        var soAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var client = wallet.Pool.GetSparkClient(soAddress);
        var headers = await wallet.GetAuthMetadataAsync(soAddress, ct).ConfigureAwait(false);

        var network = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Network.Mainnet
            : Network.Regtest;

        var response = await client.query_nodesAsync(
            new QueryNodesRequest
            {
                OwnerIdentityPubkey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                Network = network,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);

        return response.Nodes
            .Where(kv => kv.Value.Status == "AVAILABLE")
            .Select(kv => new SparkLeaf(
                Id: kv.Key,
                TreeId: kv.Value.TreeId,
                ValueSats: (long)kv.Value.Value,
                Status: kv.Value.Status)
            {
                Node = kv.Value,
            })
            .ToList();
    }

    /// <summary>
    /// <c>AVAILABLE</c> leaves that can be sent right now.
    /// </summary>
    /// <remarks>
    /// Leaves whose refund timelock is in the coordinator's renewable range are renewed first
    /// (best effort, as the reference SDK's leaf manager does before every spend). Leaves at
    /// the timelock floor are left out: the coordinator will neither move nor renew them, so
    /// including them would only make the whole operation fail. Their sats are reported as
    /// <see cref="SatsBalance.Frozen"/>. Every spend path (<c>SendAsync</c>,
    /// <c>PayLightningInvoiceAsync</c>, <c>WithdrawAsync</c>, swaps) selects from this set, so it
    /// is also the right basis for an app's "send everything" amount.
    /// </remarks>
    public static async Task<IReadOnlyList<SparkLeaf>> GetSpendableLeavesAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);

        var leaves = await wallet.GetLeavesAsync(ct).ConfigureAwait(false);
        var renewable = leaves.Where(l => l.IsRenewable).ToList();
        if (renewable.Count > 0)
        {
            try
            {
                _ = await wallet.RenewLeavesAsync(renewable, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Best effort: a leaf that could not be renewed is simply left out of this spend.
            }

            leaves = await wallet.GetLeavesAsync(ct).ConfigureAwait(false);
        }

        return leaves.Where(l => l.IsSpendable).ToList();
    }
}
