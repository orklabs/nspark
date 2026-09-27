using NSpark.Bitcoin;
using NSpark.Exceptions;
using NSpark.Proto;

namespace NSpark.Services;

/// <summary>
/// Shared relative-timelock math for leaf spend paths. A leaf's timelock lives
/// in the low 16 bits of its refund transaction's first-input nSequence; bit 30
/// (the relative-timelock type flag) must be preserved across every decrement.
/// </summary>
internal static class TimelockHelper
{
    /// <summary>Blocks subtracted from the refund timelock on every spend hop.</summary>
    internal const uint TimeLockInterval = 100;

    /// <summary>The direct refund tx timelock sits this many blocks above the CPFP one.</summary>
    internal const uint DirectTimelockOffset = 50;

    /// <summary>Lightning HTLC refunds sit this many blocks above the decremented timelock.</summary>
    internal const uint HtlcTimelockOffset = 70;

    /// <summary>Direct Lightning HTLC refunds sit this many blocks above the decremented timelock.</summary>
    internal const uint DirectHtlcTimelockOffset = 85;

    /// <summary>nSequence of the first input of a raw Bitcoin transaction (where Spark keeps leaf timelocks).</summary>
    internal static uint ParseSequence(byte[] rawTx) => RawTransaction.Parse(rawTx, "leaf tx").FirstInputSequence;

    /// <summary>
    /// A refund timelock rounded down to the 100-block interval. The operators validate every
    /// successor refund against the rounded value (<c>RoundDownToTimelockInterval</c>), so a leaf
    /// whose timelock is not a multiple of 100 — 740, left by older SDKs — counts as 700.
    /// </summary>
    internal static uint RoundedTimelock(uint timelock) => timelock - (timelock % TimeLockInterval);

    /// <summary>
    /// Whether a leaf with this refund timelock can be transferred, swapped or exited without a
    /// renewal first. The operators require the rounded timelock to stay above 100 so the next
    /// refund does not reach zero (<c>ValidateRenewalTimelockFloor</c>): a refund timelock of at
    /// least 200. Leaves at 100…199 need renewing; below 100 they cannot be renewed either.
    /// </summary>
    internal static bool IsTransferableRefundTimelock(uint timelock) => RoundedTimelock(timelock) > TimeLockInterval;

    /// <summary>
    /// The next CPFP and direct refund sequences for a transfer, swap or cooperative exit: the
    /// current refund timelock rounded down to the interval, minus 100, and the direct refunds 50
    /// above that — exactly what the operators expect (<c>ValidateSequence</c>), and what the
    /// reference SDK builds (<c>createDecrementedTimelockRefundTxs</c> with
    /// <c>enforceTimelocks</c>). A raw decrement produced 640 for a leaf at 740 where the
    /// operators require 600. Bit 30 is kept. Lightning HTLC refunds use
    /// <see cref="HtlcSequences"/> instead: they are not rounded.
    /// </summary>
    /// <exception cref="SparkLeafTimelockExhaustedException">
    /// The leaf's rounded refund timelock is at or below 100; it needs renewal before it can move.
    /// </exception>
    internal static (uint Cpfp, uint Direct) ComputeNextSequences(
        byte[] refundTxBytes, string operation, string? leafId = null)
    {
        var rawSequence = ParseSequence(refundTxBytes);
        var currentTimelock = rawSequence & 0xFFFF;
        var bit30 = rawSequence & (1u << 30);

        // Checked before subtracting: an unchecked decrement would wrap the uint.
        if (!IsTransferableRefundTimelock(currentTimelock))
        {
            throw new SparkLeafTimelockExhaustedException(
                operation,
                $"Leaf timelock exhausted ({currentTimelock}, rounded {RoundedTimelock(currentTimelock)} <= {TimeLockInterval}); needs renewal before it can move.")
            {
                LeafId = leafId,
            };
        }

        var nextTimelock = RoundedTimelock(currentTimelock) - TimeLockInterval;
        return (bit30 | nextTimelock, bit30 | (nextTimelock + DirectTimelockOffset));
    }

