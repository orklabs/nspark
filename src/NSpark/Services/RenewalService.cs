using NSpark.Bitcoin;
using NSpark.Exceptions;
using NSpark.Models;
using NSpark.Proto;

namespace NSpark.Services;

/// <summary>
/// Extension methods on <see cref="SparkWallet"/> for renewing leaf timelocks
/// via the coordinator's <c>renew_leaf</c> RPC. Spark leaves age: each transfer
/// decrements the refund timelock by 100 blocks, and at the floor the
/// coordinator refuses to move them — sends, swaps, and withdrawals of those
/// sats all fail until renewal.
/// </summary>
public static class RenewalService
{
    private const string Operation = "leaf.renew";

    /// <summary>Fresh refund txs are minted with this timelock (matches JS INITIAL_TIMELOCK).</summary>
    private const uint RenewalInitialSequence = 2000;

    /// <summary>
    /// Renew when the refund timelock drops below this — prevents it going under
    /// 100 after the next transfer, which would freeze the leaf and interfere
    /// with watchtowers (matches JS doesTxnNeedRenewed).
    /// </summary>
    internal const uint RenewalThreshold = 200;

    /// <summary>The renewal the coordinator accepts for a leaf.</summary>
    internal enum RenewalVariant
    {
        /// <summary>Another zero-timelock node (L1-deposit roots, final-sequence roots).</summary>
        ZeroTimelock,

        /// <summary>A zero-timelock split node spliced in, node and refunds reset to 2000.</summary>
        NodeTimelock,

        /// <summary>The node decremented by 100, refunds reset to 2000.</summary>
        RefundTimelock,
    }

