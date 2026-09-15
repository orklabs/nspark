using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NSpark.Exceptions;
using NSpark.Proto;
using NSpark.Proto.Token;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for the check a token commit runs on the coordinator's final transaction before the
/// wallet signs it: only server-set fields may differ from the submitted partial transaction.
/// </summary>
[TestFixture]
public sealed class TokenTransactionValidatorTests
{
    private static readonly byte[] Owner = MakeKey(0x02, 1);
    private static readonly byte[] Receiver = MakeKey(0x03, 2);
    private static readonly byte[][] OperatorKeys = [MakeKey(0x02, 10), MakeKey(0x02, 11), MakeKey(0x03, 12)];
    private static readonly string[] OperatorIds = ["01", "02", "03"];
    private static readonly byte[] TokenId = Enumerable.Repeat((byte)0xAB, 32).ToArray();

    private static TokenTransactionValidator.Expectations Expectations() => new(
        OperatorKeys,
        OperatorIds.ToHashSet(StringComparer.Ordinal),
        Threshold: 2,
        WithdrawBondSats: 10_000,
        WithdrawRelativeBlockLocktime: 1_000);

    [Test]
    public void Accepts_a_final_transaction_that_only_adds_server_set_fields()
    {
        var partial = TransferPartial();
        var final = Finalize(partial);

        var act = () => TokenTransactionValidator.Validate(final, partial, Keyshare(), Expectations());

        act.Should().NotThrow();
    }

    [Test]
    public void Accepts_mint_and_create_transactions()
    {
        var mint = MintPartial();
        var create = CreatePartial();

        ((Action)(() => TokenTransactionValidator.Validate(Finalize(mint), mint, Keyshare(), Expectations()))).Should().NotThrow();
        ((Action)(() => TokenTransactionValidator.Validate(Finalize(create), create, Keyshare(), Expectations()))).Should().NotThrow();
    }

    [Test]
    public void Rejects_a_redirected_output()
    {
        var partial = TransferPartial();
        var final = Finalize(partial);
        final.TokenOutputs[0].OwnerPublicKey = ByteString.CopyFrom(MakeKey(0x02, 99));

        Validate(final, partial).Should().Throw<SparkUntrustedResponseException>().WithMessage("*output 0 owner changed*");
    }

    [Test]
    public void Rejects_a_resized_output()
    {
        var partial = TransferPartial();
        var final = Finalize(partial);
        final.TokenOutputs[1].TokenAmount = ByteString.CopyFrom(TokenService.EncodeUInt128(999).ToByteArray());

        Validate(final, partial).Should().Throw<SparkUntrustedResponseException>().WithMessage("*output 1 amount changed*");
    }

    [Test]
    public void Rejects_added_or_removed_outputs()
    {
        var partial = TransferPartial();
        var final = Finalize(partial);
        final.TokenOutputs.RemoveAt(1);

        Validate(final, partial).Should().Throw<SparkUntrustedResponseException>().WithMessage("*output count changed*");
    }

    [Test]
    public void Rejects_changed_inputs()
    {
        var partial = TransferPartial();
        var final = Finalize(partial);
        final.TransferInput.OutputsToSpend[0].PrevTokenTransactionVout = 7;

        Validate(final, partial).Should().Throw<SparkUntrustedResponseException>().WithMessage("*input 0 changed*");
    }

    [Test]
    public void Rejects_a_transaction_type_change()
    {
        var partial = TransferPartial();
        var final = Finalize(partial);
        final.MintInput = new TokenMintInput { IssuerPublicKey = ByteString.CopyFrom(Owner), TokenIdentifier = ByteString.CopyFrom(TokenId) };

        Validate(final, partial).Should().Throw<SparkUntrustedResponseException>().WithMessage("*transaction type changed*");
    }

    [Test]
    public void Rejects_an_unexpected_withdraw_bond_or_locktime()
    {
        var partial = TransferPartial();
        var bond = Finalize(partial);
        bond.TokenOutputs[0].WithdrawBondSats = 9_999;
        var locktime = Finalize(partial);
        locktime.TokenOutputs[0].WithdrawRelativeBlockLocktime = 10;

        Validate(bond, partial).Should().Throw<SparkUntrustedResponseException>().WithMessage("*withdraw bond*");
        Validate(locktime, partial).Should().Throw<SparkUntrustedResponseException>().WithMessage("*withdraw locktime*");
    }

    [Test]
    public void Rejects_changed_operator_keys_version_or_timestamp()
    {
        var partial = TransferPartial();
        var keys = Finalize(partial);
        keys.SparkOperatorIdentityPublicKeys[0] = ByteString.CopyFrom(MakeKey(0x02, 42));
        var version = Finalize(partial);
        version.Version = 3;
        var stamp = Finalize(partial);
        stamp.ClientCreatedTimestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddDays(1));

