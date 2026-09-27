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
    /// <summary>Nodes per <c>query_nodes</c> page: the operators' maximum.</summary>
    internal const int NodePageSize = 100;

    /// <summary>Transfers per <c>query_all_transfers</c> / <c>query_pending_transfers</c> page: the operators' maximum.</summary>
    internal const int TransferPageSize = 100;

    /// <summary>Transfer statuses before the sender key tweak is applied: the sender still owns the leaves.</summary>
    internal static readonly TransferStatus[] SenderPendingStatuses =
    [
        TransferStatus.SenderInitiated,
        TransferStatus.SenderInitiatedCoordinator,
        TransferStatus.ApplyingSenderKeyTweak,
        TransferStatus.SenderKeyTweakPending,
    ];

    /// <summary>A counter-transfer's statuses until it completes.</summary>
    internal static readonly TransferStatus[] ActiveCounterSwapStatuses =
    [
        .. SenderPendingStatuses,
        TransferStatus.SenderKeyTweaked,
        TransferStatus.ReceiverKeyTweakLocked,
        TransferStatus.ReceiverKeyTweakApplied,
        TransferStatus.ReceiverKeyTweaked,
        TransferStatus.ReceiverRefundSigned,
    ];

    internal static readonly TransferType[] OutgoingTransferTypes =
        [TransferType.CooperativeExit, TransferType.UtxoSwap, TransferType.PreimageSwap, TransferType.Transfer];

    internal static readonly TransferType[] PrimarySwapTypes = [TransferType.PrimarySwapV3, TransferType.Swap];

    internal static readonly TransferType[] CounterSwapTypes = [TransferType.CounterSwapV3, TransferType.CounterSwap];

    /// <summary>The balance figures the wallet's AVAILABLE leaves classify into.</summary>
    /// <param name="Available">Sats in AVAILABLE leaves that are not frozen.</param>
    /// <param name="Frozen">Sats in AVAILABLE leaves whose refund timelock is below 100.</param>
    /// <param name="Leaves">Every AVAILABLE leaf, spendable or frozen.</param>
    internal sealed record NodeSummary(long Available, long Frozen, IReadOnlyList<SparkLeaf> Leaves);

    /// <summary>
    /// Pure classification of the wallet's AVAILABLE leaves into the balance figures. A leaf is
    /// frozen when its refund timelock is below 100 (<see cref="SparkLeaf.IsFrozen"/>) and
    /// available otherwise: leaves at 100…199 count as available because every spend path renews
    /// them first, as the reference SDK does before counting them. Nodes in any other status are
    /// ignored — in-flight sats come from the transfers holding them.
    /// </summary>
    internal static NodeSummary SummarizeNodes(IEnumerable<KeyValuePair<string, TreeNode>> nodes)
    {
        long available = 0;
        long frozen = 0;
        var leaves = new List<SparkLeaf>();

        foreach (var (id, node) in nodes)
        {
            if (node.Status != "AVAILABLE")
            {
                continue;
            }

            var value = TransferMapping.ReportedSats(node.Value);
            var leaf = new SparkLeaf(id, node.TreeId, value, node.Status) { Node = node };
            if (leaf.IsFrozen)
            {
                frozen += value;
            }
            else
            {
                available += value;
            }

            leaves.Add(leaf);
        }

        return new NodeSummary(available, frozen, leaves);
    }

    /// <summary>
    /// The wallet's balance, modelled on the reference SDK's leaf manager.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description><b>Available</b> / <b>Frozen</b>: AVAILABLE leaves, split by
    ///   <see cref="SparkLeaf.IsFrozen"/> (refund timelock below 100).</description></item>
    ///   <item><description><b>Owned</b>: those plus the leaves an in-flight operation still holds
    ///   for the wallet — an outgoing transfer, Lightning payment or cooperative exit before the
    ///   operators apply the sender's key tweak, a swap the wallet started, and its
    ///   counter-transfer until claimed. Once the sender's key tweak is applied the sats belong to
    ///   the receiver.</description></item>
    ///   <item><description><b>Incoming</b>: the leaves of every page of pending inbound
    ///   transfers, except counter-transfers of the wallet's own swaps (already counted as locked)
    ///   and leaves counted above.</description></item>
    ///   <item><description><b>Token balances</b>: best effort — empty when the wallet's tokens
    ///   cannot be read, since anyone can send a wallet tokens; <c>GetTokenBalancesAsync</c>
    ///   throws the error instead.</description></item>
    /// </list>
    /// </remarks>
    public static async Task<WalletBalance> GetBalanceAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);

        var nodesTask = wallet.QueryAvailableNodesAsync(ct);
        var inFlightTask = wallet.QueryInFlightTransfersAsync(ct);
        var pendingTask = wallet.QueryAllPendingTransfersAsync(ct);
        await Task.WhenAll(nodesTask, inFlightTask, pendingTask).ConfigureAwait(false);

        var summary = SummarizeNodes(await nodesTask.ConfigureAwait(false));
        var availableIds = summary.Leaves.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var inFlight = await inFlightTask.ConfigureAwait(false);
        var lockedSats = LeafSats(inFlight, availableIds);
        var excluded = new HashSet<string>(availableIds, StringComparer.Ordinal);
        excluded.UnionWith(inFlight.SelectMany(t => t.Leaves).Where(l => l.Leaf is not null).Select(l => l.Leaf.Id));
        var incomingSats = IncomingSats(await pendingTask.ConfigureAwait(false), excluded, wallet.IdentityPublicKey);

        var satsBalance = new SatsBalance(
            Available: summary.Available,
            Owned: summary.Available + summary.Frozen + lockedSats,
            Incoming: incomingSats,
            Frozen: summary.Frozen);

        // Best effort: tokens anyone can send must not cost the wallet its sats balance.
        // GetTokenBalancesAsync reports what went wrong. A cancelled call still throws rather
        // than report no tokens.
        IReadOnlyList<TokenBalance> tokenBalances;
        try
        {
            tokenBalances = await wallet.GetTokenBalancesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            tokenBalances = Array.Empty<TokenBalance>();
        }

        return new WalletBalance(satsBalance, tokenBalances, summary.Leaves);
    }

    /// <summary>
    /// Sats in the leaves of <paramref name="transfers"/>, each leaf counted once and none in
    /// <paramref name="excluded"/> (leaves already counted, e.g. the wallet's AVAILABLE leaves).
    /// </summary>
    internal static long LeafSats(IEnumerable<Transfer> transfers, IReadOnlySet<string> excluded)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var transfer in transfers)
        {
            foreach (var transferLeaf in transfer.Leaves)
            {
                if (transferLeaf.Leaf is not null && !excluded.Contains(transferLeaf.Leaf.Id))
                {
                    values[transferLeaf.Leaf.Id] = TransferMapping.ReportedSats(transferLeaf.Leaf.Value);
                }
            }
        }

        return values.Values.Sum();
    }

    /// <summary>
    /// Sats pending inbound: <paramref name="receiver"/>'s own leaves of <paramref name="pending"/>
    /// (a multi-receiver transfer also carries the other receivers'), except counter-transfers of
    /// the wallet's own swaps — the reference SDK leaves those out of incoming because the swap
    /// already counts them — and leaves in <paramref name="excluded"/> (a self-transfer shows up as
    /// outgoing too).
    /// </summary>
    internal static long IncomingSats(IEnumerable<Transfer> pending, IReadOnlySet<string> excluded, byte[] receiver)
    {
        var own = new List<Transfer>();
        foreach (var transfer in pending)
        {
            if (CounterSwapTypes.Contains(transfer.Type))
            {
                continue;
            }

            try
            {
                own.Add(TransferLeafVerifier.Scoped(transfer, receiver));
            }
            catch (Exceptions.SparkUntrustedResponseException)
            {
                // Not addressed to this wallet: none of its leaves are incoming.
            }
        }

        return LeafSats(own, excluded);
    }

    /// <summary>
    /// The transfers that hold leaves the wallet still owns, as the reference SDK queries them:
    /// outgoing transfers and swaps it sent that are still before the sender key tweak, and
    /// counter-transfers of its swaps until they complete.
    /// </summary>
    internal static async Task<IReadOnlyList<Transfer>> QueryInFlightTransfersAsync(this SparkWallet wallet, CancellationToken ct)
    {
        var outgoing = wallet.QueryAllTransferPagesAsync(senderOnly: true, OutgoingTransferTypes, SenderPendingStatuses, ct);
        var primarySwaps = wallet.QueryAllTransferPagesAsync(senderOnly: true, PrimarySwapTypes, SenderPendingStatuses, ct);
        var counterSwaps = wallet.QueryAllTransferPagesAsync(senderOnly: false, CounterSwapTypes, ActiveCounterSwapStatuses, ct);
        await Task.WhenAll(outgoing, primarySwaps, counterSwaps).ConfigureAwait(false);
        return [.. await outgoing.ConfigureAwait(false), .. await primarySwaps.ConfigureAwait(false), .. await counterSwaps.ConfigureAwait(false)];
    }

    /// <summary>Every page of the wallet's pending inbound transfers.</summary>
    internal static async Task<IReadOnlyList<Transfer>> QueryAllPendingTransfersAsync(this SparkWallet wallet, CancellationToken ct)
    {
        var transfers = new List<Transfer>();
        var offset = 0;
        while (true)
        {
            var page = await wallet.QueryPendingTransfersAsync(TransferPageSize, offset, ct).ConfigureAwait(false);
            transfers.AddRange(page);
            if (page.Count < TransferPageSize)
            {
                return transfers;
            }

            offset += page.Count;
        }
    }

    /// <summary>
    /// Every page of the wallet's transfers of <paramref name="types"/> in
    /// <paramref name="statuses"/> (100 per page, the server's maximum), as the sender or as
    /// either party.
    /// </summary>
    internal static async Task<IReadOnlyList<Transfer>> QueryAllTransferPagesAsync(
        this SparkWallet wallet,
        bool senderOnly,
        IReadOnlyList<TransferType> types,
        IReadOnlyList<TransferStatus> statuses,
        CancellationToken ct)
    {
        var client = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        var identity = ByteString.CopyFrom(wallet.IdentityPublicKey);
        var transfers = new List<Transfer>();
        long offset = 0;
        long previousOffset = -1;
        while (offset >= 0)
        {
            var filter = new TransferFilter
            {
                Network = wallet.Options.ProtoNetwork(),
                Limit = TransferPageSize,
                Offset = offset,
            };
            if (senderOnly)
            {
                filter.SenderIdentityPublicKey = identity;
            }
            else
            {
                filter.SenderOrReceiverIdentityPublicKey = identity;
            }
            filter.Types_.AddRange(types);
            filter.Statuses.AddRange(statuses);

            var response = await client.query_all_transfersAsync(filter, headers, cancellationToken: ct).ConfigureAwait(false);
            transfers.AddRange(response.Transfers);
            if (response.Transfers.Count < TransferPageSize || response.Offset == previousOffset)
            {
                break;
            }

            previousOffset = response.Offset;
            offset = response.Offset;
        }

        return transfers;
    }

    /// <summary>The wallet's AVAILABLE leaves on the coordinator.</summary>
    internal static Task<IReadOnlyDictionary<string, TreeNode>> QueryAvailableNodesAsync(this SparkWallet wallet, CancellationToken ct)
    {
        var request = new QueryNodesRequest
        {
            OwnerIdentityPubkey = ByteString.CopyFrom(wallet.IdentityPublicKey),
            Network = wallet.Options.ProtoNetwork(),
        };
        request.Statuses.Add(TreeNodeStatus.Available);
        return wallet.QueryAllNodesAsync(request, wallet.GetCoordinatorClient(), ct);
    }

    /// <summary>
    /// Nodes the operators return for <paramref name="request"/>, a page of 100 at a time (their
    /// maximum), as the reference SDK pages them: without a limit the whole set comes back in one
    /// response, which outgrows the message-size limit for a wallet with many leaves. Pages are
    /// counted here rather than following the response's offset (proto3 cannot tell "0" from
    /// unset); with <c>include_parents</c> the parents pad the pages, which costs at most one
    /// extra request.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, TreeNode>> QueryAllNodesAsync(
        this SparkWallet wallet,
        QueryNodesRequest request,
        SparkService.SparkServiceClient client,
        CancellationToken ct)
    {
        var nodes = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
        var page = request.Clone();
        page.Limit = NodePageSize;
        page.Offset = 0;
        while (true)
        {
            var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
            var response = await client.query_nodesAsync(page, headers, cancellationToken: ct).ConfigureAwait(false);
            var countBefore = nodes.Count;
            foreach (var (id, node) in response.Nodes)
            {
                nodes[id] = node;
            }

            // A short page ends the set; a page that adds nothing means the operator is not
            // paging, and asking again would never end.
            if (response.Nodes.Count < NodePageSize || nodes.Count <= countBefore)
            {
                return nodes;
            }

            page.Offset += NodePageSize;
        }
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

        var nodes = await wallet.QueryAvailableNodesAsync(ct).ConfigureAwait(false);
        return nodes
            .Where(kv => kv.Value.Status == "AVAILABLE")
            .Select(kv => new SparkLeaf(
                Id: kv.Key,
                TreeId: kv.Value.TreeId,
                ValueSats: TransferMapping.ReportedSats(kv.Value.Value),
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
    /// (best effort, as the reference SDK's leaf manager does before every spend). Leaves that
    /// still cannot move are left out, since including them would only make the whole operation
    /// fail: frozen leaves (refund timelock below 100, reported as <see cref="SatsBalance.Frozen"/>)
    /// and any renewable leaf the operators did not renew. Every spend path (<c>SendAsync</c>,
    /// <c>PayLightningInvoiceAsync</c>, <c>WithdrawAsync</c>, <c>WithdrawAllAsync</c>, swaps)
    /// selects from this set, so it is also the right basis for an app's "send everything" amount.
    /// </remarks>
    public static async Task<IReadOnlyList<SparkLeaf>> GetSpendableLeavesAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);

        var leaves = await wallet.GetLeavesAsync(ct).ConfigureAwait(false);
        if (RenewalService.RenewalCandidates(leaves).Renewable.Count > 0)
        {
            try
            {
                _ = await wallet.RenewLeavesAsync(leaves, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best effort: a leaf that could not be renewed is simply left out of this spend.
            }

            leaves = await wallet.GetLeavesAsync(ct).ConfigureAwait(false);
        }

        return leaves.Where(l => l.IsSpendable).ToList();
    }
}
