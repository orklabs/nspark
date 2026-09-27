using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using NSpark.Exceptions;
using NSpark.GraphQL;
using NSpark.Proto;
using NSpark.Services;
using NSpark.Signer;
using NSpark.UnitTests.TestSupport;
using uniffi.spark_frost;

namespace NSpark.UnitTests.Services;

/// <summary>Client-side checks on Lightning sends and on invoices the SSP creates.</summary>
[TestFixture]
public class LightningValidatorTests
{
    private static readonly byte[] s_specHash = Convert.FromHexString(Bolt11InvoiceTests.SpecPaymentHashHex);

    [TestCase(250_000_000UL, null, 250_000L)]
    [TestCase(250_000_000UL, 250_000L, 250_000L, TestName = "a matching caller amount is accepted")]
    [TestCase(1500UL, null, 2L, TestName = "sub-sat amounts round up")]
    [TestCase(null, 500L, 500L, TestName = "amountless invoices take the caller amount")]
    public void The_amount_comes_from_the_invoice_or_from_the_caller_only_for_amountless_invoices(ulong? invoiceMsat, long? requested, long expected)
    {
        LightningValidator.ResolvePaymentAmountSats(invoiceMsat, requested).Should().Be(expected);
    }

    [Test]
    public void A_caller_amount_that_contradicts_the_invoice_or_a_missing_amount_is_refused()
    {
        var contradicts = () => LightningValidator.ResolvePaymentAmountSats(250_000_000, 1);
        contradicts.Should().Throw<ArgumentException>().WithMessage("*does not match*");
        var missing = () => LightningValidator.ResolvePaymentAmountSats(null, null);
        missing.Should().Throw<ArgumentException>().WithMessage("*pass amountSats*");
        var zero = () => LightningValidator.ResolvePaymentAmountSats(null, 0);
        zero.Should().Throw<ArgumentOutOfRangeException>();
        var negative = () => LightningValidator.ResolvePaymentAmountSats(null, -5);
        negative.Should().Throw<ArgumentOutOfRangeException>();
        var zeroInvoice = () => LightningValidator.ResolvePaymentAmountSats(0, null);
        zeroInvoice.Should().Throw<InvalidBolt11Exception>();
    }

    [Test]
    public void An_SSP_created_invoice_must_carry_our_payment_hash_amount_and_network()
    {
        LightningValidator.VerifyCreatedInvoice(
                Bolt11InvoiceTests.Coffee2500u, Bolt11InvoiceTests.SpecPaymentHashHex, s_specHash, 250_000, SparkNetwork.Mainnet)
            .AmountMsat.Should().Be(250_000_000UL);
        // The SSP's reported hash is optional and case-insensitive.
        LightningValidator.VerifyCreatedInvoice(Bolt11InvoiceTests.Coffee2500u, null, s_specHash, 250_000, SparkNetwork.Mainnet);
        LightningValidator.VerifyCreatedInvoice(
            Bolt11InvoiceTests.Coffee2500u, Bolt11InvoiceTests.SpecPaymentHashHex.ToUpperInvariant(), s_specHash, 250_000, SparkNetwork.Mainnet);
        // Amountless invoice for an amountless request.
        LightningValidator.VerifyCreatedInvoice(Bolt11InvoiceTests.Donation, null, s_specHash, 0, SparkNetwork.Mainnet)
            .AmountMsat.Should().BeNull();

        var wrongHash = Enumerable.Repeat((byte)0xAB, 32).ToArray();
        var refusals = new (string Label, Func<Bolt11Invoice> Verify, string Message)[]
        {
            ("another payment hash", () => LightningValidator.VerifyCreatedInvoice(Bolt11InvoiceTests.Coffee2500u, null, wrongHash, 250_000, SparkNetwork.Mainnet), "*payment hash*"),
            ("a reported hash that is not ours", () => LightningValidator.VerifyCreatedInvoice(Bolt11InvoiceTests.Coffee2500u, Convert.ToHexString(wrongHash), s_specHash, 250_000, SparkNetwork.Mainnet), "*reported payment hash*"),
            ("another amount", () => LightningValidator.VerifyCreatedInvoice(Bolt11InvoiceTests.Coffee2500u, null, s_specHash, 250_001, SparkNetwork.Mainnet), "*amount*"),
            ("an amount on an amountless request", () => LightningValidator.VerifyCreatedInvoice(Bolt11InvoiceTests.Coffee2500u, null, s_specHash, 0, SparkNetwork.Mainnet), "*amountless*"),
            ("no amount on a request with one", () => LightningValidator.VerifyCreatedInvoice(Bolt11InvoiceTests.Donation, null, s_specHash, 100, SparkNetwork.Mainnet), "*amount*"),
            ("another network", () => LightningValidator.VerifyCreatedInvoice(Bolt11InvoiceTests.Coffee2500u, null, s_specHash, 250_000, SparkNetwork.Regtest), "*wallet is on*"),
            ("a testnet invoice", () => LightningValidator.VerifyCreatedInvoice(Bolt11InvoiceTests.Testnet20m, null, s_specHash, 2_000_000, SparkNetwork.Mainnet), "*wallet is on*"),
            ("garbage", () => LightningValidator.VerifyCreatedInvoice("lnbc1garbage", null, s_specHash, 0, SparkNetwork.Mainnet), "*does not decode*"),
        };
        foreach (var (label, verify, message) in refusals)
        {
            verify.Should().Throw<SparkUntrustedResponseException>(label).WithMessage(message);
        }
    }

