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
    /// <summary>Fresh refund txs are minted with this timelock (matches JS INITIAL_TIMELOCK).</summary>
    private const uint RenewalInitialSequence = 2000;

    /// <summary>
    /// Renew when the refund timelock drops below this — prevents it going under
    /// 100 after the next transfer, which would freeze the leaf and interfere
    /// with watchtowers (matches JS doesTxnNeedRenewed).
    /// </summary>
    internal const uint RenewalThreshold = 200;

    /// <summary>
    /// Renew every leaf whose refund timelock has run low (&lt; 200 blocks).
    /// </summary>
    /// <remarks>
    /// Three protocol variants, chosen per leaf like the reference SDKs do:
    /// <list type="bullet">
    ///   <item><description>node timelock == 0 → <c>renew_node_zero_timelock</c> (L1-deposit roots)</description></item>
    ///   <item><description>node timelock &lt; 200 → <c>renew_node_timelock</c> (splices in a
    ///   zero-timelock "split node", resets node+refund to 2000)</description></item>
    ///   <item><description>otherwise → <c>renew_refund_timelock</c> (decrements node by 100,
    ///   resets refund to 2000)</description></item>
    /// </list>
    /// Renewals are per-leaf and best-effort: a failing leaf is reported in
    /// <see cref="SparkLeafRenewal.Failures"/> and never aborts the sweep.
    /// </remarks>
    public static async Task<SparkLeafRenewal> RenewExhaustedLeavesAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);

        var leaves = await wallet.GetLeavesAsync(ct).ConfigureAwait(false);
        var needing = leaves.Where(l => l.RefundTimelockBlocks < RenewalThreshold).ToList();
        return await RenewLeavesAsync(wallet, needing, leaves.Count, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Renew exactly the given leaves (the spend paths pass the ones in the coordinator's
    /// renewable range, <see cref="SparkLeaf.IsRenewable"/>). Per-leaf and best-effort like
    /// <see cref="RenewExhaustedLeavesAsync"/>.
    /// </summary>
    internal static Task<SparkLeafRenewal> RenewLeavesAsync(
        this SparkWallet wallet,
        IReadOnlyList<SparkLeaf> needing,
        CancellationToken ct = default)
        => RenewLeavesAsync(wallet, needing, needing.Count, ct);

    private static async Task<SparkLeafRenewal> RenewLeavesAsync(
        SparkWallet wallet,
        IReadOnlyList<SparkLeaf> needing,
        int checkedCount,
        CancellationToken ct)
    {
        if (needing.Count == 0)
        {
            return new SparkLeafRenewal(checkedCount, 0, Array.Empty<string>());
        }

        var soAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var client = wallet.Pool.GetSparkClient(soAddress);
        var headers = await wallet.GetAuthMetadataAsync(soAddress, ct).ConfigureAwait(false);

        // Parents provide the prev-out context for the new node txs.
        var parentIds = needing
            .Where(l => l.Node.HasParentNodeId && l.Node.ParentNodeId.Length > 0)
            .Select(l => l.Node.ParentNodeId)
            .Distinct()
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var parents = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
        if (parentIds.Count > 0)
        {
            var request = new QueryNodesRequest { NodeIds = new TreeNodeIds() };
            request.NodeIds.NodeIds.AddRange(parentIds);
            var response = await client.query_nodesAsync(
                request, headers, cancellationToken: ct).ConfigureAwait(false);
            foreach (var kv in response.Nodes)
            {
                parents[kv.Key] = kv.Value;
            }
        }

        var renewed = 0;
        var failures = new List<string>();
        foreach (var leaf in needing)
        {
            try
            {
                await RenewLeafAsync(wallet, client, headers, leaf.Node, parents, ct).ConfigureAwait(false);
                renewed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{leaf.Id}: {ex.Message}");
            }
        }

        return new SparkLeafRenewal(checkedCount, renewed, failures);
    }

    private static async Task RenewLeafAsync(
        SparkWallet wallet,
        SparkService.SparkServiceClient client,
        Grpc.Core.Metadata headers,
        TreeNode node,
        Dictionary<string, TreeNode> parents,
        CancellationToken ct)
    {
        var nodeTimelock = ClaimService.ParseInputSequence(node.NodeTx.ToByteArray()) & 0xFFFF;
        if (nodeTimelock == 0)
        {
            await RenewZeroTimelockNodeAsync(wallet, client, headers, node, ct).ConfigureAwait(false);
            return;
        }

        if (!node.HasParentNodeId || !parents.TryGetValue(node.ParentNodeId, out var parent))
        {
            throw new InvalidOperationException(
                $"Parent node {node.ParentNodeId} not found for leaf {node.Id}.");
        }

        if (nodeTimelock < RenewalThreshold)
        {
            await RenewNodeTimelockAsync(wallet, client, headers, node, parent, ct).ConfigureAwait(false);
        }
        else
        {
            await RenewRefundTimelockAsync(wallet, client, headers, node, parent, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Refund-only renewal: new node tx with timelock −100, fresh refunds at 2000.</summary>
    private static async Task RenewRefundTimelockAsync(
        SparkWallet wallet,
        SparkService.SparkServiceClient client,
        Grpc.Core.Metadata headers,
        TreeNode node,
        TreeNode parent,
        CancellationToken ct)
    {
        var context = await CreateContextAsync(wallet, node, ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(wallet.Client.Options.Network);
        var parentTx = parent.NodeTx.ToByteArray();
        var address = P2trAddress(WithdrawalService.ParseTxOutput(parentTx, 0).Script, networkStr);

        var nodeSequence = ClaimService.ParseInputSequence(node.NodeTx.ToByteArray());
        var bit30 = nodeSequence & (1u << 30);
        var nodeTimelock = nodeSequence & 0xFFFF;
        // Node timelocks may legitimately reach 0 (handled by the zero variant),
        // so this boundary is >= 100, unlike the strict > 100 refund floor guard.
        if (nodeTimelock < TimelockHelper.TimeLockInterval)
        {
            throw new SparkLeafTimelockExhaustedException(
                "leaf.renew", $"Node timelock {nodeTimelock} too low for refund renewal.")
            {
                LeafId = node.Id,
            };
        }

        var newNodeSequence = bit30 | (nodeTimelock - TimelockHelper.TimeLockInterval);

        var nodePair = SparkTxBuilder.BuildNodeTxPair(
            parentTx,
            vout: 0,
            address: address,
            sequence: newNodeSequence,
            directSequence: newNodeSequence + TimelockHelper.DirectTimelockOffset,
            feeSats: SparkConstants.DefaultRefundFeeSats);
        var trio = SparkTxBuilder.BuildRefundTxTrio(
            nodePair.Cpfp.Tx,
            nodePair.Direct.Tx,
            vout: 0,
            receivingPublicKey: context.SigningPublicKey,
            network: networkStr,
            sequence: RenewalInitialSequence,
            directSequence: RenewalInitialSequence + TimelockHelper.DirectTimelockOffset,
            feeSats: SparkConstants.DefaultRefundFeeSats);

        // Order defines which SO commitment each job consumes.
        var specs = new List<(string Slot, byte[] Tx, byte[] Sighash)>
        {
            ("node", nodePair.Cpfp.Tx, nodePair.Cpfp.Sighash),
            ("directNode", nodePair.Direct.Tx, nodePair.Direct.Sighash),
            ("cpfp", trio.CpfpRefund.Tx, trio.CpfpRefund.Sighash),
        };
        if (trio.DirectRefund != null)
        {
            specs.Add(("direct", trio.DirectRefund.Tx, trio.DirectRefund.Sighash));
        }
        specs.Add(("directFromCpfp", trio.DirectFromCpfpRefund.Tx, trio.DirectFromCpfpRefund.Sighash));

        var jobs = await SignRenewalJobsAsync(wallet, client, headers, specs, context, ct).ConfigureAwait(false);

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
        await SubmitRenewalAsync(client, headers, request, node.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Full node renewal: zero-timelock "split node" spliced above a fresh node
    /// tx at 2000, refunds reset to 2000.
    /// </summary>
    private static async Task RenewNodeTimelockAsync(
        SparkWallet wallet,
        SparkService.SparkServiceClient client,
        Grpc.Core.Metadata headers,
        TreeNode node,
        TreeNode parent,
        CancellationToken ct)
    {
        var context = await CreateContextAsync(wallet, node, ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(wallet.Client.Options.Network);
        var parentTx = parent.NodeTx.ToByteArray();
        var address = P2trAddress(WithdrawalService.ParseTxOutput(parentTx, 0).Script, networkStr);

        // Split node: spends the parent output with zero timelock.
        var splitPair = SparkTxBuilder.BuildNodeTxPair(
            parentTx,
            vout: 0,
            address: address,
            sequence: 0,
            directSequence: TimelockHelper.DirectTimelockOffset,
            feeSats: SparkConstants.DefaultRefundFeeSats);

        // New node: spends the split node output at the initial timelock.
        var splitAddress = P2trAddress(
            WithdrawalService.ParseTxOutput(splitPair.Cpfp.Tx, 0).Script, networkStr);
        var nodePair = SparkTxBuilder.BuildNodeTxPair(
            splitPair.Cpfp.Tx,
            vout: 0,
            address: splitAddress,
            sequence: RenewalInitialSequence,
            directSequence: RenewalInitialSequence + TimelockHelper.DirectTimelockOffset,
            feeSats: SparkConstants.DefaultRefundFeeSats);
        var trio = SparkTxBuilder.BuildRefundTxTrio(
            nodePair.Cpfp.Tx,
            nodePair.Direct.Tx,
            vout: 0,
            receivingPublicKey: context.SigningPublicKey,
            network: networkStr,
            sequence: RenewalInitialSequence,
            directSequence: RenewalInitialSequence + TimelockHelper.DirectTimelockOffset,
            feeSats: SparkConstants.DefaultRefundFeeSats);

        var specs = new List<(string Slot, byte[] Tx, byte[] Sighash)>
        {
            ("split", splitPair.Cpfp.Tx, splitPair.Cpfp.Sighash),
            ("directSplit", splitPair.Direct.Tx, splitPair.Direct.Sighash),
            ("node", nodePair.Cpfp.Tx, nodePair.Cpfp.Sighash),
            ("directNode", nodePair.Direct.Tx, nodePair.Direct.Sighash),
            ("cpfp", trio.CpfpRefund.Tx, trio.CpfpRefund.Sighash),
        };
        if (trio.DirectRefund != null)
        {
            specs.Add(("direct", trio.DirectRefund.Tx, trio.DirectRefund.Sighash));
        }
        specs.Add(("directFromCpfp", trio.DirectFromCpfpRefund.Tx, trio.DirectFromCpfpRefund.Sighash));

        var jobs = await SignRenewalJobsAsync(wallet, client, headers, specs, context, ct).ConfigureAwait(false);

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
        await SubmitRenewalAsync(client, headers, request, node.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Zero-node renewal: the node tx is at timelock 0 (L1-deposit roots) —
    /// appends another zero-timelock node and resets the refunds.
    /// </summary>
    private static async Task RenewZeroTimelockNodeAsync(
        SparkWallet wallet,
        SparkService.SparkServiceClient client,
        Grpc.Core.Metadata headers,
        TreeNode node,
        CancellationToken ct)
    {
        var context = await CreateContextAsync(wallet, node, ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(wallet.Client.Options.Network);
        var nodeTx = node.NodeTx.ToByteArray();
        var address = P2trAddress(WithdrawalService.ParseTxOutput(nodeTx, 0).Script, networkStr);

        var nodePair = SparkTxBuilder.BuildNodeTxPair(
            nodeTx,
            vout: 0,
            address: address,
            sequence: 0,
            directSequence: TimelockHelper.DirectTimelockOffset,
            feeSats: SparkConstants.DefaultRefundFeeSats);

        // Zero-timelock node → no direct node context for the refunds.
        var trio = SparkTxBuilder.BuildRefundTxTrio(
            nodePair.Cpfp.Tx,
            directNodeTx: null,
            vout: 0,
            receivingPublicKey: context.SigningPublicKey,
            network: networkStr,
            sequence: RenewalInitialSequence,
            directSequence: RenewalInitialSequence + TimelockHelper.DirectTimelockOffset,
            feeSats: SparkConstants.DefaultRefundFeeSats);

        var specs = new List<(string Slot, byte[] Tx, byte[] Sighash)>
        {
            ("node", nodePair.Cpfp.Tx, nodePair.Cpfp.Sighash),
            ("directNode", nodePair.Direct.Tx, nodePair.Direct.Sighash),
            ("cpfp", trio.CpfpRefund.Tx, trio.CpfpRefund.Sighash),
            ("directFromCpfp", trio.DirectFromCpfpRefund.Tx, trio.DirectFromCpfpRefund.Sighash),
        };

        var jobs = await SignRenewalJobsAsync(wallet, client, headers, specs, context, ct).ConfigureAwait(false);

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
        await SubmitRenewalAsync(client, headers, request, node.Id, ct).ConfigureAwait(false);
    }

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
        Grpc.Core.Metadata headers,
        List<(string Slot, byte[] Tx, byte[] Sighash)> specs,
        RenewalContext context,
        CancellationToken ct)
    {
        var commitmentsRequest = new GetSigningCommitmentsRequest { Count = (uint)specs.Count };
        commitmentsRequest.NodeIds.Add(context.LeafId);
        var commitmentsResponse = await client.get_signing_commitmentsAsync(
            commitmentsRequest, headers, cancellationToken: ct).ConfigureAwait(false);

        var allCommitments = commitmentsResponse.SigningCommitments;
        if (allCommitments.Count < specs.Count)
        {
            throw new InvalidOperationException(
                $"Got {allCommitments.Count} signing commitments, need {specs.Count}.");
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

    private static async Task SubmitRenewalAsync(
        SparkService.SparkServiceClient client,
        Grpc.Core.Metadata headers,
        RenewLeafRequest request,
        string leafId,
        CancellationToken ct)
    {
        var response = await client.renew_leafAsync(request, headers, cancellationToken: ct).ConfigureAwait(false);
        if (response.RenewResultCase == RenewLeafResponse.RenewResultOneofCase.None)
        {
            throw new InvalidOperationException($"renew_leaf returned no result for leaf {leafId}.");
        }
    }

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
