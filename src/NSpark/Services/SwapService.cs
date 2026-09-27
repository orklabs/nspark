using Google.Protobuf;
using NSpark.Exceptions;
using NSpark.GraphQL;
using NSpark.Models;
using NSpark.Proto;

namespace NSpark.Services;

/// <summary>
/// Leaf swap service — splits existing leaves into target denominations via SSP.
/// Ported from Swift SDK SwapService.swift.
/// </summary>
public static class SwapService
{
    private const string Operation = "swap.batch";

    /// <summary>
    /// Select leaves that exactly cover the target amount. If no exact match exists,
    /// triggers a leaf swap via SSP to split leaves into the required denominations.
    /// Only spendable leaves take part (see <see cref="BalanceService.GetSpendableLeavesAsync"/>):
    /// frozen leaves are left out instead of failing the whole operation.
    /// </summary>
    public static async Task<IReadOnlyList<SparkLeaf>> SelectLeavesWithSwapAsync(
        this SparkWallet wallet,
        long amountSats,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        if (amountSats <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amountSats), amountSats, "Target amount must be positive.");
        }

        var leaves = await wallet.GetSpendableLeavesAsync(ct).ConfigureAwait(false);

        // First try exact selection (leaves that sum exactly to the target)
        var exact = TryExactSelection(leaves, amountSats);
        if (exact != null)
        {
            return exact;
        }

        // No exact match — swap leaves via SSP to get right denominations
        _ = await wallet.RequestLeavesSwapAsync([amountSats], ct).ConfigureAwait(false);

        // Retry selection with the swap's output (must find an exact match — never overspend).
        // The SSP may return leaves in the renewal range; renew them rather than leave them out,
        // as the reference SDK does before it uses swap outputs.
        var afterSwap = await wallet.GetSpendableLeavesAsync(ct).ConfigureAwait(false);
        exact = TryExactSelection(afterSwap, amountSats);
        if (exact != null)
        {
            return exact;
        }

        throw new SparkTransferException(
            Operation,
            $"Leaf swap did not produce an exact denomination for {amountSats} sats. Spendable: {string.Join(", ", afterSwap.Select(l => l.ValueSats))} sats.");
    }

    /// <summary>
    /// Try to find spendable leaves that exactly sum to the target amount.
    /// Returns null if no exact combination found.
    /// </summary>
    internal static IReadOnlyList<SparkLeaf>? TryExactSelection(IReadOnlyList<SparkLeaf> leaves, long amountSats)
    {
        var available = leaves.Where(l => l.Status == "AVAILABLE" && l.IsSpendable).ToList();

        // Check if single leaf matches exactly
        var single = available.FirstOrDefault(l => l.ValueSats == amountSats);
        if (single != null)
        {
            return [single];
        }

        // Greedy descending: take each leaf if it fits in the remaining amount.
        // For power-of-2 denominations this always finds an exact match if one exists.
        var sorted = available.OrderByDescending(l => l.ValueSats).ToList();
        var selected = new List<SparkLeaf>();
        var remaining = amountSats;
        foreach (var leaf in sorted)
        {
            if (leaf.ValueSats <= remaining)
            {
                selected.Add(leaf);
                remaining -= leaf.ValueSats;
                if (remaining == 0)
                {
                    return selected;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Request leaf swap via SSP: splits existing leaves into target denominations.
    /// Returns the wallet's leaves after the swap's counter-transfer is claimed.
    /// </summary>
    public static async Task<IReadOnlyList<SparkLeaf>> RequestLeavesSwapAsync(
        this SparkWallet wallet,
        long[] targetAmounts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentNullException.ThrowIfNull(targetAmounts);
        var totalTarget = targetAmounts.Sum();
        var leaves = await wallet.GetSpendableLeavesAsync(ct).ConfigureAwait(false);

        // Select leaves covering the total target (smallest first)
        var sorted = leaves
            .Where(l => l.Status == "AVAILABLE" && l.IsSpendable)
            .OrderBy(l => l.ValueSats)
            .ToList();
        var selected = new List<SparkLeaf>();
        long total = 0;
        foreach (var leaf in sorted)
        {
            if (total < totalTarget)
            {
                selected.Add(leaf);
                total += leaf.ValueSats;
            }
        }

        if (total < totalTarget)
        {
            throw new SparkTransferException(
                "swap.select", $"Insufficient balance for swap: need {totalTarget} sats, have {total} sats.");
        }

        return await wallet.ProcessSwapBatchAsync(selected, targetAmounts, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Process a single batch of leaves for swapping.
    /// 1. Send swap transfer to coordinator (with adaptor key)
    /// 2. Aggregate FROST signatures with adaptor pubkey
    /// 3. Call SSP request_swap mutation
    /// 4. Claim inbound transfer from SSP
    /// </summary>
    internal static async Task<IReadOnlyList<SparkLeaf>> ProcessSwapBatchAsync(
        this SparkWallet wallet,
        IReadOnlyList<SparkLeaf> leaves,
        long[] targetAmounts,
        CancellationToken ct = default)
    {
        var options = wallet.Options;
        var coordinatorClient = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(options.Network);
        var receiverPubKey = options.RequireSspIdentityPublicKey();

        // Get SO operator list
        var soListResponse = await coordinatorClient.get_signing_operator_listAsync(
            new Google.Protobuf.WellKnownTypes.Empty(), headers, cancellationToken: ct).ConfigureAwait(false);
        var soTargets = FrostSigningHelper.BuildSoTargets(soListResponse.SigningOperators, options.SigningOperators);
        var threshold = options.EffectiveSigningThreshold;
        FrostSigningHelper.ValidateThreshold(threshold, soTargets.Count);

        // Generate adaptor keypair via the signer — the adaptor private key never leaves the signer.
        var adaptorKey = await wallet.Signer.GenerateAdaptorKeyAsync(ct).ConfigureAwait(false);
        var adaptorPubKey = adaptorKey.PublicKey;

        var transferId = Guid.NewGuid().ToString("D");
        var expiryTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
            DateTimeOffset.UtcNow + TransferService.TransferExpiry);

        // Get signing commitments (count=3; only cpfp is used, direct/directFromCpfp are cleared for swaps)
        var commitmentsRequest = new GetSigningCommitmentsRequest { Count = 3 };
        commitmentsRequest.NodeIds.AddRange(leaves.Select(l => l.Id));
        var commitmentsResponse = await coordinatorClient.get_signing_commitmentsAsync(
            commitmentsRequest, headers, cancellationToken: ct).ConfigureAwait(false);
        var allCommitments = commitmentsResponse.SigningCommitments;
        if (allCommitments.Count < leaves.Count)
        {
            throw new SparkUntrustedResponseException(
                Operation, $"Got {allCommitments.Count} signing commitments, need {leaves.Count}.");
        }

        // Build encrypted per-SO tweak packages via the signer (no plaintext shares cross the wallet).
        var leafDescriptors = leaves
            .Select(l => new Signer.SendTweakLeafDescriptor(l.Id, receiverPubKey))
            .ToList();
        var encryptedBatch = await wallet.Signer.BuildEncryptedSendTweaksAsync(
            leafDescriptors, soTargets, transferId, threshold, ct).ConfigureAwait(false);

        var keyTweakPackage = new Dictionary<string, ByteString>(encryptedBatch.EncryptedPackageBySoId.Count);
        foreach (var (soId, blob) in encryptedBatch.EncryptedPackageBySoId)
        {
            keyTweakPackage[soId] = ByteString.CopyFrom(blob);
        }

        // FROST sign CPFP refund txs with adaptor (one round per leaf).
        var cpfpRefundJobs = new List<UserSignedTxSigningJob>();
        var leafSigningInfos = new List<(string LeafId, Signer.SigningCommitment SelfCommitment, byte[] Sighash)>();

        for (var i = 0; i < leaves.Count; i++)
        {
            var leaf = leaves[i];
            var node = leaf.Node;
            var verifyingKey = node.VerifyingPublicKey.ToByteArray();
            var cpfpCommitments = allCommitments[i].SigningNonceCommitments;

            var (cpfpSequence, directSequence) = TimelockHelper.ComputeNextSequences(
                node.RefundTx.ToByteArray(), Operation, leaf.Id);
            var refundTrio = TimelockHelper.LeafRefundTrio(node, receiverPubKey, networkStr, cpfpSequence, directSequence);

            var (job, selfCommitment, sighash) = await FrostSigningHelper.BuildSigningJobWithAdaptorAsync(
                wallet.Signer, leaf.Id, verifyingKey,
                refundTrio.CpfpRefund.Tx, refundTrio.CpfpRefund.Sighash,
                cpfpCommitments, adaptorPubKey, ct)
                .ConfigureAwait(false);
            cpfpRefundJobs.Add(job);
            leafSigningInfos.Add((leaf.Id, selfCommitment, sighash));
        }

        // Sign the transfer package
        var packageHash = SparkTaggedHash.Create("spark", "transfer", "signing payload")
            .AddBytes(ClaimService.TransferIdBytes(transferId))
            .AddMapStringToBytes(keyTweakPackage)
            .Hash();
        var packageSignature = await wallet.Signer.SignWithIdentityKeyAsync(packageHash, ct).ConfigureAwait(false);

        // Build TransferPackage (direct/directFromCpfp cleared for swap, as the reference SDK does)
        var transferPackage = new TransferPackage
        {
            UserSignature = ByteString.CopyFrom(packageSignature),
            HashVariant = HashVariant.V2,
        };
        transferPackage.LeavesToSend.AddRange(cpfpRefundJobs);
        foreach (var (soId, cipher) in keyTweakPackage)
        {
            transferPackage.KeyTweakPackage.Add(soId, cipher);
        }

        // Send swap transfer to coordinator
        var swapRequest = new InitiateSwapPrimaryTransferRequest
        {
            Transfer = new StartTransferRequest
            {
                TransferId = transferId,
                OwnerIdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                ReceiverIdentityPublicKey = ByteString.CopyFrom(receiverPubKey),
                ExpiryTime = expiryTime,
                TransferPackage = transferPackage,
            },
            AdaptorPublicKeys = new AdaptorPublicKeyPackage
            {
                AdaptorPublicKey = ByteString.CopyFrom(adaptorPubKey),
            },
        };

        var swapResponse = await coordinatorClient.initiate_swap_primary_transferAsync(
            swapRequest, headers, cancellationToken: ct).ConfigureAwait(false);

        if (swapResponse.Transfer == null)
        {
            throw new SparkUntrustedResponseException(Operation, "No transfer in swap response.");
        }

        // Aggregate FROST signatures with adaptor pubkey for each leaf
        var adaptorSignatures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var signingResult in swapResponse.SigningResults)
        {
            var info = leafSigningInfos.FirstOrDefault(x => x.LeafId == signingResult.LeafId);
            var job = cpfpRefundJobs.FirstOrDefault(j => j.LeafId == signingResult.LeafId);
            if (info.LeafId == null || job == null)
            {
                continue;
            }

            adaptorSignatures[signingResult.LeafId] = FrostSigningHelper.AggregateFrostSignature(
                sighash: info.Sighash,
                selfCommitment: info.SelfCommitment,
                selfSignature: job.UserSignature.ToByteArray(),
                selfPublicKey: job.SigningPublicKey.ToByteArray(),
                verifyingKey: signingResult.VerifyingKey.ToByteArray(),
                signingResult: signingResult.RefundTxSigningResult,
                adaptorPublicKey: adaptorPubKey);
        }

        // Build user leaves for SSP request_swap mutation
        var userLeaves = new List<Dictionary<string, string>>();
        foreach (var signingResult in swapResponse.SigningResults)
        {
            if (!adaptorSignatures.TryGetValue(signingResult.LeafId, out var adaptorSig))
            {
                continue;
            }

            var adaptorSigHex = Convert.ToHexString(adaptorSig).ToLowerInvariant();
            var transferLeaf = swapResponse.Transfer.Leaves.FirstOrDefault(l => l.Leaf.Id == signingResult.LeafId);

            userLeaves.Add(new Dictionary<string, string>
            {
                ["leaf_id"] = signingResult.LeafId,
                ["raw_unsigned_refund_transaction"] = Convert.ToHexString(
                    transferLeaf?.IntermediateRefundTx.ToByteArray() ?? []).ToLowerInvariant(),
                ["direct_raw_unsigned_refund_transaction"] = Convert.ToHexString(
                    transferLeaf?.IntermediateDirectRefundTx.ToByteArray() ?? []).ToLowerInvariant(),
                ["direct_from_cpfp_raw_unsigned_refund_transaction"] = Convert.ToHexString(
                    transferLeaf?.IntermediateDirectFromCpfpRefundTx.ToByteArray() ?? []).ToLowerInvariant(),
                ["adaptor_added_signature"] = adaptorSigHex,
                ["direct_adaptor_added_signature"] = adaptorSigHex,
                ["direct_from_cpfp_adaptor_added_signature"] = adaptorSigHex,
            });
        }

        // Call SSP request_swap mutation
        var totalAmountSats = leaves.Sum(l => l.ValueSats);
        var variables = new Dictionary<string, object?>
        {
            ["adaptor_pubkey"] = Convert.ToHexString(adaptorPubKey).ToLowerInvariant(),
            ["total_amount_sats"] = totalAmountSats,
            ["target_amount_sats"] = targetAmounts,
            ["fee_sats"] = 0L,
            ["user_leaves"] = userLeaves,
            ["user_outbound_transfer_external_id"] = swapResponse.Transfer.Id,
        };

        var sspResponse = await wallet.SspClient.ExecuteAsync<RequestSwapResponse>(
            Mutations.RequestSwap, variables, ct).ConfigureAwait(false);

        var request = sspResponse.RequestSwap?.Request;
        if (request is null || request.Status == "FAILED")
        {
            throw new SparkTransferException(Operation, "Leaf swap request failed.");
        }

        var inboundSparkId = request.InboundTransfer?.SparkId
            ?? throw new SparkUntrustedResponseException(Operation, "No inbound transfer in swap response.");

        // The counter-transfer, by id, claimed under the wallet's claim lock: a concurrent claim
        // pass may already have claimed it.
        var inboundTransfer = await wallet.QueryTransferByIdAsync(inboundSparkId, ct).ConfigureAwait(false);
        await ClaimService.ClaimTransferAsync(wallet, inboundTransfer, ct).ConfigureAwait(false);

        // Return the new leaves
        return await wallet.GetLeavesAsync(ct).ConfigureAwait(false);
    }
}