    [Test]
    public void An_SSP_created_invoice_that_carries_a_Spark_fallback_is_refused()
    {
        var act = () => LightningValidator.VerifyCreatedInvoice(
            Bolt11InvoiceTests.SparkRouteHintInvoice,
            null,
            Convert.FromHexString("178bc72279f0a62f683f4bc1511b773ae2eb674884f74b4f5ebd61d58509fdb7"),
            1_300,
            SparkNetwork.Mainnet);

        act.Should().Throw<SparkUntrustedResponseException>().WithMessage("*Spark fallback*");
    }

    [Test]
    public void SSP_fees_are_read_in_their_reported_unit_and_other_units_are_refused()
    {
        CurrencyAmountExtensions.ToFeeSats(2_000, "MILLISATOSHI", "fee").Should().Be(2);
        CurrencyAmountExtensions.ToFeeSats(2, "SATOSHI", "fee").Should().Be(2);
        CurrencyAmountExtensions.ToFeeSats(2_001, "MILLISATOSHI", "fee").Should().Be(3);
        CurrencyAmountExtensions.ToFeeSats(0, "MILLISATOSHI", "fee").Should().Be(0);
        foreach (var (value, unit) in new (long, string?)[] { (1, "BITCOIN"), (1, "USD"), (1, null), (-1, "SATOSHI") })
        {
            var act = () => CurrencyAmountExtensions.ToFeeSats(value, unit, "fee");
            act.Should().Throw<SparkUntrustedResponseException>($"{value} {unit ?? "(no unit)"}");
        }

        Queries.LightningSendFeeEstimate.Should().Contain("original_unit");
    }

    [Test]
    public void The_SSP_gets_amount_sats_for_an_amountless_invoice_only_and_one_of_idempotency_key_or_transfer_id()
    {
        var amountless = LightningService.LightningSendVariables("lnbc1...", 1_000, idempotencyKey: null, transferId: "t");
        amountless["amount_sats"].Should().Be(1_000L);
        amountless["user_outbound_transfer_external_id"].Should().Be("t");
        amountless.Should().NotContainKey("idempotency_key");

        var fixedAmount = LightningService.LightningSendVariables("lnbc10n1...", null, idempotencyKey: "key", transferId: "t");
        fixedAmount.Should().NotContainKey("amount_sats");
        fixedAmount["idempotency_key"].Should().Be("key");
        fixedAmount.Should().NotContainKey("user_outbound_transfer_external_id");

        // The mutation declares the variable and passes it to the input (RequestLightningSendInput).
        Mutations.RequestLightningSend.Should().Contain("$amount_sats: Long").And.Contain("amount_sats: $amount_sats");
    }

