using Google.Protobuf;
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

    /// <summary>
    /// Create a Lightning invoice to receive a payment.
    /// Generates a preimage, requests the invoice from SSP, then splits
    /// and stores preimage shares with Signing Operators via FROST VSS.
    /// </summary>
    public static async Task<LightningInvoice> CreateLightningInvoiceAsync(
        this SparkWallet wallet,
        long amountSats,
        string? memo = null,
        int? expirySecs = null,
        byte[]? receiverIdentityPublicKey = null,
        byte[]? descriptionHash = null,
        CancellationToken ct = default)
    {
        if (descriptionHash is { Length: not 32 })
        {
            throw new ArgumentException("descriptionHash must be 32 bytes (SHA-256).", nameof(descriptionHash));
        }

        // Step 1: Build per-SO encrypted preimage shares via the signer. The preimage
        // is derived deterministically from transferId inside the signer and never
        // crosses into the wallet's address space — the wallet receives only the
        // public payment_hash and per-SO encrypted SecretShare proto blobs.
        if (amountSats < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amountSats), amountSats, "amountSats must not be negative.");
        }
        if (expirySecs is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expirySecs), expirySecs, "expirySecs must be positive.");
        }

        var transferId = Guid.NewGuid().ToString();
        var soConfigs = wallet.Client.Options.SigningOperators;
        var threshold = wallet.Client.Options.EffectiveSigningThreshold;

        var preimageSoTargets = soConfigs
            .Select((cfg, i) => new SoTarget(
                cfg.Identifier,
                (uint)(i + 1),
                Convert.FromHexString(cfg.IdentityPublicKeyHex)))
            .ToList();

        var preimageBundle = await wallet.Signer.BuildEncryptedPreimageSharesAsync(
            transferId, preimageSoTargets, threshold, ct).ConfigureAwait(false);
        var paymentHash = preimageBundle.PaymentHash;
        var paymentHashHex = Convert.ToHexString(paymentHash).ToLowerInvariant();

        // Step 2: Request invoice from SSP via GraphQL
        var network = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? "MAINNET" : "REGTEST";

        var variables = new Dictionary<string, object?>
        {
            ["network"] = network,
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

        var requestData = response.RequestLightningReceive.Request;
        var invoiceData = requestData.Invoice;

        // The invoice we hand out must be the one we asked for: our payment hash, our amount,
        // our network. Checked before any preimage share leaves the wallet, as the reference
        // SDK's validateAndCreateLightningInvoice does.
        var decodedInvoice = LightningValidator.VerifyCreatedInvoice(
            invoiceData.EncodedInvoice,
            invoiceData.PaymentHash,
            paymentHash,
            amountSats,
            wallet.Client.Options.Network);

        // Step 3: Store encrypted shares with SOs
        var coordinatorAddress = soConfigs[0].Address;
        var coordinatorClient = wallet.Pool.GetSparkClient(coordinatorAddress);
        var headers = await wallet.GetAuthMetadataAsync(coordinatorAddress, ct).ConfigureAwait(false);

        var storeRequest = new StorePreimageShareV2Request
        {
            PaymentHash = ByteString.CopyFrom(paymentHash),
            Threshold = threshold,
            InvoiceString = invoiceData.EncodedInvoice,
            UserIdentityPublicKey = ByteString.CopyFrom(receiverIdentityPublicKey ?? wallet.IdentityPublicKey),
        };

        // The signer already ECIES-encrypted each SO's SecretShare proto to that SO's
        // identity public key — the wallet just plugs the blobs into the request map.
        foreach (var (soId, encrypted) in preimageBundle.EncryptedShareBySoId)
        {
            storeRequest.EncryptedPreimageShares.Add(soId, ByteString.CopyFrom(encrypted));
        }

        await coordinatorClient.store_preimage_share_v2Async(
            storeRequest, headers, cancellationToken: ct);

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

        // Honour the SSP's CurrencyAmount unit discriminator — same dispatch as
        // GetLightningSendFeeEstimateAsync / WithdrawalService.GetFeeQuoteAsync.
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
    /// Get a fee estimate for sending a Lightning payment.
    /// Returns estimated fee in satoshis.
    /// </summary>
    public static async Task<long> GetLightningSendFeeEstimateAsync(
        this SparkWallet wallet,
        string encodedInvoice,
        long? amountSats = null,
        CancellationToken ct = default)
    {
        var variables = new Dictionary<string, object?>
        {
            ["encoded_invoice"] = encodedInvoice,
            ["amount_sats"] = amountSats,
        };

        var response = await wallet.SspClient.ExecuteAsync<LightningSendFeeEstimateResponse>(
            Queries.LightningSendFeeEstimate, variables, ct).ConfigureAwait(false);

        // Honour the SSP's CurrencyAmount unit discriminator — the same field can come back as
        // SATOSHI or MILLISATOSHI depending on the route. See GraphQL.CurrencyAmountExtensions.
        var fee = response.LightningSendFeeEstimate.FeeEstimate;
        return CurrencyAmountExtensions.ToSats(fee.OriginalValue, fee.OriginalUnit);
    }

    /// <summary>
    /// Query transfers for a given receiver identity public key.
    /// Can be used to check if a Lightning invoice was paid (funds transferred to receiver).
    /// Internal: returns raw protobuf transfers, not part of the public NSpark contract.
    /// </summary>
    internal static async Task<IReadOnlyList<Proto.Transfer>> QueryTransfersForReceiverAsync(
        this SparkWallet wallet,
        byte[] receiverIdentityPublicKey,
        CancellationToken ct = default)
    {
        var coordinatorAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var coordinatorClient = wallet.Pool.GetSparkClient(coordinatorAddress);
        var headers = await wallet.GetAuthMetadataAsync(coordinatorAddress, ct).ConfigureAwait(false);

        var protoNetwork = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Network.Mainnet : Network.Regtest;
        var filter = new Proto.TransferFilter
        {
            ReceiverIdentityPublicKey = ByteString.CopyFrom(receiverIdentityPublicKey),
            Network = protoNetwork,
        };
        var response = await coordinatorClient.query_all_transfersAsync(
            filter, headers, cancellationToken: ct);

        return response.Transfers.ToList();
    }

    // JS SDK constants for sequence computation
    private const uint HtlcTimelockOffset = 70;
    private const uint DirectHtlcTimelockOffset = 85;
    private const uint LightningHtlcSequence = 2160;
    // DEFAULT_FEE_SATS = ESTIMATED_TX_SIZE(191) * DEFAULT_SATS_PER_VBYTE(5)
    private const ulong DefaultFeeSats = SparkConstants.DefaultRefundFeeSats;

    /// <summary>
    /// Pay a BOLT-11 invoice through the SSP with the v3 preimage-swap flow (the reference
    /// SDK's <c>payLightningInvoice</c>: <c>prepareTransferForLightning</c> +
    /// <c>swapNodesForPreimage</c> + <c>requestLightningSend</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The invoice is decoded with a BOLT-11 parser that verifies the bech32 checksum and
    /// enforces the wallet's network. The SSP's fee estimate is fetched first and the payment is
    /// refused with <see cref="FeeExceedsLimitException"/> if it is above
    /// <paramref name="maxFeeSats"/>; only then are leaves selected and locked.
    /// </para>
    /// <para>
    /// Once <c>initiate_preimage_swap_v3</c> succeeds the coordinator holds the leaves for the
    /// transfer. If the SSP then cannot be asked to pay, the call throws
    /// <see cref="SparkLightningSendIncompleteException"/> carrying that transfer id: call again
    /// with the same invoice and <paramref name="transferId"/> to resume (the coordinator
    /// returns the transfer it already holds instead of locking more leaves), or reconcile
    /// through the SSP with the payment hash.
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
    /// Optional UUID that makes the send resumable. On
    /// <see cref="SparkLightningSendIncompleteException"/> call again with the same id. It is
    /// also sent to the coordinator as the idempotency key, so a retry after a partial failure
    /// resumes the existing swap instead of starting a second one.
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
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentRequest);
        if (maxFeeSats < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFeeSats), maxFeeSats, "maxFeeSats must not be negative.");
        }

        var options = wallet.Client.Options;
        var invoice = Bolt11Invoice.Decode(paymentRequest);
        if (!invoice.BelongsTo(options.Network))
        {
            throw new InvalidBolt11Exception(
                PayOperation, $"The invoice is for {invoice.Network}; the wallet is on {options.Network}.")
            {
                PaymentRequest = paymentRequest,
            };
        }

        var paymentHash = invoice.PaymentHash;
        var isAmountless = invoice.AmountMsat is null;
        var invoiceAmountSats = LightningValidator.ResolvePaymentAmountSats(invoice.AmountMsat, amountSats);
        var resumeTransferId = LightningValidator.NormalizeTransferId(transferId);

        // Fee estimate from the SSP; refuse anything above the caller's cap before any leaf moves.
        var feeEstimate = await wallet.GetLightningSendFeeEstimateAsync(
            paymentRequest, isAmountless ? invoiceAmountSats : null, ct).ConfigureAwait(false);
        var feeSats = Math.Max(feeEstimate, 1);
        if (feeSats > maxFeeSats)
        {
            throw new FeeExceedsLimitException(PayOperation, feeSats, maxFeeSats);
        }

        long totalNeeded;
        try
        {
            totalNeeded = checked(invoiceAmountSats + feeSats);
        }
        catch (OverflowException ex)
        {
            throw new ArgumentException("Amount plus fee overflows.", nameof(amountSats), ex);
        }

        var coordinatorAddress = options.SigningOperatorAddresses[0];
        var coordinatorClient = wallet.Pool.GetSparkClient(coordinatorAddress);
        var headers = await wallet.GetAuthMetadataAsync(coordinatorAddress, ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(options.Network);

        // Step 1: Select leaves covering invoice amount + fee (exact match or swap)
        var selectedLeaves = await wallet.SelectLeavesWithSwapAsync(totalNeeded, ct).ConfigureAwait(false);
        var leafIds = selectedLeaves.Select(l => l.Id).ToList();

        // Step 2: Get SO operator info
        var soListResponse = await coordinatorClient.get_signing_operator_listAsync(
            new Google.Protobuf.WellKnownTypes.Empty(), headers, cancellationToken: ct).ConfigureAwait(false);
        var soOperators = soListResponse.SigningOperators;

        var sspPubKey = Convert.FromHexString(options.SspIdentityPublicKeyHex);
        var senderPubKey = wallet.IdentityPublicKey;
        var swapTransferId = resumeTransferId ?? Guid.NewGuid().ToString("D");
        // Single shared expiry time: 16 days from now (matching the reference SDK)
        var expiryTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
            DateTimeOffset.UtcNow.AddDays(16));

        // ── prepareTransferForLightning: key tweaks + HTLC refund txs ──

        // Step 3-4: Build encrypted per-SO tweak packages via the signer. All share material
        // stays inside the signer's trust boundary; the wallet only sees the encrypted blobs.
        var soTargets = FrostSigningHelper.BuildSoTargets(soOperators, options.SigningOperators);
        var leafDescriptors = selectedLeaves
            .Select(l => new SendTweakLeafDescriptor(l.Id, sspPubKey))
            .ToList();
        var encryptedBatch = await wallet.Signer.BuildEncryptedSendTweaksAsync(
            leafDescriptors, soTargets, swapTransferId, options.EffectiveSigningThreshold, ct).ConfigureAwait(false);

        var keyTweakPackage = new Dictionary<string, ByteString>(encryptedBatch.EncryptedPackageBySoId.Count);
        foreach (var (soId, blob) in encryptedBatch.EncryptedPackageBySoId)
        {
            keyTweakPackage[soId] = ByteString.CopyFrom(blob);
        }

        // Step 5: Get signing commitments for HTLC refund txs (Count=3: cpfp, direct, directFromCpfp)
        var htlcCommitmentsReq = new GetSigningCommitmentsRequest { Count = 3 };
        htlcCommitmentsReq.NodeIds.AddRange(leafIds);
        var htlcCommitmentsResp = await coordinatorClient.get_signing_commitmentsAsync(
            htlcCommitmentsReq, headers, cancellationToken: ct).ConfigureAwait(false);
        var htlcCommitments = htlcCommitmentsResp.SigningCommitments.ToList();
        if (htlcCommitments.Count < 3 * selectedLeaves.Count)
        {
            throw new PaymentFailedException(
                PayOperation, $"Got {htlcCommitments.Count} signing commitments, need {3 * selectedLeaves.Count}.");
        }

        // Step 6: Build and sign HTLC refund txs (signRefundsForLightning)
        var htlcCpfpJobs = new List<UserSignedTxSigningJob>();
        var htlcDirectJobs = new List<UserSignedTxSigningJob>();
        var htlcDirectFromCpfpJobs = new List<UserSignedTxSigningJob>();

        for (int i = 0; i < selectedLeaves.Count; i++)
        {
            var leaf = selectedLeaves[i];
            var node = leaf.Node;
            var verifyingKey = node.VerifyingPublicKey.ToByteArray();
            var nodeTxBytes = node.NodeTx.ToByteArray();

            // Read current sequence from refund tx
            var refundTxBytes = node.RefundTx.Length > 0
                ? node.RefundTx.ToByteArray()
                : nodeTxBytes;
            var (cpfpSeq, _) = TimelockHelper.ComputeNextSequences(
                refundTxBytes, PayOperation, leaf.Id);
            var bit30 = cpfpSeq & (1u << 30);
            var nextTimelock = cpfpSeq & 0xFFFF;
            var htlcSeq = bit30 | (nextTimelock + HtlcTimelockOffset);
            var htlcDirectSeq = bit30 | (nextTimelock + DirectHtlcTimelockOffset);

            // CPFP HTLC refund tx (applyFee: false)
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

            // Direct HTLC refund tx (if directTx exists)
            if (node.DirectTx.Length > 0)
            {
                var directHtlc = SparkTxBuilder.BuildHtlcTransaction(
                    nodeTx: node.DirectTx.ToByteArray(), vout: 0, sequence: htlcDirectSeq,
                    paymentHash: paymentHash, hashlockPubkey: sspPubKey,
                    seqlockPubkey: senderPubKey, htlcSequence: LightningHtlcSequence,
                    applyFee: true, feeSats: DefaultFeeSats, network: networkStr);

                htlcDirectJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                    wallet.Signer, node.Id, verifyingKey,
                    directHtlc.Tx, directHtlc.Sighash,
                    htlcCommitments[i + selectedLeaves.Count].SigningNonceCommitments, ct)
                    .ConfigureAwait(false));
            }

            // DirectFromCpfp HTLC refund tx (applyFee: true)
            var directFromCpfpHtlc = SparkTxBuilder.BuildHtlcTransaction(
                nodeTx: nodeTxBytes, vout: 0, sequence: htlcDirectSeq,
                paymentHash: paymentHash, hashlockPubkey: sspPubKey,
                seqlockPubkey: senderPubKey, htlcSequence: LightningHtlcSequence,
                applyFee: true, feeSats: DefaultFeeSats, network: networkStr);

            htlcDirectFromCpfpJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                wallet.Signer, node.Id, verifyingKey,
                directFromCpfpHtlc.Tx, directFromCpfpHtlc.Sighash,
                htlcCommitments[i + (2 * selectedLeaves.Count)].SigningNonceCommitments, ct)
                .ConfigureAwait(false));
        }

        // Step 7: Build TransferPackage with HTLC jobs + key tweaks
        var transferPackage = new TransferPackage
        {
            UserSignature = ByteString.Empty, // signed below
            HashVariant = HashVariant.V2,
        };
        transferPackage.LeavesToSend.AddRange(htlcCpfpJobs);
        transferPackage.DirectLeavesToSend.AddRange(htlcDirectJobs);
        transferPackage.DirectFromCpfpLeavesToSend.AddRange(htlcDirectFromCpfpJobs);
        foreach (var (soId, cipher) in keyTweakPackage)
        {
            transferPackage.KeyTweakPackage.Add(soId, cipher);
        }

        // Sign the transfer package
        var transferIdBytes = Convert.FromHexString(swapTransferId.Replace("-", "", StringComparison.Ordinal));
        var packageHash = SparkTaggedHash.Create("spark", "transfer", "signing payload")
            .AddBytes(transferIdBytes)
            .AddMapStringToBytes(keyTweakPackage)
            .Hash();
        transferPackage.UserSignature = ByteString.CopyFrom(
            await wallet.Signer.SignWithIdentityKeyAsync(packageHash, ct).ConfigureAwait(false));

        // Step 8: StartTransferRequest carrying the TransferPackage. The reference SDK leaves
        // leaves_to_send empty and does not populate the legacy `transfer` field of the swap
        // request: the coordinator reads everything from transfer_request.transfer_package.
        var startTransferRequest = new StartTransferRequest
        {
            TransferId = swapTransferId,
            OwnerIdentityPublicKey = ByteString.CopyFrom(senderPubKey),
            ReceiverIdentityPublicKey = ByteString.CopyFrom(sspPubKey),
            ExpiryTime = expiryTime,
            TransferPackage = transferPackage,
        };

        // Step 9: initiate_preimage_swap_v3. The transfer id doubles as the coordinator
        // idempotency key, so a retry after a partial failure resumes the existing swap.
        var swapHeaders = new Grpc.Core.Metadata();
        foreach (var entry in headers)
        {
            swapHeaders.Add(entry);
        }
        swapHeaders.Add("x-idempotency-key", swapTransferId);

        var swapResponse = await coordinatorClient.initiate_preimage_swap_v3Async(
            new InitiatePreimageSwapRequest
            {
                PaymentHash = ByteString.CopyFrom(paymentHash),
                InvoiceAmount = new InvoiceAmount
                {
                    ValueSats = (ulong)invoiceAmountSats,
                    InvoiceAmountProof = new InvoiceAmountProof
                    {
                        Bolt11Invoice = paymentRequest,
                    },
                },
                Reason = InitiatePreimageSwapRequest.Types.Reason.Send,
                ReceiverIdentityPublicKey = ByteString.CopyFrom(sspPubKey),
                FeeSats = (ulong)feeSats,
                TransferRequest = startTransferRequest,
            },
            swapHeaders,
            cancellationToken: ct).ConfigureAwait(false);
        if (swapResponse.Transfer is null)
        {
            throw new PaymentFailedException(PayOperation, "initiate_preimage_swap_v3 returned no transfer.");
        }

        // Step 10: Ask the SSP to pay, naming the transfer that holds the leaves. From here on
        // the coordinator holds the leaves for this transfer: surface the transfer id on failure
        // so the app can resume (same transferId) or reconcile via the SSP.
        var variables = new Dictionary<string, object?>
        {
            ["encoded_invoice"] = paymentRequest,
            ["amount_sats"] = isAmountless ? invoiceAmountSats : null,
            ["user_outbound_transfer_external_id"] = swapResponse.Transfer.Id,
        };

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
                $"The coordinator holds the leaves for transfer {swapResponse.Transfer.Id} but the SSP could not be asked to pay: {ex.Message}",
                swapResponse.Transfer.Id,
                invoice.PaymentHashHex,
                ex);
        }

        var requestId = sspResponse.RequestLightningSend?.Request?.Id;
        if (string.IsNullOrEmpty(requestId))
        {
            throw new SparkLightningSendIncompleteException(
                PayOperation,
                $"The coordinator holds the leaves for transfer {swapResponse.Transfer.Id} but the SSP returned no Lightning send request id.",
                swapResponse.Transfer.Id,
                invoice.PaymentHashHex);
        }

        return requestId;
    }
}
