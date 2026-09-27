using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NSpark.Exceptions;
using NSpark.Proto;
using NSpark.Proto.Token;
using NSpark.Services;
using SparkAddress = NSpark.Services.SparkAddress;

namespace NSpark.UnitTests.Services;

/// <summary>
/// The reference SDK's known-answer vectors for V2 token transaction hashes
/// (<c>token-hashing.test.ts</c>, on the operators' Go test data), and the operators' rules for
/// invoice attachments (<c>TestHashTokenTransactionV2UniqueHash</c>).
/// </summary>
[TestFixture]
public class TokenHashVectorTests
{
    internal static byte[] Key(byte second, byte last = 46) =>
    [
        0x02, second, 155, 208, 90, 72, 211, 120, 244, 69, 99, 28, 101, 149, 222, 123,
        50, 252, 63, 99, 54, 137, 226, 7, 224, 163, 122, 93, 248, 42, 159, 173, last,
    ];

    /// <summary>Two regtest invoices whose ids (01992fa6… and 01992fac…) sort the other way from their strings.</summary>
    private static readonly string[] s_invoices =
    [
        "sparkrt1pgssx5us3wkqjza8g80xz3a9gznx25msq6g3ty8exfym9q3ahcv86vsnzfmssqgjzqqejtaxmwj8ms9rn58574nvlq4j5zr5v4ehgnt9d4hnyggr2wgghtqfpwn5rhnpg7j5pfn92dcqdyg4jrunyjdjsg7muxraxgfn5rqgandgr3sxzrqdmew8qydzvz3qpylysylkgcaw9vpm2jzspls0qtr5kfmlwz244rvuk25w5w2sgc2pyqsraqdyp8tf57a6cn2egttaas9ms3whssenmjqt8wag3lgyvdzjskfeupt8xwwdx4agxdm9f0wefzj28jmdxqeudwcwdj9vfl9sdr65x06r0tasf5fwz2",
        "sparkrt1pgssx5us3wkqjza8g80xz3a9gznx25msq6g3ty8exfym9q3ahcv86vsnzfmqsqgjzqqejtavuhf8n5uh9a74zw66kqaz5zr5v4ehgnt9d4hnyggr2wgghtqfpwn5rhnpg7j5pfn92dcqdyg4jrunyjdjsg7muxraxgfn5zcglrwcr3sxzzqt3wrjrgnq5gqf8eyp8ajx8t3tqw65s5q0urczca9jwlmsj4dgm89j4r4rj5zxzsfqyqlgrfqw9ucldgmfzs5zmkekj90thwzmn6ps55gdjnz23aarjkf245608yg0v2x6xdpdrz6m8xjlhtru0kygcu4zhqwlth9duadfqpruuzx4tc7fdckn",
    ];

    /// <summary>The vectors' transfer: one input, one output, one operator, regtest, created at 100 ms.</summary>
    private static TokenTransaction Transfer(IEnumerable<string> invoices)
    {
        var input = new TokenTransferInput();
        input.OutputsToSpend.Add(new TokenOutputToSpend
        {
            PrevTokenTransactionHash = ByteString.CopyFrom(SHA256.HashData(Encoding.UTF8.GetBytes("previous transaction"))),
            PrevTokenTransactionVout = 0,
        });

        var tx = new TokenTransaction
        {
            Version = 2,
            TransferInput = input,
            Network = Network.Regtest,
            ExpiryTime = new Timestamp { Seconds = 0, Nanos = 0 },
            ClientCreatedTimestamp = new Timestamp { Seconds = 0, Nanos = 100_000_000 },
        };
        tx.TokenOutputs.Add(new TokenOutput
        {
            Id = "db1a4e48-0fc5-4f6c-8a80-d9d6c561a436",
            OwnerPublicKey = ByteString.CopyFrom(Key(25)),
            TokenPublicKey = ByteString.CopyFrom(Key(242, last: 45)),
            TokenAmount = TokenService.EncodeUInt128(1000),
            RevocationCommitment = ByteString.CopyFrom(Key(100)),
            WithdrawBondSats = 10_000,
            WithdrawRelativeBlockLocktime = 100,
        });
        tx.SparkOperatorIdentityPublicKeys.Add(ByteString.CopyFrom(Key(200)));
        tx.InvoiceAttachments.AddRange(invoices.Select(invoice => new InvoiceAttachment { SparkInvoice = invoice }));
        return tx;
    }

    [Test]
    public void Transfer_without_invoice_attachments()
    {
        byte[] expected =
        [
            28, 151, 252, 16, 41, 53, 194, 50, 190, 167, 55, 2, 43, 179, 179, 255,
            117, 150, 148, 29, 158, 203, 107, 193, 82, 1, 77, 95, 41, 168, 208, 179,
        ];

        TokenHashing.HashTokenTransactionV2(Transfer([]), partialHash: false).Should().Equal(expected);
    }

    [Test]
    public void Transfer_with_two_invoice_attachments_hashed_in_invoice_id_order_whatever_order_they_come_in()
    {
        byte[] expected =
        [
            0xb0, 0x98, 0xdc, 0x22, 0x8a, 0x0d, 0x82, 0x64, 0x25, 0x4a, 0x2d, 0xef,
            0x34, 0x42, 0x5c, 0xab, 0xe2, 0x23, 0x0d, 0x4f, 0x7b, 0xa4, 0x3c, 0xf2,
            0xa3, 0x2c, 0x27, 0xf0, 0x31, 0xae, 0x08, 0x83,
        ];

        TokenHashing.HashTokenTransactionV2(Transfer(s_invoices), partialHash: false).Should().Equal(expected);
        TokenHashing.HashTokenTransactionV2(Transfer(Enumerable.Reverse(s_invoices)), partialHash: false).Should().Equal(expected);
    }

    [Test]
    public void An_attachment_that_is_not_a_Spark_invoice_cannot_be_hashed()
    {
        foreach (var invoice in new[] { string.Empty, "invalid", SparkAddress.Encode(Key(25), SparkNetwork.Regtest) })
        {
            var act = () => TokenHashing.HashTokenTransactionV2(Transfer([invoice]), partialHash: false);
            act.Should().Throw<SparkConfigurationException>(invoice);
        }
    }
}
