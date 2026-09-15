using NSpark.Models;

namespace NSpark.Services;

/// <summary>
/// Extension methods on <see cref="SparkWallet"/> for consolidating the leaf
/// set. Fewer leaves = a recovery snapshot that is kilobytes instead of
/// megabytes, and a unilateral exit that costs a handful of transaction chains
/// instead of one per dust leaf.
/// </summary>
public static class ConsolidationService
{
    private const int MaxRounds = 12;

    /// <summary>
    /// Swap the wallet's leaves toward the fewest denominations (the binary
    /// decomposition of the total — the same greedy power-of-two shape exit
    /// tooling consolidates toward).
    /// </summary>
    /// <remarks>
    /// Off-chain and instant: each round is an atomic SSP leaf swap (the same
    /// mechanism sends already use for denominations), requested with
    /// fee_sats 0. Requires operators online — this is maintenance, not the
    /// emergency path. Batched so a very fragmented wallet never swaps more
    /// than <paramref name="maxLeavesPerRound"/> leaves in one request.
    /// </remarks>
    public static async Task<SparkLeafConsolidation> ConsolidateLeavesAsync(
        this SparkWallet wallet,
        int maxLeavesPerRound = 100,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);

        // Un-freeze what we can first: renewal resets low refund timelocks so
        // those leaves can join the swap instead of being skipped. Best-effort —
        // a failed renewal just leaves that leaf in the skipped bucket.
        try
        {
            _ = await wallet.RenewExhaustedLeavesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Best-effort: the affected leaves surface in SkippedLeaves.
        }

        var current = await wallet.GetLeavesAsync(ct).ConfigureAwait(false);
        var leavesBefore = current.Count;
        var totalBefore = current.Sum(l => l.ValueSats);

        var rounds = 0;
        while (rounds < MaxRounds)
        {
            var movable = Swappable(current);
            var ideal = BinaryDecomposition(movable.Sum(l => l.ValueSats));
            if (movable.Count <= ideal.Length)
            {
                break;
            }

            // Merge the smallest leaves first — they are the ones that make
            // exits uneconomical and bundles huge.
            var batch = movable
                .OrderBy(l => l.ValueSats)
                .Take(maxLeavesPerRound)
                .ToList();
            var batchTotal = batch.Sum(l => l.ValueSats);
            var targets = BinaryDecomposition(batchTotal);
            if (batch.Count <= targets.Length || batchTotal == 0)
            {
                break;
            }

            _ = await wallet.ProcessSwapBatchAsync(batch, targets, ct).ConfigureAwait(false);
            rounds++;

            var refreshed = await wallet.GetLeavesAsync(ct).ConfigureAwait(false);
            if (refreshed.Count >= current.Count)
            {
                break; // no progress — stop
            }

            current = refreshed;
        }

        return new SparkLeafConsolidation(
            LeavesBefore: leavesBefore,
            LeavesAfter: current.Count,
            TotalSatsBefore: totalBefore,
            TotalSatsAfter: current.Sum(l => l.ValueSats),
            Rounds: rounds,
            SkippedLeaves: current.Count - Swappable(current).Count);
    }

    /// <summary>
    /// Leaves at the timelock floor cannot be swapped until renewed —
    /// consolidate around them instead of failing the whole run.
    /// </summary>
    private static List<SparkLeaf> Swappable(IReadOnlyList<SparkLeaf> leaves)
    {
        return leaves.Where(l => l.IsSpendable).ToList();
    }

    /// <summary>
    /// Power-of-two denominations summing exactly to <paramref name="total"/>
    /// (its set bits), largest first. The minimal leaf set the SSP denomination
    /// system can represent the amount with.
    /// </summary>
    internal static long[] BinaryDecomposition(long total)
    {
        if (total <= 0)
        {
            return Array.Empty<long>();
        }

        var result = new List<long>();
        for (var bit = 62; bit >= 0; bit--)
        {
            if (((total >> bit) & 1) == 1)
            {
                result.Add(1L << bit);
            }
        }

        return result.ToArray();
    }
}
