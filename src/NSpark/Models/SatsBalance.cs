namespace NSpark.Models;

/// <summary>
/// Breakdown of the satoshi value held in a Spark wallet.
/// Matches the Swift / Kotlin / TS Spark SDKs' <c>SatsBalance</c> shape, plus the
/// <see cref="Frozen"/> figure the Swift SDK added for leaves stuck at the timelock floor.
/// </summary>
/// <param name="Available">
/// Satoshis that can be sent right now: <c>AVAILABLE</c> leaves whose refund timelock is above
/// the floor the coordinator enforces. Every spend path selects from exactly this set, so sending
/// the full <see cref="Available"/> balance always succeeds.
/// </param>
/// <param name="Owned">
/// All satoshis owned by the wallet: <see cref="Available"/> plus <see cref="Frozen"/> plus value
/// locked in in-flight outgoing transfers, swaps, or renewals (<c>TRANSFER_LOCKED</c>,
/// <c>SPLIT_LOCKED</c>, <c>AGGREGATE_LOCK</c>, <c>RENEW_LOCKED</c>).
/// </param>
/// <param name="Incoming">
/// Sats arriving but not yet spendable: pending inbound transfers waiting to be
/// claimed plus deposit leaves in the <c>CREATING</c> state.
/// </param>
/// <param name="Frozen">
/// Sats in <c>AVAILABLE</c> leaves whose refund timelock has reached the floor. The coordinator
/// will neither move nor renew them; they stay owned and can only be recovered by a unilateral
/// exit (see <c>docs/recovery.md</c>).
/// </param>
public sealed record SatsBalance(long Available, long Owned, long Incoming, long Frozen = 0)
{
    /// <summary>
    /// Sats held by an in-flight transfer, swap, renewal, or cooperative exit:
    /// <see cref="Owned"/> minus <see cref="Available"/> minus <see cref="Frozen"/>.
    /// </summary>
    public long Locked => Owned - Available - Frozen;
}
