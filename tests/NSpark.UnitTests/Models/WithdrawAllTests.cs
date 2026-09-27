using Google.Protobuf;
using NSpark.Models;
using NSpark.Proto;
using NSpark.Services;
using NSpark.UnitTests.Services;

namespace NSpark.UnitTests.Models;

/// <summary>What a withdraw-all would move and what it leaves behind, and the leaf flags it selects by.</summary>
[TestFixture]
public sealed class WithdrawAllTests
{
    [Test]
    public void A_quote_derives_payout_frozen_share_and_fee_coverage()
    {
        var quote = new WithdrawAllQuote(SpendableSats: 9_700, QuotedFeeSats: 1_950, FrozenSats: 300, UnrenewedSats: 0, LockedSats: 0, IncomingSats: 0, LeafCount: 4);
        quote.EstimatedPayoutSats.Should().Be(7_750);
        quote.FrozenFraction.Should().BeApproximately(0.03, 1e-9);
        quote.CoversFee.Should().BeTrue();

        var tiny = new WithdrawAllQuote(1_000, 1_950, 0, 0, 0, 0, 1);
        tiny.CoversFee.Should().BeFalse();
        tiny.EstimatedPayoutSats.Should().BeNegative();
        tiny.FrozenFraction.Should().Be(0);

        var onlyFrozen = new WithdrawAllQuote(0, 0, 66, 0, 0, 0, 0);
        onlyFrozen.FrozenFraction.Should().Be(1);
        onlyFrozen.CoversFee.Should().BeFalse();

        var empty = new WithdrawAllQuote(0, 0, 0, 0, 0, 0, 0);
        empty.FrozenFraction.Should().Be(0);
        empty.CoversFee.Should().BeFalse();
    }

    [Test]
    public void A_result_reports_the_fee_the_SSP_actually_took()
    {
        new WithdrawAllResult("ab", SentSats: 5_000, PayoutSats: 3_290, FrozenSats: 66, UnrenewedSats: 0, LockedSats: 0, UnclaimedSats: 0)
            .FeeSats.Should().Be(1_710);
    }

    private static SparkLeaf Leaf(string id, uint timelock) => new(id, "tree", 1, "AVAILABLE")
    {
        Node = new TreeNode
        {
            Id = id,
            NodeTx = ByteString.CopyFrom(TestTransactions.NodeTx(0)),
            RefundTx = ByteString.CopyFrom(TestTransactions.NodeTx(timelock)),
        },
    };

    [Test]
    public void Leaf_flags_follow_the_rounded_timelock_floor_and_the_renewal_minimum()
    {
        Leaf("a", 0).IsSpendable.Should().BeFalse();
        Leaf("b", 100).IsSpendable.Should().BeFalse();
        // The floor applies to the timelock rounded down to the interval: 101…199 count as 100.
        Leaf("c", 101).IsSpendable.Should().BeFalse();
        Leaf("c", 199).IsSpendable.Should().BeFalse();
        Leaf("c", 200).IsSpendable.Should().BeTrue();
        Leaf("c", 740).IsSpendable.Should().BeTrue();
        Leaf("d", 2000).IsSpendable.Should().BeTrue();

        // Frozen means below the renewal minimum; a leaf at exactly 100 is renewable.
        Leaf("a", 0).IsFrozen.Should().BeTrue();
        Leaf("a", 99).IsFrozen.Should().BeTrue();
        Leaf("b", 100).IsFrozen.Should().BeFalse();
        Leaf("c", 150).IsFrozen.Should().BeFalse();
        Leaf("d", 2000).IsFrozen.Should().BeFalse();
        Leaf("a", 99).IsRenewable.Should().BeFalse();
        Leaf("b", 100).IsRenewable.Should().BeTrue();
        Leaf("c", 199).IsRenewable.Should().BeTrue();
        Leaf("d", 200).IsRenewable.Should().BeFalse();

        var garbage = new SparkLeaf("garbage", "t", 5, "AVAILABLE") { Node = new TreeNode() };
        garbage.IsSpendable.Should().BeFalse();
        garbage.IsRenewable.Should().BeFalse();
        garbage.IsFrozen.Should().BeTrue();

        // The public flag and the renewal split agree.
        var leaves = new[] { Leaf("x", 0), Leaf("y", 100), Leaf("z", 150), Leaf("w", 250) };
        leaves.Where(l => l.IsSpendable).Select(l => l.Id).Should().Equal("w");
        var (renewable, stuck) = RenewalService.RenewalCandidates(leaves);
        renewable.Select(l => l.Id).Should().Equal("y", "z");
        stuck.Select(l => l.Id).Should().Equal("x");
    }
}
