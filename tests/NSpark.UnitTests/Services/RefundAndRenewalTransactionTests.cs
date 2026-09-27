using Google.Protobuf;
using NBitcoin;
using NSpark.Bitcoin;
using NSpark.Exceptions;
using NSpark.Proto;
using NSpark.Services;
using uniffi.spark_frost;

namespace NSpark.UnitTests.Services;

/// <summary>Shared transaction builders for the refund and renewal tests.</summary>
internal static class TestTransactions
{
    public static byte[] P2trScript(byte fill) => [0x51, 0x20, .. Enumerable.Repeat(fill, 32)];

    /// <summary>The script a node transaction for <paramref name="verifyingKey"/> pays: P2TR of its BIP-86 tweak.</summary>
    public static byte[] P2tr(byte[] verifyingKey) => [0x51, 0x20, .. SparkFrostMethods.GetTaprootPubkey(verifyingKey)[1..]];

    public static byte[] Tx(uint sequence, params RawTransaction.Output[] outputs) => Tx(sequence, 0xAB, outputs);

    public static byte[] Tx(uint sequence, byte prevTxidFill, params RawTransaction.Output[] outputs) => new RawTransaction(
            3,
            [new RawTransaction.Input(Enumerable.Repeat(prevTxidFill, 32).ToArray(), 0, sequence: sequence)],
            [.. outputs],
            0,
            false)
        .Serialize(includeWitness: true);

    /// <summary>A minimal node transaction: one input with the given timelock (bit 30 set), one P2TR output.</summary>
    public static byte[] NodeTx(uint timelock, ulong value = 10_000, byte tag = 0x11) =>
        Tx((1u << 30) | timelock, tag, new RawTransaction.Output(value, P2trScript(tag)));
}

/// <summary>
/// The refunds a send or a claim signs for a leaf. The operators' rule
/// (<c>base_transfer_handler.go</c>: "zero nodes must not have a direct refund tx", with
/// <c>IsZeroNode</c> = node transaction timelock 0) and the reference SDK's <c>isZeroNode</c> check
/// decide whether a direct refund is built.
/// </summary>
[TestFixture]
public class RefundConstructionTests
{
    private static readonly byte[] s_receiver = new Key().PubKey.ToBytes();

    private static TreeNode Leaf(uint nodeTimelock, bool withDirect)
    {
        var node = new TreeNode
        {
            Id = "leaf",
            NodeTx = ByteString.CopyFrom(TestTransactions.NodeTx(nodeTimelock)),
            RefundTx = ByteString.CopyFrom(TestTransactions.NodeTx(2000, value: 9_045, tag: 0x22)),
        };
        if (withDirect)
        {
            node.DirectTx = ByteString.CopyFrom(TestTransactions.NodeTx(nodeTimelock + 50, tag: 0x33));
        }

        return node;
    }

    private static SparkRefundTxTrio Trio(TreeNode node)
    {
        var (cpfp, direct) = TimelockHelper.ComputeNextSequences(node.RefundTx.ToByteArray(), "test");
        return TimelockHelper.LeafRefundTrio(node, s_receiver, "mainnet", cpfp, direct);
    }

    [Test]
    public void A_zero_timelock_node_gets_no_direct_refund_even_with_a_direct_node_transaction()
    {
        // Zero-timelock renewal leaves this shape: node transaction at timelock 0 plus a direct one.
        var zero = Leaf(0, withDirect: true);

        TimelockHelper.IsZeroTimelockNode(zero.NodeTx.ToByteArray()).Should().BeTrue();
        TimelockHelper.DirectNodeTxForRefund(zero).Should().BeNull();
        var refunds = Trio(zero);
        refunds.DirectRefund.Should().BeNull();
        var nodeTxid = RawTransaction.Parse(zero.NodeTx.Span).Txid;
        RawTransaction.Parse(refunds.CpfpRefund.Tx).Inputs[0].PreviousTxid.Should().Equal(nodeTxid);
        RawTransaction.Parse(refunds.DirectFromCpfpRefund.Tx).Inputs[0].PreviousTxid.Should().Equal(nodeTxid);
    }

