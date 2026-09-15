using Google.Protobuf;
using NSpark.Models;
using NSpark.Proto;

namespace NSpark.Services;

/// <inheritdoc/>
public static class ClaimService
{
    private const uint TimeLockInterval = 100;
    private const uint DirectTimelockOffset = 50;

    /// <summary>
    /// Claim all pending incoming transfers (Spark transfers and Lightning receives).
    /// Without calling this, incoming funds remain in "pending" state and never appear in the wallet balance.
    /// Transfers that fail to claim are skipped; successfully claimed transfers are returned.
    /// </summary>
    public static async Task<IReadOnlyList<SparkTransfer>> ClaimPendingTransfersAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        var coordinatorAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var coordinatorClient = wallet.Pool.GetSparkClient(coordinatorAddress);
        var headers = await wallet.GetAuthMetadataAsync(coordinatorAddress, ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(wallet.Client.Options.Network);

        // Step 1: Query pending transfers where we are the receiver
        var protoNetwork = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Network.Mainnet : Network.Regtest;
        var filter = new TransferFilter
        {
            ReceiverIdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
            Network = protoNetwork,
        };
        var pendingResponse = await coordinatorClient.query_pending_transfersAsync(
            filter, headers, cancellationToken: ct);

        var claimed = new List<SparkTransfer>();
        if (pendingResponse.Transfers.Count == 0)
        {
            return claimed;
        }

        // Step 2: Get SO operator info
        var soListResponse = await coordinatorClient.get_signing_operator_listAsync(
            new Google.Protobuf.WellKnownTypes.Empty(), headers, cancellationToken: ct);
        var soOperators = soListResponse.SigningOperators;

        // Process each pending transfer
        foreach (var transfer in pendingResponse.Transfers)
        {
            var transferLeaves = transfer.Leaves.ToList();
            if (transferLeaves.Count == 0)
            {
                continue;
            }

            try
            {
                var result = await ClaimSingleTransferAsync(
                    wallet, coordinatorClient, headers, networkStr,
                    soOperators, transfer, transferLeaves, ct).ConfigureAwait(false);
                claimed.Add(result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Skip transfers that fail (a transfer that fails sender-signature verification,
                // or corrupted state from previous attempts); the rest are still claimed.
            }
        }

        return claimed;
    }

    internal static async Task<SparkTransfer> ClaimSingleTransferAsync(
        SparkWallet wallet,
        SparkService.SparkServiceClient coordinatorClient,
        Grpc.Core.Metadata headers,
        string networkStr,
        Google.Protobuf.Collections.MapField<string, SigningOperatorInfo> soOperators,
        Transfer transfer,
        List<TransferLeaf> transferLeaves,
        CancellationToken ct)
    {
        // The sender's signature on every leaf is verified first; a transfer that fails
        // verification is refused before any secret is decrypted or any refund is signed.
        TransferLeafVerifier.Verify(transfer, wallet.IdentityPublicKey);

        // Step 3: Get signing commitments (Count=3: cpfp, direct, directFromCpfp)
        var commitmentsRequest = new GetSigningCommitmentsRequest
        {
            Count = 3,
            NodeIdCount = (uint)transferLeaves.Count,
        };
        var commitmentsResponse = await coordinatorClient.get_signing_commitmentsAsync(
            commitmentsRequest, headers, cancellationToken: ct);
        var allCommitments = commitmentsResponse.SigningCommitments.ToList();

        // Step 4: Build encrypted per-SO claim-tweak packages via the signer in one call.
        // The signer ECIES-decrypts each leaf's senderSecretCipher, derives the receiver's
        // new per-leaf key, VSS-splits the tweak, and ECIES-encrypts each SO's package —
        // no plaintext share material crosses the wallet boundary.
        var threshold = wallet.Client.Options.EffectiveSigningThreshold;
        var soTargets = FrostSigningHelper.BuildSoTargets(soOperators, wallet.Client.Options.SigningOperators);
        var claimDescriptors = transferLeaves
            .Select(tl => new NSpark.Signer.ClaimTweakLeafDescriptor(
                tl.Leaf.Id,
                tl.SecretCipher.ToByteArray()))
            .ToList();
        var encryptedClaim = await wallet.Signer.BuildEncryptedClaimTweaksAsync(
            claimDescriptors, soTargets, threshold, ct).ConfigureAwait(false);

        // Step 5: FROST sign refund trios for each leaf using the new per-leaf public key
        // returned by the signer.
        var cpfpRefundJobs = new List<UserSignedTxSigningJob>();
        var directRefundJobs = new List<UserSignedTxSigningJob>();
        var directFromCpfpRefundJobs = new List<UserSignedTxSigningJob>();

        for (int i = 0; i < transferLeaves.Count; i++)
        {
            var transferLeaf = transferLeaves[i];
            var node = transferLeaf.Leaf;
            var verifyingKey = node.VerifyingPublicKey.ToByteArray();
            var newSigningPubKey = encryptedClaim.NewPublicKeyByLeafId[node.Id];

            // Extract the refund sequence from the sender's intermediate refund tx
            var intermediateRefundBytes = transferLeaf.IntermediateRefundTx.ToByteArray();
            var nodeRefundBytes = node.RefundTx.ToByteArray();
            uint currentSequence;
            if (intermediateRefundBytes.Length > 0)
            {
                currentSequence = ParseInputSequence(intermediateRefundBytes);
            }
            else if (nodeRefundBytes.Length > 0)
            {
                currentSequence = ParseInputSequence(nodeRefundBytes);
            }
            else
            {
                currentSequence = ParseInputSequence(node.NodeTx.ToByteArray());
            }

            // Round DOWN to nearest TimeLockInterval for claim sequence
            var currentTimelock = currentSequence & 0xFFFF;
            var bit30 = currentSequence & (1u << 30);
            var remainder = currentTimelock % TimeLockInterval;
            if (remainder != 0)
            {
                currentTimelock -= remainder;
            }

            var claimSeq = bit30 | currentTimelock;
            var claimDirectSeq = bit30 | (currentTimelock + DirectTimelockOffset);

            // Construct refund tx trio (cpfp, direct, directFromCpfp)
            var cpfpNodeTx = node.NodeTx.ToByteArray();
            var directNodeTx = node.DirectTx.Length > 0 ? node.DirectTx.ToByteArray() : null;

            var refundTrio = SparkTxBuilder.BuildRefundTxTrio(
                cpfpNodeTx: cpfpNodeTx,
                directNodeTx: directNodeTx,
                vout: 0,
                receivingPublicKey: newSigningPubKey,
                network: networkStr,
                sequence: claimSeq,
                directSequence: claimDirectSeq,
                feeSats: SparkConstants.DefaultRefundFeeSats);

            // Commitments are interleaved: [leaf0_r0, leaf1_r0, ..., leaf0_r1, leaf1_r1, ...]
            var cpfpCommitments = allCommitments[i].SigningNonceCommitments;
            var directCommitments = allCommitments[i + transferLeaves.Count].SigningNonceCommitments;
            var directFromCpfpCommitments = allCommitments[i + 2 * transferLeaves.Count].SigningNonceCommitments;

            // FROST sign cpfp refund
            cpfpRefundJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                wallet.Signer, node.Id, verifyingKey,
                refundTrio.CpfpRefund.Tx, refundTrio.CpfpRefund.Sighash, cpfpCommitments, ct)
                .ConfigureAwait(false));

            // FROST sign direct refund (if direct tx exists)
            if (refundTrio.DirectRefund != null)
            {
                directRefundJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                    wallet.Signer, node.Id, verifyingKey,
                    refundTrio.DirectRefund.Tx, refundTrio.DirectRefund.Sighash, directCommitments, ct)
                    .ConfigureAwait(false));
            }

