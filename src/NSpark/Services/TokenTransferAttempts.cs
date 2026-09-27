using NSpark.Proto.Token;

namespace NSpark.Services;

/// <summary>
/// A token transaction the wallet has built and not yet sent, in the configured version
/// (<see cref="SparkOptions.TokenTransactionVersion"/>).
/// </summary>
internal abstract record TokenTransactionDraft
{
    private TokenTransactionDraft()
    {
    }

    /// <summary>Sent with <c>start_transaction</c> and <c>commit_transaction</c>.</summary>
    internal sealed record V2(TokenTransaction Transaction) : TokenTransactionDraft;

    /// <summary>Sent with <c>broadcast_transaction</c>.</summary>
    internal sealed record V3(PartialTokenTransaction Partial) : TokenTransactionDraft;
}

/// <summary>
/// Token transfers sent with an idempotency key, so a retry with the key resends the same
/// transaction rather than building another.
/// </summary>
/// <remarks>
/// The operators recognise the same transaction again. They answer a repeated key from their
/// idempotency records (kept 24 hours). Without the record, V3's <c>broadcast_transaction</c>
/// answers with the transaction stored under the partial transaction's hash, and V2's
/// <c>start_transaction</c> answers from the stored transaction while it is started, where
/// <c>commit_transaction</c> of a finalized transfer reports it finalized. A rebuilt transaction
/// would differ in its client timestamp, and in its outputs while the first transaction's outputs
/// are spent or locked (<see cref="TokenOutputLocks"/>), so it could not match what the operators
/// answer for the key. Resending never makes a second transfer: an attempt whose transaction
/// expired unsent fails again, and a new key starts a new transfer.
/// </remarks>
internal sealed class TokenTransferAttempts
{
    /// <summary>How many keys are remembered; the oldest is forgotten first.</summary>
    internal const int Capacity = 1_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);

    /// <summary>Keys, oldest first.</summary>
    private readonly LinkedList<string> _order = new();

    /// <summary>What a transfer asked for: a retry with the key must ask for the same.</summary>
    internal sealed record Request(byte[] TokenIdentifier, UInt128 Amount, byte[] ReceiverIdentityPublicKey)
    {
        public bool Matches(Request other) =>
            TokenIdentifier.AsSpan().SequenceEqual(other.TokenIdentifier)
            && Amount == other.Amount
            && ReceiverIdentityPublicKey.AsSpan().SequenceEqual(other.ReceiverIdentityPublicKey);
    }

    /// <param name="Request">What the transfer asked for.</param>
    /// <param name="Transaction">The transaction, as first built.</param>
    /// <param name="SpentOutputs">The outputs it spends.</param>
    internal sealed record Attempt(
        Request Request,
        TokenTransactionDraft Transaction,
        IReadOnlyList<OutputWithPreviousTransactionData> SpentOutputs);

    public Attempt? Get(string key)
    {
        lock (_gate)
        {
            return _attempts.GetValueOrDefault(key);
        }
    }

    public void Remember(Attempt attempt, string key)
    {
        lock (_gate)
        {
            if (!_attempts.ContainsKey(key))
            {
                _order.AddLast(key);
            }

            _attempts[key] = attempt;
            while (_order.Count > Capacity)
            {
                _attempts.Remove(_order.First!.Value);
                _order.RemoveFirst();
            }
        }
    }
}
