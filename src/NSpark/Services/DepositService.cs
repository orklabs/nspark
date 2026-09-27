using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using NSpark.Bitcoin;
using NSpark.Exceptions;
using NSpark.GraphQL;
using NSpark.Models;
using NSpark.Proto;

namespace NSpark.Services;

/// <summary>
/// Extension methods on <see cref="SparkWallet"/> for on-chain Bitcoin deposits
/// — generating addresses, claiming confirmed deposits, and managing static
/// deposit flows.
/// </summary>
public static class DepositService
{
    private const uint InitialRefundSequence = 2000;
    private const uint DirectTimelockOffset = 50;
    private const ulong DefaultFeeSats = SparkConstants.DefaultRefundFeeSats; // 191 vbytes × 5 sat/vbyte

    /// <summary>
    /// Generate a one-time deposit address for receiving on-chain BTC into the Spark wallet.
    /// After the deposit confirms, call <see cref="ClaimDepositAsync"/>.
    /// </summary>
    /// <remarks>
    /// The address is verified before it is returned, as the reference SDK does: the operators'
    /// proof of possession, every non-coordinator operator's signature over the address against
    /// the configured keys, and that the address pays the reported verifying key.
    /// </remarks>
    /// <exception cref="SparkUntrustedResponseException">The coordinator's address fails verification.</exception>
    public static async Task<DepositAddress> GetDepositAddressAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var client = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);

        var leafId = Guid.NewGuid().ToString().ToLowerInvariant();
        var signingPubKey = await wallet.Signer.GetLeafPublicKeyAsync(leafId, ct).ConfigureAwait(false);

        var response = await client.generate_deposit_addressAsync(
            new GenerateDepositAddressRequest
            {
                IdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                SigningPublicKey = ByteString.CopyFrom(signingPubKey),
                Network = wallet.Options.ProtoNetwork(),
                LeafId = leafId,
                HashVariant = HashVariant.V2,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);

        var deposit = response.DepositAddress
            ?? throw new SparkUntrustedResponseException("deposit.address", "The coordinator returned no deposit address.");
        DepositAddressVerifier.Verify(deposit, signingPubKey, wallet.IdentityPublicKey, isStatic: false, wallet.Options);

        return new DepositAddress(
            Address: deposit.Address_,
            LeafId: leafId,
            UserPublicKey: signingPubKey,
            VerifyingKey: deposit.VerifyingKey.ToByteArray());
    }

    /// <summary>
    /// Claim a confirmed on-chain deposit, creating the Spark tree. The transaction's outputs are
    /// matched against the wallet's unused deposit addresses, so the claim is built for the leaf
    /// that actually received the funds.
    /// </summary>
    /// <param name="wallet">The Spark wallet.</param>
    /// <param name="depositTxId">The on-chain transaction ID (display-order hex, any case).</param>
    /// <param name="vout">
    /// The output index. <c>null</c> (the default) locates the output that pays one of this
    /// wallet's deposit addresses; an explicit index must pay one of them.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ClaimDepositAsync(
        this SparkWallet wallet,
        string depositTxId,
        uint? vout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var options = wallet.Options;
        var client = wallet.GetCoordinatorClient();
        var networkStr = FrostSigningHelper.GetNetworkString(options.Network);

        // The deposit transaction, checked to be the one asked for.
        var depositTx = await wallet.FetchDepositTransactionAsync(depositTxId, ct).ConfigureAwait(false);
        var rawTx = depositTx.Serialize(includeWitness: true);

        // Every unused deposit address, and the output that pays one of them.
        var candidates = (await wallet.QueryAllUnusedDepositAddressesAsync(ct).ConfigureAwait(false))
            .Where(d => d.HasLeafId && !string.IsNullOrEmpty(d.LeafId))
            .ToList();
        var (outputIndex, matchedAddress) = DepositHelpers.MatchDepositOutput(
            depositTx, candidates.Select(c => c.DepositAddress), vout, options.Network);
        var depositInfo = candidates.First(c => string.Equals(c.DepositAddress, matchedAddress, StringComparison.Ordinal));

        var leafId = depositInfo.LeafId;
        var verifyingKey = depositInfo.VerifyingPublicKey.ToByteArray();

        // The per-leaf public key from the signer (no private key needed in process).
        var signingPubKey = await wallet.Signer.GetLeafPublicKeyAsync(leafId, ct).ConfigureAwait(false);

        // Root node tx pair (CPFP + direct) spending the deposit output.
        var rootNodeTx = SparkTxBuilder.BuildNodeTxPair(
            parentTx: rawTx,
            vout: outputIndex,
            address: depositInfo.DepositAddress,
            sequence: 0,
            directSequence: DirectTimelockOffset,
            feeSats: DefaultFeeSats);

        // Initial refunds: cpfp at 2000, directFromCpfp at 2050.
        var refundTrio = SparkTxBuilder.BuildRefundTxTrio(
            cpfpNodeTx: rootNodeTx.Cpfp.Tx,
            directNodeTx: null,
            vout: 0,
            receivingPublicKey: signingPubKey,
            network: networkStr,
            sequence: InitialRefundSequence,
            directSequence: InitialRefundSequence + DirectTimelockOffset,
            feeSats: DefaultFeeSats);

        // Signing commitments (3: root, cpfpRefund, directFromCpfpRefund).
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        var commitmentsResponse = await client.get_signing_commitmentsAsync(
            new GetSigningCommitmentsRequest { Count = 3, NodeIdCount = 1 },
            headers,
            cancellationToken: ct).ConfigureAwait(false);
        var allCommitments = commitmentsResponse.SigningCommitments;
        if (allCommitments.Count < 3)
        {
            throw new SparkUntrustedResponseException(
                "deposit.claim", $"Got {allCommitments.Count} signing commitments, need 3.");
        }

        // Signing jobs (FROST signing happens inside the signer).
        var rootJob = await FrostSigningHelper.BuildSigningJobAsync(
            wallet.Signer, leafId, verifyingKey,
            rootNodeTx.Cpfp.Tx, rootNodeTx.Cpfp.Sighash,
            allCommitments[0].SigningNonceCommitments, ct).ConfigureAwait(false);

        var refundJob = await FrostSigningHelper.BuildSigningJobAsync(
            wallet.Signer, leafId, verifyingKey,
            refundTrio.CpfpRefund.Tx, refundTrio.CpfpRefund.Sighash,
            allCommitments[1].SigningNonceCommitments, ct).ConfigureAwait(false);

        var directFromCpfpRefundJob = await FrostSigningHelper.BuildSigningJobAsync(
            wallet.Signer, leafId, verifyingKey,
            refundTrio.DirectFromCpfpRefund.Tx, refundTrio.DirectFromCpfpRefund.Sighash,
            allCommitments[2].SigningNonceCommitments, ct).ConfigureAwait(false);

        // The operators derive the deposit from the raw transaction; the txid goes in display
        // order, as UTXO.txid is specified.
        var outpoint = new DepositOutpoint(depositTx.TxidHex, outputIndex);
        var utxo = outpoint.ToUtxo(options.ProtoNetwork());
        utxo.RawTx = ByteString.CopyFrom(rawTx);

        await client.finalize_deposit_tree_creationAsync(
            new FinalizeDepositTreeCreationRequest
            {
                IdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                OnChainUtxo = utxo,
                RootTxSigningJob = rootJob,
                RefundTxSigningJob = refundJob,
                DirectFromCpfpRefundTxSigningJob = directFromCpfpRefundJob,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Generate a static (reusable) deposit address, verified like
    /// <see cref="GetDepositAddressAsync"/> — the coordinator's signature included, since the address
    /// is reused for every deposit.
    /// </summary>
    /// <exception cref="SparkUntrustedResponseException">The coordinator's address fails verification.</exception>
    public static async Task<StaticDepositAddress> GetStaticDepositAddressAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var client = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);

        var staticPubKey = await wallet.Signer.GetStaticDepositPublicKeyAsync(0, ct).ConfigureAwait(false);

        var response = await client.generate_static_deposit_addressAsync(
            new GenerateStaticDepositAddressRequest
            {
                SigningPublicKey = ByteString.CopyFrom(staticPubKey),
                IdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                Network = wallet.Options.ProtoNetwork(),
                HashVariant = HashVariant.V2,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);

        var deposit = response.DepositAddress
            ?? throw new SparkUntrustedResponseException("deposit.address", "The coordinator returned no static deposit address.");
        DepositAddressVerifier.Verify(deposit, staticPubKey, wallet.IdentityPublicKey, isStatic: true, wallet.Options);

        return new StaticDepositAddress(
            Address: deposit.Address_,
            VerifyingKey: deposit.VerifyingKey.ToByteArray());
    }

    /// <summary>Query all static deposit addresses of this wallet.</summary>
    public static async Task<IReadOnlyList<StaticDepositAddress>> QueryStaticDepositAddressesAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var client = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);

        var response = await client.query_static_deposit_addressesAsync(
            new QueryStaticDepositAddressesRequest
            {
                IdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                Network = wallet.Options.ProtoNetwork(),
                HashVariant = HashVariant.V2,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);

        return response.DepositAddresses
            .Select(d => new StaticDepositAddress(d.DepositAddress, d.VerifyingPublicKey.ToByteArray()))
            .ToList();
    }

    /// <summary>
    /// Claim a static deposit for whatever credit the SSP quotes, unchecked. Returns the Spark
    /// transfer ID.
    /// </summary>
    /// <param name="wallet">The Spark wallet.</param>
    /// <param name="transactionId">The on-chain transaction id (display-order hex, any case).</param>
    /// <param name="outputIndex">The output index; by default the output that pays this wallet's static deposit address.</param>
    /// <param name="ct">Cancellation token.</param>
    [Obsolete("Signs whatever credit the SSP quotes. Use ClaimStaticDepositWithMaxFeeAsync, or ClaimStaticDepositAsync(transactionId, quote) with a quote you checked.")]