    /// <summary>
    /// Sequences of a Lightning send's HTLC refunds: the current refund timelock minus 100, plus
    /// 70 for the CPFP HTLC and 85 for the direct ones — the reference SDK's
    /// <c>getNextHTLCTransactionSequence</c>, and what the operators rebuild (refund sequence − 30
    /// and − 15, <c>lightning_handler.go</c>). Unlike transfer refunds these are NOT rounded down
    /// to the interval. Spend paths only select leaves <see cref="Models.SparkLeaf.IsSpendable"/>
    /// allows, which keeps a leaf the operators would refuse to let the receiver claim out.
    /// </summary>
    internal static (uint Cpfp, uint Direct) HtlcSequences(byte[] refundTxBytes, string operation, string? leafId = null)
    {
        var rawSequence = ParseSequence(refundTxBytes);
        var currentTimelock = rawSequence & 0xFFFF;
        if (currentTimelock <= TimeLockInterval)
        {
            throw new SparkLeafTimelockExhaustedException(
                operation,
                $"Leaf timelock exhausted ({currentTimelock} <= {TimeLockInterval}); needs renewal before it can pay.")
            {
                LeafId = leafId,
            };
        }

        var nextTimelock = currentTimelock - TimeLockInterval;
        var bit30 = rawSequence & (1u << 30);
        return (bit30 | (nextTimelock + HtlcTimelockOffset), bit30 | (nextTimelock + DirectHtlcTimelockOffset));
    }

    /// <summary>
    /// Whether the leaf can be transferred, swapped or exited without an operator renewal
    /// (<see cref="IsTransferableRefundTimelock"/> on its refund transaction). An unparseable
    /// refund transaction counts as exhausted: the leaf is skipped rather than handed to the
    /// coordinator with a bogus sequence.
    /// </summary>
    internal static bool TimelockCanDecrement(byte[] refundTxBytes)
    {
        try
        {
            return IsTransferableRefundTimelock(ParseSequence(refundTxBytes) & 0xFFFF);
        }
        catch (SparkUntrustedResponseException)
        {
            return false;
        }
    }

    /// <summary>Whether a node transaction has a zero timelock (sequence &amp; 0xFFFF == 0).</summary>
    internal static bool IsZeroTimelockNode(byte[] nodeTx) => (ParseSequence(nodeTx) & 0xFFFF) == 0;

    /// <summary>
    /// The direct node transaction a leaf's direct refund spends, or <c>null</c> when the leaf has
    /// none or is a zero-timelock node. The operators reject a direct refund for a zero node
    /// ("zero nodes must not have a direct refund tx"), and zero-timelock renewal leaves exactly
    /// that shape: a timelock-0 node transaction together with a direct one. Mirrors the reference
    /// SDK's <c>isZeroNode</c> check in its refund builders. (Lightning HTLC refunds follow a
    /// different rule: the operators expect a direct HTLC refund whenever a direct node
    /// transaction exists.)
    /// </summary>
    internal static byte[]? DirectNodeTxForRefund(TreeNode node)
    {
        if (node.DirectTx.IsEmpty || IsZeroTimelockNode(node.NodeTx.ToByteArray()))
        {
            return null;
        }

        return node.DirectTx.ToByteArray();
    }

    /// <summary>
    /// A leaf's refund transactions paying <paramref name="receivingPublicKey"/> at the given
    /// sequences: the CPFP refund, the direct-from-CPFP refund, and a direct refund when
    /// <see cref="DirectNodeTxForRefund"/> allows one. Shared by send, claim and cooperative exit.
    /// </summary>
    internal static SparkRefundTxTrio LeafRefundTrio(
        TreeNode node,
        byte[] receivingPublicKey,
        string network,
        uint sequence,
        uint directSequence)
    {
        return SparkTxBuilder.BuildRefundTxTrio(
            cpfpNodeTx: node.NodeTx.ToByteArray(),
            directNodeTx: DirectNodeTxForRefund(node),
            vout: 0,
            receivingPublicKey: receivingPublicKey,
            network: network,
            sequence: sequence,
            directSequence: directSequence,
            feeSats: SparkConstants.DefaultRefundFeeSats);
    }
}
