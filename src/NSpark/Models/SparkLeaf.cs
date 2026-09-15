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
    /// Remaining refund-tx timelock in blocks. Below 200 the leaf needs
    /// renewal; at or below 100 it cannot move at all until renewed. See
    /// <c>RenewalService.RenewExhaustedLeavesAsync</c>. An unparseable refund
    /// transaction reads as 0 ("exhausted"): the leaf is never selected for a
    /// spend and a renewal attempt reports the failure per leaf instead of
    /// throwing from a property getter.
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
                return ClaimService.ExtractRefundSequence(Node) & 0xFFFF;
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Whether the leaf can be transferred, paid, or exited right now: its refund timelock is
    /// above the floor the coordinator enforces. Leaves in the renewable range just above the
    /// floor are still spendable; <c>BalanceService.GetSpendableLeavesAsync</c> renews them
    /// first. Every spend path selects only from spendable leaves.
    /// </summary>
    public bool IsSpendable => RefundTimelockBlocks > TimelockHelper.TimeLockInterval;

    /// <summary>
    /// Whether the coordinator is expected to renew this leaf's timelocks: its refund timelock
    /// is in <c>[100, 200)</c>. A leaf below that range is frozen; only a unilateral exit can
    /// recover it.
    /// </summary>
    public bool IsRenewable =>
        RefundTimelockBlocks >= TimelockHelper.TimeLockInterval
        && RefundTimelockBlocks < RenewalService.RenewalThreshold;
}
