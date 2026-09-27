using NSpark.Exceptions;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for the BOLT-11 decoder every Lightning send and invoice verification goes through.
/// The reference vectors come from the BOLT-11 specification (its current examples, which all
/// carry a payment secret); the synthetic ones re-encode a spec data part under another
/// human-readable part so amount and network parsing can be exercised without hand-writing
/// bech32 checksums.
/// </summary>
[TestFixture]
public sealed class Bolt11InvoiceTests
{
    // https://github.com/lightning/bolts/blob/master/11-payment-encoding.md#examples
    internal const string Donation =
        "lnbc1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpl2pkx2ctnv5sxxmmwwd5kgetjypeh2ursdae8g6twvus8g6rfwvs8qun0dfjkxaq9qrsgq357wnc5r2ueh7ck6q93dj32dlqnls087fxdwk8qakdyafkq3yap9us6v52vjjsrvywa6rt52cm9r9zqt8r2t7mlcwspyetp5h2tztugp9lfyql";

    internal const string Coffee2500u =
        "lnbc2500u1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpu9qrsgquk0rl77nj30yxdy8j9vdx85fkpmdla2087ne0xh8nhedh8w27kyke0lp53ut353s06fv3qfegext0eh0ymjpf39tuven09sam30g4vgpfna3rh";

    internal const string List20m =
        "lnbc20m1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqhp58yjmdan79s6qqdhdzgynm4zwqd5d7xmw5fk98klysy043l2ahrqs9qrsgq7ea976txfraylvgzuxs8kgcw23ezlrszfnh8r6qtfpr6cxga50aj6txm9rxrydzd06dfeawfk6swupvz4erwnyutnjq7x39ymw6j38gp7ynn44";

    internal const string Testnet20m =
        "lntb20m1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygshp58yjmdan79s6qqdhdzgynm4zwqd5d7xmw5fk98klysy043l2ahrqspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqfpp3x9et2e20v6pu37c5d9vax37wxq72un989qrsgqdj545axuxtnfemtpwkc45hx9d2ft7x04mt8q7y6t0k2dge9e7h8kpy9p34ytyslj3yu569aalz2xdk8xkd7ltxqld94u8h2esmsmacgpghe9k8";

    internal const string Pico =
        "lnbc9678785340p1pwmna7lpp5gc3xfm08u9qy06djf8dfflhugl6p7lgza6dsjxq454gxhj9t7a0sd8dgfkx7cmtwd68yetpd5s9xar0wfjn5gpc8qhrsdfq24f5ggrxdaezqsnvda3kkum5wfjkzmfqf3jkgem9wgsyuctwdus9xgrcyqcjcgpzgfskx6eqf9hzqnteypzxz7fzypfhg6trddjhygrcyqezcgpzfysywmm5ypxxjemgw3hxjmn8yptk7untd9hxwg3q2d6xjcmtv4ezq7pqxgsxzmnyyqcjqmt0wfjjq6t5v4khxsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygsxqyjw5qcqp2rzjq0gxwkzc8w6323m55m4jyxcjwmy7stt9hwkwe2qxmy8zpsgg7jcuwz87fcqqeuqqqyqqqqlgqqqqn3qq9q9qrsgqrvgkpnmps664wgkp43l22qsgdw4ve24aca4nymnxddlnp8vh9v2sdxlu5ywdxefsfvm0fq3sesf08uf6q9a2ke0hc9j6z6wlxg5z5kqpu2v9wz";

    internal const string Upper25m =
        "LNBC25M1PVJLUEZPP5QQQSYQCYQ5RQWZQFQQQSYQCYQ5RQWZQFQQQSYQCYQ5RQWZQFQYPQDQ5VDHKVEN9V5SXYETPDEESSP5ZYG3ZYG3ZYG3ZYG3ZYG3ZYG3ZYG3ZYG3ZYG3ZYG3ZYG3ZYG3ZYGS9Q5SQQQQQQQQQQQQQQQQSGQ2A25DXL5HRNTDTN6ZVYDT7D66HYZSYHQS4WDYNAVYS42XGL6SGX9C4G7ME86A27T07MDTFRY458RTJR0V92CNMSWPSJSCGT2VCSE3SGPZ3UAPA";

