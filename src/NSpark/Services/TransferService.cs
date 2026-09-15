using Google.Protobuf;
using NSpark.Models;
using NSpark.Proto;
using NSpark.Signer;

namespace NSpark.Services;

/// <inheritdoc/>
public static class TransferService
{
    /// <summary>
    /// Send a Spark transfer to another wallet's identity public key.
    /// Uses the TransferPackage flow (start_transfer_v2) with FROST threshold signing.
    /// </summary>
    public static async Task<SparkTransfer> SendAsync(
        this SparkWallet wallet,
        byte[] receiverIdentityPublicKey,
        long amountSats,
        string? transferId = null,
        CancellationToken ct = default)
    {
        // Step 1: Select leaves to cover amount (exact match or swap)
        var selectedLeaves = await wallet.SelectLeavesWithSwapAsync(amountSats, ct).ConfigureAwait(false);

        var coordinatorAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var coordinatorClient = wallet.Pool.GetSparkClient(coordinatorAddress);
        var headers = await wallet.GetAuthMetadataAsync(coordinatorAddress, ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(wallet.Client.Options.Network);

        // Step 2: Get SO operator info (identifiers + public keys)
        var soListResponse = await coordinatorClient.get_signing_operator_listAsync(
            new Google.Protobuf.WellKnownTypes.Empty(),
            headers,
            cancellationToken: ct);
        var soOperators = soListResponse.SigningOperators;

        // Step 3: Get SO signing commitments for selected leaves
        // Count=3: cpfp, direct, directFromCpfp refund
        var leafIds = selectedLeaves.Select(l => l.Id).ToList();
        var commitmentsRequest = new GetSigningCommitmentsRequest { Count = 3 };
        commitmentsRequest.NodeIds.AddRange(leafIds);
        var commitmentsResponse = await coordinatorClient.get_signing_commitmentsAsync(
            commitmentsRequest, headers, cancellationToken: ct);

        // Commitments are interleaved: [leaf0_r0, leaf1_r0, ..., leaf0_r1, leaf1_r1, ...]
        var allCommitments = commitmentsResponse.SigningCommitments.ToList();

        // Step 4: Build encrypted per-SO tweak packages via the signer — no plaintext share
        // material ever crosses the wallet boundary.
        transferId ??= Guid.NewGuid().ToString();
        var threshold = wallet.Client.Options.EffectiveSigningThreshold;

        var soTargets = FrostSigningHelper.BuildSoTargets(soOperators, wallet.Client.Options.SigningOperators);
        var leafDescriptors = selectedLeaves
            .Select(l => new SendTweakLeafDescriptor(l.Id, receiverIdentityPublicKey))
            .ToList();
        var encryptedBatch = await wallet.Signer.BuildEncryptedSendTweaksAsync(
            leafDescriptors, soTargets, transferId, threshold, ct).ConfigureAwait(false);

        var keyTweakPackage = new Dictionary<string, ByteString>(encryptedBatch.EncryptedPackageBySoId.Count);
        foreach (var (soId, blob) in encryptedBatch.EncryptedPackageBySoId)
        {
            keyTweakPackage[soId] = ByteString.CopyFrom(blob);
        }

        // Step 5: Sign FROST refund txs (one round per leaf × three refund types)
        var cpfpRefundJobs = new List<UserSignedTxSigningJob>();
        var directRefundJobs = new List<UserSignedTxSigningJob>();
        var directFromCpfpRefundJobs = new List<UserSignedTxSigningJob>();

        for (int i = 0; i < selectedLeaves.Count; i++)
        {
            var leaf = selectedLeaves[i];
            var node = leaf.Node;
            var verifyingKey = node.VerifyingPublicKey.ToByteArray();

            // Compute decremented sequence from leaf's current refund tx
            var nodeTxBytes = node.NodeTx.ToByteArray();
            var directNodeTx = node.DirectTx.Length > 0 ? node.DirectTx.ToByteArray() : null;
            var refundTxBytes = node.RefundTx.Length > 0
                ? node.RefundTx.ToByteArray()
                : nodeTxBytes;
            var (normalSeq, normalDirectSeq) = TimelockHelper.ComputeNextSequences(
                refundTxBytes, "transfer.send", leaf.Id);

            // Commitments are interleaved: [leaf0_cpfp, leaf1_cpfp, ..., leaf0_direct, leaf1_direct, ..., leaf0_dfcpfp, leaf1_dfcpfp, ...]
            var cpfpCommitments = allCommitments[i].SigningNonceCommitments;
            var directCommitments = allCommitments[i + selectedLeaves.Count].SigningNonceCommitments;
            var directFromCpfpCommitments = allCommitments[i + 2 * selectedLeaves.Count].SigningNonceCommitments;

            // Construct refund tx trio (cpfp, direct, directFromCpfp) with decremented timelock
            // receivingPubkey = receiver's identity pubkey (server validates this)
            var refundTrio = SparkTxBuilder.BuildRefundTxTrio(
                cpfpNodeTx: nodeTxBytes,
                directNodeTx: directNodeTx,
                vout: 0,
                receivingPublicKey: receiverIdentityPublicKey,
                network: networkStr,
                sequence: normalSeq,
                directSequence: normalDirectSeq,
                feeSats: SparkConstants.DefaultRefundFeeSats);

            // FROST sign cpfp refund
            cpfpRefundJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                wallet.Signer, leaf.Id, verifyingKey,
                refundTrio.CpfpRefund.Tx, refundTrio.CpfpRefund.Sighash, cpfpCommitments, ct)
                .ConfigureAwait(false));

            // FROST sign direct refund (if direct tx exists)
            if (refundTrio.DirectRefund != null)
            {
                directRefundJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                    wallet.Signer, leaf.Id, verifyingKey,
                    refundTrio.DirectRefund.Tx, refundTrio.DirectRefund.Sighash, directCommitments, ct)
                    .ConfigureAwait(false));
            }

            // FROST sign direct-from-cpfp refund
            directFromCpfpRefundJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                wallet.Signer, leaf.Id, verifyingKey,
                refundTrio.DirectFromCpfpRefund.Tx, refundTrio.DirectFromCpfpRefund.Sighash,
                directFromCpfpCommitments, ct)
                .ConfigureAwait(false));
        }

        // Step 6: Sign the key tweak package (BIP-340 tagged hash with domain "spark/transfer/signing payload")
        var transferIdBytes = Convert.FromHexString(transferId.Replace("-", ""));
        var packageHash = SparkTaggedHash.Create("spark", "transfer", "signing payload")
            .AddBytes(transferIdBytes)
            .AddMapStringToBytes(keyTweakPackage)
            .Hash();
        var packageSignature = await wallet.Signer.SignWithIdentityKeyAsync(packageHash, ct).ConfigureAwait(false);

        // Step 7: Assemble TransferPackage and submit
        var transferPackage = new TransferPackage
        {
            UserSignature = ByteString.CopyFrom(packageSignature),
            HashVariant = HashVariant.V2,
        };
        foreach (var job in cpfpRefundJobs)
        {
            transferPackage.LeavesToSend.Add(job);
        }

        foreach (var job in directRefundJobs)
        {
            transferPackage.DirectLeavesToSend.Add(job);
        }

        foreach (var job in directFromCpfpRefundJobs)
        {
            transferPackage.DirectFromCpfpLeavesToSend.Add(job);
        }

        foreach (var (soId, cipher) in keyTweakPackage)
        {
            transferPackage.KeyTweakPackage.Add(soId, cipher);
        }

        var response = await coordinatorClient.start_transfer_v2Async(
            new StartTransferRequest
            {
                TransferId = transferId,
                OwnerIdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                ReceiverIdentityPublicKey = ByteString.CopyFrom(receiverIdentityPublicKey),
                ExpiryTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                    DateTimeOffset.UtcNow.AddMinutes(10)),
                TransferPackage = transferPackage,
            },
            headers,
            cancellationToken: ct);

        var transfer = response.Transfer;
        return new SparkTransfer(
            Id: transfer.Id,
            SenderIdentityPublicKey: Convert.ToHexString(transfer.SenderIdentityPublicKey.ToByteArray()),
            ReceiverIdentityPublicKey: Convert.ToHexString(transfer.ReceiverIdentityPublicKey.ToByteArray()),
            TotalValueSats: (long)transfer.TotalValue,
            Status: transfer.Status.ToString(),
            CreatedAt: transfer.CreatedTime.ToDateTimeOffset());
    }

    /// <summary>
    /// Get a single transfer by ID. Returns null if not found.
    /// </summary>
    public static async Task<SparkTransfer?> GetTransferAsync(
        this SparkWallet wallet,
        string transferId,
        CancellationToken ct = default)
    {
        var coordinatorAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var coordinatorClient = wallet.Pool.GetSparkClient(coordinatorAddress);
        var headers = await wallet.GetAuthMetadataAsync(coordinatorAddress, ct).ConfigureAwait(false);

        var protoNetwork = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Proto.Network.Mainnet : Proto.Network.Regtest;

        var filter = new Proto.TransferFilter
        {
            SenderOrReceiverIdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
            Network = protoNetwork,
        };
        filter.TransferIds.Add(transferId);

        var response = await coordinatorClient.query_all_transfersAsync(
            filter, headers, cancellationToken: ct);

        var transfer = response.Transfers.FirstOrDefault();
        return transfer == null ? null : MapTransfer(transfer);
    }

    /// <summary>
    /// Query transfers with pagination and optional time filters.
    /// </summary>
    public static async Task<TransferPage> GetTransfersAsync(
        this SparkWallet wallet,
        int limit = 100,
        long offset = 0,
        DateTimeOffset? createdAfter = null,
        DateTimeOffset? createdBefore = null,
        CancellationToken ct = default)
    {
        var coordinatorAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var coordinatorClient = wallet.Pool.GetSparkClient(coordinatorAddress);
        var headers = await wallet.GetAuthMetadataAsync(coordinatorAddress, ct).ConfigureAwait(false);

        var protoNetwork = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Proto.Network.Mainnet : Proto.Network.Regtest;

        var filter = new Proto.TransferFilter
        {
            SenderOrReceiverIdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
            Network = protoNetwork,
            Limit = limit,
            Offset = offset,
        };

        if (createdAfter.HasValue)
        {
            filter.CreatedAfter = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(createdAfter.Value);
        }
        else if (createdBefore.HasValue)
        {
            filter.CreatedBefore = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(createdBefore.Value);
        }

        var response = await coordinatorClient.query_all_transfersAsync(
            filter, headers, cancellationToken: ct);

        var transfers = response.Transfers.Select(MapTransfer).ToList();
        return new TransferPage(transfers, response.Offset);
    }

    private static SparkTransfer MapTransfer(Proto.Transfer t)
    {
        return new SparkTransfer(
            Id: t.Id,
            SenderIdentityPublicKey: Convert.ToHexString(t.SenderIdentityPublicKey.ToByteArray()).ToLowerInvariant(),
            ReceiverIdentityPublicKey: Convert.ToHexString(t.ReceiverIdentityPublicKey.ToByteArray()).ToLowerInvariant(),
            TotalValueSats: (long)t.TotalValue,
            Status: t.Status.ToString(),
            CreatedAt: t.CreatedTime.ToDateTimeOffset(),
            Type: t.Type.ToString());
    }

    internal static IReadOnlyList<SparkLeaf> SelectLeaves(IReadOnlyList<SparkLeaf> leaves, long amountSats)
    {
        var sorted = leaves
            .Where(l => l.Status == "AVAILABLE")
            .OrderByDescending(l => l.ValueSats)
            .ToList();
        var selected = new List<SparkLeaf>();
        long total = 0;
        foreach (var leaf in sorted)
        {
            selected.Add(leaf);
            total += leaf.ValueSats;
            if (total >= amountSats)
            {
                return selected;
            }
        }
        throw new InvalidOperationException(
            $"Insufficient balance: need {amountSats} sats, have {total} sats.");
    }
}
