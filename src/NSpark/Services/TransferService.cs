using Google.Protobuf;
using NSpark.Exceptions;
using NSpark.Models;
using NSpark.Proto;
using NSpark.Signer;

namespace NSpark.Services;

/// <summary>Which side of a transfer the wallet is on.</summary>
public enum TransferDirection
{
    /// <summary>Transfers the wallet sent or received.</summary>
    Both,

    /// <summary>Transfers the wallet sent.</summary>
    Sent,

    /// <summary>Transfers the wallet received.</summary>
    Received,
}

/// <inheritdoc/>
public static class TransferService
{
    private const string Operation = "transfer.send";

    /// <summary>How long a transfer stays claimable before the sender may take it back: 16 days.</summary>
    internal static readonly TimeSpan TransferExpiry = TimeSpan.FromDays(16);

    /// <summary>
    /// The transfer types <see cref="GetTransfersAsync"/> lists: the reference SDK's
    /// <c>getTransfers</c> types — Spark transfers, Lightning payments (preimage swaps),
    /// cooperative exits and static deposit claims (UTXO swaps).
    /// </summary>
    internal static readonly TransferType[] ListedTransferTypes =
    [
        TransferType.CooperativeExit,
        TransferType.PreimageSwap,
        TransferType.UtxoSwap,
        TransferType.Transfer,
    ];

    /// <summary>
    /// Send sats to another Spark wallet identified by its bech32m Spark address
    /// (<c>spark1...</c> on mainnet, <c>sparkrt1...</c> on regtest). The address must be for the
    /// wallet's network.
    /// </summary>
    /// <exception cref="SparkConfigurationException">
    /// The address is malformed, for another network, or a Spark invoice: sending to an invoice
    /// as if it were an address would ignore its amount, expiry and sender, and the payee would
    /// not see it paid.
    /// </exception>
#pragma warning disable RS0026 // Optional parameters on parallel overloads — alpha API.
    public static Task<SparkTransfer> SendAsync(
        this SparkWallet wallet,
        string receiverSparkAddress,
        long amountSats,
        CancellationToken ct = default)
#pragma warning restore RS0026
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var receiver = SparkAddress.Decode(receiverSparkAddress, wallet.Options.Network);
        return wallet.SendAsync(receiver, amountSats, transferId: null, ct);
    }

    /// <summary>
    /// Send a Spark transfer to another wallet's identity public key.
    /// Uses the TransferPackage flow (start_transfer_v2) with FROST threshold signing.
    /// </summary>
#pragma warning disable RS0026
    public static async Task<SparkTransfer> SendAsync(
        this SparkWallet wallet,
        byte[] receiverIdentityPublicKey,
        long amountSats,
        string? transferId = null,
        CancellationToken ct = default)
