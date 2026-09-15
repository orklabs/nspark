using NSpark.Exceptions;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for the BOLT-11 decoder every Lightning send and invoice verification goes through.
/// The reference vectors come from the BOLT-11 specification; the synthetic ones re-encode the
/// spec data part under a different human-readable part so amount and network parsing can be
/// exercised without hand-writing bech32 checksums.
/// </summary>
[TestFixture]
public sealed class Bolt11InvoiceTests
{
    // https://github.com/lightning/bolts/blob/master/11-payment-encoding.md#examples
    private const string SpecNoAmount =
        "lnbc1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpl2pkx2ctnv5sxxmmwwd5kgetjypeh2ursdae8g6twvus8g6rfwvs8qun0dfjkxaq8rkx3yf5tcsyz3d73gafnh3cax9rn449d9p5uxz9ezhhypd0elx87sjle52x86fux2ypatgddc6k63n7erqz25le42c4u4ecky03ylcqca784w";

    private const string Spec2500u =
        "lnbc2500u1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpuaztrnwngzn3kdzw5hydlzf03qdgm2hdq27cqv3agm2awhz5se903vruatfhq77w3ls4evs3ch9zw97j25emudupq63nyw24cg27h2rspfj9srp";

    private const string Spec20mDescriptionHash =
        "lnbc20m1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqhp58yjmdan79s6qqdhdzgynm4zwqd5d7xmw5fk98klysy043l2ahrqscc6gd6ql3jrc5yzme8v4ntcewwz5cnw92tz0pc8qcuufvq7khhr8wpald05e92xw006sq94mg8v2ndf4sefvf9sygkshp5zfem29trqq2yxxz7";

    private const string SpecTestnet20m =
        "lntb20m1pvjluezhp58yjmdan79s6qqdhdzgynm4zwqd5d7xmw5fk98klysy043l2ahrqspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqfpp3x9et2e20v6pu37c5d9vax37wxq72un98kmzzhznpurw9sgl2v0nklu2g4d0keph5t7tj9tcqd8rexnd07ux4uv2cjvcqwaxgj7v4uwn5wmypjd5n69z2xm3xgksg28nwht7f6zspwp3f9t";

    private const string SpecPaymentHashHex =
        "0001020304050607080900010203040506070809000102030405060708090102";

    private const ulong SpecTimestamp = 1496314658;