    [Test]
    public void A_timelocked_node_with_a_direct_node_transaction_gets_a_direct_refund_spending_it()
    {
        var node = Leaf(1000, withDirect: true);

        TimelockHelper.IsZeroTimelockNode(node.NodeTx.ToByteArray()).Should().BeFalse();
        TimelockHelper.DirectNodeTxForRefund(node).Should().Equal(node.DirectTx.ToByteArray());
        var direct = Trio(node).DirectRefund;
        direct.Should().NotBeNull();
        var directTx = RawTransaction.Parse(direct!.Tx);
        directTx.Inputs[0].PreviousTxid.Should().Equal(RawTransaction.Parse(node.DirectTx.Span).Txid);
        (directTx.Inputs[0].Sequence & 0xFFFF).Should().Be(1950u);
    }

    [Test]
    public void A_leaf_without_a_direct_node_transaction_gets_no_direct_refund()
    {
        var node = Leaf(1000, withDirect: false);

        TimelockHelper.DirectNodeTxForRefund(node).Should().BeNull();
        Trio(node).DirectRefund.Should().BeNull();
    }
}

/// <summary>
/// Refund timelock arithmetic, pinned to the operators' own test vectors (<c>validation_test.go</c>:
/// RoundDownToTimelockInterval, ValidateSequence misaligned/aligned/floor) and the reference SDK's
/// (<c>transaction-construction.test.ts</c>: createDecrementedTimelockRefundTxs).
/// </summary>
[TestFixture]
public class TimelockArithmeticTests
{
    private static byte[] RefundTx(uint timelock) => TestTransactions.NodeTx(timelock);

    [TestCase(100u, 100u)]
    [TestCase(1000u, 1000u)]
    [TestCase(740u, 700u)]
    [TestCase(670u, 600u)]
    [TestCase(0u, 0u)]
    [TestCase(1970u, 1900u)]
    [TestCase(1870u, 1800u)]
    public void Timelocks_round_down_to_the_100_block_interval_like_the_operators(uint timelock, uint rounded)
    {
        TimelockHelper.RoundedTimelock(timelock).Should().Be(rounded);
    }

    [TestCase(2000u, 1900u, TestName = "reference SDK: decrements by TIME_LOCK_INTERVAL")]
    [TestCase(550u, 400u, TestName = "reference SDK: a non-aligned 550 rounds to 500, then decrements")]
    [TestCase(740u, 600u, TestName = "operators: 600 accepted for a 740 leaf, 640 rejected")]
    [TestCase(700u, 600u, TestName = "operators: aligned")]
    [TestCase(1000u, 900u, TestName = "operators: 1000 expects 900")]
    [TestCase(200u, 100u, TestName = "the last decrement")]
    public void The_next_refund_is_the_rounded_timelock_minus_100_and_the_direct_refunds_50_above_it(uint current, uint next)
    {
        var (cpfp, direct) = TimelockHelper.ComputeNextSequences(RefundTx(current), "test");

        cpfp.Should().Be((1u << 30) | next);
        direct.Should().Be((1u << 30) | (next + 50));
    }

    [Test]
    public void Every_timelock_the_operators_accept_decrements_to_its_rounded_value_minus_100()
    {
        for (var current = 200u; current <= 2100; current++)
        {
            var (cpfp, _) = TimelockHelper.ComputeNextSequences(RefundTx(current), "test");
            (cpfp & 0xFFFF).Should().Be(current - (current % 100) - 100, "current {0}", current);
        }
    }

    [Test]
    public void A_refund_timelock_that_rounds_to_100_or_less_cannot_be_decremented_without_a_renewal()
    {
        foreach (var current in new uint[] { 0, 50, 99, 100, 101, 150, 199 })
        {
            var act = () => TimelockHelper.ComputeNextSequences(RefundTx(current), "test");
            act.Should().Throw<SparkLeafTimelockExhaustedException>(current.ToString(System.Globalization.CultureInfo.InvariantCulture));
            TimelockHelper.TimelockCanDecrement(RefundTx(current)).Should().BeFalse();
        }

        TimelockHelper.TimelockCanDecrement(RefundTx(200)).Should().BeTrue();
        TimelockHelper.TimelockCanDecrement(RefundTx(740)).Should().BeTrue();
    }

    [TestCase(2000u, 1970u, 1985u)]
    [TestCase(740u, 710u, 725u)]
    [TestCase(1234u, 1204u, 1219u)]
    public void Lightning_HTLC_refunds_are_not_rounded_but_refund_sequence_minus_30_and_minus_15(uint current, uint cpfp, uint direct)
    {
        TimelockHelper.HtlcSequences(RefundTx(current), "test").Should().Be(((1u << 30) | cpfp, (1u << 30) | direct));
    }

