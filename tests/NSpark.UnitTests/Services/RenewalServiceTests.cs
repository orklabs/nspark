using Google.Protobuf;
using NSpark.Models;
using NSpark.Proto;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for <see cref="RenewalService"/>'s pure helpers: the P2TR address
/// encoder (BIP-86 reference vector) and <see cref="SparkLeaf.RefundTimelockBlocks"/>.
/// </summary>
[TestFixture]
public sealed class RenewalServiceTests
{
    // BIP-86 first receive address: scriptPubKey → bech32m address.
    private const string Bip86ScriptHex =
        "5120a60869f0dbcf1dc659c9cecbaf8050135ea9e8cdc487053f1dc6880949dc684c";

    private const string Bip86Address =
        "bc1p5cyxnuxmeuwuvkwfem96lqzszd02n6xdcjrs20cac6yqjjwudpxqkedrcr";

    [Test]
    public void P2trAddress_matches_the_bip86_reference_vector()
    {
        var script = Convert.FromHexString(Bip86ScriptHex);

        RenewalService.P2trAddress(script, "mainnet").Should().Be(Bip86Address);
    }

    [TestCase("regtest", "bcrt1p")]
    [TestCase("testnet", "tb1p")]
    public void P2trAddress_uses_the_network_hrp(string network, string prefix)
    {
        var script = Convert.FromHexString(Bip86ScriptHex);

        RenewalService.P2trAddress(script, network).Should().StartWith(prefix);
    }

    [Test]
    public void P2trAddress_rejects_non_p2tr_scripts()
    {
        // P2WPKH: OP_0 <20 bytes> — wrong witness version and length.
        var p2wpkh = new byte[22];
        p2wpkh[0] = 0x00;
        p2wpkh[1] = 0x14;

        var act = () => RenewalService.P2trAddress(p2wpkh, "mainnet");

        act.Should().Throw<InvalidOperationException>().WithMessage("*not P2TR*");
    }

    [Test]
    public void RefundTimelockBlocks_reads_the_low_16_bits_of_the_refund_sequence()
    {
        var leaf = MakeLeaf(refundTx: MakeRawTx((1u << 30) | 1900));

        leaf.RefundTimelockBlocks.Should().Be(1900);
    }

    [Test]
    public void RefundTimelockBlocks_reads_the_refund_tx_only()
    {
        // A leaf's timelock is its refund's; without one it reads as exhausted rather than
        // borrowing the node transaction's.
        var leaf = MakeLeaf(refundTx: null, nodeTx: MakeRawTx(2000));

        leaf.RefundTimelockBlocks.Should().Be(0);
        leaf.IsFrozen.Should().BeTrue();
        leaf.IsSpendable.Should().BeFalse();
    }

    private static SparkLeaf MakeLeaf(byte[]? refundTx, byte[]? nodeTx = null)
    {
        var node = new TreeNode
        {
            Id = "leaf-1",
            TreeId = "tree-1",
            Value = 1000,
            Status = "AVAILABLE",
            NodeTx = ByteString.CopyFrom(nodeTx ?? MakeRawTx(0)),
        };
        if (refundTx != null)
        {
            node.RefundTx = ByteString.CopyFrom(refundTx);
        }

        return new SparkLeaf("leaf-1", "tree-1", 1000, "AVAILABLE") { Node = node };
    }

    private static byte[] MakeRawTx(uint sequence)
    {
        var tx = new byte[4 + 1 + 36 + 1 + 4 + 1 + 4]; // version, 1 input, 0 outputs, locktime
        tx[0] = 0x02; // version 2
        tx[4] = 0x01; // one input
        BitConverter.GetBytes(sequence).CopyTo(tx, 42);
        return tx;
    }
}
