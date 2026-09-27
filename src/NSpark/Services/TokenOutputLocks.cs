using System.Diagnostics;
using NSpark.Proto.Token;

namespace NSpark.Services;

/// <summary>
/// Token outputs the wallet has picked for a transaction that may still be in flight, kept as
/// the reference SDK's <c>TokenOutputManager</c> keeps them. The operators report an output of a
/// started transaction as AVAILABLE until that transaction is signed, and of two transactions
/// spending one output they keep only one (the earlier client timestamp), so two sends from one
/// wallet must not pick the same outputs.
/// </summary>
/// <remarks>
/// A lock ends after <see cref="Expiry"/> (30 s, the reference SDK's default) or once the
/// operators report the output PENDING_OUTBOUND. A failed send does not release its outputs
/// early: the operators may already hold its transaction.
/// </remarks>
internal sealed class TokenOutputLocks
{
    private readonly object _gate = new();

    /// <summary>When each locked output (<see cref="Key"/>) was picked (<see cref="Stopwatch"/> timestamp).</summary>
    private readonly Dictionary<string, long> _lockedAt = new(StringComparer.Ordinal);

    public TokenOutputLocks(TimeSpan? expiry = null)
    {
        Expiry = expiry ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>How long a picked output stays locked.</summary>
    public TimeSpan Expiry { get; }

    /// <summary>
    /// Picks with <paramref name="select"/> among the <paramref name="outputs"/> that can be spent
    /// (available and not locked) and locks the picked ones. Picking and locking happen under one
    /// lock, so concurrent sends never pick the same output.
    /// </summary>
    public List<OutputWithPreviousTransactionData> Acquire(
        IReadOnlyList<OutputWithPreviousTransactionData> outputs,
        Func<IReadOnlyList<OutputWithPreviousTransactionData>, List<OutputWithPreviousTransactionData>> select)
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            foreach (var key in _lockedAt.Where(kv => Stopwatch.GetElapsedTime(kv.Value, now) >= Expiry).Select(kv => kv.Key).ToList())
            {
                _lockedAt.Remove(key);
            }

            // Pending on the operators: their status covers it from here.
            foreach (var output in outputs)
            {
                if (output.Output.HasStatus && output.Output.Status == TokenOutputStatus.PendingOutbound)
                {
                    _lockedAt.Remove(Key(output));
                }
            }

            var spendable = outputs.Where(o => IsAvailable(o) && !_lockedAt.ContainsKey(Key(o))).ToList();
            var selected = select(spendable);
            foreach (var output in selected)
            {
                _lockedAt[Key(output)] = now;
            }

            return selected;
        }
    }

    /// <summary>
    /// Whether the operators report <paramref name="output"/> spendable: AVAILABLE, or no status at
    /// all. PENDING_OUTBOUND outputs belong to a signed transaction that has not finalized.
    /// </summary>
    public static bool IsAvailable(OutputWithPreviousTransactionData output) =>
        !output.Output.HasStatus || output.Output.Status == TokenOutputStatus.Available;

    /// <summary>The token transaction output that <paramref name="output"/> is: previous transaction hash and vout.</summary>
    public static string Key(OutputWithPreviousTransactionData output) =>
        $"{Convert.ToHexString(output.PreviousTransactionHash.Span)}:{output.PreviousTransactionVout}";
}
