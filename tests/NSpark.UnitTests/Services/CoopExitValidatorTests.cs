using NBitcoin;
using NSpark.Exceptions;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for the checks a withdrawal runs on the SSP's cooperative-exit response before any
/// refund is signed. The exit and connector transactions are built with NBitcoin so the vectors
/// are real, parseable Bitcoin transactions.
/// </summary>
[TestFixture]
public sealed class CoopExitValidatorTests
{
    // BIP-173 P2WPKH test vector.
    private const string Payout = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";

    // BIP-86 first receive address (P2TR).
    private const string OtherPayout = "bc1p5cyxnuxmeuwuvkwfem96lqzszd02n6xdcjrs20cac6yqjjwudpxqkedrcr";

    private sealed record Fixture(Transaction Exit, Transaction Connector)
    {
        public string ExitHex => Exit.ToHex();
        public string ConnectorHex => Connector.ToHex();
        public string ExitTxidHex => Exit.GetHash().ToString();
    }

    private static Fixture Build(long payoutSats = 9_000, int connectorOutputs = 3, bool connectorSpendsExit = true, string payoutAddress = Payout)
    {
        var exit = Transaction.Create(Network.Main);
        exit.Inputs.Add(new TxIn(new OutPoint(new uint256(7), 0)));
        exit.Outputs.Add(new TxOut(Money.Satoshis(payoutSats), BitcoinAddress.Create(payoutAddress, Network.Main).ScriptPubKey));
        exit.Outputs.Add(new TxOut(Money.Satoshis(50_000), new Key().PubKey.WitHash.ScriptPubKey));

        var connector = Transaction.Create(Network.Main);
        connector.Inputs.Add(new TxIn(new OutPoint(connectorSpendsExit ? exit.GetHash() : new uint256(9), 1)));
        for (int i = 0; i < connectorOutputs; i++)
        {
            connector.Outputs.Add(new TxOut(Money.Satoshis(330), new Key().PubKey.WitHash.ScriptPubKey));
        }
        return new Fixture(exit, connector);
    }

    [Test]
    public void Accepts_a_consistent_response()
    {
        var f = Build();

        var validated = CoopExitValidator.Validate(
            f.ExitHex, f.ConnectorHex, f.ExitTxidHex, Payout, minimumPayoutSats: 9_000, leafCount: 2, SparkNetwork.Mainnet);

        validated.ExitTxidHex.Should().Be(f.ExitTxidHex);
        validated.ExitTxidInternal.Should().Equal(f.Exit.GetHash().ToBytes());
        validated.ExitTxidInternal.Should().Equal(Enumerable.Reverse(Convert.FromHexString(f.ExitTxidHex)).ToArray(),
            "the coordinator wants the txid in internal byte order");
        validated.PayoutVout.Should().Be(0);
        validated.PayoutSats.Should().Be(9_000UL);
        validated.ConnectorTxidInternal.Should().Equal(f.Connector.GetHash().ToBytes());
        validated.ConnectorTx.Outputs.Should().HaveCount(3);
    }

    [Test]
    public void Accepts_the_reported_txid_in_internal_byte_order()
    {
        var f = Build();
        var internalHex = Convert.ToHexString(f.Exit.GetHash().ToBytes()).ToLowerInvariant();

        var act = () => CoopExitValidator.Validate(f.ExitHex, f.ConnectorHex, internalHex, Payout, 9_000, 2, SparkNetwork.Mainnet);

        act.Should().NotThrow();
    }

    [Test]
    public void Accepts_a_payout_above_the_minimum()
    {
        var f = Build(payoutSats: 9_500);

        var validated = CoopExitValidator.Validate(f.ExitHex, f.ConnectorHex, f.ExitTxidHex, Payout, 9_000, 2, SparkNetwork.Mainnet);

        validated.PayoutSats.Should().Be(9_500UL);
    }

    [Test]
    public void Rejects_a_txid_that_does_not_match_the_exit_transaction()
    {
        var f = Build();
        var wrong = Build().ExitTxidHex;

        var act = () => CoopExitValidator.Validate(f.ExitHex, f.ConnectorHex, wrong, Payout, 9_000, 2, SparkNetwork.Mainnet);

        act.Should().Throw<SparkUntrustedResponseException>().WithMessage("*coop_exit_txid*does not match*");
    }

    [Test]
    public void Rejects_a_payout_below_the_minimum()
    {
        var f = Build(payoutSats: 8_999);

        var act = () => CoopExitValidator.Validate(f.ExitHex, f.ConnectorHex, f.ExitTxidHex, Payout, 9_000, 2, SparkNetwork.Mainnet);

        act.Should().Throw<SparkUntrustedResponseException>().WithMessage("*does not pay*at least 9000 sats*");
    }

    [Test]
    public void Rejects_a_payout_to_another_address()
    {
        var f = Build(payoutAddress: OtherPayout);

        var act = () => CoopExitValidator.Validate(f.ExitHex, f.ConnectorHex, f.ExitTxidHex, Payout, 9_000, 2, SparkNetwork.Mainnet);

        act.Should().Throw<SparkUntrustedResponseException>().WithMessage("*does not pay*");
    }

    [Test]
    public void Rejects_a_connector_that_does_not_spend_the_exit_transaction()
    {
        var f = Build(connectorSpendsExit: false);

        var act = () => CoopExitValidator.Validate(f.ExitHex, f.ConnectorHex, f.ExitTxidHex, Payout, 9_000, 2, SparkNetwork.Mainnet);

        act.Should().Throw<SparkUntrustedResponseException>().WithMessage("*does not spend*");
    }