    [Test]
    public async Task Preimage_shares_are_encrypted_to_each_operators_key_at_its_own_index_whatever_the_configured_order()
    {
        // Operators listed out of order: identifiers 3, 1, 2.
        var keys = Enumerable.Range(0, 3).Select(_ => SparkFrostMethods.RandomSecretKeyBytes()).ToArray();
        var operators = new[] { 3, 1, 2 }.Select((id, i) => new SigningOperatorConfig(
                $"https://{id}.example",
                id.ToString("x64", System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToHexString(SparkFrostMethods.GetPublicKeyBytes(keys[i], compressed: true))))
            .ToArray();
        var targets = FrostSigningHelper.BuildConfiguredSoTargets(operators);
        var signer = SparkSigner.FromMnemonic(FakeOperator.Mnemonic, 0);

        var bundle = await signer.BuildEncryptedPreimageSharesAsync("0199a8f0-0000-7000-8000-000000000001", targets, 2);

        // Recover with the index each operator validates at (its identifier): only a correct
        // pairing reproduces the preimage.
        var recovered = operators.Select((config, i) =>
        {
            var share = SecretShare.Parser.ParseFrom(SparkFrostMethods.DecryptEcies(bundle.EncryptedShareBySoId[config.Identifier], keys[i]));
            share.Proofs.Should().HaveCount(2);
            return new SecretShareResult(2, FrostSigningHelper.OperatorShareIndex(config.Identifier)!.Value, share.SecretShare_.ToByteArray());
        }).ToList();
        foreach (var pair in new[] { recovered.Take(2), recovered.Skip(1), new[] { recovered[0], recovered[2] } })
        {
            SHA256.HashData(SparkFrostMethods.RecoverSecretUniffi(pair.ToList())).Should().Equal(bundle.PaymentHash);
        }

        FrostSigningHelper.OperatorShareIndex(2.ToString("x64", System.Globalization.CultureInfo.InvariantCulture)).Should().Be(2u);
        FrostSigningHelper.OperatorShareIndex(new string('0', 64)).Should().BeNull();
        FrostSigningHelper.OperatorShareIndex("01").Should().BeNull();
        FrostSigningHelper.OperatorShareIndex(new string('f', 64)).Should().BeNull();
    }

    [Test]
    public void A_Lightning_sends_preimage_swap_carries_only_the_HTLC_transfer_request()
    {
        var transferRequest = new StartTransferRequest
        {
            TransferId = "0199a8f0-0000-7000-8000-000000000001",
            ReceiverIdentityPublicKey = ByteString.CopyFrom([0x02, .. Enumerable.Repeat((byte)0xAA, 32)]),
            TransferPackage = new TransferPackage { UserSignature = ByteString.CopyFrom(1, 2, 3) },
        };

        var request = LightningService.PreimageSwapRequest(
            Enumerable.Repeat((byte)0x42, 32).ToArray(), 12, "lnbc120n1...", 2, transferRequest);

        request.TransferRequest.Should().Be(transferRequest);
        request.ReceiverIdentityPublicKey.Should().Equal(transferRequest.ReceiverIdentityPublicKey);
        request.Reason.Should().Be(InitiatePreimageSwapRequest.Types.Reason.Send);
        request.FeeSats.Should().Be(2);
        request.InvoiceAmount.ValueSats.Should().Be(12);
        request.InvoiceAmount.InvoiceAmountProof.Bolt11Invoice.Should().Be("lnbc120n1...");
    }

    [Test]
    public void The_SSP_is_offered_its_fee_estimate_as_is_and_an_estimate_above_the_cap_is_refused()
    {
        LightningValidator.SendFeeSats(0, 0).Should().Be(0);
        LightningValidator.SendFeeSats(2, 2).Should().Be(2);
        LightningValidator.SendFeeSats(2, 50).Should().Be(2);
        var overCap = () => LightningValidator.SendFeeSats(3, 2);
        overCap.Should().Throw<FeeExceedsLimitException>().Which.Should().Match<FeeExceedsLimitException>(e => e.FeeSats == 3 && e.MaxFeeSats == 2);
        var negative = () => LightningValidator.SendFeeSats(-1, 5);
        negative.Should().Throw<SparkUntrustedResponseException>();
    }

    [Test]
    public void A_payment_forwards_the_invoice_it_validated_trimmed_and_lower_case_with_mixed_case_still_refused()
    {
        var upper = new LightningPayment($" {Bolt11InvoiceTests.Upper25m}\n", 5, null, null, SparkNetwork.Mainnet);
        upper.EncodedInvoice.Should().Be(Bolt11InvoiceTests.Upper25m.ToLowerInvariant());
        Bolt11Invoice.Decode(upper.EncodedInvoice).Should().BeEquivalentTo(upper.Invoice);
        upper.AmountSats.Should().Be(2_500_000);

        new LightningPayment(Bolt11InvoiceTests.Coffee2500u, 5, null, null, SparkNetwork.Mainnet)
            .EncodedInvoice.Should().Be(Bolt11InvoiceTests.Coffee2500u);

        var mixed = () => new LightningPayment(
            "lnbc" + Bolt11InvoiceTests.Coffee2500u[4..].ToUpperInvariant(), 5, null, null, SparkNetwork.Mainnet);
        mixed.Should().Throw<InvalidBolt11Exception>();
        var otherNetwork = () => new LightningPayment(Bolt11InvoiceTests.Coffee2500u, 5, null, null, SparkNetwork.Regtest);
        otherNetwork.Should().Throw<InvalidBolt11Exception>();
        var negativeCap = () => new LightningPayment(Bolt11InvoiceTests.Coffee2500u, -1, null, null, SparkNetwork.Mainnet);
        negativeCap.Should().Throw<ArgumentOutOfRangeException>();

        // Everything the SSP and the coordinator are sent carries that form.
        LightningService.LightningSendVariables(upper.EncodedInvoice, null, null, "t")["encoded_invoice"]
            .Should().Be(Bolt11InvoiceTests.Upper25m.ToLowerInvariant());
    }

    [Test]
    public void Every_preimage_swap_carries_an_idempotency_key_the_callers_else_the_transfer_id()
    {
        LightningService.PreimageSwapIdempotencyKey(null, "t").Should().Be("t");
        LightningService.PreimageSwapIdempotencyKey("key", "t").Should().Be("key");
        // The header the operators' idempotency interceptor reads (common.IdempotencyKeyHeader).
        RenewalService.IdempotencyKeyHeader.Should().Be("x-idempotency-key");
    }

    [Test]
    public void A_resumed_send_must_be_this_wallets_HTLC_to_the_SSP_for_this_invoice_not_returned_within_the_fee_cap()
    {
        byte[] identity = [0x02, .. Enumerable.Repeat((byte)0x11, 32)];
        byte[] ssp = [0x03, .. Enumerable.Repeat((byte)0x22, 32)];
        const string transferId = LightningResumeTests.TransferId;
        var payment = new LightningPayment(Bolt11InvoiceTests.Coffee2500u, 5, null, null, SparkNetwork.Mainnet);
        var held = LightningResumeTests.HeldSend(identity, ssp, payment.Invoice.PaymentHash, 250_002);
        void Verify(PreimageRequestWithTransfer candidate) =>
            LightningValidator.VerifyHeldSend(candidate, transferId, payment, identity, ssp);

        Verify(held);
        // A send that went through resumes too: the SSP answers with the request it already has.
        var paid = held.Clone();
        paid.Status = PreimageRequestStatus.PreimageShared;
        paid.Transfer.Status = TransferStatus.Completed;
        Verify(paid);
        var atCap = held.Clone();
        atCap.Transfer.TotalValue = 250_005;
        Verify(atCap);

        var refused = new List<(string Label, PreimageRequestWithTransfer Candidate)>();
        void Refuse(string label, Action<PreimageRequestWithTransfer> change)
        {
            var candidate = held.Clone();
            change(candidate);
            refused.Add((label, candidate));
        }

        Refuse("another invoice", c => c.PaymentHash = ByteString.CopyFrom(Enumerable.Repeat((byte)0xAB, 32).ToArray()));
        Refuse("another transfer id", c => c.Transfer.Id = "0199a8f0-0000-7000-8000-000000000002");
        Refuse("HTLC to someone else", c => c.ReceiverIdentityPubkey = ByteString.CopyFrom(identity));
        Refuse("transfer to someone else", c => c.Transfer.ReceiverIdentityPublicKey = ByteString.CopyFrom(identity));
        Refuse("someone else's HTLC", c => c.SenderIdentityPubkey = ByteString.CopyFrom(ssp));
        Refuse("not a preimage swap", c => c.Transfer.Type = TransferType.Transfer);
        Refuse("no transfer", c => c.Transfer = null);
        Refuse("HTLC returned", c => c.Status = PreimageRequestStatus.Returned);
        Refuse("transfer returned", c => c.Transfer.Status = TransferStatus.Returned);
        Refuse("transfer expired", c => c.Transfer.Status = TransferStatus.Expired);
        Refuse("less than the amount", c => c.Transfer.TotalValue = 249_999);
        foreach (var (label, candidate) in refused)
        {
            var act = () => Verify(candidate);
            act.Should().Throw<ArgumentException>(label);
        }

        // Above the cap the SSP would keep more than maxFeeSats.
        var overCap = held.Clone();
        overCap.Transfer.TotalValue = 250_006;
        var over = () => Verify(overCap);
        over.Should().Throw<FeeExceedsLimitException>().Which.Should().Match<FeeExceedsLimitException>(e => e.FeeSats == 6 && e.MaxFeeSats == 5);
    }

    [Test]
    public void Resumable_transfer_ids_must_be_UUIDs_and_are_normalised_to_lower_case()
    {
        LightningValidator.NormalizeTransferId(null).Should().BeNull();
        LightningValidator.NormalizeTransferId("0190A1B2-C3D4-7E5F-8A9B-0C1D2E3F4A5B").Should().Be("0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
        var notUuid = () => LightningValidator.NormalizeTransferId("not-a-uuid");
        notUuid.Should().Throw<ArgumentException>();
        var empty = () => LightningValidator.NormalizeTransferId(string.Empty);
        empty.Should().Throw<ArgumentException>();
    }
}

/// <summary>
/// The resume path of <c>PayLightningInvoiceAsync</c> against the operator stand-in, whose
/// <c>query_htlc</c> reports a held send; the wallet's SSP is unreachable, so the SSP request
/// always fails.
/// </summary>
[TestFixture]
public class LightningResumeTests
{
    internal const string TransferId = "0199a8f0-0000-7000-8000-000000000001";

    /// <summary>The specification's 2500u coffee invoice under the regtest prefix (the signature is not checked client-side).</summary>
    private static string RegtestInvoice() => Bolt11InvoiceTests.WithHrp("lnbcrt2500u");

    /// <summary>A pending Lightning send as <c>query_htlc</c> reports it: the wallet's HTLC to the SSP with its preimage-swap transfer.</summary>
    internal static PreimageRequestWithTransfer HeldSend(byte[] identity, byte[] ssp, byte[] paymentHash, ulong totalValue, string transferId = TransferId) => new()
    {
        PaymentHash = ByteString.CopyFrom(paymentHash),
        SenderIdentityPubkey = ByteString.CopyFrom(identity),
        ReceiverIdentityPubkey = ByteString.CopyFrom(ssp),
        Status = PreimageRequestStatus.WaitingForPreimage,
        Transfer = new Transfer
        {
            Id = transferId,
            Type = TransferType.PreimageSwap,
            Status = TransferStatus.SenderKeyTweakPending,
            SenderIdentityPublicKey = ByteString.CopyFrom(identity),
            ReceiverIdentityPublicKey = ByteString.CopyFrom(ssp),
            TotalValue = totalValue,
        },
    };

    [Test]
    [CancelAfter(60_000)]
    public async Task Resuming_a_send_the_coordinator_holds_selects_signs_and_locks_nothing_before_asking_the_SSP(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        var invoice = RegtestInvoice();
        var paymentHash = Bolt11Invoice.Decode(invoice).PaymentHash;

        await FakeOperator.RunAsync(state, async wallet =>
        {
            state.Hold(HeldSend(wallet.IdentityPublicKey, wallet.Options.RequireSspIdentityPublicKey(), paymentHash, 250_002));
            var act = () => wallet.PayLightningInvoiceAsync(invoice, maxFeeSats: 5, transferId: TransferId.ToUpperInvariant(), ct: ct);
            (await act.Should().ThrowAsync<SparkLightningSendIncompleteException>()).Which.TransferId.Should().Be(TransferId);
        });

        // Only the lookup reached the operator: no node query, signing commitments or preimage swap.
        state.Methods.Should().Equal("query_htlc");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_preimage_swap_whose_outcome_is_unknown_reports_the_transfer_id_to_resume_and_a_refused_one_does_not(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        var request = new InitiatePreimageSwapRequest { TransferRequest = new StartTransferRequest { TransferId = TransferId } };

        await FakeOperator.RunAsync(state, async wallet =>
        {
            state.PreimageSwapError = new Status(StatusCode.Internal, "failed to commit");
            var unknown = () => wallet.SubmitPreimageSwapAsync(request, TransferId, Bolt11InvoiceTests.SpecPaymentHashHex, ct);
            (await unknown.Should().ThrowAsync<SparkLightningSendIncompleteException>()).Which.TransferId.Should().Be(TransferId);

            state.PreimageSwapError = new Status(StatusCode.FailedPrecondition, "leaf is not available");
            var refused = () => wallet.SubmitPreimageSwapAsync(request, TransferId, Bolt11InvoiceTests.SpecPaymentHashHex, ct);
            (await refused.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        });

        state.PreimageSwapIdempotencyKeys.Should().Equal(TransferId, TransferId);
    }

    [Test]
    public void Only_a_swap_the_operators_refused_before_committing_counts_as_not_taken()
    {
        StatusCode[] refused =
        [
            StatusCode.InvalidArgument, StatusCode.FailedPrecondition, StatusCode.OutOfRange, StatusCode.NotFound,
            StatusCode.AlreadyExists, StatusCode.PermissionDenied, StatusCode.Unauthenticated, StatusCode.ResourceExhausted,
            StatusCode.Aborted, StatusCode.Unimplemented,
        ];
        foreach (var code in refused)
        {
            LightningService.PreimageSwapMayHaveCommitted(new RpcException(new Status(code, string.Empty))).Should().BeFalse(code.ToString());
        }

        foreach (var code in new[] { StatusCode.Unavailable, StatusCode.DeadlineExceeded, StatusCode.Cancelled, StatusCode.Internal, StatusCode.Unknown, StatusCode.DataLoss })
        {
            LightningService.PreimageSwapMayHaveCommitted(new RpcException(new Status(code, string.Empty))).Should().BeTrue(code.ToString());
        }

        LightningService.PreimageSwapMayHaveCommitted(new OperationCanceledException()).Should().BeTrue();
        LightningService.PreimageSwapMayHaveCommitted(new SparkUntrustedResponseException("lightning.pay", "truncated")).Should().BeTrue();
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_held_send_for_another_invoice_or_above_the_fee_cap_is_refused_without_asking_the_SSP(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        var invoice = RegtestInvoice();
        var paymentHash = Bolt11Invoice.Decode(invoice).PaymentHash;
        const string otherInvoiceId = "0199a8f0-0000-7000-8000-00000000000a";
        const string overCapId = "0199a8f0-0000-7000-8000-00000000000b";

        await FakeOperator.RunAsync(state, async wallet =>
        {
            var identity = wallet.IdentityPublicKey;
            var ssp = wallet.Options.RequireSspIdentityPublicKey();
            state.Hold(HeldSend(identity, ssp, Enumerable.Repeat((byte)0xAB, 32).ToArray(), 250_002, otherInvoiceId));
            state.Hold(HeldSend(identity, ssp, paymentHash, 250_010, overCapId));

            var otherInvoice = () => wallet.PayLightningInvoiceAsync(invoice, maxFeeSats: 5, transferId: otherInvoiceId, ct: ct);
            await otherInvoice.Should().ThrowAsync<ArgumentException>().WithMessage("*another invoice*");
            var overCap = () => wallet.PayLightningInvoiceAsync(invoice, maxFeeSats: 5, transferId: overCapId, ct: ct);
            (await overCap.Should().ThrowAsync<FeeExceedsLimitException>())
                .Which.Should().Match<FeeExceedsLimitException>(e => e.FeeSats == 10 && e.MaxFeeSats == 5);
        });

        state.Methods.Should().Equal("query_htlc", "query_htlc");
    }
}
