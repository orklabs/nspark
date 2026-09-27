using Google.Protobuf;
using Grpc.Core;
using NSpark.Models;
using NSpark.Proto;

namespace NSpark.Services;

/// <inheritdoc/>
public static class ClaimService
{
    private const string Operation = "transfer.claim";

    /// <summary>
    /// Claim every pending inbound transfer (Spark transfers, Lightning receives, deposits the SSP
    /// credited) and report what could not be claimed. Without claiming, incoming funds stay
    /// pending and never appear in the wallet balance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Claims run one at a time, wallet-wide: a swap's claim of its counter-transfer or a
    /// concurrent pass waits for this one. A transfer that cannot be claimed — for example one
    /// whose sender signature does not verify — is recorded in
    /// <see cref="PendingTransferClaim.Failures"/> and the pass moves on to the rest, as the
    /// reference SDK does; it is tried again on the next pass. A transfer the operators already
    /// recorded as claimed by this wallet counts as claimed.
    /// </para>
    /// <para>
    /// Claimed leaves whose refund timelock is in the renewal range (100…199 — a transfer from a
    /// leaf at 200 arrives at 100) are renewed right away, best effort, as the reference SDK does
    /// when it registers claimed leaves; spend paths renew anything that is left.
    /// </para>
    /// </remarks>
    public static async Task<PendingTransferClaim> ClaimPendingTransfersAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var result = await wallet.ClaimPendingTransfersCoreAsync(ct).ConfigureAwait(false);
        return new PendingTransferClaim(
            result.Claimed.Select(TransferMapping.ToModel).ToList(),
            result.Failures);
    }

    /// <summary>One claim pass, returning the claimed transfers as the operators reported them.</summary>
    internal static async Task<PendingTransferDrain.Result> ClaimPendingTransfersCoreAsync(
        this SparkWallet wallet,
        CancellationToken ct)
    {
        PendingTransferDrain.Result result;
        await wallet.ClaimLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            result = await PendingTransferDrain.RunAsync(
                (limit, offset, innerCt) => wallet.QueryPendingTransfersAsync(limit, offset, innerCt),
                (transfer, innerCt) => ClaimTreatingDuplicatesAsClaimedAsync(wallet, transfer, innerCt),
                ct).ConfigureAwait(false);
        }
        finally
        {
            wallet.ClaimLock.Release();
        }

        await wallet.RenewClaimedLeavesAsync(result.ClaimedLeafIds, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Pending transfers where this wallet is the receiver, one page. <paramref name="limit"/> 0
    /// asks for the server's largest page (100).
    /// </summary>
    internal static async Task<IReadOnlyList<Transfer>> QueryPendingTransfersAsync(
        this SparkWallet wallet,
        int limit,
        int offset,
        CancellationToken ct)
    {
        var client = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        var filter = new TransferFilter
        {
            ReceiverIdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
            Network = wallet.Options.ProtoNetwork(),
            Limit = limit,
            Offset = offset,
        };
        var response = await client.query_pending_transfersAsync(filter, headers, cancellationToken: ct).ConfigureAwait(false);
        return response.Transfers;
    }

    /// <summary>
    /// Claim one transfer under the wallet-wide claim lock (used by swaps for their
    /// counter-transfer, which a concurrent claim pass may already have claimed).
    /// </summary>
    internal static async Task ClaimTransferAsync(SparkWallet wallet, Transfer transfer, CancellationToken ct)
    {
        await wallet.ClaimLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ClaimTreatingDuplicatesAsClaimedAsync(wallet, transfer, ct).ConfigureAwait(false);
        }
        finally
        {
            wallet.ClaimLock.Release();
        }
    }

    /// <summary>
    /// Best-effort renewal of the renewable leaves among <paramref name="leafIds"/>.
    /// </summary>
    internal static async Task RenewClaimedLeavesAsync(
        this SparkWallet wallet,
        IReadOnlyCollection<string> leafIds,
        CancellationToken ct)
    {
        if (leafIds.Count == 0)
        {
            return;
        }

        try
        {
            var ids = leafIds.ToHashSet(StringComparer.Ordinal);
            var leaves = (await wallet.GetLeavesAsync(ct).ConfigureAwait(false)).Where(l => ids.Contains(l.Id)).ToList();
            if (RenewalService.RenewalCandidates(leaves).Renewable.Count > 0)
            {
                _ = await wallet.RenewLeavesAsync(leaves, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort: spend paths renew anything left.
        }
    }

    /// <summary>
    /// The operators answer ALREADY_EXISTS once this receiver has claimed the transfer; like the
    /// reference SDK, confirm this wallet's leg is complete and treat it as claimed.
    /// </summary>
    private static async Task ClaimTreatingDuplicatesAsClaimedAsync(SparkWallet wallet, Transfer transfer, CancellationToken ct)
    {
        try
        {
            await ClaimTransferNowAsync(wallet, transfer, ct).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            var current = await wallet.QueryTransferByIdAsync(transfer.Id, ct).ConfigureAwait(false);
            if (!TransferLeafVerifier.IsReceiverLegComplete(current, wallet.IdentityPublicKey))
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Claim a single pending transfer with <c>claim_transfer</c> and a claim package. A
    /// multi-receiver transfer is narrowed to this wallet's own leaves, and the sender's signature
    /// on every leaf is verified first; a transfer that fails verification is refused before any
    /// secret is decrypted or any refund is signed. Callers hold the claim lock.
    /// </summary>
    private static async Task ClaimTransferNowAsync(SparkWallet wallet, Transfer pending, CancellationToken ct)
    {
        var transfer = TransferLeafVerifier.Scoped(pending, wallet.IdentityPublicKey);
        TransferLeafVerifier.Verify(transfer, wallet.IdentityPublicKey);

        var options = wallet.Options;
        var coordinatorClient = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(options.Network);
        var transferLeaves = transfer.Leaves;

        var soListResponse = await coordinatorClient.get_signing_operator_listAsync(
            new Google.Protobuf.WellKnownTypes.Empty(), headers, cancellationToken: ct).ConfigureAwait(false);
        var soTargets = FrostSigningHelper.BuildSoTargets(soListResponse.SigningOperators, options.SigningOperators);
        var threshold = options.EffectiveSigningThreshold;
        FrostSigningHelper.ValidateThreshold(threshold, soTargets.Count);

        // Signing commitments (Count=3: cpfp, direct, directFromCpfp), leaf-major.
        var commitmentsResponse = await coordinatorClient.get_signing_commitmentsAsync(
            new GetSigningCommitmentsRequest { Count = 3, NodeIdCount = (uint)transferLeaves.Count },
            headers,
            cancellationToken: ct).ConfigureAwait(false);
        var allCommitments = commitmentsResponse.SigningCommitments;
        if (allCommitments.Count < 3 * transferLeaves.Count)
        {
            throw new Exceptions.SparkUntrustedResponseException(
                Operation, $"Got {allCommitments.Count} signing commitments, need {3 * transferLeaves.Count}.");
        }

        // Encrypted per-SO claim-tweak packages from the signer, which ECIES-decrypts each leaf's
        // secret cipher, derives the receiver's new per-leaf key, VSS-splits the tweak and
        // encrypts each SO's package — no plaintext share material crosses the wallet boundary.
        var claimDescriptors = transferLeaves
            .Select(tl => new Signer.ClaimTweakLeafDescriptor(tl.Leaf.Id, tl.SecretCipher.ToByteArray()))
            .ToList();
        var encryptedClaim = await wallet.Signer.BuildEncryptedClaimTweaksAsync(
            claimDescriptors, soTargets, threshold, ct).ConfigureAwait(false);

        // FROST-sign each leaf's refunds to the new per-leaf public key the signer returned.
        var cpfpRefundJobs = new List<UserSignedTxSigningJob>();
        var directRefundJobs = new List<UserSignedTxSigningJob>();
        var directFromCpfpRefundJobs = new List<UserSignedTxSigningJob>();

        for (var i = 0; i < transferLeaves.Count; i++)
        {
            var transferLeaf = transferLeaves[i];
            var node = transferLeaf.Leaf;
            var verifyingKey = node.VerifyingPublicKey.ToByteArray();
            var newSigningPubKey = encryptedClaim.NewPublicKeyByLeafId[node.Id];

            var (claimSeq, claimDirectSeq) = ClaimSequences(transferLeaf);
            var refundTrio = TimelockHelper.LeafRefundTrio(node, newSigningPubKey, networkStr, claimSeq, claimDirectSeq);

            // Commitments are interleaved: [leaf0_r0, leaf1_r0, ..., leaf0_r1, leaf1_r1, ...]
            var cpfpCommitments = allCommitments[i].SigningNonceCommitments;
            var directCommitments = allCommitments[i + transferLeaves.Count].SigningNonceCommitments;
            var directFromCpfpCommitments = allCommitments[i + (2 * transferLeaves.Count)].SigningNonceCommitments;

            cpfpRefundJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                wallet.Signer, node.Id, verifyingKey,
                refundTrio.CpfpRefund.Tx, refundTrio.CpfpRefund.Sighash, cpfpCommitments, ct)
                .ConfigureAwait(false));

            if (refundTrio.DirectRefund != null)
            {
                directRefundJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                    wallet.Signer, node.Id, verifyingKey,
                    refundTrio.DirectRefund.Tx, refundTrio.DirectRefund.Sighash, directCommitments, ct)
                    .ConfigureAwait(false));
            }

            directFromCpfpRefundJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                wallet.Signer, node.Id, verifyingKey,
                refundTrio.DirectFromCpfpRefund.Tx, refundTrio.DirectFromCpfpRefund.Sighash,
                directFromCpfpCommitments, ct)
                .ConfigureAwait(false));
        }

        var keyTweakPackage = new Dictionary<string, ByteString>(encryptedClaim.EncryptedPackageBySoId.Count);
        foreach (var (soId, blob) in encryptedClaim.EncryptedPackageBySoId)
        {
            keyTweakPackage[soId] = ByteString.CopyFrom(blob);
        }

        // Sign the key tweak package (BIP-340 tagged hash).
        var packageHash = SparkTaggedHash.Create("spark", "claim", "signing payload")
            .AddBytes(TransferIdBytes(transfer.Id))
            .AddMapStringToBytes(keyTweakPackage)
            .Hash();
        var packageSignature = await wallet.Signer.SignWithIdentityKeyAsync(packageHash, ct).ConfigureAwait(false);

        var claimPackage = new ClaimPackage
        {
            UserSignature = ByteString.CopyFrom(packageSignature),
            HashVariant = HashVariant.V2,
        };
        claimPackage.LeavesToClaim.AddRange(cpfpRefundJobs);
        claimPackage.DirectLeavesToClaim.AddRange(directRefundJobs);
        claimPackage.DirectFromCpfpLeavesToClaim.AddRange(directFromCpfpRefundJobs);
        foreach (var (soId, cipher) in keyTweakPackage)
        {
            claimPackage.KeyTweakPackage.Add(soId, cipher);
        }

        await coordinatorClient.claim_transferAsync(
            new ClaimTransferRequest
            {
                TransferId = transfer.Id,
                OwnerIdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                ClaimPackage = claimPackage,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A claim's refund sequences: the timelock of the sender's intermediate refund (else the
    /// leaf's refund, else its node transaction) rounded down to the 100-block interval, and the
    /// direct refunds 50 above — the reference SDK's claim with <c>enforceTimelocks</c>. Bit 30 is
    /// kept.
    /// </summary>
    internal static (uint Cpfp, uint Direct) ClaimSequences(TransferLeaf transferLeaf)
    {
        var node = transferLeaf.Leaf;
        var source = !transferLeaf.IntermediateRefundTx.IsEmpty
            ? transferLeaf.IntermediateRefundTx
            : !node.RefundTx.IsEmpty ? node.RefundTx : node.NodeTx;
        var rawSequence = TimelockHelper.ParseSequence(source.ToByteArray());
        var timelock = TimelockHelper.RoundedTimelock(rawSequence & 0xFFFF);
        var bit30 = rawSequence & (1u << 30);
        return (bit30 | timelock, bit30 | ((timelock + TimelockHelper.DirectTimelockOffset) & 0xFFFF));
    }

    /// <summary>
    /// The raw nSequence of the first input of a raw Bitcoin transaction, bit 30 (the relative
    /// timelock type) included. Bounds-checked: malformed bytes throw
    /// <see cref="Exceptions.SparkUntrustedResponseException"/>.
    /// </summary>
    internal static uint ParseInputSequence(byte[] txBytes) => TimelockHelper.ParseSequence(txBytes);

    /// <summary>The 16 bytes of a transfer id (a UUID), as the signing payloads hash it.</summary>
    internal static byte[] TransferIdBytes(string transferId)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(transferId.Replace("-", string.Empty, StringComparison.Ordinal));
        }
        catch (FormatException)
        {
            bytes = [];
        }

        if (bytes.Length != 16)
        {
            throw new Exceptions.SparkConfigurationException(Operation, $"Transfer id '{transferId}' is not a UUID.");
        }

        return bytes;
    }
}