    [Test]
    public void An_HTLC_refund_needs_a_timelock_above_one_interval()
    {
        var act = () => TimelockHelper.HtlcSequences(RefundTx(100), "test");

        act.Should().Throw<SparkLeafTimelockExhaustedException>();
    }
}

/// <summary>
/// Renewal transactions must be exactly what the operators rebuild and compare byte for byte
/// (<c>renew_leaf_handler.go</c>): the new node (or split node) spends the parent's output at the
/// leaf's <c>vout</c> and pays P2TR(leaf verifying key); the zero-timelock variant spends the leaf's
/// own node transaction. Renewals used to spend parent output 0, so a leaf at another output
/// could never be renewed.
/// </summary>
[TestFixture]
public class RenewalTransactionTests
{
    private static readonly byte[] s_verifyingKey = new Key().PubKey.ToBytes();
    private static readonly byte[] s_signingPublicKey = new Key().PubKey.ToBytes();
    private static readonly byte[] s_otherKey = new Key().PubKey.ToBytes();

    /// <summary>A parent node transaction that split into a sibling at output 0 and our leaf at output 1.</summary>
    private static (TreeNode Parent, TreeNode Leaf) ParentAndLeaf()
    {
        var parent = new TreeNode
        {
            Id = "parent",
            NodeTx = ByteString.CopyFrom(TestTransactions.Tx(
                1u << 30,
                new RawTransaction.Output(3_000, TestTransactions.P2tr(s_otherKey)),
                new RawTransaction.Output(5_000, TestTransactions.P2tr(s_verifyingKey)))),
        };
        var leaf = new TreeNode
        {
            Id = "leaf",
            ParentNodeId = parent.Id,
            Vout = 1,
            VerifyingPublicKey = ByteString.CopyFrom(s_verifyingKey),
            NodeTx = ByteString.CopyFrom(TestTransactions.Tx((1u << 30) | 2000, new RawTransaction.Output(5_000, TestTransactions.P2tr(s_verifyingKey)))),
            RefundTx = ByteString.CopyFrom(TestTransactions.Tx((1u << 30) | 150, new RawTransaction.Output(5_000, TestTransactions.P2tr(s_signingPublicKey)))),
        };
        return (parent, leaf);
    }

    [Test]
    public void The_leafs_node_address_is_its_verifying_key_with_the_BIP_86_tweak()
    {
        // BIP-86 test vector: internal key cc8a4bc6… → bc1p5cyxnuxmeuwuvkwfem96lqzszd02n6xdcjrs20cac6yqjjwudpxqkedrcr
        var internalKey = Convert.FromHexString("02cc8a4bc64d897bddc5fbc2f670f7a8ba0b386779106cf1223c6fc5d7cd6fc115");

        RenewalService.LeafNodeAddress(internalKey, SparkNetwork.Mainnet).Should().Be("bc1p5cyxnuxmeuwuvkwfem96lqzszd02n6xdcjrs20cac6yqjjwudpxqkedrcr");
        RenewalService.LeafNodeAddress(internalKey, SparkNetwork.Regtest).Should().StartWith("bcrt1p");
    }

    [Test]
    public void Refund_renewal_spends_the_parents_output_at_the_leafs_vout_and_pays_the_leafs_node_address()
    {
        var (parent, leaf) = ParentAndLeaf();

        var txs = RenewalService.RefundRenewalTransactions(leaf, parent, s_signingPublicKey, SparkNetwork.Mainnet);

        var parentTxid = RawTransaction.Parse(parent.NodeTx.Span).Txid;
        foreach (var nodeTx in new[] { txs.Node.Cpfp.Tx, txs.Node.Direct.Tx })
        {
            var parsed = RawTransaction.Parse(nodeTx);
            parsed.Inputs[0].PreviousTxid.Should().Equal(parentTxid);
            parsed.Inputs[0].PreviousIndex.Should().Be(1u);
            parsed.Outputs[0].ScriptPubKey.Should().Equal(TestTransactions.P2tr(s_verifyingKey));
        }

        RawTransaction.Parse(txs.Node.Cpfp.Tx).Outputs[0].Value.Should().Be(5_000UL);
        RawTransaction.Parse(txs.Node.Cpfp.Tx).Inputs[0].Sequence.Should().Be((1u << 30) | 1900);
        (RawTransaction.Parse(txs.Refunds.CpfpRefund.Tx).Inputs[0].Sequence & 0xFFFF).Should().Be(2000u);
        txs.Split.Should().BeNull();
    }

