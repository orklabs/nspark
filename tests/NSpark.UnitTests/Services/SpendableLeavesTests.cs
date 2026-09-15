using Google.Protobuf;
using NSpark.Models;
using NSpark.Proto;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for the leaf spendability rules every spend path and the balance report share: a leaf
/// at the timelock floor is frozen, one just above it is renewable, and neither state may sink
/// a send that other leaves could cover.
/// </summary>
[TestFixture]
public sealed class SpendableLeavesTests
{
    private const uint Bit30 = 1u << 30;

    [TestCase(2000u, true, false)]
    [TestCase(200u, true, false)]
    [TestCase(199u, true, true)]
    [TestCase(150u, true, true)]
    [TestCase(101u, true, true)]
    [TestCase(100u, false, true)]
    [TestCase(99u, false, false)]
    [TestCase(0u, false, false)]
    [TestCase(Bit30 | 150u, true, true)]
    [TestCase(Bit30 | 100u, false, true)]
    public void IsSpendable_and_IsRenewable_follow_the_coordinator_thresholds(uint sequence, bool spendable, bool renewable)
    {
        var leaf = MakeLeaf("leaf", 1_000, sequence);

        leaf.IsSpendable.Should().Be(spendable);
        leaf.IsRenewable.Should().Be(renewable);
    }

    [Test]
    public void Unparseable_refund_transactions_read_as_exhausted_instead_of_throwing()
    {
        var node = new TreeNode
        {
            Id = "leaf",
            TreeId = "tree",
            Value = 1_000,
            Status = "AVAILABLE",
            NodeTx = ByteString.CopyFrom(MakeRawTx(2000)),
            RefundTx = ByteString.CopyFrom([1, 2, 3]),
        };
        var leaf = new SparkLeaf("leaf", "tree", 1_000, "AVAILABLE") { Node = node };
        var detached = new SparkLeaf("leaf", "tree", 1_000, "AVAILABLE");

        leaf.RefundTimelockBlocks.Should().Be(0);
        leaf.IsSpendable.Should().BeFalse();
        leaf.IsRenewable.Should().BeFalse();
        detached.RefundTimelockBlocks.Should().Be(0);
    }

    [Test]
    public void SummarizeNodes_reports_frozen_leaves_separately_from_available()
    {
        var nodes = new Dictionary<string, TreeNode>
        {
            ["spendable"] = Node("spendable", 1_000, "AVAILABLE", 2000),
            ["frozen"] = Node("frozen", 500, "AVAILABLE", 100),
            ["locked"] = Node("locked", 300, "TRANSFER_LOCKED", 2000),
            ["creating"] = Node("creating", 200, "CREATING", 2000),
            ["other"] = Node("other", 50, "SPLITTED", 2000),
        };

        var summary = BalanceService.SummarizeNodes(nodes);

        summary.Available.Should().Be(1_000);
        summary.Frozen.Should().Be(500);
        summary.Owned.Should().Be(1_800);
        summary.Creating.Should().Be(200);
        summary.Leaves.Select(l => l.Id).Should().BeEquivalentTo(["spendable", "frozen"]);
    }

    [Test]
    public void SatsBalance_derives_locked_from_the_other_figures()
    {
        var balance = new SatsBalance(Available: 1_000, Owned: 1_800, Incoming: 0, Frozen: 500);

        balance.Locked.Should().Be(300);
        new SatsBalance(1, 1, 0).Frozen.Should().Be(0, "the fourth field defaults to zero for existing callers");
    }

    [Test]
    public void TryExactSelection_never_picks_a_frozen_leaf()
    {
        var leaves = new List<SparkLeaf>
        {
            MakeLeaf("frozen-1000", 1_000, 100),
            MakeLeaf("ok-600", 600, 1900),
            MakeLeaf("ok-400", 400, 2000),
        };

        var selected = SwapService.TryExactSelection(leaves, 1_000);

        selected.Should().NotBeNull();
        selected!.Select(l => l.Id).Should().BeEquivalentTo(["ok-600", "ok-400"]);
        SwapService.TryExactSelection(leaves, 1_400).Should().BeNull("only 1000 spendable sats exist");
    }

    private static TreeNode Node(string id, ulong value, string status, uint refundSequence) => new()
    {
        Id = id,
        TreeId = "tree",
        Value = value,
        Status = status,
        NodeTx = ByteString.CopyFrom(MakeRawTx(0)),
        RefundTx = ByteString.CopyFrom(MakeRawTx(refundSequence)),
    };

    private static SparkLeaf MakeLeaf(string id, long value, uint refundSequence) =>
        new(id, "tree", value, "AVAILABLE") { Node = Node(id, (ulong)value, "AVAILABLE", refundSequence) };

    /// <summary>Minimal single-input legacy tx: version, one input with an empty script and the given nSequence.</summary>
    private static byte[] MakeRawTx(uint sequence)
    {
        var tx = new byte[4 + 1 + 36 + 1 + 4];
        tx[0] = 0x02;
        tx[4] = 0x01;
        BitConverter.GetBytes(sequence).CopyTo(tx, 42);
        return tx;
    }
}