    internal const string Metadata10m =
        "lnbc10m1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdp9wpshjmt9de6zqmt9w3skgct5vysxjmnnd9jx2mq8q8a04uqsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygs9q2gqqqqqqsgq7hf8he7ecf7n4ffphs6awl9t6676rrclv9ckg3d3ncn7fct63p6s365duk5wrk202cfy3aj5xnnp5gs3vrdvruverwwq7yzhkf5a3xqpd05wjc";

    /// <summary>
    /// The reference SDK's vector (<c>bolt11-spark.test.ts</c>): a mainnet invoice whose sentinel
    /// route hint carries the receiver's Spark identity.
    /// </summary>
    internal const string SparkRouteHintInvoice =
        "lnbc13u1p5xalmkpp5z79uwgne7znz76plf0q4zxmh8t3wke6gsnm5kn67h4satpgflkmssp5azht5ywc5s4m40jf9h0nwlr959a34n72pns50lfm93zz8lvs7nqsxq9z0rgqnp4q0p92sfan5vj2a4f8q3gsfsy8qp60maeuxz858c5x0hvt5u0p0h9jr9yqtqd37k2ya0pv8pqeyjs4lklcexjyw600g9qqp62r4j0ph8fcmlfwqqqqzfv7u6g85qqqqqqqqqqthqq9qpz9cat0ndmwmfx036y9fxfhdufta3mn95ta9xw34ynlwg7euxjck85ysq0gfqqqqq7u6egqrhxk2qqn3qqcqzpgdq2w3jhxap3xv9qyyssqfahd64hu0lffl7cw2e4evu400s09yeupypvnfjvjjyq8rh05y9gzd3dqnmkvuyd9jszyhmdey75dujz8xaufgahsxkqktf3wxny8ghsqpk4mg8";

    /// <summary>The spec's <c>lnbc20m</c> vector without its <c>s</c> field (the reference SDK's "invalid payment secret" vector).</summary>
    private const string MissingPaymentSecret =
        "lnbc20m1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqhp58yjmdan79s6qqdhdzgynm4zwqd5d7xmw5fk98klysy043l2ahrqs9qrsgq7ea976txfraylvgzuxs8kgcw23ezlrszfnh8r6qtfpr6cxga50aj6txm9rxrydzd06dfeawfk6swupvz4erwnyutnjq7x39ymw6j38gp49qdkj";

    internal const string SpecPaymentHashHex =
        "0001020304050607080900010203040506070809000102030405060708090102";

    private const ulong SpecTimestamp = 1496314658;

    /// <summary>A valid invoice's data part re-encoded under <paramref name="hrp"/>, so only the prefix under test changes.</summary>
    internal static string WithHrp(string hrp, string invoice = Coffee2500u, Bech32Variant variant = Bech32Variant.Bech32)
    {
        var (_, words, _) = Bech32mHelper.DecodeWords(invoice);
        return Bech32mHelper.EncodeWords(hrp, words, variant);
    }