#pragma warning restore RS0026
    {
        ArgumentNullException.ThrowIfNull(wallet);
        ValidateSendArguments(receiverIdentityPublicKey, amountSats);

        // Select leaves to cover amount (exact match or swap)
        var selectedLeaves = await wallet.SelectLeavesWithSwapAsync(amountSats, ct).ConfigureAwait(false);
        return await wallet.TransferLeavesAsync(selectedLeaves, receiverIdentityPublicKey, transferId, ct).ConfigureAwait(false);
    }

    /// <summary>Validate the arguments of a Spark transfer before any leaf is selected or swapped.</summary>
    internal static void ValidateSendArguments(byte[]? receiverIdentityPublicKey, long amountSats)
    {
        if (amountSats <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amountSats), amountSats, "amountSats must be positive.");
        }

        if (receiverIdentityPublicKey is not { Length: 33 } || (receiverIdentityPublicKey[0] != 0x02 && receiverIdentityPublicKey[0] != 0x03))
        {
            throw new ArgumentException(
                "receiverIdentityPublicKey must be a 33-byte compressed secp256k1 public key.",
                nameof(receiverIdentityPublicKey));
        }
    }

    /// <summary>Transfer exactly <paramref name="selectedLeaves"/> to the receiver in one Spark transfer.</summary>
    internal static async Task<SparkTransfer> TransferLeavesAsync(
        this SparkWallet wallet,
        IReadOnlyList<SparkLeaf> selectedLeaves,
        byte[] receiverIdentityPublicKey,
        string? transferId,
        CancellationToken ct)
    {
        var options = wallet.Options;
        var coordinatorClient = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(options.Network);

        var soListResponse = await coordinatorClient.get_signing_operator_listAsync(
            new Google.Protobuf.WellKnownTypes.Empty(), headers, cancellationToken: ct).ConfigureAwait(false);
        var soTargets = FrostSigningHelper.BuildSoTargets(soListResponse.SigningOperators, options.SigningOperators);
        var threshold = options.EffectiveSigningThreshold;
        FrostSigningHelper.ValidateThreshold(threshold, soTargets.Count);

        // SO signing commitments, three per leaf (cpfp, direct, directFromCpfp), leaf-major.
        var commitmentsRequest = new GetSigningCommitmentsRequest { Count = 3 };
        commitmentsRequest.NodeIds.AddRange(selectedLeaves.Select(l => l.Id));
        var commitmentsResponse = await coordinatorClient.get_signing_commitmentsAsync(
            commitmentsRequest, headers, cancellationToken: ct).ConfigureAwait(false);
        var allCommitments = commitmentsResponse.SigningCommitments;
        if (allCommitments.Count < 3 * selectedLeaves.Count)
        {
            throw new SparkUntrustedResponseException(
                Operation, $"Got {allCommitments.Count} signing commitments, need {3 * selectedLeaves.Count}.");
        }

        // Encrypted per-SO tweak packages from the signer — no plaintext share material crosses
        // the wallet boundary.
        transferId ??= Guid.NewGuid().ToString("D");
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

        // FROST-sign each leaf's refunds to the receiver at the next timelock.
        var cpfpRefundJobs = new List<UserSignedTxSigningJob>();
        var directRefundJobs = new List<UserSignedTxSigningJob>();
        var directFromCpfpRefundJobs = new List<UserSignedTxSigningJob>();

        for (var i = 0; i < selectedLeaves.Count; i++)
        {
            var leaf = selectedLeaves[i];
            var node = leaf.Node;
            var verifyingKey = node.VerifyingPublicKey.ToByteArray();

            var (cpfpSequence, directSequence) = TimelockHelper.ComputeNextSequences(
                node.RefundTx.ToByteArray(), Operation, leaf.Id);
            var refundTrio = TimelockHelper.LeafRefundTrio(
                node, receiverIdentityPublicKey, networkStr, cpfpSequence, directSequence);

            var cpfpCommitments = allCommitments[i].SigningNonceCommitments;
            var directCommitments = allCommitments[i + selectedLeaves.Count].SigningNonceCommitments;
            var directFromCpfpCommitments = allCommitments[i + (2 * selectedLeaves.Count)].SigningNonceCommitments;

            cpfpRefundJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                wallet.Signer, leaf.Id, verifyingKey,
                refundTrio.CpfpRefund.Tx, refundTrio.CpfpRefund.Sighash, cpfpCommitments, ct)
                .ConfigureAwait(false));

            if (refundTrio.DirectRefund != null)
            {
                directRefundJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                    wallet.Signer, leaf.Id, verifyingKey,
                    refundTrio.DirectRefund.Tx, refundTrio.DirectRefund.Sighash, directCommitments, ct)
                    .ConfigureAwait(false));
            }

            directFromCpfpRefundJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                wallet.Signer, leaf.Id, verifyingKey,
                refundTrio.DirectFromCpfpRefund.Tx, refundTrio.DirectFromCpfpRefund.Sighash,
                directFromCpfpCommitments, ct)
                .ConfigureAwait(false));
        }

        // Sign the key tweak package (BIP-340 tagged hash with domain "spark/transfer/signing payload").
        var packageHash = SparkTaggedHash.Create("spark", "transfer", "signing payload")
            .AddBytes(ClaimService.TransferIdBytes(transferId))
            .AddMapStringToBytes(keyTweakPackage)
            .Hash();
        var packageSignature = await wallet.Signer.SignWithIdentityKeyAsync(packageHash, ct).ConfigureAwait(false);

        var transferPackage = new TransferPackage
        {
            UserSignature = ByteString.CopyFrom(packageSignature),
            HashVariant = HashVariant.V2,
        };
        transferPackage.LeavesToSend.AddRange(cpfpRefundJobs);
        transferPackage.DirectLeavesToSend.AddRange(directRefundJobs);
        transferPackage.DirectFromCpfpLeavesToSend.AddRange(directFromCpfpRefundJobs);
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
                    DateTimeOffset.UtcNow + TransferExpiry),
                TransferPackage = transferPackage,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);

        return TransferMapping.ToModel(response.Transfer
            ?? throw new SparkUntrustedResponseException(Operation, "start_transfer_v2 returned no transfer."));
    }

    /// <summary>
    /// Get a single transfer by ID, of any type, from the operators' by-id lookup
    /// (<c>query_transfers_by_id</c>, as the reference SDK queries it). Returns null if not found.
    /// </summary>
    public static async Task<SparkTransfer?> GetTransferAsync(
        this SparkWallet wallet,
        string transferId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
        var transfer = await wallet.TryQueryTransferByIdAsync(transferId, ct).ConfigureAwait(false);
        return transfer is null ? null : TransferMapping.ToModel(transfer);
    }

    /// <summary>
    /// A transfer this wallet sent or receives, by id, from the operators' by-id lookup
    /// (<c>query_transfers_by_id</c>, the reference SDK's <c>queryTransfer</c>): the whole
    /// transfer, every receiver's leaves included.
    /// </summary>
    /// <exception cref="SparkUntrustedResponseException">The operators do not return the transfer.</exception>
    internal static async Task<Transfer> QueryTransferByIdAsync(this SparkWallet wallet, string transferId, CancellationToken ct)
    {
        return await wallet.TryQueryTransferByIdAsync(transferId, ct).ConfigureAwait(false)
            ?? throw new SparkUntrustedResponseException(Operation, $"Transfer not found: {transferId}.");
    }

    private static async Task<Transfer?> TryQueryTransferByIdAsync(this SparkWallet wallet, string transferId, CancellationToken ct)
    {
        var client = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        var request = new QueryTransfersByIdRequest { Network = wallet.Options.ProtoNetwork() };
        request.TransferIds.Add(transferId.Trim().ToLowerInvariant());

        var response = await client.query_transfers_by_idAsync(request, headers, cancellationToken: ct).ConfigureAwait(false);
        return response.Transfers.FirstOrDefault(t => string.Equals(t.Id, transferId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Query transfers with pagination and optional time filters.
    /// </summary>
    /// <remarks>
    /// Only the transfers a user makes are listed, as the reference SDK lists them: Spark
    /// transfers, Lightning payments (preimage swaps), cooperative exits and static deposit claims
    /// (UTXO swaps). The legs of leaf swaps are left out; the operators also answer that query in
    /// well under a second, where an unfiltered one took them 17 s to over a minute for a wallet
    /// with a long history. Use <see cref="GetTransferAsync"/> to look up a transfer of any type.
    /// </remarks>
    /// <param name="wallet">The Spark wallet.</param>
    /// <param name="limit">Maximum transfers per page.</param>
    /// <param name="offset">Pagination offset.</param>
    /// <param name="createdAfter">Only transfers created strictly after this time.</param>
    /// <param name="createdBefore">Only transfers created strictly before this time (ignored when <paramref name="createdAfter"/> is set).</param>
    /// <param name="direction">Sent, received, or both (the default).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<TransferPage> GetTransfersAsync(
        this SparkWallet wallet,
        int limit = 100,
        long offset = 0,
        DateTimeOffset? createdAfter = null,
        DateTimeOffset? createdBefore = null,
        TransferDirection direction = TransferDirection.Both,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var coordinatorClient = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);

        var identity = ByteString.CopyFrom(wallet.IdentityPublicKey);
        var filter = new TransferFilter
        {
            Network = wallet.Options.ProtoNetwork(),
            Limit = limit,
            Offset = offset,
        };
        switch (direction)
        {
            case TransferDirection.Sent:
                filter.SenderIdentityPublicKey = identity;
                break;
            case TransferDirection.Received:
                filter.ReceiverIdentityPublicKey = identity;
                break;
            default:
                filter.SenderOrReceiverIdentityPublicKey = identity;
                break;
        }
        filter.Types_.AddRange(ListedTransferTypes);

        if (createdAfter.HasValue)
        {
            filter.CreatedAfter = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(createdAfter.Value);
        }
        else if (createdBefore.HasValue)
        {
            filter.CreatedBefore = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(createdBefore.Value);
        }

        var response = await coordinatorClient.query_all_transfersAsync(
            filter, headers, cancellationToken: ct).ConfigureAwait(false);

        var transfers = response.Transfers.Select(TransferMapping.ToModel).ToList();
        return new TransferPage(transfers, response.Offset);
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