    [Test]
    public void Node_renewals_split_node_spends_the_parents_output_at_the_leafs_vout()
    {
        var (parent, leaf) = ParentAndLeaf();

        var txs = RenewalService.NodeRenewalTransactions(leaf, parent, s_signingPublicKey, SparkNetwork.Mainnet);

        txs.Split.Should().NotBeNull();
        var splitTx = RawTransaction.Parse(txs.Split!.Cpfp.Tx);
        splitTx.Inputs[0].PreviousTxid.Should().Equal(RawTransaction.Parse(parent.NodeTx.Span).Txid);
        splitTx.Inputs[0].PreviousIndex.Should().Be(1u);
        (splitTx.Inputs[0].Sequence & 0xFFFF).Should().Be(0u);
        splitTx.Outputs[0].ScriptPubKey.Should().Equal(TestTransactions.P2tr(s_verifyingKey));
        splitTx.Outputs[0].Value.Should().Be(5_000UL);
        var nodeTx = RawTransaction.Parse(txs.Node.Cpfp.Tx);
        nodeTx.Inputs[0].PreviousTxid.Should().Equal(splitTx.Txid);
        nodeTx.Inputs[0].PreviousIndex.Should().Be(0u);
        (nodeTx.Inputs[0].Sequence & 0xFFFF).Should().Be(2000u);
        nodeTx.Outputs[0].ScriptPubKey.Should().Equal(TestTransactions.P2tr(s_verifyingKey));
    }

    [Test]
    public void Zero_timelock_renewal_spends_the_leafs_own_node_transaction()
    {
        var (_, leaf) = ParentAndLeaf();
        leaf.NodeTx = ByteString.CopyFrom(TestTransactions.Tx(1u << 30, new RawTransaction.Output(5_000, TestTransactions.P2tr(s_verifyingKey))));

        var txs = RenewalService.ZeroTimelockRenewalTransactions(leaf, s_signingPublicKey, SparkNetwork.Mainnet);

        var nodeTx = RawTransaction.Parse(txs.Node.Cpfp.Tx);
        nodeTx.Inputs[0].PreviousTxid.Should().Equal(RawTransaction.Parse(leaf.NodeTx.Span).Txid);
        nodeTx.Inputs[0].PreviousIndex.Should().Be(0u);
        (nodeTx.Inputs[0].Sequence & 0xFFFF).Should().Be(0u);
        nodeTx.Outputs[0].ScriptPubKey.Should().Equal(TestTransactions.P2tr(s_verifyingKey));
        txs.Refunds.DirectRefund.Should().BeNull();
    }

    [TestCase(0u, "ZeroTimelock")]
    [TestCase(1u << 30, "ZeroTimelock")]
    [TestCase(0xFFFF_FFFFu, "ZeroTimelock", TestName = "A legacy deposit root's final sequence is renewed like a zero-timelock node")]
    [TestCase(0xFFFF_FFFEu, "ZeroTimelock")]
    [TestCase((1u << 30) | 100, "NodeTimelock")]
    [TestCase((1u << 30) | 199, "NodeTimelock")]
    [TestCase((1u << 30) | 200, "RefundTimelock")]
    [TestCase((1u << 30) | 2000, "RefundTimelock")]
    public void The_renewal_variant_follows_the_operators_rules(uint nodeSequence, string variant)
    {
        RenewalService.Variant(nodeSequence).Should().Be(Enum.Parse<RenewalService.RenewalVariant>(variant));
    }

    [Test]
    public void A_renewal_is_keyed_by_the_txid_of_the_refund_it_replaces_as_in_the_reference_SDK()
    {
        var refund = new RawTransaction(
            3,
            [new RawTransaction.Input(Enumerable.Repeat((byte)0x21, 32).ToArray(), 0, sequence: 150)],
            [new RawTransaction.Output(1_000, TestTransactions.P2trScript(0x07))],
            0,
            false);
        var node = new TreeNode { RefundTx = ByteString.CopyFrom(refund.Serialize(includeWitness: false)) };

        RenewalService.RenewalIdempotencyKey(node).Should().Be(refund.TxidHex).And.HaveLength(64);
        node.RefundTx = ByteString.Empty;
        var act = () => RenewalService.RenewalIdempotencyKey(node);
        act.Should().Throw<SparkUntrustedResponseException>();
    }
}