/// <summary>
/// One claim pass over the pending inbound transfers, following the reference SDK's
/// <c>claimTransfers</c>: pages of 25, only transfers in a claimable status, a failure is
/// recorded and the pass moves on, and after any progress it restarts from the head (claimed
/// transfers leave the pending set, shifting later ones forward); otherwise it advances past the
/// page. The pass is bounded to 100 pages, as the reference SDK's fallback is. A transfer that
/// failed is not tried again within the same pass.
/// </summary>
internal static class PendingTransferDrain
{
    internal const int BatchSize = 25;
    internal const int MaxBatches = 100;

    /// <summary>Statuses the reference SDK claims; anything else is left for a later pass.</summary>
    internal static readonly IReadOnlySet<TransferStatus> ClaimableStatuses = new HashSet<TransferStatus>
    {
        TransferStatus.SenderKeyTweaked,
        TransferStatus.ReceiverKeyTweaked,
        TransferStatus.ReceiverRefundSigned,
        TransferStatus.ReceiverKeyTweakApplied,
        TransferStatus.ReceiverKeyTweakLocked,
    };

    /// <summary>The outcome of one pass.</summary>
    internal sealed record Result(IReadOnlyList<Transfer> Claimed, IReadOnlyList<PendingTransferClaimFailure> Failures)
    {
        /// <summary>Leaves of the claimed transfers.</summary>
        public IReadOnlyList<string> ClaimedLeafIds =>
            Claimed.SelectMany(t => t.Leaves.Where(l => l.Leaf is not null).Select(l => l.Leaf.Id)).ToList();
    }

    internal static async Task<Result> RunAsync(
        Func<int, int, CancellationToken, Task<IReadOnlyList<Transfer>>> fetch,
        Func<Transfer, CancellationToken, Task> claim,
        CancellationToken ct)
    {
        var claimed = new List<Transfer>();
        var failures = new List<PendingTransferClaimFailure>();
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        for (var batchNumber = 0; batchNumber < MaxBatches; batchNumber++)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await fetch(BatchSize, offset, ct).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                break;
            }

            var progress = false;
            foreach (var transfer in batch)
            {
                if (!ClaimableStatuses.Contains(transfer.Status) || !attempted.Add(transfer.Id))
                {
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                try
                {
                    await claim(transfer, ct).ConfigureAwait(false);
                    claimed.Add(transfer);
                    progress = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add(new PendingTransferClaimFailure(transfer.Id, ex));
                }
            }

            if (batch.Count < BatchSize)
            {
                break;
            }

            offset = progress ? 0 : offset + batch.Count;
        }

        return new Result(claimed, failures);
    }
}