            // FROST sign direct-from-cpfp refund
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

        // Step 6: Sign the key tweak package (BIP-340 tagged hash)
        var transferIdBytes = Convert.FromHexString(transfer.Id.Replace("-", ""));
        var packageHash = SparkTaggedHash.Create("spark", "claim", "signing payload")
            .AddBytes(transferIdBytes)
            .AddMapStringToBytes(keyTweakPackage)
            .Hash();
        var packageSignature = await wallet.Signer.SignWithIdentityKeyAsync(packageHash, ct).ConfigureAwait(false);

        // Step 7: Build ClaimPackage
        var claimPackage = new ClaimPackage
        {
            UserSignature = ByteString.CopyFrom(packageSignature),
            HashVariant = HashVariant.V2,
        };
        foreach (var job in cpfpRefundJobs)
        {
            claimPackage.LeavesToClaim.Add(job);
        }

        foreach (var job in directRefundJobs)
        {
            claimPackage.DirectLeavesToClaim.Add(job);
        }

        foreach (var job in directFromCpfpRefundJobs)
        {
            claimPackage.DirectFromCpfpLeavesToClaim.Add(job);
        }

        foreach (var (soId, cipher) in keyTweakPackage)
        {
            claimPackage.KeyTweakPackage.Add(soId, cipher);
        }

