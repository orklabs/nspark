using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NSpark.Bitcoin;
using NSpark.Exceptions;
using NSpark.Models;
using NSpark.Services;
using NSpark.Signer;
using NSpark.UnitTests.TestSupport;

namespace NSpark.UnitTests.Services;

/// <summary>
/// The pieces of a static-deposit refund and claim, checked against what the operators verify
/// (<c>static_deposit_handler.go</c>, <c>internal_deposit_handler.go</c>) and what the reference
/// SDK sends.
/// </summary>
[TestFixture]
public class StaticDepositTests
{
    private const string Txid = "4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b";
    private const string Destination = "bc1p0xlxvlhemja6c4dqv22uapctqupfhlxm9h8z3k2e72q4k9hcz7vqzk5jj0";
    private static readonly byte[] s_destinationScript = Convert.FromHexString("512079be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798");

    private static byte[] Le32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Le64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>
    /// BIP-341 key-path sighash with SIGHASH_DEFAULT for input 0 of a one-input transaction — what
    /// the operators compute (<c>sighash.FromTx</c>), written out independently.
    /// </summary>
    private static byte[] TaprootSighash(RawTransaction tx, byte[] prevoutScript, ulong prevoutValue)
    {
        var prevouts = Concat(tx.Inputs.SelectMany(i => new[] { i.PreviousTxid, Le32(i.PreviousIndex) }).ToArray());
        var sequences = Concat(tx.Inputs.Select(i => Le32(i.Sequence)).ToArray());
        var outputs = Concat(tx.Outputs.SelectMany(o => new[] { Le64(o.Value), [(byte)o.ScriptPubKey.Length], o.ScriptPubKey }).ToArray());
        var scripts = Concat([(byte)prevoutScript.Length], prevoutScript);
        var message = Concat(
            [0x00, 0x00], // epoch, hash type
            Le32(tx.Version),
            Le32(tx.Locktime),
            SHA256.HashData(prevouts),
            SHA256.HashData(Le64(prevoutValue)),
            SHA256.HashData(scripts),
            SHA256.HashData(sequences),
            SHA256.HashData(outputs),
            [0x00], // key path, no annex
            Le32(0)); // input index
        var tag = SHA256.HashData(Encoding.UTF8.GetBytes("TapSighash"));
        return SHA256.HashData(Concat(tag, tag, message));
    }

    [Test]
    public void A_deposit_outpoint_is_a_lower_case_display_txid_the_operators_look_up_in_display_order()
    {
        var outpoint = new DepositOutpoint($" {Txid.ToUpperInvariant()}\n", 2);

        outpoint.Txid.Should().Be(Txid);
        outpoint.DisplayOrderTxid.Should().Equal(Convert.FromHexString(Txid));
        outpoint.InternalOrderTxid.Should().Equal(Enumerable.Reverse(Convert.FromHexString(Txid)));
        // hexToBytes(depositTransactionId) in the reference SDK; the operators match it against the
        // txid string decoded as stored.
        var utxo = outpoint.ToUtxo(NSpark.Proto.Network.Mainnet);
        utxo.Txid.ToByteArray().Should().Equal(Convert.FromHexString(Txid));
        utxo.Vout.Should().Be(2u);
        foreach (var bad in new[] { string.Empty, "abc", Txid[..^1], Txid[..^1] + "g", Txid + "00" })
        {
            var act = () => new DepositOutpoint(bad, 0);
            act.Should().Throw<SparkConfigurationException>(bad);
        }
    }