    [Test]
    public void Decodes_the_spec_vector_without_amount()
    {
        var invoice = Bolt11Invoice.Decode(SpecNoAmount);

        invoice.Network.Should().Be(Bolt11Network.Mainnet);
        invoice.AmountMsat.Should().BeNull();
        invoice.PaymentHashHex.Should().Be(SpecPaymentHashHex);
        invoice.PaymentHash.Should().HaveCount(32);
        invoice.Timestamp.Should().Be(SpecTimestamp);
        invoice.ExpirySeconds.Should().Be(3600, "the x field defaults to one hour");
        invoice.Description.Should().Be("Please consider supporting this project");
        invoice.ExpiresAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds((long)SpecTimestamp + 3600));
        invoice.BelongsTo(SparkNetwork.Mainnet).Should().BeTrue();
        invoice.BelongsTo(SparkNetwork.Regtest).Should().BeFalse();
    }

    [Test]
    public void Decodes_the_spec_vector_with_amount_and_expiry()
    {
        var invoice = Bolt11Invoice.Decode(Spec2500u);

        invoice.AmountMsat.Should().Be(250_000_000UL, "2500 micro-bitcoin is 250,000 sats");
        invoice.ExpirySeconds.Should().Be(60);
        invoice.Description.Should().Be("1 cup coffee");
        invoice.PaymentHashHex.Should().Be(SpecPaymentHashHex);
    }

    [Test]
    public void Decodes_the_spec_vector_with_a_description_hash_instead_of_a_description()
    {
        var invoice = Bolt11Invoice.Decode(Spec20mDescriptionHash);

        invoice.AmountMsat.Should().Be(2_000_000_000UL);
        invoice.Description.Should().BeNull();
        invoice.PaymentHashHex.Should().Be(SpecPaymentHashHex);
    }

    [Test]
    public void Decodes_a_testnet_invoice_and_refuses_to_pair_it_with_mainnet_or_regtest()
    {
        var invoice = Bolt11Invoice.Decode(SpecTestnet20m);

        invoice.Network.Should().Be(Bolt11Network.Testnet);
        invoice.BelongsTo(SparkNetwork.Mainnet).Should().BeFalse();
        invoice.BelongsTo(SparkNetwork.Regtest).Should().BeFalse();
    }

    [Test]
    public void Accepts_upper_case_and_surrounding_whitespace()
    {
        var invoice = Bolt11Invoice.Decode("  " + SpecNoAmount.ToUpperInvariant() + "\n");

        invoice.PaymentHashHex.Should().Be(SpecPaymentHashHex);
    }

    [Test]
    public void Rejects_mixed_case()
    {
        var mixed = SpecNoAmount[..10].ToUpperInvariant() + SpecNoAmount[10..];

        var act = () => Bolt11Invoice.Decode(mixed);

        act.Should().Throw<InvalidBolt11Exception>().WithMessage("*mix*case*");
    }

    [Test]
    public void Rejects_a_corrupted_checksum()
    {
        var last = SpecNoAmount[^1];
        var corrupted = SpecNoAmount[..^1] + (last == 'q' ? 'p' : 'q');

        var act = () => Bolt11Invoice.Decode(corrupted);

        act.Should().Throw<InvalidBolt11Exception>().WithMessage("*checksum*");
    }

    [Test]
    public void Rejects_a_bech32m_checksum()
    {
        var (hrp, words, _) = Bech32mHelper.DecodeWords(SpecNoAmount);
        var bech32m = Bech32mHelper.EncodeWords(hrp, words, Bech32Variant.Bech32m);

        var act = () => Bolt11Invoice.Decode(bech32m);

        act.Should().Throw<InvalidBolt11Exception>().WithMessage("*bech32m*");
    }

    [TestCase("lnbc25m", 2_500_000_000UL, "Mainnet")]
    [TestCase("lnbc20m", 2_000_000_000UL, "Mainnet")]
    [TestCase("lnbc1500n", 150_000UL, "Mainnet")]
    [TestCase("lnbc2500u", 250_000_000UL, "Mainnet")]
    [TestCase("lnbc1", 100_000_000_000UL, "Mainnet")]
    [TestCase("lnbc10p", 1UL, "Mainnet")]
    [TestCase("lnbc1p", 0UL, "Mainnet")]
    [TestCase("lnbcrt1u", 100_000UL, "Regtest")]
    [TestCase("lntbs1u", 100_000UL, "Signet")]
    [TestCase("lntb1u", 100_000UL, "Testnet")]
    public void Parses_amounts_and_networks_from_the_human_readable_part(string hrp, ulong expectedMsat, string network)
    {
        var expectedNetwork = Enum.Parse<Bolt11Network>(network);
        if (hrp == "lnbc1p")
        {
            var act = () => Bolt11Invoice.Decode(Reencode(hrp));
            act.Should().Throw<InvalidBolt11Exception>().WithMessage("*sub-millisatoshi*");
            return;
        }

        var invoice = Bolt11Invoice.Decode(Reencode(hrp));

        invoice.AmountMsat.Should().Be(expectedMsat);
        invoice.Network.Should().Be(expectedNetwork);
    }

    [TestCase("lnbc0m", "*leading zero*")]
    [TestCase("lnbc25z", "*multiplier*")]
    [TestCase("lnbc12345678901234567890m", "*out of range*")]
    [TestCase("lnbc22000000", "*supply*")]
    [TestCase("lnbc999999999999m", "*supply*")]
    [TestCase("lnxx1u", "*currency prefix*")]
    [TestCase("abc1u", "*not a lightning invoice*")]
    [TestCase("lnbc1x2m", "*amount*")]
    public void Rejects_hostile_amounts_and_unknown_prefixes(string hrp, string expectedMessage)
    {
        var act = () => Bolt11Invoice.Decode(Reencode(hrp));

        act.Should().Throw<InvalidBolt11Exception>().WithMessage(expectedMessage);
    }

    [Test]
    public void Rejects_an_invoice_without_a_payment_hash()
    {
        // Timestamp words plus a zero signature and no tagged fields at all.
        var words = new byte[7 + 104];
        var encoded = Bech32mHelper.EncodeWords("lnbc", words, Bech32Variant.Bech32);

        var act = () => Bolt11Invoice.Decode(encoded);

        act.Should().Throw<InvalidBolt11Exception>().WithMessage("*payment hash*");
    }

    [Test]
    public void Rejects_a_tagged_field_that_runs_past_the_end()
    {
        // One tagged field claiming 52 words of data but carrying none.
        var words = new byte[7 + 3 + 104];
        words[7] = 1;
        words[8] = 1;
        words[9] = 20; // 1 * 32 + 20 = 52
        var encoded = Bech32mHelper.EncodeWords("lnbc", words, Bech32Variant.Bech32);

        var act = () => Bolt11Invoice.Decode(encoded);

        act.Should().Throw<InvalidBolt11Exception>().WithMessage("*runs past*");
    }

    [Test]
    public void Rejects_a_data_part_shorter_than_timestamp_plus_signature()
    {
        var encoded = Bech32mHelper.EncodeWords("lnbc", new byte[50], Bech32Variant.Bech32);

        var act = () => Bolt11Invoice.Decode(encoded);

        act.Should().Throw<InvalidBolt11Exception>().WithMessage("*too short*");
    }

    [Test]
    public void Reports_the_payment_request_on_the_exception()
    {
        var act = () => Bolt11Invoice.Decode("lnbc1notaninvoice");

        act.Should().Throw<InvalidBolt11Exception>()
            .Which.PaymentRequest.Should().Be("lnbc1notaninvoice");
    }

    [TestCase(250_000_000UL, null, 250_000L)]
    [TestCase(1500UL, null, 2L, TestName = "sub-sat amounts round up")]
    [TestCase(1500UL, 2L, 2L, TestName = "a matching caller amount is accepted")]
    [TestCase(null, 21L, 21L, TestName = "amountless invoices take the caller amount")]
    public void ResolvePaymentAmountSats_resolves_the_amount_to_pay(ulong? invoiceMsat, long? requested, long expected)
    {
        LightningValidator.ResolvePaymentAmountSats(invoiceMsat, requested).Should().Be(expected);
    }

    [Test]
    public void ResolvePaymentAmountSats_refuses_a_caller_amount_that_contradicts_the_invoice()
    {
        var act = () => LightningValidator.ResolvePaymentAmountSats(1500, 1);

        act.Should().Throw<ArgumentException>().WithMessage("*does not match*");
    }

    [Test]
    public void ResolvePaymentAmountSats_requires_an_amount_for_amountless_invoices()
    {
        var missing = () => LightningValidator.ResolvePaymentAmountSats(null, null);
        var zero = () => LightningValidator.ResolvePaymentAmountSats(null, 0);

        missing.Should().Throw<ArgumentException>().WithMessage("*pass amountSats*");
        zero.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void NormalizeTransferId_canonicalises_uuids_and_rejects_anything_else()
    {
        LightningValidator.NormalizeTransferId(null).Should().BeNull();
        LightningValidator.NormalizeTransferId("0192A1B2-C3D4-7E5F-8A9B-0C1D2E3F4A5B")
            .Should().Be("0192a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

        var act = () => LightningValidator.NormalizeTransferId("nope");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void VerifyCreatedInvoice_accepts_the_invoice_we_asked_for()
    {
        var hash = Convert.FromHexString(SpecPaymentHashHex);

        var amountless = LightningValidator.VerifyCreatedInvoice(SpecNoAmount, SpecPaymentHashHex, hash, 0, SparkNetwork.Mainnet);
        var withAmount = LightningValidator.VerifyCreatedInvoice(Spec2500u, null, hash, 250_000, SparkNetwork.Mainnet);

        amountless.AmountMsat.Should().BeNull();
        withAmount.AmountMsat.Should().Be(250_000_000UL);
    }

    [Test]
    public void VerifyCreatedInvoice_rejects_a_different_payment_hash()
    {
        var other = new byte[32];
        other[0] = 0xFF;

        var act = () => LightningValidator.VerifyCreatedInvoice(SpecNoAmount, null, other, 0, SparkNetwork.Mainnet);

        act.Should().Throw<SparkUntrustedResponseException>().WithMessage("*payment hash*");
    }

    [Test]
    public void VerifyCreatedInvoice_rejects_a_reported_hash_that_disagrees_with_ours()
    {
        var hash = Convert.FromHexString(SpecPaymentHashHex);

        var act = () => LightningValidator.VerifyCreatedInvoice(SpecNoAmount, new string('f', 64), hash, 0, SparkNetwork.Mainnet);

        act.Should().Throw<SparkUntrustedResponseException>().WithMessage("*reported payment hash*");
    }

    [Test]
    public void VerifyCreatedInvoice_rejects_a_different_amount()
    {
        var hash = Convert.FromHexString(SpecPaymentHashHex);

        var tooMuch = () => LightningValidator.VerifyCreatedInvoice(Spec2500u, null, hash, 250_001, SparkNetwork.Mainnet);
        var unexpected = () => LightningValidator.VerifyCreatedInvoice(Spec2500u, null, hash, 0, SparkNetwork.Mainnet);

        tooMuch.Should().Throw<SparkUntrustedResponseException>().WithMessage("*amount*");
        unexpected.Should().Throw<SparkUntrustedResponseException>().WithMessage("*amountless*");
    }

    [Test]
    public void VerifyCreatedInvoice_rejects_another_network_and_undecodable_strings()
    {
        var hash = Convert.FromHexString(SpecPaymentHashHex);

        var network = () => LightningValidator.VerifyCreatedInvoice(SpecTestnet20m, null, hash, 2_000_000, SparkNetwork.Mainnet);
        var garbage = () => LightningValidator.VerifyCreatedInvoice("lnbc1garbage", null, hash, 0, SparkNetwork.Mainnet);

        network.Should().Throw<SparkUntrustedResponseException>().WithMessage("*wallet is on*");
        garbage.Should().Throw<SparkUntrustedResponseException>().WithMessage("*does not decode*");
    }

    /// <summary>The spec data part re-encoded under another HRP, so only the HRP parsing changes.</summary>
    private static string Reencode(string hrp)
    {
        var (_, words, _) = Bech32mHelper.DecodeWords(SpecNoAmount);
        return Bech32mHelper.EncodeWords(hrp, words, Bech32Variant.Bech32);
    }
}
