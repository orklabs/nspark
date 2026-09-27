namespace NSpark.Models;

/// <summary>
/// Breakdown of the satoshi value held in a Spark wallet, modelled on the reference SDK's leaf
/// manager (the Swift / Kotlin / TS Spark SDKs report the same figures).
/// </summary>
/// <param name="Available">
/// Satoshis the wallet can send: <c>AVAILABLE</c> leaves whose refund timelock is at least 100.
/// Leaves at 100…199 are renewed by every spend path before they are selected (the coordinator
/// will not move them otherwise), as the reference SDK does.
/// </param>
/// <param name="Owned">
/// All satoshis the wallet owns: <see cref="Available"/> + <see cref="Frozen"/> + locked. Locked
/// sats are held by an in-flight operation that can still come back: an outgoing transfer,
/// Lightning payment or cooperative exit before the operators apply the sender's key tweak (a
/// cooperative exit until its transaction confirms), a swap the wallet started, and its
/// counter-transfer until claimed. Sent sats leave <see cref="Owned"/> once the sender's key tweak
/// is applied, even before the receiver claims them.
/// </param>
/// <param name="Incoming">
/// Sats arriving but not yet spendable: this wallet's leaves of pending inbound transfers waiting
/// to be claimed.
/// </param>
/// <param name="Frozen">
/// Satoshis in <c>AVAILABLE</c> leaves whose refund timelock is below 100
/// (<see cref="SparkLeaf.IsFrozen"/>). The coordinator will neither move nor renew them; only a
/// unilateral on-chain exit can recover them (see <c>docs/recovery.md</c>). A leaf at exactly 100
/// is renewable and counts as available.
/// </param>
public sealed record SatsBalance(long Available, long Owned, long Incoming, long Frozen = 0)
{
    /// <summary>
    /// Satoshis held by an in-flight transfer, swap or exit the wallet still owns (see
    /// <see cref="Owned"/>): <see cref="Owned"/> minus <see cref="Available"/> minus
    /// <see cref="Frozen"/>, never negative.
    /// </summary>
    public long Locked => Math.Max(0, Owned - Available - Frozen);
}
