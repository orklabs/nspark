using System.Security.Cryptography;
using NBitcoin;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for the raw-transaction helpers a cooperative exit uses to append the connector input
/// to each refund transaction. NBitcoin builds the expected serialisations independently.
/// </summary>
[TestFixture]
public sealed class WithdrawalServiceTests
{
    private static readonly byte[] ConnectorTxid = SHA256.HashData("connector"u8.ToArray());

    [Test]
    public void AddInputToRawTx_appends_a_final_sequence_input_to_a_legacy_transaction()
    {
        var tx = SampleTx(withWitness: false);

        var actual = WithdrawalService.AddInputToRawTx(tx.ToBytes(), WithdrawalService.MakeConnectorInputBytes(ConnectorTxid, 2));

        var expected = tx.Clone();
        expected.Inputs.Add(new TxIn(new OutPoint(new uint256(ConnectorTxid), 2)) { Sequence = Sequence.Final });
        actual.Should().Equal(expected.ToBytes());

        var parsed = Transaction.Load(actual, Network.Main);
        parsed.Inputs.Should().HaveCount(2);
        parsed.Inputs[1].PrevOut.Hash.ToBytes().Should().Equal(ConnectorTxid, "the txid is used in internal byte order");
        parsed.Inputs[1].PrevOut.N.Should().Be(2);
        parsed.Inputs[1].Sequence.Value.Should().Be(0xFFFFFFFF);
        parsed.Outputs.Should().HaveCount(2);
    }

    [Test]
    public void AddInputToRawTx_preserves_witness_serialisation_and_existing_witnesses()
    {
        var tx = SampleTx(withWitness: true);

        var actual = WithdrawalService.AddInputToRawTx(tx.ToBytes(), WithdrawalService.MakeConnectorInputBytes(ConnectorTxid, 0));

        var expected = tx.Clone();
        expected.Inputs.Add(new TxIn(new OutPoint(new uint256(ConnectorTxid), 0)) { Sequence = Sequence.Final });
        actual.Should().Equal(expected.ToBytes());

        var parsed = Transaction.Load(actual, Network.Main);
        parsed.HasWitness.Should().BeTrue();
        parsed.Inputs[0].WitScript.Should().Be(tx.Inputs[0].WitScript);
        parsed.Inputs[1].WitScript.PushCount.Should().Be(0);
    }

    [Test]
    public void ComputeTxId_and_StripWitness_ignore_witness_data()
    {
        var tx = SampleTx(withWitness: true);

        WithdrawalService.ComputeTxId(tx.ToBytes()).Should().Equal(tx.GetHash().ToBytes());
        WithdrawalService.StripWitness(tx.ToBytes()).Should().Equal(tx.WithOptions(TransactionOptions.None).ToBytes());

        var legacy = SampleTx(withWitness: false);
        WithdrawalService.ComputeTxId(legacy.ToBytes()).Should().Equal(legacy.GetHash().ToBytes());
        WithdrawalService.StripWitness(legacy.ToBytes()).Should().Equal(legacy.ToBytes(), "a legacy transaction is returned as is");
    }

    [Test]
    public void ParseTxOutput_reads_the_requested_output()
    {
        var tx = SampleTx(withWitness: true);

        var (script, value) = WithdrawalService.ParseTxOutput(tx.ToBytes(), 1);

        script.Should().Equal(tx.Outputs[1].ScriptPubKey.ToBytes());
        value.Should().Be((ulong)tx.Outputs[1].Value.Satoshi);

        var act = () => WithdrawalService.ParseTxOutput(tx.ToBytes(), 5);
        act.Should().Throw<Exception>();
    }

    [Test]
    public void IsZeroTimelockNode_reads_the_low_16_bits_of_the_first_input_sequence()
    {
        var zero = SampleTx(withWitness: false, sequence: 1u << 30);
        var nonZero = SampleTx(withWitness: false, sequence: (1u << 30) | 2000);

        WithdrawalService.IsZeroTimelockNode(zero.ToBytes()).Should().BeTrue();
        WithdrawalService.IsZeroTimelockNode(nonZero.ToBytes()).Should().BeFalse();
    }

    private static Transaction SampleTx(bool withWitness, uint sequence = 0xFFFFFFFE)
    {
        var tx = Transaction.Create(Network.Main);
        tx.Version = 3;
        tx.Inputs.Add(new TxIn(new OutPoint(new uint256(SHA256.HashData("parent"u8.ToArray())), 0)) { Sequence = new Sequence(sequence) });
        if (withWitness)
        {
            tx.Inputs[0].WitScript = new WitScript(Op.GetPushOp(Enumerable.Repeat((byte)0x42, 64).ToArray()));
        }
        tx.Outputs.Add(new TxOut(Money.Satoshis(9_045), new Key().PubKey.GetTaprootFullPubKey().ScriptPubKey));
        tx.Outputs.Add(new TxOut(Money.Zero, new Script(new byte[] { 0x51, 0x02, 0x4e, 0x73 })));
        return tx;
    }
}