    /// <summary>
    /// Renew every leaf whose refund timelock has run low (&lt; 200 blocks).
    /// </summary>
    /// <remarks>
    /// Three protocol variants, chosen per leaf like the reference SDK does
    /// (<see cref="Variant"/>):
    /// <list type="bullet">
    ///   <item><description>node timelock == 0, or a final node sequence → <c>renew_node_zero_timelock</c> (L1-deposit roots)</description></item>
    ///   <item><description>node timelock &lt; 200 → <c>renew_node_timelock</c> (splices in a
    ///   zero-timelock "split node", resets node+refund to 2000)</description></item>
    ///   <item><description>otherwise → <c>renew_refund_timelock</c> (decrements node by 100,
    ///   resets refund to 2000)</description></item>
    /// </list>
    /// Renewals are per-leaf and best-effort: a failing leaf is reported in
    /// <see cref="SparkLeafRenewal.Failures"/> and never aborts the sweep. Leaves whose refund
    /// timelock is below 100 are reported without a round trip: the coordinator will not renew
    /// them, and only a unilateral exit can recover them.
    /// </remarks>
    public static async Task<SparkLeafRenewal> RenewExhaustedLeavesAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);

        var leaves = await wallet.GetLeavesAsync(ct).ConfigureAwait(false);
        return await wallet.RenewLeavesAsync(leaves, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Split leaves into those the coordinator will renew (refund timelock in <c>[100, 200)</c>)
    /// and those it will not (below 100), which only a unilateral exit can recover.
    /// </summary>
    internal static (IReadOnlyList<SparkLeaf> Renewable, IReadOnlyList<SparkLeaf> Stuck) RenewalCandidates(
        IEnumerable<SparkLeaf> leaves)
    {
        var renewable = new List<SparkLeaf>();
        var stuck = new List<SparkLeaf>();
        foreach (var leaf in leaves)
        {
            var timelock = leaf.RefundTimelockBlocks;
            if (timelock >= RenewalThreshold)
            {
                continue;
            }

            if (timelock >= TimelockHelper.TimeLockInterval)
            {
                renewable.Add(leaf);
            }
            else
            {
                stuck.Add(leaf);
            }
        }

        return (renewable, stuck);
    }

    /// <summary>
    /// Renew the renewable leaves among <paramref name="leaves"/> (refund timelock in
    /// <c>[100, 200)</c>) and report the ones below the renewal minimum as failures. Each renewal
    /// is independent: one failing leaf never stops the others.
    /// </summary>
    internal static async Task<SparkLeafRenewal> RenewLeavesAsync(
        this SparkWallet wallet,
        IReadOnlyList<SparkLeaf> leaves,
        CancellationToken ct = default)
    {
        var (needing, stuck) = RenewalCandidates(leaves);

        // The coordinator refuses to renew a leaf whose refund timelock is already below one
        // interval (100 blocks); report those without a round trip.
        var failures = stuck
            .Select(l => $"{l.Id}: refund timelock {l.RefundTimelockBlocks} is below the coordinator's renewal minimum of {TimelockHelper.TimeLockInterval}; only a unilateral exit can recover it")
            .ToList();
        if (needing.Count == 0)
        {
            return new SparkLeafRenewal(leaves.Count, 0, failures);
        }

        var client = wallet.GetCoordinatorClient();

        // Parents provide the prev-out context for the new node txs.
        var parentIds = needing
            .Where(l => l.Node.HasParentNodeId && l.Node.ParentNodeId.Length > 0)
            .Select(l => l.Node.ParentNodeId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var parents = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
        if (parentIds.Count > 0)
        {
            var request = new QueryNodesRequest { NodeIds = new TreeNodeIds() };
            request.NodeIds.NodeIds.AddRange(parentIds);
            var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
            var response = await client.query_nodesAsync(request, headers, cancellationToken: ct).ConfigureAwait(false);
            foreach (var (id, node) in response.Nodes)
            {
                parents[id] = node;
            }
        }

        var renewed = 0;
        foreach (var leaf in needing)
        {
            try
            {
                await RenewLeafAsync(wallet, client, leaf.Node, parents, ct).ConfigureAwait(false);
                renewed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{leaf.Id}: {ex.Message}");
            }
        }

        return new SparkLeafRenewal(leaves.Count, renewed, failures);
    }

    /// <summary>
    /// The renewal the coordinator accepts for a leaf, from its node transaction's sequence:
    /// zero-timelock renewal for a node timelock of 0 or a final (timelock-disabled, bit 31)
    /// sequence — a legacy deposit root's, which cannot be decremented
    /// (<c>validateRenewZeroTimelock</c>) — node renewal below 200, refund renewal otherwise.
    /// </summary>
    internal static RenewalVariant Variant(uint nodeSequence)
    {
        var timelockDisabled = (nodeSequence & (1u << 31)) != 0;
        var nodeTimelock = nodeSequence & 0xFFFF;
        if (nodeTimelock == 0 || timelockDisabled)
        {
            return RenewalVariant.ZeroTimelock;
        }

        return nodeTimelock < RenewalThreshold ? RenewalVariant.NodeTimelock : RenewalVariant.RefundTimelock;
    }

    private static async Task RenewLeafAsync(
        SparkWallet wallet,
        SparkService.SparkServiceClient client,
        TreeNode node,
        Dictionary<string, TreeNode> parents,
        CancellationToken ct)
    {
        var variant = Variant(TimelockHelper.ParseSequence(node.NodeTx.ToByteArray()));
        if (variant == RenewalVariant.ZeroTimelock)
        {
            await RenewZeroTimelockNodeAsync(wallet, client, node, ct).ConfigureAwait(false);
            return;
        }

        if (!node.HasParentNodeId || !parents.TryGetValue(node.ParentNodeId, out var parent))
        {
            throw new SparkUntrustedResponseException(
                Operation, $"Parent node {node.ParentNodeId} not found for leaf {node.Id}.");
        }

        if (variant == RenewalVariant.NodeTimelock)
        {
            await RenewNodeTimelockAsync(wallet, client, node, parent, ct).ConfigureAwait(false);
        }
        else
        {
            await RenewRefundTimelockAsync(wallet, client, node, parent, ct).ConfigureAwait(false);
        }
    }

    // ── Variants ──

    /// <summary>Refund-only renewal: new node tx with timelock −100, fresh refunds at 2000.</summary>
    private static async Task RenewRefundTimelockAsync(
        SparkWallet wallet,
        SparkService.SparkServiceClient client,
        TreeNode node,
        TreeNode parent,
        CancellationToken ct)
    {
        var context = await CreateContextAsync(wallet, node, ct).ConfigureAwait(false);
        var txs = RefundRenewalTransactions(node, parent, context.SigningPublicKey, wallet.Options.Network);

        // Order defines which SO commitment each job consumes.
        var specs = new List<(string Slot, byte[] Tx, byte[] Sighash)>
        {
            ("node", txs.Node.Cpfp.Tx, txs.Node.Cpfp.Sighash),
            ("directNode", txs.Node.Direct.Tx, txs.Node.Direct.Sighash),
            ("cpfp", txs.Refunds.CpfpRefund.Tx, txs.Refunds.CpfpRefund.Sighash),
        };
        if (txs.Refunds.DirectRefund is { } direct)
        {
            specs.Add(("direct", direct.Tx, direct.Sighash));
        }
        specs.Add(("directFromCpfp", txs.Refunds.DirectFromCpfpRefund.Tx, txs.Refunds.DirectFromCpfpRefund.Sighash));

        var jobs = await SignRenewalJobsAsync(wallet, client, specs, context, ct).ConfigureAwait(false);

        var renewJob = new RenewRefundTimelockSigningJob
        {
            NodeTxSigningJob = jobs["node"],
            RefundTxSigningJob = jobs["cpfp"],
            DirectNodeTxSigningJob = jobs["directNode"],
            DirectFromCpfpRefundTxSigningJob = jobs["directFromCpfp"],
        };
        if (jobs.TryGetValue("direct", out var directJob))
        {
            renewJob.DirectRefundTxSigningJob = directJob;
        }

        var request = new RenewLeafRequest
        {
            LeafId = node.Id,
            RenewRefundTimelockSigningJob = renewJob,
        };
        await SubmitRenewalAsync(wallet, client, request, node, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Full node renewal: zero-timelock "split node" spliced above a fresh node
    /// tx at 2000, refunds reset to 2000.
    /// </summary>
    private static async Task RenewNodeTimelockAsync(
        SparkWallet wallet,
        SparkService.SparkServiceClient client,
        TreeNode node,
        TreeNode parent,
        CancellationToken ct)
    {
        var context = await CreateContextAsync(wallet, node, ct).ConfigureAwait(false);
        var txs = NodeRenewalTransactions(node, parent, context.SigningPublicKey, wallet.Options.Network);
        var split = txs.Split
            ?? throw new InvalidOperationException($"Node renewal for leaf {node.Id} built no split node.");

        var specs = new List<(string Slot, byte[] Tx, byte[] Sighash)>
        {
            ("split", split.Cpfp.Tx, split.Cpfp.Sighash),
            ("directSplit", split.Direct.Tx, split.Direct.Sighash),
            ("node", txs.Node.Cpfp.Tx, txs.Node.Cpfp.Sighash),
            ("directNode", txs.Node.Direct.Tx, txs.Node.Direct.Sighash),
            ("cpfp", txs.Refunds.CpfpRefund.Tx, txs.Refunds.CpfpRefund.Sighash),
        };
        if (txs.Refunds.DirectRefund is { } direct)
        {
            specs.Add(("direct", direct.Tx, direct.Sighash));
        }
        specs.Add(("directFromCpfp", txs.Refunds.DirectFromCpfpRefund.Tx, txs.Refunds.DirectFromCpfpRefund.Sighash));

        var jobs = await SignRenewalJobsAsync(wallet, client, specs, context, ct).ConfigureAwait(false);

        var renewJob = new RenewNodeTimelockSigningJob
        {
            SplitNodeTxSigningJob = jobs["split"],
            SplitNodeDirectTxSigningJob = jobs["directSplit"],
            NodeTxSigningJob = jobs["node"],
            RefundTxSigningJob = jobs["cpfp"],
            DirectNodeTxSigningJob = jobs["directNode"],
            DirectFromCpfpRefundTxSigningJob = jobs["directFromCpfp"],
        };
        if (jobs.TryGetValue("direct", out var directJob))
        {
            renewJob.DirectRefundTxSigningJob = directJob;
        }

        var request = new RenewLeafRequest
        {
            LeafId = node.Id,
            RenewNodeTimelockSigningJob = renewJob,
        };
        await SubmitRenewalAsync(wallet, client, request, node, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Zero-node renewal: the node tx is at timelock 0 (L1-deposit roots) —
    /// appends another zero-timelock node and resets the refunds.
    /// </summary>
    private static async Task RenewZeroTimelockNodeAsync(
        SparkWallet wallet,
        SparkService.SparkServiceClient client,
        TreeNode node,
        CancellationToken ct)
    {
        var context = await CreateContextAsync(wallet, node, ct).ConfigureAwait(false);
        var txs = ZeroTimelockRenewalTransactions(node, context.SigningPublicKey, wallet.Options.Network);

        var specs = new List<(string Slot, byte[] Tx, byte[] Sighash)>
        {
            ("node", txs.Node.Cpfp.Tx, txs.Node.Cpfp.Sighash),
            ("directNode", txs.Node.Direct.Tx, txs.Node.Direct.Sighash),
            ("cpfp", txs.Refunds.CpfpRefund.Tx, txs.Refunds.CpfpRefund.Sighash),
            ("directFromCpfp", txs.Refunds.DirectFromCpfpRefund.Tx, txs.Refunds.DirectFromCpfpRefund.Sighash),
        };

        var jobs = await SignRenewalJobsAsync(wallet, client, specs, context, ct).ConfigureAwait(false);

        var renewJob = new RenewNodeZeroTimelockSigningJob
        {
            NodeTxSigningJob = jobs["node"],
            RefundTxSigningJob = jobs["cpfp"],
            DirectNodeTxSigningJob = jobs["directNode"],
            DirectFromCpfpRefundTxSigningJob = jobs["directFromCpfp"],
        };

        var request = new RenewLeafRequest
        {
            LeafId = node.Id,
            RenewNodeZeroTimelockSigningJob = renewJob,
        };
        await SubmitRenewalAsync(wallet, client, request, node, ct).ConfigureAwait(false);
    }

    // ── Renewal transactions (what the operators rebuild, renew_leaf_handler.go) ──

    /// <summary>The transactions of one renewal.</summary>
    /// <param name="Split">The split node (node renewal only).</param>
    /// <param name="Node">The new node transaction pair.</param>
    /// <param name="Refunds">The fresh refunds.</param>
    internal sealed record RenewalTransactions(SparkNodeTxPair? Split, SparkNodeTxPair Node, SparkRefundTxTrio Refunds);

    /// <summary>
    /// The P2TR address a leaf's node transaction pays: the leaf's verifying key with the BIP-86
    /// key-path tweak (<c>P2TRScriptFromPubKey(leaf.VerifyingPubkey)</c> on the operators).
    /// </summary>
    internal static string LeafNodeAddress(byte[] verifyingKey, SparkNetwork network)
    {
        var tweaked = uniffi.spark_frost.SparkFrostMethods.GetTaprootPubkey(verifyingKey);
        if (tweaked.Length != 33)
        {
            throw new SparkUntrustedResponseException(Operation, $"Unexpected taproot key length {tweaked.Length}.");
        }

        var script = new byte[34];
        script[0] = 0x51;
        script[1] = 0x20;
        tweaked.AsSpan(1).CopyTo(script.AsSpan(2));
        return P2trAddress(script, FrostSigningHelper.GetNetworkString(network));
    }

    /// <summary>
    /// Refund renewal: a new node transaction spending the parent's output <c>node.vout</c> at the
    /// node timelock minus 100, paying the leaf's node address, and fresh refunds at 2000.
    /// </summary>
    internal static RenewalTransactions RefundRenewalTransactions(
        TreeNode node,
        TreeNode parent,
        byte[] signingPublicKey,
        SparkNetwork network)
    {
        var nodeSequence = TimelockHelper.ParseSequence(node.NodeTx.ToByteArray());
        var bit30 = nodeSequence & (1u << 30);
        var nodeTimelock = nodeSequence & 0xFFFF;
        // Node timelocks may legitimately reach 0 (handled by the zero variant),
        // so this boundary is >= 100, unlike the strict refund floor guard.
        if (nodeTimelock < TimelockHelper.TimeLockInterval)
        {
            throw new SparkLeafTimelockExhaustedException(
                Operation, $"Node timelock {nodeTimelock} too low for refund renewal.")
            {
                LeafId = node.Id,
            };
        }

        var newNodeSequence = bit30 | (nodeTimelock - TimelockHelper.TimeLockInterval);
        var nodePair = SparkTxBuilder.BuildNodeTxPair(
            parent.NodeTx.ToByteArray(),
            vout: node.Vout,
            address: LeafNodeAddress(node.VerifyingPublicKey.ToByteArray(), network),
            sequence: newNodeSequence,
            directSequence: newNodeSequence + TimelockHelper.DirectTimelockOffset,
            feeSats: SparkConstants.DefaultRefundFeeSats);
        return new RenewalTransactions(null, nodePair, InitialRefunds(nodePair, signingPublicKey, network));
    }

    /// <summary>
    /// Node renewal: a zero-timelock split node spending the parent's output <c>node.vout</c>, a
    /// new node transaction at 2000 spending it, both paying the leaf's node address, and fresh
    /// refunds at 2000.
    /// </summary>
    internal static RenewalTransactions NodeRenewalTransactions(
        TreeNode node,
        TreeNode parent,
        byte[] signingPublicKey,
        SparkNetwork network)
    {
        var address = LeafNodeAddress(node.VerifyingPublicKey.ToByteArray(), network);
        var splitPair = SparkTxBuilder.BuildNodeTxPair(
            parent.NodeTx.ToByteArray(),
            vout: node.Vout,
            address: address,
            sequence: 0,
            directSequence: TimelockHelper.DirectTimelockOffset,
            feeSats: SparkConstants.DefaultRefundFeeSats);
        var nodePair = SparkTxBuilder.BuildNodeTxPair(
            splitPair.Cpfp.Tx,
            vout: 0,
            address: address,
            sequence: RenewalInitialSequence,
            directSequence: RenewalInitialSequence + TimelockHelper.DirectTimelockOffset,
            feeSats: SparkConstants.DefaultRefundFeeSats);
        return new RenewalTransactions(splitPair, nodePair, InitialRefunds(nodePair, signingPublicKey, network));
    }

    /// <summary>
    /// Zero-timelock renewal: another zero-timelock node spending the leaf's own node transaction
    /// (output 0), and fresh refunds at 2000 without a direct refund.
    /// </summary>
    internal static RenewalTransactions ZeroTimelockRenewalTransactions(
        TreeNode node,
        byte[] signingPublicKey,
        SparkNetwork network)
    {
        var nodePair = SparkTxBuilder.BuildNodeTxPair(
            node.NodeTx.ToByteArray(),
            vout: 0,
            address: LeafNodeAddress(node.VerifyingPublicKey.ToByteArray(), network),
            sequence: 0,
            directSequence: TimelockHelper.DirectTimelockOffset,
            feeSats: SparkConstants.DefaultRefundFeeSats);

        // Zero-timelock node → no direct node context for the refunds.
        var refunds = SparkTxBuilder.BuildRefundTxTrio(
            nodePair.Cpfp.Tx,
            directNodeTx: null,
            vout: 0,
            receivingPublicKey: signingPublicKey,
            network: FrostSigningHelper.GetNetworkString(network),
            sequence: RenewalInitialSequence,
            directSequence: RenewalInitialSequence + TimelockHelper.DirectTimelockOffset,
            feeSats: SparkConstants.DefaultRefundFeeSats);
        return new RenewalTransactions(null, nodePair, refunds);
    }

    /// <summary>Refunds at the initial timelock (2000) spending a renewed node pair.</summary>
    private static SparkRefundTxTrio InitialRefunds(SparkNodeTxPair nodePair, byte[] signingPublicKey, SparkNetwork network) =>
        SparkTxBuilder.BuildRefundTxTrio(
            nodePair.Cpfp.Tx,
            nodePair.Direct.Tx,
            vout: 0,
            receivingPublicKey: signingPublicKey,
            network: FrostSigningHelper.GetNetworkString(network),
            sequence: RenewalInitialSequence,
            directSequence: RenewalInitialSequence + TimelockHelper.DirectTimelockOffset,
            feeSats: SparkConstants.DefaultRefundFeeSats);

    // ── Shared plumbing ──

    private sealed record RenewalContext(string LeafId, byte[] SigningPublicKey, byte[] VerifyingKey);

    private static async Task<RenewalContext> CreateContextAsync(
        SparkWallet wallet, TreeNode node, CancellationToken ct)
    {
        var signingPublicKey = await wallet.Signer.GetLeafPublicKeyAsync(node.Id, ct).ConfigureAwait(false);
        return new RenewalContext(node.Id, signingPublicKey, node.VerifyingPublicKey.ToByteArray());
    }

    /// <summary>Fetch one SO commitment per job (indexed by position) and FROST-sign.</summary>
    private static async Task<Dictionary<string, UserSignedTxSigningJob>> SignRenewalJobsAsync(
        SparkWallet wallet,
        SparkService.SparkServiceClient client,
        List<(string Slot, byte[] Tx, byte[] Sighash)> specs,
        RenewalContext context,
        CancellationToken ct)
    {
        var commitmentsRequest = new GetSigningCommitmentsRequest { Count = (uint)specs.Count };
        commitmentsRequest.NodeIds.Add(context.LeafId);
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        var commitmentsResponse = await client.get_signing_commitmentsAsync(
            commitmentsRequest, headers, cancellationToken: ct).ConfigureAwait(false);

        var allCommitments = commitmentsResponse.SigningCommitments;
        if (allCommitments.Count < specs.Count)
        {
            throw new SparkUntrustedResponseException(
                Operation, $"Got {allCommitments.Count} signing commitments, need {specs.Count}.");
        }

        var jobs = new Dictionary<string, UserSignedTxSigningJob>(StringComparer.Ordinal);
        for (var i = 0; i < specs.Count; i++)
        {
            var (slot, tx, sighash) = specs[i];
            jobs[slot] = await FrostSigningHelper.BuildSigningJobAsync(
                wallet.Signer, context.LeafId, context.VerifyingKey, tx, sighash,
                allCommitments[i].SigningNonceCommitments, ct).ConfigureAwait(false);
        }

        return jobs;
    }

    /// <summary>
    /// Submits a renewal under the idempotency key <see cref="RenewalIdempotencyKey"/>, so the
    /// transport's retry of a renewal the operators already applied gets their answer instead of
    /// failing.
    /// </summary>
    private static async Task SubmitRenewalAsync(
        SparkWallet wallet,
        SparkService.SparkServiceClient client,
        RenewLeafRequest request,
        TreeNode node,
        CancellationToken ct)
    {
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        headers.Add(IdempotencyKeyHeader, RenewalIdempotencyKey(node));
        var response = await client.renew_leafAsync(request, headers, cancellationToken: ct).ConfigureAwait(false);
        if (response.RenewResultCase == RenewLeafResponse.RenewResultOneofCase.None)
        {
            throw new SparkUntrustedResponseException(Operation, $"renew_leaf returned no result for leaf {node.Id}.");
        }
    }

    /// <summary>The gRPC header carrying an idempotency key.</summary>
    internal const string IdempotencyKeyHeader = "x-idempotency-key";

    /// <summary>
    /// A leaf renewal's idempotency key: the txid of the refund transaction being replaced, as the
    /// reference SDK keys all three renewal variants. It changes with every renewal, so it names
    /// exactly one.
    /// </summary>
    internal static string RenewalIdempotencyKey(TreeNode node) =>
        RawTransaction.Parse(node.RefundTx.Span, "refund tx").TxidHex;

    /// <summary>bech32m P2TR address for an <c>OP_1 &lt;32-byte&gt;</c> output script.</summary>
    internal static string P2trAddress(byte[] pkScript, string network)
    {
        if (pkScript.Length != 34 || pkScript[0] != 0x51 || pkScript[1] != 0x20)
        {
            throw new InvalidOperationException(
                $"Output script is not P2TR ({Convert.ToHexString(pkScript).ToLowerInvariant()}).");
        }

        var hrp = network switch
        {
            "mainnet" => "bc",
            "regtest" => "bcrt",
            _ => "tb",
        };
        return Bech32mHelper.EncodeSegwit(hrp, 0x01, pkScript.AsSpan(2));
    }
}