        // Step 8: Call claim_transfer
        var claimResponse = await coordinatorClient.claim_transferAsync(
            new ClaimTransferRequest
            {
                TransferId = transfer.Id,
                OwnerIdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                ClaimPackage = claimPackage,
            },
            headers,
            cancellationToken: ct);

        var claimedTransfer = claimResponse.Transfer;
        return new SparkTransfer(
            Id: claimedTransfer.Id,
            SenderIdentityPublicKey: Convert.ToHexString(claimedTransfer.SenderIdentityPublicKey.ToByteArray()),
            ReceiverIdentityPublicKey: Convert.ToHexString(claimedTransfer.ReceiverIdentityPublicKey.ToByteArray()),
            TotalValueSats: (long)claimedTransfer.TotalValue,
            Status: claimedTransfer.Status.ToString(),
            CreatedAt: claimedTransfer.CreatedTime.ToDateTimeOffset());
    }

    /// <summary>
    /// Extract the nSequence from the leaf's refund transaction to determine the current timelock.
    /// Falls back to the node_tx input sequence if refund_tx is empty.
    /// </summary>
    internal static uint ExtractRefundSequence(TreeNode node)
    {
        var txBytes = node.RefundTx.Length > 0
            ? node.RefundTx.ToByteArray()
            : node.NodeTx.ToByteArray();
        return ParseInputSequence(txBytes);
    }

    /// <summary>
    /// Parse the raw nSequence from the first input in raw Bitcoin transaction bytes.
    /// Returns the full 32-bit sequence value (including bit 30 for relative timelock type).
    /// Callers extract lower 16 bits for timelock value and preserve upper bits as needed.
    /// </summary>
    internal static uint ParseInputSequence(byte[] txBytes)
    {
        int offset = 4; // skip version

        // Check for segwit marker (0x00 followed by 0x01)
        if (txBytes[offset] == 0x00 && txBytes[offset + 1] == 0x01)
        {
            offset += 2; // skip marker + flag
        }

        // Read input count (varint) — we only need the first input
        offset += ReadVarIntSize(txBytes, offset);

        // Skip prev_hash (32 bytes) + prev_index (4 bytes)
        offset += 32 + 4;

        // Read script length (varint) and skip script
        var (scriptLen, varIntBytes) = ReadVarInt(txBytes, offset);
        offset += varIntBytes + (int)scriptLen;

        // Read raw nSequence (4 bytes LE) — preserve all bits including bit 30
        return BitConverter.ToUInt32(txBytes, offset);
    }

    private static int ReadVarIntSize(byte[] data, int offset)
    {
        return data[offset] switch
        {
            < 0xFD => 1,
            0xFD => 3,
            0xFE => 5,
            _ => 9,
        };
    }

    private static (long value, int bytesRead) ReadVarInt(byte[] data, int offset)
    {
        var first = data[offset];
        return first switch
        {
            < 0xFD => (first, 1),
            0xFD => (BitConverter.ToUInt16(data, offset + 1), 3),
            0xFE => (BitConverter.ToUInt32(data, offset + 1), 5),
            _ => ((long)BitConverter.ToUInt64(data, offset + 1), 9),
        };
    }

}