        Validate(keys, partial).Should().Throw<SparkUntrustedResponseException>().WithMessage("*operator identity public keys changed*");
        Validate(version, partial).Should().Throw<SparkUntrustedResponseException>().WithMessage("*version changed*");
        Validate(stamp, partial).Should().Throw<SparkUntrustedResponseException>().WithMessage("*timestamp changed*");
    }

    [Test]
    public void Rejects_a_keyshare_that_does_not_name_the_configured_operators()
    {
        var partial = TransferPartial();
        var final = Finalize(partial);

        var threshold = Keyshare();
        threshold.Threshold = 3;
        var unknown = Keyshare();
        unknown.OwnerIdentifiers[2] = "99";
        var duplicate = Keyshare();
        duplicate.OwnerIdentifiers[2] = "01";
        var fewer = Keyshare();
        fewer.OwnerIdentifiers.RemoveAt(2);

        ((Action)(() => TokenTransactionValidator.Validate(final, partial, threshold, Expectations()))).Should().Throw<SparkUntrustedResponseException>().WithMessage("*threshold*");
        ((Action)(() => TokenTransactionValidator.Validate(final, partial, unknown, Expectations()))).Should().Throw<SparkUntrustedResponseException>().WithMessage("*not a configured operator*");
        ((Action)(() => TokenTransactionValidator.Validate(final, partial, duplicate, Expectations()))).Should().Throw<SparkUntrustedResponseException>().WithMessage("*duplicate*");
        ((Action)(() => TokenTransactionValidator.Validate(final, partial, fewer, Expectations()))).Should().Throw<SparkUntrustedResponseException>().WithMessage("*operator count*");
        ((Action)(() => TokenTransactionValidator.Validate(final, partial, null, Expectations()))).Should().Throw<SparkUntrustedResponseException>().WithMessage("*keyshare info missing*");
    }

    [Test]
    public void Rejects_changed_mint_and_create_parameters()
    {
        var mint = MintPartial();
        var mintFinal = Finalize(mint);
        mintFinal.MintInput.TokenIdentifier = ByteString.CopyFrom(new byte[32]);

        var create = CreatePartial();
        var createFinal = Finalize(create);
        createFinal.CreateInput.MaxSupply = ByteString.CopyFrom(TokenService.EncodeUInt128(1).ToByteArray());

        Validate(mintFinal, mint).Should().Throw<SparkUntrustedResponseException>().WithMessage("*mint token identifier changed*");
        Validate(createFinal, create).Should().Throw<SparkUntrustedResponseException>().WithMessage("*creation parameters changed*");
    }

    private static Action Validate(TokenTransaction final, TokenTransaction partial)
        => () => TokenTransactionValidator.Validate(final, partial, Keyshare(), Expectations());

    private static SigningKeyshare Keyshare()
    {
        var keyshare = new SigningKeyshare { Threshold = 2 };
        keyshare.OwnerIdentifiers.AddRange(OperatorIds);
        return keyshare;
    }

    private static TokenTransaction Skeleton()
    {
        var tx = new TokenTransaction
        {
            Version = 2,
            Network = Network.Mainnet,
            ClientCreatedTimestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000)),
        };
        foreach (var key in OperatorKeys)
        {
            tx.SparkOperatorIdentityPublicKeys.Add(ByteString.CopyFrom(key));
        }
        return tx;
    }

    private static TokenOutput Output(byte[] owner, ulong amount) => new()
    {
        OwnerPublicKey = ByteString.CopyFrom(owner),
        TokenIdentifier = ByteString.CopyFrom(TokenId),
        TokenAmount = ByteString.CopyFrom(TokenService.EncodeUInt128(amount).ToByteArray()),
    };

    private static TokenTransaction TransferPartial()
    {
        var tx = Skeleton();
        tx.TransferInput = new TokenTransferInput();
        tx.TransferInput.OutputsToSpend.Add(new TokenOutputToSpend
        {
            PrevTokenTransactionHash = ByteString.CopyFrom(Enumerable.Repeat((byte)0x11, 32).ToArray()),
            PrevTokenTransactionVout = 0,
        });
        tx.TokenOutputs.Add(Output(Receiver, 60));
        tx.TokenOutputs.Add(Output(Owner, 40));
        return tx;
    }

    private static TokenTransaction MintPartial()
    {
        var tx = Skeleton();
        tx.MintInput = new TokenMintInput { IssuerPublicKey = ByteString.CopyFrom(Owner), TokenIdentifier = ByteString.CopyFrom(TokenId) };
        tx.TokenOutputs.Add(Output(Owner, 100));
        return tx;
    }

    private static TokenTransaction CreatePartial()
    {
        var tx = Skeleton();
        tx.CreateInput = new TokenCreateInput
        {
            IssuerPublicKey = ByteString.CopyFrom(Owner),
            TokenName = "Test",
            TokenTicker = "TST",
            Decimals = 8,
            MaxSupply = ByteString.CopyFrom(TokenService.EncodeUInt128(1_000_000).ToByteArray()),
            IsFreezable = false,
        };
        return tx;
    }

    /// <summary>What the coordinator legitimately adds: ids, revocation commitments, bond, locktime, expiry.</summary>
    private static TokenTransaction Finalize(TokenTransaction partial)
    {
        var final = partial.Clone();
        final.ExpiryTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(1));
        for (int i = 0; i < final.TokenOutputs.Count; i++)
        {
            final.TokenOutputs[i].Id = Guid.NewGuid().ToString();
            final.TokenOutputs[i].RevocationCommitment = ByteString.CopyFrom(MakeKey(0x02, (byte)(50 + i)));
            final.TokenOutputs[i].WithdrawBondSats = 10_000;
            final.TokenOutputs[i].WithdrawRelativeBlockLocktime = 1_000;
        }
        return final;
    }

    private static byte[] MakeKey(byte prefix, byte seed)
    {
        var key = new byte[33];
        key[0] = prefix;
        Array.Fill(key, seed, 1, 32);
        return key;
    }
}