    [Test]
    public void The_unsigned_refund_is_exactly_the_transaction_the_operators_rebuild_v3_final_sequence_no_witness()
    {
        var outpoint = new DepositOutpoint(Txid, 1);

        var spend = DepositHelpers.ConstructSpendTx(
            outpoint, NSpark.Services.CoopExitValidator.ScriptPubKeyFor(Destination, SparkNetwork.Mainnet).ToBytes(), 12_345);

        spend.Should().Equal(Concat(
            Le32(3),
            [0x01],
            outpoint.InternalOrderTxid,
            Le32(1),
            [0x00],
            Le32(0xFFFF_FFFF),
            [0x01],
            Le64(12_345),
            [(byte)s_destinationScript.Length],
            s_destinationScript,
            Le32(0)));

        // Signing adds the witness without changing the transaction.
        var signature = Enumerable.Repeat((byte)0xCC, 64).ToArray();
        var signed = RawTransaction.Parse(DepositHelpers.AddWitnessToTx(spend, signature));
        signed.HasWitnessSerialization.Should().BeTrue();
        signed.Inputs[0].Witness.Should().ContainSingle().Which.Should().Equal(signature);
        signed.Txid.Should().Equal(RawTransaction.Parse(spend).Txid);
    }

    [Test]
    public void The_refund_sighash_is_the_BIP_341_key_path_sighash_the_operators_compute()
    {
        var spend = DepositHelpers.ConstructSpendTx(new DepositOutpoint(Txid, 0), s_destinationScript, 9_000);
        byte[] depositScript = [0x51, 0x20, .. Enumerable.Repeat((byte)0x42, 32)];

        var sighash = SparkTxBuilder.ComputeMultiInputSighash(spend, 0, [depositScript], [10_000]);

        sighash.Should().Equal(TaprootSighash(RawTransaction.Parse(spend), depositScript, 10_000));
    }

    [Test]
    public void The_refund_statement_ends_with_the_raw_32_byte_sighash_as_the_operators_verify_it()
    {
        var outpoint = new DepositOutpoint(Txid, 1);
        var sighash = Enumerable.Repeat((byte)0x11, 32).ToArray();
        static byte[] Expected(string network, byte requestType, ulong credit, byte[] authorization) => Concat(
            Encoding.UTF8.GetBytes("claim_static_deposit"),
            Encoding.UTF8.GetBytes(network),
            Encoding.UTF8.GetBytes(Txid),
            Le32(1),
            [requestType],
            Le64(credit),
            authorization);

        var statement = DepositHelpers.StaticDepositStatement(outpoint, SparkNetwork.Mainnet, StaticDepositRequestType.Refund, 1_000, sighash);

        statement.Should().Equal(Expected("mainnet", 2, 1_000, sighash));
        statement.Should().HaveCount(20 + 7 + 64 + 4 + 1 + 8 + 32);
        DepositHelpers.StaticDepositStatement(outpoint, SparkNetwork.Regtest, StaticDepositRequestType.Fixed, 1, [0xAB])
            .Should().Equal(Expected("regtest", 0, 1, [0xAB]));
    }

    [Test]
    public async Task The_refund_request_leaves_hash_variant_unset_so_the_operators_check_the_legacy_statement_it_signs()
    {
        // CreateUserStatement(…, req.hash_variant) in internal_deposit_handler.go: the SHA-256 of
        // the statement unless hash_variant is V2, a tagged hash of its fields when it is.
        var signer = SparkSigner.FromMnemonic(FakeOperator.Mnemonic, 0);
        var identity = new NBitcoin.PubKey(await signer.GetIdentityPublicKeyAsync());
        var outpoint = new DepositOutpoint(Txid, 1);
        var sighash = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var job = new NSpark.Proto.SigningJob { RawTx = Google.Protobuf.ByteString.CopyFrom(0x01, 0x02) };

        var request = await DepositService.StaticDepositRefundRequestAsync(
            signer, outpoint, SparkNetwork.Mainnet, 1_000, sighash, job, CancellationToken.None);

        request.HashVariant.Should().Be(NSpark.Proto.HashVariant.Unspecified);
        var legacy = SHA256.HashData(DepositHelpers.StaticDepositStatement(
            outpoint, SparkNetwork.Mainnet, StaticDepositRequestType.Refund, 1_000, sighash));
        identity.Verify(new NBitcoin.uint256(legacy), NBitcoin.Crypto.ECDSASignature.FromDER(request.UserSignature.ToByteArray()))
            .Should().BeTrue();
        request.OnChainUtxo.Should().Be(outpoint.ToUtxo(NSpark.Proto.Network.Mainnet));
        request.RefundTxSigningJob.Should().BeSameAs(job);
    }

