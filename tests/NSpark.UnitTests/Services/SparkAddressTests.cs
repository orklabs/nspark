using Google.Protobuf;
using NSpark.Exceptions;
using NSpark.Proto;
using NSpark.Services;
using SparkAddress = NSpark.Services.SparkAddress;

namespace NSpark.UnitTests.Services;

/// <summary>
/// A Spark invoice is a Spark address whose payload also carries invoice fields. The SDK decodes
/// the whole payload, as the reference SDK's <c>decodeSparkAddress</c> does, and refuses to pay an
/// invoice as a plain address. Vectors from the reference SDK's <c>address.test.ts</c>.
/// </summary>
[TestFixture]
public sealed class SparkAddressTests
{
    private const string RegtestAddress = "sparkrt1pgssx5us3wkqjza8g80xz3a9gznx25msq6g3ty8exfym9q3ahcv86vsnxxdy83";
    private const string LegacyRegtestAddress = "sprt1pgssx63fa5g6uyv450rajp5ndwy9laxzpsp9e37su58jddmcdsvhgm5n7y0ud6";
    private const string MainnetAddress = "spark1pgss9qg3vdslzmt2name9v550skuvlu6lj5xt9sly90k7p0gxughlqv023jqmc";
    private const string LegacyMainnetAddress = "sp1pgssxwh6hznfdc3c0cuqrhgttder539d52a0rqcf34amge69huh664gd2ew787";

    /// <summary>A signed regtest invoice for 1000 units of a token, with memo, sender and expiry.</summary>
    private const string TokensInvoice = "sparkrt1pgssx5us3wkqjza8g80xz3a9gznx25msq6g3ty8exfym9q3ahcv86vsnzfmssqgjzqqejtaxmwj8ms9rn58574nvlq4j5zr5v4ehgnt9d4hnyggr2wgghtqfpwn5rhnpg7j5pfn92dcqdyg4jrunyjdjsg7muxraxgfn5rqgandgr3sxzrqdmew8qydzvz3qpylysylkgcaw9vpm2jzspls0qtr5kfmlwz244rvuk25w5w2sgc2pyqsraqdyp8tf57a6cn2egttaas9ms3whssenmjqt8wag3lgyvdzjskfeupt8xwwdx4agxdm9f0wefzj28jmdxqeudwcwdj9vfl9sdr65x06r0tasf5fwz2";

    private const string VectorIdentity = "0353908bac090ba741de6147a540a665537006911590f93249b2823dbe187d3213";

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string Address(byte[] payload, string hrp = "spark") => Bech32mHelper.Encode(hrp, payload);

    /// <summary>An unsigned invoice for <paramref name="amountSats"/> to <paramref name="identityPublicKey"/>, as a payee would publish one.</summary>
    internal static string SatsInvoice(byte[] identityPublicKey, ulong amountSats, SparkNetwork network)
    {
        var payload = new NSpark.Proto.SparkAddress
        {
            IdentityPublicKey = ByteString.CopyFrom(identityPublicKey),
            SparkInvoiceFields = new SparkInvoiceFields
            {
                Version = 1,
                Id = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                SatsPayment = new SatsPayment { Amount = amountSats },
            },
        };
        return Bech32mHelper.Encode(SparkAddress.Prefixes(network).Current, payload.ToByteArray());
    }

    [Test]
    public void Known_addresses_decode_to_their_identity_keys_and_carry_no_invoice()
    {
        Hex(SparkAddress.Decode(RegtestAddress, SparkNetwork.Regtest)).Should().Be(VectorIdentity);
        Hex(SparkAddress.Decode(LegacyRegtestAddress, SparkNetwork.Regtest))
            .Should().Be("036a29ed11ae1195a3c7d906936b885ff4c20c025cc7d0e50f26b7786c19746e93");
        SparkAddress.Decode(MainnetAddress, SparkNetwork.Mainnet).Should().HaveCount(33);
        SparkAddress.Decode(LegacyMainnetAddress, SparkNetwork.Mainnet).Should().HaveCount(33);

        var payload = SparkAddress.DecodePayload(RegtestAddress, SparkNetwork.Regtest);
        payload.InvoiceFields.Should().BeNull();
        payload.Signature.Should().BeNull();
    }

    [Test]
    public void An_address_for_another_network_is_refused()
    {
        var act = () => SparkAddress.Decode(RegtestAddress, SparkNetwork.Mainnet);
        act.Should().Throw<SparkConfigurationException>().WithMessage("*not a Mainnet Spark address*");
        var legacy = () => SparkAddress.Decode(LegacyMainnetAddress, SparkNetwork.Regtest);
        legacy.Should().Throw<SparkConfigurationException>();
    }

