using NSpark.Proto;
using NSpark.Services;

namespace NSpark.Models;

/// <summary>
/// A single Spark leaf — a UTXO-equivalent unit of value held by the wallet.
/// </summary>
/// <param name="Id">Unique leaf identifier assigned by the Signing Operators.</param>
/// <param name="TreeId">Identifier of the tree this leaf belongs to.</param>
/// <param name="ValueSats">Value of the leaf in satoshis.</param>
/// <param name="Status">Server-reported status string (e.g. <c>"AVAILABLE"</c>).</param>
public sealed record SparkLeaf(string Id, string TreeId, long ValueSats, string Status)
{
    /// <summary>
    /// The underlying protobuf TreeNode for this leaf. Internal because the
    /// generated proto types are an implementation detail; they are not part
    /// of the public NSpark API contract.
    /// </summary>
    internal TreeNode Node { get; init; } = null!;

    /// <summary>
    /// Remaining refund-tx timelock in blocks. Below 200 the leaf must be renewed before it can
    /// move; below 100 the coordinator will not renew it either (<see cref="IsFrozen"/>). An
    /// unparseable refund transaction reads as 0 ("exhausted"): the leaf is never selected for a
    /// spend and a renewal attempt reports the failure per leaf instead of throwing from a
    /// property getter.
    /// </summary>
    public uint RefundTimelockBlocks
    {
        get
        {
            if (Node is null)
            {
                return 0;
            }

            try
            {
                return TimelockHelper.ParseSequence(Node.RefundTx.ToByteArray()) & 0xFFFF;
            }
            catch (Exceptions.SparkUntrustedResponseException)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Whether the leaf can be transferred, paid, or exited right now without a renewal: its
    /// refund timelock, rounded down to the 100-block interval, is above the floor the coordinator
    /// enforces — at least 200. Leaves at 100…199 are renewable (<see cref="IsRenewable"/>);
    /// <c>BalanceService.GetSpendableLeavesAsync</c> renews them first.
    /// </summary>
    public bool IsSpendable => TimelockHelper.IsTransferableRefundTimelock(RefundTimelockBlocks);

    /// <summary>
    /// Whether the coordinator will renew this leaf's timelocks: its refund timelock is in
    /// <c>[100, 200)</c>. A leaf below that range is frozen: only a unilateral exit can recover it.
    /// </summary>
    public bool IsRenewable =>
        RefundTimelockBlocks >= TimelockHelper.TimeLockInterval
        && RefundTimelockBlocks < RenewalService.RenewalThreshold;

    /// <summary>
    /// Whether the leaf is frozen: its refund timelock is below 100, the minimum the coordinator
    /// renews, and it is too low to move, so only a unilateral on-chain exit can recover it. A leaf
    /// at exactly 100 is renewable, not frozen. Leaves only get here through SDKs that decremented
    /// timelocks without renewing.
    /// </summary>
    public bool IsFrozen => RefundTimelockBlocks < TimelockHelper.TimeLockInterval;
}