    [Test]
    public void Decodes_the_spec_vector_without_amount()
    {
        var invoice = Bolt11Invoice.Decode(Donation);

        invoice.Network.Should().Be(Bolt11Network.Mainnet);
        invoice.AmountMsat.Should().BeNull();
        invoice.PaymentHashHex.Should().Be(SpecPaymentHashHex);
        invoice.Timestamp.Should().Be(SpecTimestamp);
        invoice.ExpirySeconds.Should().Be(3600, "the x field defaults to one hour");
        invoice.PaymentSecret.Should().Equal(Enumerable.Repeat((byte)0x11, 32));
        invoice.Description.Should().Be("Please consider supporting this project");
        invoice.ExpiresAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds((long)SpecTimestamp + 3600));
        invoice.SparkFallback.Should().BeNull();
        invoice.BelongsTo(SparkNetwork.Mainnet).Should().BeTrue();
        invoice.BelongsTo(SparkNetwork.Regtest).Should().BeFalse();
    }

    [Test]
    public void Decodes_the_spec_vectors_with_amounts()
    {
        var coffee = Bolt11Invoice.Decode(Coffee2500u);
        coffee.AmountMsat.Should().Be(250_000_000UL, "2500 micro-bitcoin is 250,000 sats");
        LightningValidator.ResolvePaymentAmountSats(coffee.AmountMsat, null).Should().Be(250_000);
        coffee.ExpirySeconds.Should().Be(60);
        coffee.Description.Should().Be("1 cup coffee");
        coffee.ExpiresAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds((long)SpecTimestamp + 60));

        var list = Bolt11Invoice.Decode(List20m);
        list.AmountMsat.Should().Be(2_000_000_000UL);
        list.Description.Should().BeNull("it carries a description hash instead");
        list.PaymentHashHex.Should().Be(SpecPaymentHashHex);

        var pico = Bolt11Invoice.Decode(Pico);
        pico.AmountMsat.Should().Be(967_878_534UL);
        LightningValidator.ResolvePaymentAmountSats(pico.AmountMsat, null).Should().Be(967_879);
        pico.PaymentHashHex.Should().Be("462264ede7e14047e9b249da94fefc47f41f7d02ee9b091815a5506bc8abf75f");
        pico.Timestamp.Should().Be(1_572_468_703UL);
        pico.ExpirySeconds.Should().Be(604_800UL);

        var upper = Bolt11Invoice.Decode(Upper25m);
        upper.AmountMsat.Should().Be(2_500_000_000UL);
        upper.PaymentHashHex.Should().Be(SpecPaymentHashHex);

        Bolt11Invoice.Decode(Metadata10m).AmountMsat.Should().Be(1_000_000_000UL);
    }

    [Test]
    public void Decodes_a_testnet_invoice_and_refuses_to_pair_it_with_mainnet_or_regtest()
    {
        var invoice = Bolt11Invoice.Decode(Testnet20m);

        invoice.Network.Should().Be(Bolt11Network.Testnet);
        invoice.AmountMsat.Should().Be(2_000_000_000UL);
        invoice.BelongsTo(SparkNetwork.Mainnet).Should().BeFalse();
        invoice.BelongsTo(SparkNetwork.Regtest).Should().BeFalse();
    }

    [Test]
    public void Accepts_upper_case_and_surrounding_whitespace()
    {
        Bolt11Invoice.Decode("  " + Donation.ToUpperInvariant() + "\n").PaymentHashHex.Should().Be(SpecPaymentHashHex);
        Bolt11Invoice.Decode("  " + Coffee2500u + "\n").Should().BeEquivalentTo(Bolt11Invoice.Decode(Coffee2500u));
    }

    [Test]
    public void A_Spark_identity_in_the_sentinel_route_hint_is_decoded_as_in_the_reference_SDKs_vector()
    {
        var invoice = Bolt11Invoice.Decode(SparkRouteHintInvoice);

        invoice.SparkFallback.Should().Be("0222e3ab7cdbb76d267c7442a4c9bb7895f63b9968be94ce8d493fb91ecf0d2c58");
        invoice.AmountMsat.Should().Be(1_300_000UL);
        invoice.PaymentHashHex.Should().Be("178bc72279f0a62f683f4bc1511b773ae2eb674884f74b4f5ebd61d58509fdb7");
        // An on-chain fallback address (version 17, P2PKH) is not a Spark fallback.
        Bolt11Invoice.Decode(Testnet20m).SparkFallback.Should().BeNull();
        Bolt11Invoice.Decode(Coffee2500u).SparkFallback.Should().BeNull();
    }

    [Test]
    public void A_Spark_invoice_in_a_version_31_fallback_address_field_is_decoded()
    {
        const string sparkInvoice = "spark1pgssyut2gu37y00dg7pf5d2uc6nm00tdu4xujpmfykg24mjy9rzvt4w3me9q6g";
        var (hrp, words, _) = Bech32mHelper.DecodeWords(Coffee2500u);
        byte[] fieldWords = [31, .. Bech32mHelper.ConvertBits(System.Text.Encoding.UTF8.GetBytes(sparkInvoice), 8, 5, pad: true)!];
        byte[] tagged = [9, (byte)(fieldWords.Length / 32), (byte)(fieldWords.Length % 32), .. fieldWords];
        // Tagged fields follow the 7-word timestamp; their order does not matter.
        var rebuilt = Bech32mHelper.EncodeWords(hrp, [.. words[..7], .. tagged, .. words[7..]], Bech32Variant.Bech32);

        Bolt11Invoice.Decode(rebuilt).SparkFallback.Should().Be(sparkInvoice);
    }

    [TestCase("lnbc2500u1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpquwpc4curk03c9wlrswe78q4eyqc7d8d0xqzpuyk0sg5g70me25alkluzd2x62aysf2pyy8edtjeevuv4p2d5p76r4zkmneet7uvyakky2zr4cusd45tftc9c5fh0nnqpnl2jfll544esqchsrnt", TestName = "Refuses an invalid bech32 checksum")]
    [TestCase("pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpquwpc4curk03c9wlrswe78q4eyqc7d8d0xqzpuyk0sg5g70me25alkluzd2x62aysf2pyy8edtjeevuv4p2d5p76r4zkmneet7uvyakky2zr4cusd45tftc9c5fh0nnqpnl2jfll544esqchsrny", TestName = "Refuses a bech32 string without a separator")]
    [TestCase("LNBC2500u1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpquwpc4curk03c9wlrswe78q4eyqc7d8d0xqzpuyk0sg5g70me25alkluzd2x62aysf2pyy8edtjeevuv4p2d5p76r4zkmneet7uvyakky2zr4cusd45tftc9c5fh0nnqpnl2jfll544esqchsrny", TestName = "Refuses mixed case")]
    [TestCase("lnbc1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpl2pkx2ctnv5sxxmmwwd5kgetjypeh2ursdae8g6na6hlh", TestName = "Refuses a string that is too short")]
    [TestCase("lnbc2500x1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpusp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygs9qrsgqrrzc4cvfue4zp3hggxp47ag7xnrlr8vgcmkjxk3j5jqethnumgkpqp23z9jclu3v0a7e0aruz366e9wqdykw6dxhdzcjjhldxq0w6wgqcnu43j", TestName = "Refuses an invalid multiplier")]
    [TestCase("lnbc2500000001p1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpusp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygs9qrsgq0lzc236j96a95uv0m3umg28gclm5lqxtqqwk32uuk4k6673k6n5kfvx3d2h8s295fad45fdhmusm8sjudfhlf6dcsxmfvkeywmjdkxcp99202x", TestName = "Refuses sub-millisatoshi precision")]
    [TestCase(MissingPaymentSecret, TestName = "Refuses an invoice without a payment secret")]
    [TestCase("lnbc", TestName = "Refuses a bare prefix")]
    [TestCase("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4", TestName = "Refuses an on-chain address")]
    public void Refuses_the_specifications_invalid_invoices(string invoice)
    {
        var act = () => Bolt11Invoice.Decode(invoice);

        act.Should().Throw<InvalidBolt11Exception>();
    }

    [Test]
    public void Refuses_an_empty_string()
    {
        var act = () => Bolt11Invoice.Decode("  ");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void An_invoice_without_a_payment_secret_is_refused_as_BOLT_11_readers_and_the_reference_SDK_do()
    {
        var missing = () => Bolt11Invoice.Decode(MissingPaymentSecret);
        missing.Should().Throw<InvalidBolt11Exception>().WithMessage("*payment secret*");

        // An s field of the wrong length is skipped as BOLT-11 requires, which leaves none.
        var (hrp, words, _) = Bech32mHelper.DecodeWords(Coffee2500u);
        var fields = words[7..^104];
        var rebuilt = new List<byte>();
        var position = 0;
        while (position + 3 <= fields.Length)
        {
            var length = (fields[position + 1] * 32) + fields[position + 2];
            var field = fields[position..(position + 3 + length)];
            // Shorten the 52-word payment secret to 51 words.
            rebuilt.AddRange(field[0] == 16 ? [16, 1, 19, .. field[3..^1]] : field);
            position += 3 + length;
        }

        var shortSecret = Bech32mHelper.EncodeWords(hrp, [.. words[..7], .. rebuilt, .. words[^104..]], Bech32Variant.Bech32);
        var act = () => Bolt11Invoice.Decode(shortSecret);
        act.Should().Throw<InvalidBolt11Exception>().WithMessage("*payment secret*");
    }

    [Test]
    public void Rejects_a_corrupted_checksum()
    {
        var last = Donation[^1];
        var corrupted = Donation[..^1] + (last == 'q' ? 'p' : 'q');

        var act = () => Bolt11Invoice.Decode(corrupted);

        act.Should().Throw<InvalidBolt11Exception>().WithMessage("*checksum*");
    }

    [Test]
    public void Rejects_a_bech32m_checksum()
    {
        var act = () => Bolt11Invoice.Decode(WithHrp("lnbc2500u", Coffee2500u, Bech32Variant.Bech32m));

        act.Should().Throw<InvalidBolt11Exception>().WithMessage("*bech32m*");
    }

    [TestCase("lnbc25m", 2_500_000_000UL, "Mainnet")]
    [TestCase("lnbc20m", 2_000_000_000UL, "Mainnet")]
    [TestCase("lnbc1500n", 150_000UL, "Mainnet")]
    [TestCase("lnbc2500u", 250_000_000UL, "Mainnet")]
    [TestCase("lnbc1", 100_000_000_000UL, "Mainnet")]
    [TestCase("lnbc21000000", 21_000_000UL * 100_000_000_000UL, "Mainnet")]
    [TestCase("lnbc10n", 1_000UL, "Mainnet")]
    [TestCase("lnbc10p", 1UL, "Mainnet")]
    [TestCase("lnbcrt1u", 100_000UL, "Regtest")]
    [TestCase("lnbcrt2500u", 250_000_000UL, "Regtest")]
    [TestCase("lntbs1u", 100_000UL, "Signet")]
    [TestCase("lntb1u", 100_000UL, "Testnet")]
    public void Parses_amounts_and_networks_from_the_human_readable_part(string hrp, ulong expectedMsat, string network)
    {
        var invoice = Bolt11Invoice.Decode(WithHrp(hrp));

        invoice.AmountMsat.Should().Be(expectedMsat);
        invoice.Network.Should().Be(Enum.Parse<Bolt11Network>(network));
    }

    [TestCase("lnbc1p", "*sub-millisatoshi*")]
    [TestCase("lnbc0m", "*leading zero*")]
    [TestCase("lnbc0250u", "*leading zero*")]
    [TestCase("lnbc0", "*leading zero*")]
    [TestCase("lnbc25z", "*multiplier*")]
    [TestCase("lnbc12345678901234567890m", "*out of range*")]
    [TestCase("lnbc99999999999999999999u", "*out of range*")]
    [TestCase("lnbc9223372036854775807m", "*supply*")]
    [TestCase("lnbc18446744073709551615", "*out of range*")]
    [TestCase("lnbc21000001", "*supply*")]
    [TestCase("lnbc22000000", "*supply*")]
    [TestCase("lnbc999999999999m", "*supply*")]
    [TestCase("lnbc25.0m", "*amount*")]
    [TestCase("lnbc-1m", "*amount*")]
    [TestCase("lnbc2500um", "*amount*")]
    [TestCase("lnbc1x2m", "*amount*")]
    [TestCase("lnxx2500u", "*currency prefix*")]
    [TestCase("ln2500u", "*currency prefix*")]
    [TestCase("bc2500u", "*not a lightning invoice*")]
    [TestCase("abc1u", "*not a lightning invoice*")]
    public void Rejects_hostile_amounts_and_unknown_prefixes(string hrp, string expectedMessage)
    {
        var act = () => Bolt11Invoice.Decode(WithHrp(hrp));

        act.Should().Throw<InvalidBolt11Exception>().WithMessage(expectedMessage);
    }

    [Test]
    public void Matches_the_wallets_network()
    {
        var mainnet = Bolt11Invoice.Decode(Coffee2500u);
        mainnet.BelongsTo(SparkNetwork.Mainnet).Should().BeTrue();
        mainnet.BelongsTo(SparkNetwork.Regtest).Should().BeFalse();

        var regtest = Bolt11Invoice.Decode(WithHrp("lnbcrt2500u"));
        regtest.BelongsTo(SparkNetwork.Regtest).Should().BeTrue();
        regtest.BelongsTo(SparkNetwork.Mainnet).Should().BeFalse();
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
}