    [Test]
    public void Rejects_a_connector_with_the_wrong_number_of_outputs()
    {
        var f = Build(connectorOutputs: 2);

        var act = () => CoopExitValidator.Validate(f.ExitHex, f.ConnectorHex, f.ExitTxidHex, Payout, 9_000, 2, SparkNetwork.Mainnet);

        act.Should().Throw<SparkUntrustedResponseException>().WithMessage("*2 outputs for 2 leaves*");
    }

    [Test]
    public void Rejects_a_destination_on_another_network_or_malformed()
    {
        var f = Build();

        var regtest = () => CoopExitValidator.Validate(
            f.ExitHex, f.ConnectorHex, f.ExitTxidHex, "bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4", 9_000, 2, SparkNetwork.Mainnet);
        var garbage = () => CoopExitValidator.ScriptPubKeyFor("not-an-address", SparkNetwork.Mainnet);

        regtest.Should().Throw<SparkConfigurationException>();
        garbage.Should().Throw<SparkConfigurationException>();
    }

    [Test]
    public void Rejects_missing_or_unparseable_transactions()
    {
        var f = Build();

        var missing = () => CoopExitValidator.Validate(null, f.ConnectorHex, f.ExitTxidHex, Payout, 9_000, 2, SparkNetwork.Mainnet);
        var notHex = () => CoopExitValidator.Validate("zz", f.ConnectorHex, f.ExitTxidHex, Payout, 9_000, 2, SparkNetwork.Mainnet);
        var truncated = () => CoopExitValidator.Validate(f.ExitHex[..20], f.ConnectorHex, f.ExitTxidHex, Payout, 9_000, 2, SparkNetwork.Mainnet);

        missing.Should().Throw<SparkUntrustedResponseException>().WithMessage("*missing raw_coop_exit_transaction*");
        notHex.Should().Throw<SparkUntrustedResponseException>().WithMessage("*not valid hex*");
        truncated.Should().Throw<SparkUntrustedResponseException>().WithMessage("*not a parseable transaction*");
    }

    [Test]
    public void Tolerates_witness_serialised_transactions_with_empty_witnesses()
    {
        // An unsigned connector transaction may come back with the segwit marker and all-empty
        // witness stacks, a form NBitcoin refuses as non-canonical; the validator must still read it.
        var f = Build();
        var legacy = f.Connector.ToHex();
        var witnessForm = legacy[..8] + "0001" + legacy[8..^8] + "00" + legacy[^8..];

        var validated = CoopExitValidator.Validate(f.ExitHex, witnessForm, f.ExitTxidHex, Payout, 9_000, 2, SparkNetwork.Mainnet);

        validated.ConnectorTxidInternal.Should().Equal(f.Connector.GetHash().ToBytes());
    }

    [Test]
    public void TxidMatches_accepts_either_byte_order_and_nothing_else()
    {
        var txid = Build().Exit.GetHash().ToBytes();
        var reversed = Enumerable.Reverse(txid).ToArray();
        var other = new byte[32];

        CoopExitValidator.TxidMatches(txid, txid).Should().BeTrue();
        CoopExitValidator.TxidMatches(txid, reversed).Should().BeTrue();
        CoopExitValidator.TxidMatches(txid, other).Should().BeFalse();
        CoopExitValidator.TxidMatches(txid, txid.AsSpan(0, 31)).Should().BeFalse();
    }

    [TestCase(100L, null, 1_000L, 100L, TestName = "no cap: the quote bounds the fee")]
    [TestCase(100L, 200L, 1_000L, 200L, TestName = "a cap above the quote is the bound")]
    [TestCase(100L, 100L, 1_000L, 100L, TestName = "a cap equal to the quote is accepted")]
    public void ResolveFeeCap_returns_the_bound(long quoted, long? max, long amount, long expected)
    {
        CoopExitValidator.ResolveFeeCap(quoted, max, amount).Should().Be(expected);
    }

    [Test]
    public void ResolveFeeCap_refuses_a_quote_above_the_cap()
    {
        var act = () => CoopExitValidator.ResolveFeeCap(300, 200, 1_000);

        act.Should().Throw<FeeExceedsLimitException>()
            .Which.Should().Match<FeeExceedsLimitException>(e => e.FeeSats == 300 && e.MaxFeeSats == 200);
    }

    [Test]
    public void ResolveFeeCap_refuses_a_fee_that_would_consume_the_amount()
    {
        var act = () => CoopExitValidator.ResolveFeeCap(100, null, 100);

        act.Should().Throw<FeeExceedsLimitException>()
            .Which.Should().Match<FeeExceedsLimitException>(e => e.FeeSats == 100 && e.MaxFeeSats == 99);
    }

    [Test]
    public void ResolveFeeCap_validates_its_inputs()
    {
        var negativeQuote = () => CoopExitValidator.ResolveFeeCap(-1, null, 1_000);
        var negativeCap = () => CoopExitValidator.ResolveFeeCap(100, -1, 1_000);
        var zeroAmount = () => CoopExitValidator.ResolveFeeCap(100, null, 0);

        negativeQuote.Should().Throw<SparkUntrustedResponseException>();
        negativeCap.Should().Throw<ArgumentOutOfRangeException>();
        zeroAmount.Should().Throw<ArgumentOutOfRangeException>();
    }
}
