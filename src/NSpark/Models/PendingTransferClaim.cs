using System.Collections;

namespace NSpark.Models;

/// <summary>
/// The outcome of one pass over the wallet's pending inbound transfers
/// (<c>ClaimService.ClaimPendingTransfersAsync</c>): the transfers claimed, in the order they were
/// claimed — the claim is itself that list — and the ones that could not be claimed.
/// </summary>
/// <param name="ClaimedTransfers">Transfers claimed in this pass, in the order they were claimed.</param>
/// <param name="Failures">
/// Transfers that could not be claimed. They stay pending and are tried again on the next pass.
/// One the SDK refuses to claim (for example because a sender signature does not verify) fails
/// every time, but never stops the others from being claimed.
/// </param>
public sealed record PendingTransferClaim(
    IReadOnlyList<SparkTransfer> ClaimedTransfers,
    IReadOnlyList<PendingTransferClaimFailure> Failures) : IReadOnlyList<SparkTransfer>
{
    /// <summary>Ids of the transfers claimed in this pass.</summary>
    public IReadOnlyList<string> ClaimedTransferIds => ClaimedTransfers.Select(t => t.Id).ToList();

    /// <summary>Number of transfers claimed in this pass.</summary>
    public int Count => ClaimedTransfers.Count;

    /// <summary>The <paramref name="index"/>-th transfer claimed in this pass.</summary>
    public SparkTransfer this[int index] => ClaimedTransfers[index];

    /// <inheritdoc/>
    public IEnumerator<SparkTransfer> GetEnumerator() => ClaimedTransfers.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A pending transfer that could not be claimed, and why.</summary>
/// <param name="TransferId">The transfer's id.</param>
/// <param name="Error">What went wrong.</param>
public sealed record PendingTransferClaimFailure(string TransferId, Exception Error);
