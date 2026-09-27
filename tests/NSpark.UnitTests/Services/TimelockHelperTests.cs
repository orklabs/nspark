using NSpark.Exceptions;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for <see cref="TimelockHelper"/> — the shared refund-timelock
/// decrement used by every spend path.
/// </summary>
/// <remarks>
/// The floor guard is the load-bearing part: <c>uint</c> subtraction wraps
/// silently in C#, so a leaf at the floor would otherwise produce a garbage
/// sequence (65,436+) instead of a typed error telling the caller to renew.
/// </remarks>
[TestFixture]
public sealed class TimelockHelperTests
{
    private const uint Bit30 = 1u << 30;

    [Test]
    public void ComputeNextSequences_decrements_one_interval_and_offsets_direct()
    {
        var (cpfp, direct) = TimelockHelper.ComputeNextSequences(MakeRawTx(2000), "test.op");

        cpfp.Should().Be(1900);
        direct.Should().Be(1950);
    }

    [Test]
    public void ComputeNextSequences_preserves_bit30_across_the_decrement()
    {
        var (cpfp, direct) = TimelockHelper.ComputeNextSequences(MakeRawTx(Bit30 | 2000), "test.op");

        cpfp.Should().Be(Bit30 | 1900);
        direct.Should().Be(Bit30 | 1950);
    }

    [Test]
    public void ComputeNextSequences_rounds_down_to_the_interval_before_decrementing()
    {
        // The operators validate the successor refund against the rounded timelock: 740 → 600,
        // where a raw decrement gave 640.
        var (cpfp, direct) = TimelockHelper.ComputeNextSequences(MakeRawTx(740), "test.op");

        cpfp.Should().Be(600);
        direct.Should().Be(650);
        TimelockHelper.ComputeNextSequences(MakeRawTx(Bit30 | 1999), "test.op").Should().Be((Bit30 | 1800, Bit30 | 1850));
    }

    [Test]
    public void ComputeNextSequences_allows_the_last_decrement_above_the_floor()
    {
        // 200 is the smallest timelock that can still move: rounded 200 - 100 = 100.
        var (cpfp, direct) = TimelockHelper.ComputeNextSequences(MakeRawTx(200), "test.op");

        cpfp.Should().Be(100);
        direct.Should().Be(150);
    }

    [TestCase(199u)]
    [TestCase(101u)]
    public void ComputeNextSequences_throws_when_the_rounded_timelock_is_at_the_floor(uint sequence)
    {
        var act = () => TimelockHelper.ComputeNextSequences(MakeRawTx(sequence), "test.op");

        act.Should().Throw<SparkLeafTimelockExhaustedException>();
    }

    [Test]
    public void HtlcSequences_are_not_rounded()
    {
        TimelockHelper.HtlcSequences(MakeRawTx(740), "test.op").Should().Be((710u, 725u));
        TimelockHelper.HtlcSequences(MakeRawTx(Bit30 | 2000), "test.op").Should().Be((Bit30 | 1970, Bit30 | 1985));
        var exhausted = () => TimelockHelper.HtlcSequences(MakeRawTx(100), "test.op");
        exhausted.Should().Throw<SparkLeafTimelockExhaustedException>();
    }

    [Test]
    public void ComputeNextSequences_throws_at_the_floor_instead_of_underflowing()
    {
        // 100 - 100 would reach zero, which the coordinator rejects; anything
        // below would wrap the uint. Both must throw the typed exception.
        var act = () => TimelockHelper.ComputeNextSequences(MakeRawTx(100), "test.op", "leaf-1");

        act.Should().Throw<SparkLeafTimelockExhaustedException>()
            .Which.Should().Match<SparkLeafTimelockExhaustedException>(e =>
                e.Operation == "test.op" && e.LeafId == "leaf-1");
    }

    [Test]
    public void ComputeNextSequences_throws_below_the_floor()
    {
        var act = () => TimelockHelper.ComputeNextSequences(MakeRawTx(40), "test.op");

        act.Should().Throw<SparkLeafTimelockExhaustedException>();
    }

    [Test]
    public void ComputeNextSequences_ignores_bit30_when_checking_the_floor()
    {
        var act = () => TimelockHelper.ComputeNextSequences(MakeRawTx(Bit30 | 100), "test.op");

        act.Should().Throw<SparkLeafTimelockExhaustedException>();
    }

    [TestCase(200u, true)]
    [TestCase(2000u, true)]
    [TestCase(199u, false)]
    [TestCase(101u, false)]
    [TestCase(100u, false)]
    [TestCase(0u, false)]
    [TestCase(Bit30 | 100u, false)]
    [TestCase(Bit30 | 199u, false)]
    [TestCase(Bit30 | 200u, true)]
    public void TimelockCanDecrement_needs_a_rounded_timelock_above_one_interval(uint sequence, bool expected)
    {
        TimelockHelper.TimelockCanDecrement(MakeRawTx(sequence)).Should().Be(expected);
    }

    /// <summary>
    /// Minimal single-input non-segwit tx: version(4) + input count(1) +
    /// prevout(36) + empty script(1) + nSequence(4 LE). ParseInputSequence
    /// never reads past the first input's sequence.
    /// </summary>
    private static byte[] MakeRawTx(uint sequence)
    {
        var tx = new byte[4 + 1 + 36 + 1 + 4 + 1 + 4]; // version, 1 input, 0 outputs, locktime
        tx[0] = 0x02; // version 2
        tx[4] = 0x01; // one input
        // prevout hash (32) + index (4) stay zero; script length byte stays 0x00
        BitConverter.GetBytes(sequence).CopyTo(tx, 42);
        return tx;
    }
}
