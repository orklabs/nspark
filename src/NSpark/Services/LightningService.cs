using Google.Protobuf;
using Grpc.Core;
using NSpark.Exceptions;
using NSpark.GraphQL;
using NSpark.Models;
using NSpark.Proto;
using NSpark.Signer;

namespace NSpark.Services;

/// <summary>
/// Extension methods on <see cref="SparkWallet"/> for receiving and sending Lightning payments
/// through the SSP.
/// </summary>
public static class LightningService
{
    private const string InvoiceOperation = "lightning.invoice";
    private const string PayOperation = "lightning.pay";

    /// <summary>Longest memo, in UTF-8 bytes, a BOLT-11 description can carry.</summary>
    private const int MaxMemoBytes = 639;

    /// <summary>
    /// Create a Lightning invoice to receive a payment.
    /// Derives a preimage, requests the invoice from the SSP, verifies it, then splits the
    /// preimage and stores one encrypted share with each Signing Operator.
    /// </summary>
    /// <remarks>
    /// The SSP's invoice is verified before any preimage share is stored or the invoice is handed
    /// out: our payment hash, our amount, our network, and no Spark fallback — the wallet never
    /// asks for one, and a fallback naming someone else would let payers that prefer Spark pay
    /// them instead. Each operator gets the preimage share at its own index (encoded in its
    /// identifier), whatever the order of the configuration.
    /// </remarks>
    public static async Task<LightningInvoice> CreateLightningInvoiceAsync(
        this SparkWallet wallet,
        long amountSats,
        string? memo = null,
        int? expirySecs = null,
        byte[]? receiverIdentityPublicKey = null,
        byte[]? descriptionHash = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        if (descriptionHash is { Length: not 32 })
        {
            throw new ArgumentException("descriptionHash must be 32 bytes (SHA-256).", nameof(descriptionHash));
        }
        if (amountSats < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amountSats), amountSats, "amountSats must not be negative.");
        }
        if (expirySecs is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expirySecs), expirySecs, "expirySecs must be positive.");
        }
        if (memo is not null && System.Text.Encoding.UTF8.GetByteCount(memo) > MaxMemoBytes)
        {
            throw new ArgumentException($"memo must be at most {MaxMemoBytes} bytes.", nameof(memo));
        }

        // Step 1: per-SO encrypted preimage shares from the signer. The preimage is derived
        // deterministically from transferId inside the signer and never crosses into the
        // wallet's address space — the wallet receives only the public payment hash and the
        // per-SO encrypted SecretShare blobs.
        var options = wallet.Options;
        var transferId = Guid.NewGuid().ToString();
        var threshold = options.EffectiveSigningThreshold;
        var preimageSoTargets = FrostSigningHelper.BuildConfiguredSoTargets(options.SigningOperators);
        FrostSigningHelper.ValidateThreshold(threshold, preimageSoTargets.Count);

        var preimageBundle = await wallet.Signer.BuildEncryptedPreimageSharesAsync(
            transferId, preimageSoTargets, threshold, ct).ConfigureAwait(false);
        var paymentHash = preimageBundle.PaymentHash;
        var paymentHashHex = Convert.ToHexString(paymentHash).ToLowerInvariant();

        // Step 2: request the invoice from the SSP.
        var variables = new Dictionary<string, object?>
        {
            ["network"] = options.Network.GraphQLName(),
            ["amount_sats"] = amountSats,
            ["payment_hash"] = paymentHashHex,
            ["expiry_secs"] = expirySecs,
            ["memo"] = memo,
            // BOLT11 description_hash (BIP-21 'h' field). Used by NIP-57 zaps where the
            // description is a signed zap-request JSON the recipient must commit to without
            // putting the full payload in the invoice.
            ["description_hash"] = descriptionHash != null
                ? Convert.ToHexString(descriptionHash).ToLowerInvariant()
                : null,
            ["receiver_identity_pubkey"] = receiverIdentityPublicKey != null
                ? Convert.ToHexString(receiverIdentityPublicKey).ToLowerInvariant()
                : null,
        };

        var response = await wallet.SspClient.ExecuteAsync<RequestLightningReceiveResponse>(
            Mutations.RequestLightningReceive, variables, ct).ConfigureAwait(false);

        var requestData = response.RequestLightningReceive?.Request
            ?? throw new SparkUntrustedResponseException(InvoiceOperation, "The SSP returned no Lightning receive request.");
        var invoiceData = requestData.Invoice
            ?? throw new SparkUntrustedResponseException(InvoiceOperation, "The SSP returned no invoice.");

        // The invoice we hand out must be the one we asked for. Checked before any preimage share
        // leaves the wallet, as the reference SDK's validateAndCreateLightningInvoice does.
        var decodedInvoice = LightningValidator.VerifyCreatedInvoice(
            invoiceData.EncodedInvoice,
            invoiceData.PaymentHash,
            paymentHash,
            amountSats,
            options.Network);

        // Step 3: store the encrypted shares with the operators. No user signature: the current
        // protocol reserves that field and the operators never read it (reference SDK 0.6.5).
        var storeRequest = new StorePreimageShareV2Request
        {
            PaymentHash = ByteString.CopyFrom(paymentHash),
            Threshold = threshold,
            InvoiceString = invoiceData.EncodedInvoice,
            UserIdentityPublicKey = ByteString.CopyFrom(receiverIdentityPublicKey ?? wallet.IdentityPublicKey),
        };
        foreach (var (soId, encrypted) in preimageBundle.EncryptedShareBySoId)
        {
            storeRequest.EncryptedPreimageShares.Add(soId, ByteString.CopyFrom(encrypted));
        }

        var coordinatorClient = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        await coordinatorClient.store_preimage_share_v2Async(storeRequest, headers, cancellationToken: ct).ConfigureAwait(false);

        return new LightningInvoice(
            PaymentRequest: invoiceData.EncodedInvoice,
            PaymentHash: paymentHashHex,
            AmountSats: amountSats,
            ExpiresAt: DateTimeOffset.TryParse(
                invoiceData.ExpiresAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var expiresAt)
                ? expiresAt
                : decodedInvoice.ExpiresAt,
            RequestId: requestData.Id);
    }

    /// <summary>
    /// Query the status of a Lightning receive request by its SSP request ID.
    /// Returns the status string (e.g. "PENDING", "COMPLETED") or null if not found.
    /// </summary>
    public static async Task<string?> GetLightningReceiveRequestStatusAsync(
        this SparkWallet wallet,
        string requestId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var response = await wallet.SspClient.ExecuteAsync<GetUserRequestResponse>(
            Queries.GetUserRequest,
            new Dictionary<string, object> { ["request_id"] = requestId },
            ct).ConfigureAwait(false);

        var data = response.UserRequest;
        if (data is null)
        {
            return null;
        }
        // Only return a status for the receive variant — otherwise the caller (typically the
        // invoice poller) would treat a send-request status string as a receive status and
        // misinterpret it.
        return string.Equals(data.TypeName, "LightningReceiveRequest", StringComparison.Ordinal)
            ? data.ReceiveStatus
            : null;
    }

    /// <summary>
    /// Query the SSP for the status of an outgoing Lightning payment by its SSP request id (the
    /// string returned from <see cref="PayLightningInvoiceAsync"/>). Returns null if the SSP has
    /// no record of the request id, or if the request id resolves to a non-send request type
    /// (e.g., a Lightning receive request).
    /// <para>
    /// Known status values are listed on Spark's <c>LightningSendRequestStatus</c> enum and
    /// include (non-exhaustive): <c>CREATED</c>, <c>REQUEST_VALIDATED</c>,
    /// <c>LIGHTNING_PAYMENT_INITIATED</c>, <c>LIGHTNING_PAYMENT_SUCCEEDED</c>,
    /// <c>LIGHTNING_PAYMENT_FAILED</c>, <c>PREIMAGE_PROVIDED</c>, <c>PREIMAGE_PROVIDING_FAILED</c>,
    /// <c>TRANSFER_COMPLETED</c>, <c>TRANSFER_FAILED</c>, <c>USER_TRANSFER_VALIDATION_FAILED</c>,
    /// <c>USER_SWAP_RETURNED</c>, <c>USER_SWAP_RETURN_FAILED</c>. Treat any value not yet on this
    /// list as still in flight — Spark explicitly reserves the right to add new ones.
    /// </para>
    /// <para>
    /// While the payment is in flight <see cref="LightningSendStatus.FeeSats"/> and
    /// <see cref="LightningSendStatus.Preimage"/> are null; the fee is reported once the SSP
    /// finalises pricing and the preimage appears once status reaches one of the succeeded states.
    /// </para>
    /// </summary>
    public static async Task<LightningSendStatus?> GetLightningSendStatusAsync(
        this SparkWallet wallet,
        string requestId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var response = await wallet.SspClient.ExecuteAsync<GetUserRequestResponse>(
            Queries.GetUserRequest,
            new Dictionary<string, object> { ["request_id"] = requestId },
            ct).ConfigureAwait(false);

        var data = response.UserRequest;
        if (data is null)
        {
            return null;
        }

        // user_request is polymorphic. We only care about the LightningSendRequest variant; anyone
        // querying the wrong id type gets a null back.
        if (!string.Equals(data.TypeName, "LightningSendRequest", StringComparison.Ordinal))
        {
            return null;
        }

        if (string.IsNullOrEmpty(data.SendStatus))
        {
            return null;
        }

        // Honour the SSP's CurrencyAmount unit discriminator.
        long? feeSats = data.SendFee is { } fee
            ? CurrencyAmountExtensions.ToSats(fee.OriginalValue, fee.OriginalUnit)
            : null;

        // The SSP's payment-hash field doesn't appear on LightningSendRequest, so we don't have it
        // here. Callers that need it must keep the (request_id ↔ payment_hash) mapping themselves.
        return new LightningSendStatus(
            PaymentHash: string.Empty,
            Status: data.SendStatus,
            FeeSats: feeSats,
            Preimage: data.SendPaymentPreimage);
    }

    /// <summary>
    /// Get a fee estimate for sending a Lightning payment, in whole sats. The estimate is read in
    /// the unit the SSP reports (SATOSHI as is, MILLISATOSHI rounded up); any other unit is
    /// refused, as the reference SDK refuses it.
    /// </summary>
    public static async Task<long> GetLightningSendFeeEstimateAsync(
        this SparkWallet wallet,
        string encodedInvoice,
        long? amountSats = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var variables = new Dictionary<string, object?>
        {
            ["encoded_invoice"] = encodedInvoice,
            ["amount_sats"] = amountSats,
        };

        var response = await wallet.SspClient.ExecuteAsync<LightningSendFeeEstimateResponse>(
            Queries.LightningSendFeeEstimate, variables, ct).ConfigureAwait(false);

        var fee = response.LightningSendFeeEstimate?.FeeEstimate
            ?? throw new SparkUntrustedResponseException("lightning.fee", "The SSP returned no Lightning fee estimate.");
        return CurrencyAmountExtensions.ToFeeSats(fee.OriginalValue, fee.OriginalUnit, "lightning fee estimate");
    }

    /// <summary>
    /// Query transfers for a given receiver identity public key.
    /// Can be used to check if a Lightning invoice was paid (funds transferred to receiver).
    /// Internal: returns raw protobuf transfers, not part of the public NSpark contract.
    /// </summary>
    internal static async Task<IReadOnlyList<Transfer>> QueryTransfersForReceiverAsync(
        this SparkWallet wallet,
        byte[] receiverIdentityPublicKey,
        CancellationToken ct = default)
    {
        var coordinatorClient = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        var filter = new TransferFilter
        {
            ReceiverIdentityPublicKey = ByteString.CopyFrom(receiverIdentityPublicKey),
            Network = wallet.Options.ProtoNetwork(),
        };
        var response = await coordinatorClient.query_all_transfersAsync(
            filter, headers, cancellationToken: ct).ConfigureAwait(false);

        return response.Transfers;
    }

    private const uint LightningHtlcSequence = 2160;

    /// <summary>
    /// Pay a BOLT-11 invoice through the SSP with the v3 preimage-swap flow (the reference
    /// SDK's <c>payLightningInvoice</c>: <c>prepareTransferForLightning</c> +
    /// <c>swapNodesForPreimage</c> + <c>requestLightningSend</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The invoice is decoded with a BOLT-11 parser that verifies the bech32 checksum, requires a
    /// payment secret and enforces the wallet's network; the trimmed, lower-case form is what goes
    /// to the SSP. The SSP's fee estimate is fetched first and offered as is; the payment is
    /// refused with <see cref="FeeExceedsLimitException"/> if it is above
    /// <paramref name="maxFeeSats"/>, before any leaf is selected or locked.
    /// </para>
    /// <para>
    /// The preimage swap carries the transfer id as its idempotency key, as the reference SDK's
    /// does. Once the coordinator may hold the leaves — the swap succeeded, or failed in a way
    /// that leaves its outcome unknown (a lost connection, a deadline, a cancellation, an internal
    /// error) — a failure throws <see cref="SparkLightningSendIncompleteException"/> carrying the
    /// transfer id. Call again with the same invoice and <paramref name="transferId"/> to resume:
    /// when the coordinator already holds that transfer, no leaf is selected or locked again —
    /// the held transfer must pay this invoice's payment hash with at most
    /// <paramref name="maxFeeSats"/> on top — and the SSP is asked to pay from it. The SSP answers
    /// a repeated request for a transfer with the request it already has, so a send that went
    /// through returns its request id instead of paying twice.
    /// </para>
    /// </remarks>
    /// <param name="wallet">The Spark wallet.</param>
    /// <param name="paymentRequest">BOLT-11 invoice. Must be for the wallet's network.</param>
    /// <param name="maxFeeSats">
    /// Highest routing fee the caller accepts. Lightning routing fees are not bounded by the
    /// protocol, only by what the wallet is willing to pay, so this is required.
    /// </param>
    /// <param name="amountSats">
    /// Amount to pay for an amountless (zero-amount) invoice. Must be omitted, or equal to the
    /// invoice amount, for an invoice that carries an amount.
    /// </param>
    /// <param name="transferId">
    /// Optional UUID that makes the send resumable (see remarks). On
    /// <see cref="SparkLightningSendIncompleteException"/> call again with the same id.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The SSP-side Lightning send request id, for status queries and reconciliation.</returns>
    public static async Task<string> PayLightningInvoiceAsync(
        this SparkWallet wallet,
        string paymentRequest,
        long maxFeeSats,
        long? amountSats = null,
        string? transferId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var payment = new LightningPayment(paymentRequest, maxFeeSats, amountSats, idempotencyKey: null, wallet.Options.Network);
        var resumeTransferId = LightningValidator.NormalizeTransferId(transferId);

        // Resuming a send the coordinator already holds: its leaves are locked for this payment,
        // so selecting leaves again would come up short (or swap for nothing) and a second swap
        // would be refused. Check what it holds and have the SSP pay from that.
        if (resumeTransferId is not null)
        {
            var held = await wallet.HeldLightningSendAsync(resumeTransferId, ct).ConfigureAwait(false);
            if (held is not null)
            {
                LightningValidator.VerifyHeldSend(
                    held, resumeTransferId, payment, wallet.IdentityPublicKey, wallet.Options.RequireSspIdentityPublicKey());
                return await wallet.RequestLightningSendAsync(payment, resumeTransferId, ct).ConfigureAwait(false);
            }
        }

        var transfer = await wallet.StartLightningSendAsync(
            payment, resumeTransferId ?? Guid.NewGuid().ToString("D"), ct).ConfigureAwait(false);
        return await wallet.RequestLightningSendAsync(payment, transfer.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The Lightning send this wallet started under <paramref name="transferId"/>, as the
    /// coordinator holds it — its HTLC (preimage request) with the transfer — or <c>null</c> when
    /// the coordinator holds none.
    /// </summary>
    internal static async Task<PreimageRequestWithTransfer?> HeldLightningSendAsync(
        this SparkWallet wallet,
        string transferId,
        CancellationToken ct)
    {
        var request = new QueryHtlcRequest
        {
            IdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
            MatchRole = PreimageRequestRole.Sender,
            Limit = 1,
        };
        request.TransferIds.Add(transferId);
        var client = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        var response = await client.query_htlcAsync(request, headers, cancellationToken: ct).ConfigureAwait(false);
        return response.PreimageRequests.FirstOrDefault();
    }

    /// <summary>
    /// Steps 1–3 of a Lightning send: quote the fee against the cap, select leaves for amount +
    /// fee (swapping if needed) and hand them to the coordinator as an HTLC transfer to the SSP in
    /// one <c>initiate_preimage_swap_v3</c>. Returns the transfer the coordinator now holds.
    /// </summary>
    internal static async Task<Transfer> StartLightningSendAsync(
        this SparkWallet wallet,
        LightningPayment payment,
        string transferId,
        CancellationToken ct)
    {
        var options = wallet.Options;
        var feeEstimate = await wallet.GetLightningSendFeeEstimateAsync(
            payment.EncodedInvoice, payment.AmountlessInvoiceAmountSats, ct).ConfigureAwait(false);
        var feeSats = LightningValidator.SendFeeSats(feeEstimate, payment.MaxFeeSats);

        long totalNeeded;
        try
        {
            totalNeeded = checked(payment.AmountSats + (long)feeSats);
        }
        catch (OverflowException ex)
        {
            throw new ArgumentException("Amount plus fee overflows.", nameof(payment), ex);
        }

        // The SSP's key, before any leaf moves: a transfer to it needs one.
        var sspPubKey = options.RequireSspIdentityPublicKey();

        // Select leaves covering invoice amount + fee (exact match or swap).
        var selectedLeaves = await wallet.SelectLeavesWithSwapAsync(totalNeeded, ct).ConfigureAwait(false);

        var coordinatorClient = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(options.Network);

        var soListResponse = await coordinatorClient.get_signing_operator_listAsync(
            new Google.Protobuf.WellKnownTypes.Empty(), headers, cancellationToken: ct).ConfigureAwait(false);
        var soTargets = FrostSigningHelper.BuildSoTargets(soListResponse.SigningOperators, options.SigningOperators);
        var threshold = options.EffectiveSigningThreshold;
        FrostSigningHelper.ValidateThreshold(threshold, soTargets.Count);

        // ── prepareTransferForLightning: key tweaks + HTLC refund txs ──

        // Encrypted per-SO tweak packages from the signer: all share material stays inside the
        // signer's trust boundary; the wallet only sees the encrypted blobs.
        var leafDescriptors = selectedLeaves
            .Select(l => new SendTweakLeafDescriptor(l.Id, sspPubKey))
            .ToList();
        var encryptedBatch = await wallet.Signer.BuildEncryptedSendTweaksAsync(
            leafDescriptors, soTargets, transferId, threshold, ct).ConfigureAwait(false);

        var keyTweakPackage = new Dictionary<string, ByteString>(encryptedBatch.EncryptedPackageBySoId.Count);
        foreach (var (soId, blob) in encryptedBatch.EncryptedPackageBySoId)
        {
            keyTweakPackage[soId] = ByteString.CopyFrom(blob);
        }

        // Signing commitments for the HTLC refunds (cpfp, direct, directFromCpfp), leaf-major.
        var htlcCommitmentsReq = new GetSigningCommitmentsRequest { Count = 3 };
        htlcCommitmentsReq.NodeIds.AddRange(selectedLeaves.Select(l => l.Id));
        var htlcCommitmentsResp = await coordinatorClient.get_signing_commitmentsAsync(
            htlcCommitmentsReq, headers, cancellationToken: ct).ConfigureAwait(false);
        var htlcCommitments = htlcCommitmentsResp.SigningCommitments;
        if (htlcCommitments.Count < 3 * selectedLeaves.Count)
        {
            throw new SparkUntrustedResponseException(
                PayOperation, $"Got {htlcCommitments.Count} signing commitments, need {3 * selectedLeaves.Count}.");
        }

        // HTLC refund transactions (signRefundsForLightning). The seqlock path pays the sender.
        var senderPubKey = wallet.IdentityPublicKey;
        var paymentHash = payment.Invoice.PaymentHash;
        var htlcCpfpJobs = new List<UserSignedTxSigningJob>();
        var htlcDirectJobs = new List<UserSignedTxSigningJob>();
        var htlcDirectFromCpfpJobs = new List<UserSignedTxSigningJob>();

        for (var i = 0; i < selectedLeaves.Count; i++)
        {
            var leaf = selectedLeaves[i];
            var node = leaf.Node;
            var verifyingKey = node.VerifyingPublicKey.ToByteArray();
            var nodeTxBytes = node.NodeTx.ToByteArray();
            var (htlcSeq, htlcDirectSeq) = TimelockHelper.HtlcSequences(node.RefundTx.ToByteArray(), PayOperation, leaf.Id);

            // CPFP HTLC refund (no fee applied).
            var cpfpHtlc = SparkTxBuilder.BuildHtlcTransaction(
                nodeTx: nodeTxBytes, vout: 0, sequence: htlcSeq,
                paymentHash: paymentHash, hashlockPubkey: sspPubKey,
                seqlockPubkey: senderPubKey, htlcSequence: LightningHtlcSequence,
                applyFee: false, feeSats: 0, network: networkStr);
            htlcCpfpJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                wallet.Signer, node.Id, verifyingKey,
                cpfpHtlc.Tx, cpfpHtlc.Sighash,
                htlcCommitments[i].SigningNonceCommitments, ct)
                .ConfigureAwait(false));

            // Direct HTLC refund whenever a direct node transaction exists — the operators expect
            // one then, unlike plain refunds.
            if (!node.DirectTx.IsEmpty)
            {
                var directHtlc = SparkTxBuilder.BuildHtlcTransaction(
                    nodeTx: node.DirectTx.ToByteArray(), vout: 0, sequence: htlcDirectSeq,
                    paymentHash: paymentHash, hashlockPubkey: sspPubKey,
                    seqlockPubkey: senderPubKey, htlcSequence: LightningHtlcSequence,
                    applyFee: true, feeSats: SparkConstants.DefaultRefundFeeSats, network: networkStr);
                htlcDirectJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                    wallet.Signer, node.Id, verifyingKey,
                    directHtlc.Tx, directHtlc.Sighash,
                    htlcCommitments[i + selectedLeaves.Count].SigningNonceCommitments, ct)
                    .ConfigureAwait(false));
            }

            // DirectFromCpfp HTLC refund (always, from the cpfp node tx).
            var directFromCpfpHtlc = SparkTxBuilder.BuildHtlcTransaction(
                nodeTx: nodeTxBytes, vout: 0, sequence: htlcDirectSeq,
                paymentHash: paymentHash, hashlockPubkey: sspPubKey,
                seqlockPubkey: senderPubKey, htlcSequence: LightningHtlcSequence,
                applyFee: true, feeSats: SparkConstants.DefaultRefundFeeSats, network: networkStr);
            htlcDirectFromCpfpJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                wallet.Signer, node.Id, verifyingKey,
                directFromCpfpHtlc.Tx, directFromCpfpHtlc.Sighash,
                htlcCommitments[i + (2 * selectedLeaves.Count)].SigningNonceCommitments, ct)
                .ConfigureAwait(false));
        }

        var transferPackage = new TransferPackage { HashVariant = HashVariant.V2 };
        transferPackage.LeavesToSend.AddRange(htlcCpfpJobs);
        transferPackage.DirectLeavesToSend.AddRange(htlcDirectJobs);
        transferPackage.DirectFromCpfpLeavesToSend.AddRange(htlcDirectFromCpfpJobs);
        foreach (var (soId, cipher) in keyTweakPackage)
        {
            transferPackage.KeyTweakPackage.Add(soId, cipher);
        }

        var packageHash = SparkTaggedHash.Create("spark", "transfer", "signing payload")
            .AddBytes(ClaimService.TransferIdBytes(transferId))
            .AddMapStringToBytes(keyTweakPackage)
            .Hash();
        transferPackage.UserSignature = ByteString.CopyFrom(
            await wallet.Signer.SignWithIdentityKeyAsync(packageHash, ct).ConfigureAwait(false));

        // ── One call to initiate_preimage_swap_v3 ──
        var transferRequest = new StartTransferRequest
        {
            TransferId = transferId,
            OwnerIdentityPublicKey = ByteString.CopyFrom(senderPubKey),
            ReceiverIdentityPublicKey = ByteString.CopyFrom(sspPubKey),
            // 16 days from now, as the reference SDK sets it.
            ExpiryTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                DateTimeOffset.UtcNow + TransferService.TransferExpiry),
            TransferPackage = transferPackage,
        };
        var swapRequest = PreimageSwapRequest(
            paymentHash, payment.AmountSats, payment.EncodedInvoice, feeSats, transferRequest);

        return await wallet.SubmitPreimageSwapAsync(
            swapRequest, PreimageSwapIdempotencyKey(payment.IdempotencyKey, transferId), payment.Invoice.PaymentHashHex, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Hand a Lightning send's preimage swap to the coordinator. A failure after which the
    /// coordinator may still have committed the swap — leaves locked under the transfer id —
    /// surfaces as <see cref="SparkLightningSendIncompleteException"/> with that id, so the caller
    /// can resume instead of losing track of the leaves until the transfer expires.
    /// </summary>
    internal static async Task<Transfer> SubmitPreimageSwapAsync(
        this SparkWallet wallet,
        InitiatePreimageSwapRequest request,
        string idempotencyKey,
        string paymentHashHex,
        CancellationToken ct)
    {
        var client = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        headers.Add(RenewalService.IdempotencyKeyHeader, idempotencyKey);
        var transferId = request.TransferRequest.TransferId;

        InitiatePreimageSwapResponse response;
        try
        {
            response = await client.initiate_preimage_swap_v3Async(request, headers, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (PreimageSwapMayHaveCommitted(ex))
        {
            throw new SparkLightningSendIncompleteException(
                PayOperation,
                $"The preimage swap's outcome is unknown: {ex.Message}",
                transferId,
                paymentHashHex,
                ex);
        }

        return response.Transfer
            ?? throw new SparkLightningSendIncompleteException(
                PayOperation, "initiate_preimage_swap_v3 returned no transfer.", transferId, paymentHashHex);
    }

    /// <summary>
    /// Whether a failed <c>initiate_preimage_swap_v3</c> may still have been committed by the
    /// coordinator. The statuses the operators give a request they refused before committing —
    /// validation, authentication, a leaf or resource that is not available, a lock conflict —
    /// rule it out. Anything else (a connection lost after the request went out, a deadline, a
    /// cancellation, an internal or unknown error) does not.
    /// </summary>
    internal static bool PreimageSwapMayHaveCommitted(Exception error)
    {
        if (error is not RpcException rpc)
        {
            return true;
        }

        return rpc.StatusCode switch
        {
            StatusCode.InvalidArgument or StatusCode.FailedPrecondition or StatusCode.OutOfRange
                or StatusCode.NotFound or StatusCode.AlreadyExists or StatusCode.PermissionDenied
                or StatusCode.Unauthenticated or StatusCode.ResourceExhausted or StatusCode.Aborted
                or StatusCode.Unimplemented => false,
            _ => true,
        };
    }

    /// <summary>
    /// Step 4 of a Lightning send: ask the SSP to pay the invoice from the transfer the
    /// coordinator holds. The leaves are locked for that transfer by now, so any failure surfaces
    /// its id for the app to resume (same <c>transferId</c>) or reconcile via the SSP.
    /// </summary>
    internal static async Task<string> RequestLightningSendAsync(
        this SparkWallet wallet,
        LightningPayment payment,
        string transferId,
        CancellationToken ct)
    {
        var variables = LightningSendVariables(
            payment.EncodedInvoice, payment.AmountlessInvoiceAmountSats, payment.IdempotencyKey, transferId);

        RequestLightningSendResponse sspResponse;
        try
        {
            sspResponse = await wallet.SspClient.ExecuteAsync<RequestLightningSendResponse>(
                Mutations.RequestLightningSend, variables, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new SparkLightningSendIncompleteException(
                PayOperation,
                $"The coordinator holds the leaves for transfer {transferId} but the SSP could not be asked to pay: {ex.Message}",
                transferId,
                payment.Invoice.PaymentHashHex,
                ex);
        }

        var requestId = sspResponse.RequestLightningSend?.Request?.Id;
        if (string.IsNullOrEmpty(requestId))
        {
            throw new SparkLightningSendIncompleteException(
                PayOperation,
                $"The coordinator holds the leaves for transfer {transferId} but the SSP returned no Lightning send request id.",
                transferId,
                payment.Invoice.PaymentHashHex);
        }

        return requestId;
    }

    /// <summary>
    /// The <c>initiate_preimage_swap_v3</c> request of a Lightning send: the HTLC transfer to the
    /// SSP in <c>transfer_request</c>, whose receiver the top-level receiver must equal. Only
    /// <c>transfer_request</c>: the operators build the swap from it alone, and the legacy
    /// <c>transfer</c> field is reserved in the current protocol; the reference SDK stopped
    /// sending it in 0.9.0.
    /// </summary>
    internal static InitiatePreimageSwapRequest PreimageSwapRequest(
        byte[] paymentHash,
        long invoiceAmountSats,
        string bolt11Invoice,
        ulong feeSats,
        StartTransferRequest transferRequest)
    {
        return new InitiatePreimageSwapRequest
        {
            PaymentHash = ByteString.CopyFrom(paymentHash),
            Reason = InitiatePreimageSwapRequest.Types.Reason.Send,
            ReceiverIdentityPublicKey = transferRequest.ReceiverIdentityPublicKey,
            FeeSats = feeSats,
            InvoiceAmount = new InvoiceAmount
            {
                ValueSats = (ulong)invoiceAmountSats,
                InvoiceAmountProof = new InvoiceAmountProof { Bolt11Invoice = bolt11Invoice },
            },
            TransferRequest = transferRequest,
        };
    }

    /// <summary>
    /// The coordinator idempotency key of a Lightning send's preimage swap: the caller's key, else
    /// the transfer id — never none. The coordinator answers a repeated key with the transfer it
    /// already committed instead of running the swap again, so a transport retry of a swap whose
    /// answer was lost, or a retry after <see cref="SparkLightningSendIncompleteException"/>, gets
    /// that transfer rather than a duplicate-transfer rejection. The reference SDK always sends one
    /// (<c>idempotencyKey: transferId</c>).
    /// </summary>
    internal static string PreimageSwapIdempotencyKey(string? idempotencyKey, string transferId) =>
        idempotencyKey ?? transferId;

    /// <summary>
    /// Variables of the SSP's <c>request_lightning_send</c>. <c>amount_sats</c> is set for an
    /// amountless invoice only — the SSP schema says it "should ONLY be set when the invoice
    /// amount is zero", and without it the SSP cannot pay one. The SSP accepts either
    /// <c>idempotency_key</c> or <c>user_outbound_transfer_external_id</c>, not both.
    /// </summary>
    internal static Dictionary<string, object?> LightningSendVariables(
        string encodedInvoice,
        long? amountlessInvoiceAmountSats,
        string? idempotencyKey,
        string transferId)
    {
        var variables = new Dictionary<string, object?> { ["encoded_invoice"] = encodedInvoice };
        if (amountlessInvoiceAmountSats is { } amount)
        {
            variables["amount_sats"] = amount;
        }

        if (idempotencyKey is not null)
        {
            variables["idempotency_key"] = idempotencyKey;
        }
        else
        {
            variables["user_outbound_transfer_external_id"] = transferId;
        }

        return variables;
    }
}