#pragma warning disable RS0026, RS0027 // Optional parameters on parallel overloads — alpha API.
    public static async Task<string> ClaimStaticDepositAsync(
        this SparkWallet wallet,
        string transactionId,
        uint? outputIndex = null,
        CancellationToken ct = default)
#pragma warning restore RS0026, RS0027
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var vout = await wallet.StaticDepositVoutAsync(transactionId, outputIndex, transaction: null, ct).ConfigureAwait(false);
        var quote = await wallet.GetDepositFeeEstimateAsync(transactionId, vout, ct).ConfigureAwait(false);
        return await wallet.ClaimStaticDepositAsync(transactionId, quote, vout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Claim a static deposit for exactly the credit of <paramref name="quote"/> — the SSP-signed
    /// quote <see cref="GetDepositFeeEstimateAsync"/> returned for this output — as the reference
    /// SDK's <c>claimStaticDeposit</c> does: the wallet signs a fixed-amount claim for that credit
    /// and the SSP's quote signature, so the SSP cannot credit less. Returns the Spark transfer ID.
    /// </summary>
    /// <param name="wallet">The Spark wallet.</param>
    /// <param name="transactionId">The on-chain transaction id (display-order hex, any case).</param>
    /// <param name="quote">The SSP's quote for this output.</param>
    /// <param name="outputIndex">The output index; by default the output that pays this wallet's static deposit address.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// The Spark static-deposit protocol reveals the static-deposit private key to the SSP;
    /// signers that refuse to export it (HSM/KMS) throw <see cref="NotSupportedException"/>.
    /// </remarks>
#pragma warning disable RS0026, RS0027
    public static async Task<string> ClaimStaticDepositAsync(
        this SparkWallet wallet,
        string transactionId,
        DepositFeeEstimate quote,
        uint? outputIndex = null,
        CancellationToken ct = default)
#pragma warning restore RS0026, RS0027
    {
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentNullException.ThrowIfNull(quote);
        if (quote.CreditAmountSats <= 0)
        {
            throw new ArgumentException($"The quote credits {quote.CreditAmountSats} sats; nothing to claim.", nameof(quote));
        }

        byte[] quoteSignature;
        try
        {
            quoteSignature = Convert.FromHexString(quote.Signature ?? string.Empty);
        }
        catch (FormatException ex)
        {
            throw new SparkUntrustedResponseException("deposit.static.claim", "The SSP's quote signature is not hex.", ex);
        }

        if (quoteSignature.Length == 0)
        {
            throw new SparkUntrustedResponseException("deposit.static.claim", "The SSP's quote signature is empty.");
        }

        var outpoint = new DepositOutpoint(
            transactionId,
            await wallet.StaticDepositVoutAsync(transactionId, outputIndex, transaction: null, ct).ConfigureAwait(false));
        var network = wallet.Options.Network;
        var statement = DepositHelpers.StaticDepositStatement(
            outpoint, network, StaticDepositRequestType.Fixed, (ulong)quote.CreditAmountSats, quoteSignature);
        var signature = await wallet.Signer.SignWithIdentityKeyAsync(SHA256.HashData(statement), ct).ConfigureAwait(false);

        // The Spark static-deposit protocol requires revealing the raw static-deposit private key
        // to the SSP — signers that refuse to export it (HSM/KMS) throw NotSupportedException.
        var staticSecretKey = await wallet.Signer.ExportStaticDepositPrivateKeyAsync(0, ct).ConfigureAwait(false);
        var depositSecretKeyHex = Convert.ToHexString(staticSecretKey).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(staticSecretKey);

        var claimResponse = await wallet.SspClient.ExecuteAsync<ClaimStaticDepositResponse>(
            Mutations.ClaimStaticDeposit,
            new Dictionary<string, object>
            {
                ["transaction_id"] = outpoint.Txid,
                ["output_index"] = (int)outpoint.Vout,
                ["network"] = network.GraphQLName(),
                ["request_type"] = "FIXED_AMOUNT",
                ["credit_amount_sats"] = quote.CreditAmountSats,
                ["deposit_secret_key"] = depositSecretKeyHex,
                ["signature"] = Convert.ToHexString(signature).ToLowerInvariant(),
                ["quote_signature"] = quote.Signature!,
            },
            ct).ConfigureAwait(false);

        return claimResponse.ClaimStaticDeposit?.TransferId
            ?? throw new SparkUntrustedResponseException("deposit.static.claim", "ClaimStaticDeposit did not return a transfer ID.");
    }

    /// <summary>
    /// Claim a static deposit, but only if the SSP's fee is at or below <paramref name="maxFee"/>
    /// sats: the SSP's quote is checked against the deposit's value (from a transaction that
    /// hashes to the txid) and then claimed exactly, as the reference SDK does. Returns the Spark
    /// transfer ID, or <c>null</c> if the fee exceeds <paramref name="maxFee"/>.
    /// </summary>
    public static async Task<string?> ClaimStaticDepositWithMaxFeeAsync(
        this SparkWallet wallet,
        string transactionId,
        long maxFee,
        uint? outputIndex = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var depositTx = await wallet.FetchDepositTransactionAsync(transactionId, ct).ConfigureAwait(false);
        var vout = await wallet.StaticDepositVoutAsync(transactionId, outputIndex, depositTx, ct).ConfigureAwait(false);
        var outpoint = new DepositOutpoint(transactionId, vout);
        var depositSats = TransferMapping.ReportedSats(depositTx.OutputAt(vout).Value);

        var quote = await wallet.GetDepositFeeEstimateAsync(outpoint.Txid, vout, ct).ConfigureAwait(false);
        if (StaticDepositFee(depositSats, quote) > maxFee)
        {
            return null;
        }

        return await wallet.ClaimStaticDepositAsync(outpoint.Txid, quote, vout, ct).ConfigureAwait(false);
    }

    /// <summary>What the SSP keeps of a deposit under <paramref name="quote"/>.</summary>
    internal static long StaticDepositFee(long depositSats, DepositFeeEstimate quote) => depositSats - quote.CreditAmountSats;

    /// <summary>
    /// Query unused (non-static) deposit addresses for this wallet.
    /// </summary>
    /// <remarks>
    /// The operator pages over all of the wallet's single-use addresses, used ones included, and
    /// drops the used ones afterwards, so a page can hold fewer than <paramref name="limit"/>
    /// addresses before the end. Pass <c>limit: 0</c> for every unused address in one response.
    /// </remarks>
    public static async Task<IReadOnlyList<UnusedDepositAddress>> QueryUnusedDepositAddressesAsync(
        this SparkWallet wallet,
        int limit = 100,
        int offset = 0,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var response = await wallet.QueryUnusedDepositAddressesPageAsync(limit, offset, ct).ConfigureAwait(false);
        return response.DepositAddresses
            .Select(d => new UnusedDepositAddress(
                Address: d.DepositAddress,
                LeafId: d.HasLeafId ? d.LeafId : null,
                UserSigningPublicKey: d.UserSigningPublicKey.ToByteArray(),
                VerifyingPublicKey: d.VerifyingPublicKey.ToByteArray()))
            .ToList();
    }

    private static async Task<QueryUnusedDepositAddressesResponse> QueryUnusedDepositAddressesPageAsync(
        this SparkWallet wallet,
        int limit,
        int offset,
        CancellationToken ct)
    {
        var client = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        return await client.query_unused_deposit_addressesAsync(
            new QueryUnusedDepositAddressesRequest
            {
                IdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                Network = wallet.Options.ProtoNetwork(),
                Limit = limit,
                Offset = offset,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every unused deposit address of the wallet, in one unpaged request. The operator pages
    /// before it drops used addresses, so a short page does not mark the end of the list.
    /// </summary>
    internal static async Task<IReadOnlyList<DepositAddressQueryResult>> QueryAllUnusedDepositAddressesAsync(
        this SparkWallet wallet,
        CancellationToken ct)
    {
        var response = await wallet.QueryUnusedDepositAddressesPageAsync(limit: 0, offset: 0, ct).ConfigureAwait(false);
        return response.DepositAddresses;
    }

    /// <summary>
    /// Get UTXOs sent to a deposit address. Calls the Spark coordinator, which reports txids in
    /// display order.
    /// </summary>
    public static async Task<IReadOnlyList<DepositUtxo>> GetUtxosForDepositAddressAsync(
        this SparkWallet wallet,
        string address,
        bool excludeClaimed = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var client = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);

        var response = await client.get_utxos_for_addressAsync(
            new GetUtxosForAddressRequest
            {
                Address = address,
                Network = wallet.Options.ProtoNetwork(),
                ExcludeClaimed = excludeClaimed,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);

        return response.Utxos
            .Select(u => new DepositUtxo(Txid: Convert.ToHexString(u.Txid.Span).ToLowerInvariant(), Vout: u.Vout))
            .ToList();
    }

    /// <summary>
    /// Get the SSP's quote for claiming a static deposit output: how much it will credit. Without
    /// <paramref name="outputIndex"/> the quote is for the output that pays this wallet's static
    /// deposit address.
    /// </summary>
    public static async Task<DepositFeeEstimate> GetDepositFeeEstimateAsync(
        this SparkWallet wallet,
        string transactionId,
        uint? outputIndex = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var outpoint = new DepositOutpoint(
            transactionId,
            await wallet.StaticDepositVoutAsync(transactionId, outputIndex, transaction: null, ct).ConfigureAwait(false));

        var response = await wallet.SspClient.ExecuteAsync<StaticDepositQuoteResponse>(
            Mutations.StaticDepositQuote,
            new Dictionary<string, object>
            {
                ["transaction_id"] = outpoint.Txid,
                ["output_index"] = (int)outpoint.Vout,
                ["network"] = wallet.Options.Network.GraphQLName(),
            },
            ct).ConfigureAwait(false);

        var quote = response.StaticDepositQuote
            ?? throw new SparkUntrustedResponseException("deposit.static.quote", "The SSP returned no static deposit quote.");
        return new DepositFeeEstimate(CreditAmountSats: quote.CreditAmountSats, Signature: quote.Signature);
    }

    /// <summary>
    /// Refund a static deposit back to an on-chain address. Returns the signed transaction hex
    /// ready for broadcast.
    /// </summary>
    /// <param name="wallet">The Spark wallet.</param>
    /// <param name="depositTransactionId">The deposit's on-chain transaction id (display-order hex, any case).</param>
    /// <param name="destinationAddress">Bitcoin address on the wallet's network to send the refund to.</param>
    /// <param name="satsPerVbyte">Fee rate, 1 to 150 sat/vbyte.</param>
    /// <param name="outputIndex">The output index; by default the output that pays this wallet's static deposit address.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<string> RefundStaticDepositAsync(
        this SparkWallet wallet,
        string depositTransactionId,
        string destinationAddress,
        ulong satsPerVbyte,
        uint? outputIndex = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        if (satsPerVbyte > 150)
        {
            throw new ArgumentException("Fee rate must be <= 150 sat/vbyte.", nameof(satsPerVbyte));
        }

        // Estimated vbytes for a single-input single-output P2TR spend.
        const ulong estimatedVbytes = 194;
        var fee = satsPerVbyte * estimatedVbytes;
        if (fee < estimatedVbytes)
        {
            throw new ArgumentException("The fee must be at least 194 sats (1 sat/vbyte).", nameof(satsPerVbyte));
        }

        var options = wallet.Options;
        var client = wallet.GetCoordinatorClient();

        // The deposit output, from a transaction that hashes to the txid.
        var depositTx = await wallet.FetchDepositTransactionAsync(depositTransactionId, ct).ConfigureAwait(false);
        var outpoint = new DepositOutpoint(
            depositTransactionId,
            await wallet.StaticDepositVoutAsync(depositTransactionId, outputIndex, depositTx, ct).ConfigureAwait(false));
        var depositOutput = depositTx.OutputAt(outpoint.Vout);
        var creditAmountSats = TransferMapping.ReportedSats(depositOutput.Value) - (long)fee;
        if (creditAmountSats <= 0)
        {
            throw new SparkConfigurationException(
                "deposit.static.refund", $"The deposit output ({depositOutput.Value} sats) is too small to cover the fee ({fee} sats).");
        }

        // The spend transaction: 1 input (the deposit), 1 output (the destination).
        var destination = CoopExitValidator.ScriptPubKeyFor(destinationAddress, options.Network).ToBytes();
        var spendTx = DepositHelpers.ConstructSpendTx(outpoint, destination, (ulong)creditAmountSats);
        var sighash = SparkTxBuilder.ComputeMultiInputSighash(
            tx: spendTx,
            inputIndex: 0,
            prevOutScripts: [depositOutput.ScriptPubKey],
            prevOutValues: [depositOutput.Value]);

        // Phase-1 FROST nonce via the signer (the static-deposit key never leaves the signer).
        var staticNonce = await wallet.Signer.GenerateStaticDepositFrostNonceAsync(0, ct).ConfigureAwait(false);
        var staticPubKey = staticNonce.PublicKey;
        var signingJob = new SigningJob
        {
            SigningPublicKey = ByteString.CopyFrom(staticPubKey),
            RawTx = ByteString.CopyFrom(spendTx),
            SigningNonceCommitment = new Proto.Common.SigningCommitment
            {
                Hiding = ByteString.CopyFrom(staticNonce.Commitment.Hiding),
                Binding = ByteString.CopyFrom(staticNonce.Commitment.Binding),
            },
        };

        var refundRequest = await StaticDepositRefundRequestAsync(
            wallet.Signer, outpoint, options.Network, (ulong)creditAmountSats, sighash, signingJob, ct).ConfigureAwait(false);
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        var refundResponse = await client.initiate_static_deposit_utxo_refundAsync(
            refundRequest, headers, cancellationToken: ct).ConfigureAwait(false);

        // Phase-2 FROST sign via the signer with the real verifying key from the SO, then
        // aggregate locally (aggregation is a pure-public-key op).
        var verifyingKey = refundResponse.DepositAddress.VerifyingPublicKey.ToByteArray();
        var soCommitments = new Dictionary<string, Signer.SigningCommitment>();
        foreach (var (soId, commitment) in refundResponse.RefundTxSigningResult.SigningNonceCommitments)
        {
            soCommitments[soId] = new Signer.SigningCommitment(commitment.Hiding.ToByteArray(), commitment.Binding.ToByteArray());
        }
        var selfSignature = await wallet.Signer.SignStaticDepositFrostWithNonceAsync(
            0, staticNonce.NonceHandle, sighash, verifyingKey, soCommitments, ct).ConfigureAwait(false);
        var aggregatedSig = FrostSigningHelper.AggregateFrostSignature(
            sighash: sighash,
            selfCommitment: staticNonce.Commitment,
            selfSignature: selfSignature,
            selfPublicKey: staticPubKey,
            verifyingKey: verifyingKey,
            signingResult: refundResponse.RefundTxSigningResult,
            adaptorPublicKey: null);

        var signedTx = DepositHelpers.AddWitnessToTx(spendTx, aggregatedSig);
        return Convert.ToHexString(signedTx).ToLowerInvariant();
    }

    /// <summary>
    /// The refund request, authorized by the identity key's signature over the refund statement,
    /// which ends with the spend transaction's raw sighash.
    /// </summary>
    /// <remarks>
    /// The request leaves <c>hash_variant</c> unset, as the reference SDK does: the operators check
    /// the signature against the legacy (SHA-256) statement unless it is V2, and against a
    /// tagged-hash statement when it is.
    /// </remarks>
    internal static async Task<InitiateStaticDepositUtxoRefundRequest> StaticDepositRefundRequestAsync(
        Signer.ISparkSigner signer,
        DepositOutpoint outpoint,
        SparkNetwork network,
        ulong creditAmountSats,
        byte[] sighash,
        SigningJob signingJob,
        CancellationToken ct)
    {
        var statement = DepositHelpers.StaticDepositStatement(
            outpoint, network, StaticDepositRequestType.Refund, creditAmountSats, sighash);
        var userSignature = await signer.SignWithIdentityKeyAsync(SHA256.HashData(statement), ct).ConfigureAwait(false);
        return new InitiateStaticDepositUtxoRefundRequest
        {
            OnChainUtxo = outpoint.ToUtxo(network.ToProto()),
            RefundTxSigningJob = signingJob,
            UserSignature = ByteString.CopyFrom(userSignature),
        };
    }

    /// <summary>Refund a static deposit and broadcast it. Returns the txid.</summary>
    public static async Task<string> RefundAndBroadcastStaticDepositAsync(
        this SparkWallet wallet,
        string depositTransactionId,
        string destinationAddress,
        ulong satsPerVbyte,
        uint? outputIndex = null,
        CancellationToken ct = default)
    {
        var txHex = await wallet.RefundStaticDepositAsync(
            depositTransactionId, destinationAddress, satsPerVbyte, outputIndex, ct).ConfigureAwait(false);
        return await wallet.BroadcastTransactionAsync(txHex, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Broadcast a raw transaction to the Bitcoin network.
    /// Returns the transaction ID.
    /// </summary>
    public static async Task<string> BroadcastTransactionAsync(
        this SparkWallet wallet,
        string txHex,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        using var content = new StringContent(txHex, Encoding.UTF8, "text/plain");
        using var response = await wallet.Client.HttpClient.PostAsync(
            new Uri($"{ExplorerBaseUrl(wallet)}/tx"), content, ct).ConfigureAwait(false);
        var body = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
        if (!response.IsSuccessStatusCode)
        {
            throw new SparkDepositException("deposit.broadcast", $"Failed to broadcast the transaction: {body}");
        }

        return body;
    }

    // ── Internal helpers ──

    private static string ExplorerBaseUrl(SparkWallet wallet) => wallet.Options.Network == SparkNetwork.Mainnet
        ? "https://mempool.space/api"
        : "http://localhost:3000";

    /// <summary>A deposit transaction from the block explorer, checked to hash to <paramref name="txid"/>.</summary>
    internal static async Task<RawTransaction> FetchDepositTransactionAsync(
        this SparkWallet wallet, string txid, CancellationToken ct)
    {
        var normalized = DepositOutpoint.NormalizedTxid(txid);
        var tx = RawTransaction.Parse(await FetchRawTransactionAsync(wallet, normalized, ct).ConfigureAwait(false), "deposit tx");
        if (!string.Equals(tx.TxidHex, normalized, StringComparison.Ordinal))
        {
            throw new SparkUntrustedResponseException(
                "deposit.fetch", $"The block explorer returned a transaction that does not hash to {normalized}.");
        }

        return tx;
    }

    /// <summary>
    /// <paramref name="outputIndex"/>, or else the output of deposit <paramref name="txid"/> that
    /// pays this wallet's static deposit address, as the reference SDK's
    /// <c>getDepositTransactionVout</c> finds it.
    /// </summary>
    internal static async Task<uint> StaticDepositVoutAsync(
        this SparkWallet wallet,
        string txid,
        uint? outputIndex,
        RawTransaction? transaction,
        CancellationToken ct)
    {
        if (outputIndex is { } index)
        {
            return index;
        }

        var tx = transaction ?? await wallet.FetchDepositTransactionAsync(txid, ct).ConfigureAwait(false);
        var addresses = (await wallet.QueryStaticDepositAddressesAsync(ct).ConfigureAwait(false)).Select(a => a.Address);
        return DepositHelpers.StaticDepositVout(tx, addresses, wallet.Options.Network);
    }

    /// <summary>
    /// Fetch raw transaction bytes from mempool.space (mainnet) or local electrs (regtest). Throws
    /// for a txid that is not 64 hex characters or a reply that is not hex.
    /// </summary>
    internal static async Task<byte[]> FetchRawTransactionAsync(
        SparkWallet wallet, string txId, CancellationToken ct)
    {
        var txid = DepositOutpoint.NormalizedTxid(txId);
        using var response = await wallet.Client.HttpClient.GetAsync(
            new Uri($"{ExplorerBaseUrl(wallet)}/tx/{txid}/hex"), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SparkDepositException(
                "deposit.fetch", $"Failed to fetch raw transaction {txid} (HTTP {(int)response.StatusCode}).");
        }

        var hexString = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
        try
        {
            var bytes = Convert.FromHexString(hexString);
            return bytes.Length > 0
                ? bytes
                : throw new SparkUntrustedResponseException("deposit.fetch", "The block explorer returned an empty transaction.");
        }
        catch (FormatException ex)
        {
            throw new SparkUntrustedResponseException("deposit.fetch", "The block explorer's reply is not transaction hex.", ex);
        }
    }
}