    [Test]
    public void Encode_round_trips_through_Decode()
    {
        var key = Convert.FromHexString(VectorIdentity);

        SparkAddress.Encode(key, SparkNetwork.Regtest).Should().Be(RegtestAddress);
        SparkAddress.Decode(SparkAddress.Encode(key, SparkNetwork.Mainnet), SparkNetwork.Mainnet).Should().Equal(key);
    }

    [Test]
    public void A_Spark_invoice_decodes_to_its_fields_as_in_the_reference_SDKs_vector()
    {
        var payload = SparkAddress.DecodePayload(TokensInvoice, SparkNetwork.Regtest);

        Hex(payload.IdentityPublicKey).Should().Be(VectorIdentity);
        var fields = payload.InvoiceFields;
        fields.Should().NotBeNull();
        fields!.Version.Should().Be(1u);
        Hex(fields.Id.ToByteArray()).Should().Be("01992fa6dba47dc0a39d0f4f566cf82b");
        fields.PaymentTypeCase.Should().Be(SparkInvoiceFields.PaymentTypeOneofCase.TokensPayment);
        Hex(fields.TokensPayment.TokenIdentifier.ToByteArray()).Should().Be("093e4813f6463ae2b03b548500fe0f02c74b277f70955a8d9cb2a8ea39504614");
        fields.TokensPayment.Amount.ToByteArray().Should().Equal(0x03, 0xE8);
        fields.Memo.Should().Be("testMemo");
        Hex(fields.SenderPublicKey.ToByteArray()).Should().Be(VectorIdentity);
        // 2025-09-09T18:09:48.419Z
        fields.ExpiryTime.Seconds.Should().Be(1_757_441_388);
        fields.ExpiryTime.Nanos.Should().Be(419_000_000);
        Hex(payload.Signature!).Should().Be(
            "9d69a7bbac4d5942d7dec0bb845d784333dc80b3bba88fd046345285939e0567"
            + "339cd357a8337654bdd948a4a3cb6d3033c6bb0e6c8ac4fcb068f5433f437afb");
    }

    [Test]
    public void A_Spark_invoice_is_refused_where_a_Spark_address_is_expected()
    {
        var tokens = () => SparkAddress.Decode(TokensInvoice, SparkNetwork.Regtest);
        tokens.Should().Throw<SparkConfigurationException>().WithMessage("*Spark invoice*");

        var key = Convert.FromHexString(VectorIdentity);
        var sats = SatsInvoice(key, 1_000, SparkNetwork.Mainnet);
        SparkAddress.DecodePayload(sats, SparkNetwork.Mainnet).InvoiceFields!.SatsPayment.Amount.Should().Be(1_000UL);
        var satsAsAddress = () => SparkAddress.Decode(sats, SparkNetwork.Mainnet);
        satsAsAddress.Should().Throw<SparkConfigurationException>();

        // Invoice fields with nothing set still make an invoice.
        var empty = new NSpark.Proto.SparkAddress
        {
            IdentityPublicKey = ByteString.CopyFrom(key),
            SparkInvoiceFields = new SparkInvoiceFields(),
        };
        var emptyInvoice = () => SparkAddress.Decode(Address(empty.ToByteArray()), SparkNetwork.Mainnet);
        emptyInvoice.Should().Throw<SparkConfigurationException>();
    }

    [Test]
    public void The_payload_is_decoded_whole_so_field_order_is_free_the_key_must_be_a_curve_point_and_junk_is_refused()
    {
        var key = Convert.FromHexString(VectorIdentity);

        // Identity key after an unknown field: still a plain address.
        byte[] reordered = [0x78, 0x01, 0x0a, 33, .. key];
        SparkAddress.Decode(Address(reordered), SparkNetwork.Mainnet).Should().Equal(key);

        // x beyond the field prime: not a point.
        byte[] offCurve = [0x0a, 33, 0x02, .. Enumerable.Repeat((byte)0xFF, 32)];
        var notAPoint = () => SparkAddress.Decode(Address(offCurve), SparkNetwork.Mainnet);
        notAPoint.Should().Throw<SparkConfigurationException>().WithMessage("*identity public key*");

        // A truncated field.
        byte[] truncated = [0x0a, 33, .. key[..20]];
        var shortField = () => SparkAddress.Decode(Address(truncated), SparkNetwork.Mainnet);
        shortField.Should().Throw<SparkConfigurationException>();

        // No identity key at all.
        var noKey = () => SparkAddress.Decode(Address([0x78, 0x01]), SparkNetwork.Mainnet);
        noKey.Should().Throw<SparkConfigurationException>();
    }
}
