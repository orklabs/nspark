namespace NSpark.Models;

/// <summary>
/// What <c>WithdrawAllAsync</c> would do right now. Produced by <c>QuoteWithdrawAllAsync</c> after
/// pending inbound transfers were claimed and renewable leaves renewed.
/// </summary>
/// <param name="SpendableSats">Sats that would be handed to the SSP: every spendable leaf.</param>
/// <param name="QuotedFeeSats">The SSP's fee quote (fast exit) for those leaves. Zero when there is nothing to send.</param>
/// <param name="FrozenSats">Sats in frozen leaves (refund timelock below 100). They stay behind; only a unilateral exit moves them.</param>
/// <param name="UnrenewedSats">
/// Sats in leaves that need a renewal (refund timelock 100…199) the operators did not complete.
/// They stay behind this time; a later attempt can renew and move them.
/// </param>
/// <param name="LockedSats">Sats in leaves locked by an in-flight swap or exit. Withdraw again once they settle.</param>
/// <param name="IncomingSats">Inbound sats that are still unclaimed after the claim attempt.</param>
/// <param name="LeafCount">Number of leaves that would be exited.</param>
public sealed record WithdrawAllQuote(
    long SpendableSats,
    long QuotedFeeSats,
    long FrozenSats,
    long UnrenewedSats,
    long LockedSats,
    long IncomingSats,
    int LeafCount)
{
    /// <summary>What the destination would receive if the SSP charges exactly its quote.</summary>
    public long EstimatedPayoutSats => SpendableSats - QuotedFeeSats;

    /// <summary>Share of the wallet's own sats (spendable + frozen) that cannot leave off-chain, 0…1.</summary>
    public double FrozenFraction
    {
        get
        {
            var total = SpendableSats + FrozenSats;
            return total > 0 ? (double)FrozenSats / total : 0;
        }
    }

    /// <summary>Whether the quoted fee leaves anything to pay out.</summary>
    public bool CoversFee => SpendableSats > QuotedFeeSats && QuotedFeeSats >= 0;
}

/// <summary>Outcome of <c>WithdrawAllAsync</c>.</summary>
/// <param name="Txid">L1 transaction id of the cooperative exit (display order).</param>
/// <param name="SentSats">Sats handed to the SSP: every spendable sat at drain time.</param>
/// <param name="PayoutSats">Sats the verified exit transaction pays to the destination.</param>
/// <param name="FrozenSats">Sats left in frozen leaves (refund timelock below 100); only a unilateral exit can recover them.</param>
/// <param name="UnrenewedSats">Sats left in leaves whose renewal the operators did not complete; a later drain can move them.</param>
/// <param name="LockedSats">Sats left in leaves locked by an in-flight operation.</param>
/// <param name="UnclaimedSats">Inbound sats that could not be claimed before the drain.</param>
public sealed record WithdrawAllResult(
    string Txid,
    long SentSats,
    long PayoutSats,
    long FrozenSats,
    long UnrenewedSats,
    long LockedSats,
    long UnclaimedSats)
{
    /// <summary>SSP fee actually taken.</summary>
    public long FeeSats => SentSats - PayoutSats;
}
