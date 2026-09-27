using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NSpark.Exceptions;
using NSpark.Proto;
using NSpark.Proto.Token;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// <see cref="ProtoHash"/> against the operators' cross-language vectors, which the Go operators,
/// the TypeScript SDK and the Rust token primitives all check: <c>spark/testdata/*.json</c> in
/// buildonspark/spark @ 0b3a32a, copied unchanged into <c>Vectors/</c>.
/// </summary>
[TestFixture]
public class ProtoHashTests
{
    internal sealed record Vector(string Name, string ExpectedHash, string Json);

    private static readonly JsonParser s_parser = new(JsonParser.Settings.Default);

    internal static IReadOnlyList<Vector> Vectors(string file, string messageKey)
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Vectors", file + ".json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("testCases").EnumerateArray()
            .Select(testCase => new Vector(
                testCase.GetProperty("name").GetString()!,
                testCase.GetProperty("expectedHash").GetString()!,
                testCase.GetProperty(messageKey).GetRawText()))
            .ToList();
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Test]
    public void Partial_token_transactions()
    {
        var vectors = Vectors("partial_token_transaction_hash_cases", "partialTokenTransaction");

        vectors.Should().HaveCount(4);
        foreach (var vector in vectors)
        {
            var transaction = s_parser.Parse<PartialTokenTransaction>(vector.Json);
            Hex(ProtoHash.Hash(transaction)).Should().Be(vector.ExpectedHash, vector.Name);
        }
    }

    [Test]
    public void V3_token_transactions_hashed_as_the_partial_or_the_final_transaction()
    {
        var vectors = Vectors("token_transaction_v3_hash_cases", "tokenTransaction");

        vectors.Should().HaveCount(13);
        foreach (var vector in vectors)
        {
            var transaction = s_parser.Parse<TokenTransaction>(vector.Json);
            var hash = vector.Name.Contains("partial", StringComparison.Ordinal)
                ? ProtoHash.Hash(Partial(transaction))
                : ProtoHash.Hash(Final(transaction));
            Hex(hash).Should().Be(vector.ExpectedHash, vector.Name);
        }
    }

    [Test]
    public void Spark_invoice_fields()
    {
        var vectors = Vectors("invoice_hash_cases", "sparkInvoiceFields");

        vectors.Should().HaveCount(12);
        foreach (var vector in vectors)
        {
            var fields = s_parser.Parse<SparkInvoiceFields>(vector.Json);
            Hex(ProtoHash.Hash(fields)).Should().Be(vector.ExpectedHash, vector.Name);
        }
    }

    [Test]
    public void A_default_scalar_is_left_out_even_when_set_and_a_set_message_is_hashed_even_when_empty()
    {
        var explicitZero = new TokenOutput
        {
            OwnerPublicKey = ByteString.CopyFrom(Enumerable.Repeat((byte)2, 33).ToArray()),
            WithdrawBondSats = 0,
            TokenIdentifier = ByteString.Empty,
        };
        var unset = new TokenOutput { OwnerPublicKey = ByteString.CopyFrom(Enumerable.Repeat((byte)2, 33).ToArray()) };
        ProtoHash.Hash(explicitZero).Should().Equal(ProtoHash.Hash(unset));

        var withEmptyMetadata = new PartialTokenTransaction { TokenTransactionMetadata = new TokenTransactionMetadata() };
        ProtoHash.Hash(withEmptyMetadata).Should().NotEqual(ProtoHash.Hash(new PartialTokenTransaction()));
    }

    [Test]
    public void Hashes_by_the_rules_for_an_empty_message_a_Timestamp_and_list_order()
    {
        static byte[] Sha(params byte[][] parts) => SHA256.HashData(parts.SelectMany(p => p).ToArray());
        var zeroInt = Sha(Encoding.ASCII.GetBytes("i"), new byte[8]);

        ProtoHash.Hash(new TokenTransferInput()).Should().Equal(Sha(Encoding.ASCII.GetBytes("d")));
        ProtoHash.Hash(new Timestamp { Seconds = 0, Nanos = 0 }).Should().Equal(Sha(Encoding.ASCII.GetBytes("l"), zeroInt, zeroInt));

        var ascending = new TokenTransactionMetadata();
        ascending.SparkOperatorIdentityPublicKeys.Add(ByteString.CopyFrom(2));
        ascending.SparkOperatorIdentityPublicKeys.Add(ByteString.CopyFrom(3));
        var descending = new TokenTransactionMetadata();
        descending.SparkOperatorIdentityPublicKeys.Add(ByteString.CopyFrom(3));
        descending.SparkOperatorIdentityPublicKeys.Add(ByteString.CopyFrom(2));
        ProtoHash.Hash(ascending).Should().NotEqual(ProtoHash.Hash(descending));
    }

    [Test]
    public void Well_known_types_the_operators_hashed_messages_do_not_use_are_refused()
    {
        foreach (IMessage message in new IMessage[] { new BoolValue { Value = false }, new Struct(), new Any() })
        {
            var act = () => ProtoHash.Hash(message);
            act.Should().Throw<SparkConfigurationException>(message.Descriptor.FullName);
        }
    }

    // The operators' conversions from the legacy transaction shape (ConvertV2TxShapeToPartial and
    // ConvertV2TxShapeToFinal in so/protoconverter), which their V3 vectors hash through.

    private static TokenTransactionMetadata Metadata(TokenTransaction legacy)
    {
        var metadata = new TokenTransactionMetadata
        {
            Network = legacy.Network,
            ClientCreatedTimestamp = legacy.ClientCreatedTimestamp,
            ValidityDurationSeconds = legacy.ValidityDurationSeconds,
        };
        metadata.SparkOperatorIdentityPublicKeys.AddRange(legacy.SparkOperatorIdentityPublicKeys);
        metadata.InvoiceAttachments.AddRange(legacy.InvoiceAttachments);
        return metadata;
    }

    private static PartialTokenOutput PartialOutput(TokenOutput output) => new()
    {
        OwnerPublicKey = output.OwnerPublicKey,
        WithdrawBondSats = output.WithdrawBondSats,
        WithdrawRelativeBlockLocktime = output.WithdrawRelativeBlockLocktime,
        TokenIdentifier = output.TokenIdentifier,
        TokenAmount = output.TokenAmount,
    };

    internal static PartialTokenTransaction Partial(TokenTransaction legacy)
    {
        var partial = new PartialTokenTransaction
        {
            Version = legacy.Version,
            TokenTransactionMetadata = Metadata(legacy),
        };
        if (legacy.ExecuteBefore is not null)
        {
            partial.ExecuteBefore = legacy.ExecuteBefore;
        }

        switch (legacy.TokenInputsCase)
        {
            case TokenTransaction.TokenInputsOneofCase.MintInput:
                partial.MintInput = legacy.MintInput;
                break;
            case TokenTransaction.TokenInputsOneofCase.TransferInput:
                partial.TransferInput = legacy.TransferInput;
                break;
            case TokenTransaction.TokenInputsOneofCase.CreateInput:
                var create = legacy.CreateInput.Clone();
                create.ClearCreationEntityPublicKey(); // server-set: not in a partial transaction
                partial.CreateInput = create;
                break;
        }

        partial.PartialTokenOutputs.AddRange(legacy.TokenOutputs.Select(PartialOutput));
        return partial;
    }

    internal static FinalTokenTransaction Final(TokenTransaction legacy)
    {
        var final = new FinalTokenTransaction
        {
            Version = legacy.Version,
            TokenTransactionMetadata = Metadata(legacy),
        };
        if (legacy.ExecuteBefore is not null)
        {
            final.ExecuteBefore = legacy.ExecuteBefore;
        }

        switch (legacy.TokenInputsCase)
        {
            case TokenTransaction.TokenInputsOneofCase.MintInput:
                final.MintInput = legacy.MintInput;
                break;
            case TokenTransaction.TokenInputsOneofCase.TransferInput:
                final.TransferInput = legacy.TransferInput;
                break;
            case TokenTransaction.TokenInputsOneofCase.CreateInput:
                final.CreateInput = legacy.CreateInput;
                break;
        }

        final.FinalTokenOutputs.AddRange(legacy.TokenOutputs.Select(output => new FinalTokenOutput
        {
            PartialTokenOutput = PartialOutput(output),
            RevocationCommitment = output.RevocationCommitment,
        }));
        return final;
    }
}