    [Test]
    public void A_claim_statement_commits_to_the_quotes_credit_and_the_SSPs_signature_bytes()
    {
        var outpoint = new DepositOutpoint(Txid.ToUpperInvariant(), 3);
        var quote = new DepositFeeEstimate(49_000, "3045022100aabb");

        var statement = DepositHelpers.StaticDepositStatement(
            outpoint, SparkNetwork.Mainnet, StaticDepositRequestType.Fixed, (ulong)quote.CreditAmountSats, Convert.FromHexString(quote.Signature));

        // The lower-case txid, the hex-decoded quote signature.
        statement.Should().Equal(Concat(
            Encoding.UTF8.GetBytes("claim_static_deposit"),
            Encoding.UTF8.GetBytes("mainnet"),
            Encoding.UTF8.GetBytes(Txid),
            Le32(3),
            [0],
            Le64(49_000),
            Convert.FromHexString("3045022100aabb")));
        DepositService.StaticDepositFee(50_000, quote).Should().Be(1_000);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_quote_with_no_credit_or_a_signature_that_is_not_hex_is_refused_before_the_SSP_is_asked(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();

        await FakeOperator.RunAsync(state, async wallet =>
        {
            await ((Func<Task>)(() => wallet.ClaimStaticDepositAsync(Txid, new DepositFeeEstimate(0, "aa"), ct: ct)))
                .Should().ThrowAsync<ArgumentException>();
            await ((Func<Task>)(() => wallet.ClaimStaticDepositAsync(Txid, new DepositFeeEstimate(10, "not hex"), ct: ct)))
                .Should().ThrowAsync<SparkUntrustedResponseException>();
            await ((Func<Task>)(() => wallet.ClaimStaticDepositAsync(Txid, new DepositFeeEstimate(10, string.Empty), ct: ct)))
                .Should().ThrowAsync<SparkUntrustedResponseException>();
            await ((Func<Task>)(() => wallet.ClaimStaticDepositAsync("abc", new DepositFeeEstimate(10, "aa"), ct: ct)))
                .Should().ThrowAsync<SparkConfigurationException>();
        });

        state.Methods.Should().BeEmpty();
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_txid_that_is_not_64_hex_characters_throws_before_any_block_explorer_request(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();

        await FakeOperator.RunAsync(state, async wallet =>
        {
            foreach (var bad in new[] { string.Empty, "not a txid", "zz" + new string('0', 62), "a b", new string('0', 65) })
            {
                // Refused as a configuration error, not by the (unreachable) explorer.
                await ((Func<Task>)(() => DepositService.FetchRawTransactionAsync(wallet, bad, ct)))
                    .Should().ThrowAsync<SparkConfigurationException>(bad);
            }
        });

        DepositOutpoint.NormalizedTxid($" {Txid.ToUpperInvariant()} ").Should().Be(Txid);
    }
}

/// <summary>
/// Without an output index, static-deposit calls use the output that pays the wallet's static
/// deposit address, as the reference SDK's <c>getDepositTransactionVout</c> finds it.
/// </summary>
[TestFixture]
public class StaticDepositVoutTests
{
    private const string StaticAddress = "bc1p0xlxvlhemja6c4dqv22uapctqupfhlxm9h8z3k2e72q4k9hcz7vqzk5jj0";
    private const string OtherAddress = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";

    private static RawTransaction Paying(params string[] addresses) => new(
        2,
        [new RawTransaction.Input(Enumerable.Repeat((byte)0x01, 32).ToArray(), 0)],
        addresses.Select(a => new RawTransaction.Output(1_000, NSpark.Services.CoopExitValidator.ScriptPubKeyFor(a, SparkNetwork.Mainnet).ToBytes())).ToList(),
        0,
        false);

    [Test]
    public void The_first_output_paying_a_static_deposit_address_is_used_and_none_is_an_error()
    {
        DepositHelpers.StaticDepositVout(Paying(OtherAddress, StaticAddress), [StaticAddress], SparkNetwork.Mainnet).Should().Be(1u);
        DepositHelpers.StaticDepositVout(Paying(StaticAddress, StaticAddress), [StaticAddress], SparkNetwork.Mainnet).Should().Be(0u);

        var none = () => DepositHelpers.StaticDepositVout(Paying(OtherAddress), [StaticAddress], SparkNetwork.Mainnet);
        none.Should().Throw<SparkConfigurationException>();
        var noAddresses = () => DepositHelpers.StaticDepositVout(Paying(StaticAddress), [], SparkNetwork.Mainnet);
        noAddresses.Should().Throw<SparkConfigurationException>();
        var otherNetwork = () => DepositHelpers.StaticDepositVout(Paying(StaticAddress), [StaticAddress], SparkNetwork.Regtest);
        otherNetwork.Should().Throw<SparkConfigurationException>();
    }

    [Test]
    public void A_one_time_deposit_claim_is_built_for_the_output_that_pays_one_of_the_wallets_addresses()
    {
        var tx = Paying(OtherAddress, StaticAddress);

        DepositHelpers.MatchDepositOutput(tx, [StaticAddress], null, SparkNetwork.Mainnet).Should().Be((1u, StaticAddress));
        DepositHelpers.MatchDepositOutput(tx, [StaticAddress], 1, SparkNetwork.Mainnet).Should().Be((1u, StaticAddress));
        var wrongVout = () => DepositHelpers.MatchDepositOutput(tx, [StaticAddress], 0, SparkNetwork.Mainnet);
        wrongVout.Should().Throw<SparkConfigurationException>();
        var outOfRange = () => DepositHelpers.MatchDepositOutput(tx, [StaticAddress], 5, SparkNetwork.Mainnet);
        outOfRange.Should().Throw<SparkException>();
        var unpaid = () => DepositHelpers.MatchDepositOutput(Paying(OtherAddress), [StaticAddress], null, SparkNetwork.Mainnet);
        unpaid.Should().Throw<SparkConfigurationException>();
        var noCandidates = () => DepositHelpers.MatchDepositOutput(tx, [], null, SparkNetwork.Mainnet);
        noCandidates.Should().Throw<SparkConfigurationException>().WithMessage("*No unused deposit address*");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_one_time_claim_looks_through_every_unused_address_even_when_used_ones_fill_the_first_page(CancellationToken ct)
    {
        // The operator pages before it drops used addresses: 99 used ones among the newest 100
        // leave a first page of one, and no next offset, although 50 more unused ones follow.
        static NSpark.Proto.DepositAddressQueryResult Address(int i) => new() { DepositAddress = $"address-{i}", LeafId = $"leaf-{i}" };
        var state = FakeOperatorState.Accepting();
        state.SetSingleUseAddresses(Enumerable.Range(0, 150).Select(i => (Address(i), Used: i < 99)));

        await FakeOperator.RunAsync(state, async wallet =>
        {
            var all = await wallet.QueryAllUnusedDepositAddressesAsync(ct);
            all.Select(a => a.DepositAddress).Should().Equal(Enumerable.Range(99, 51).Select(i => $"address-{i}"));

            // A page of the public query stops as short as the operator pages it.
            (await wallet.QueryUnusedDepositAddressesAsync(ct: ct)).Should().ContainSingle()
                .Which.Address.Should().Be("address-99");
            (await wallet.QueryUnusedDepositAddressesAsync(limit: 0, ct: ct)).Should().HaveCount(51);
            var negative = () => wallet.QueryUnusedDepositAddressesAsync(offset: -1, ct: ct);
            await negative.Should().ThrowAsync<ArgumentOutOfRangeException>();
        });

        state.UnusedAddressPages.Should().Equal((0L, 0L), (100L, 0L), (0L, 0L));
    }
}
