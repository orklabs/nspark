using NSpark.Proto;

namespace NSpark.Models;

/// <summary>
/// A Lightning payment this wallet sent, as the Signing Operators hold it: the HTLC (preimage
/// request) for the invoice's payment hash, with the preimage-swap transfer to the SSP that funds
/// it.
/// </summary>
/// <param name="PaymentHash">The invoice's payment hash (lowercase hex, 64 chars).</param>
/// <param name="Status">The HTLC's status: <c>WaitingForPreimage</c> until the SSP has paid the
/// invoice, <c>PreimageShared</c> once it has, <c>Returned</c> once the send was given back.</param>
/// <param name="Preimage">The invoice's preimage (lowercase hex, 64 chars) once the SSP shared it:
/// proof of payment, checked to hash to <paramref name="PaymentHash"/>. Null before that.</param>
/// <param name="CreatedAt">When the operators took the HTLC.</param>
/// <param name="Transfer">The transfer funding the send, whose total is the invoice amount plus
/// the fee paid; null when the operators hold none.</param>
public sealed record LightningSend(
    string PaymentHash,
    string Status,
    string? Preimage,
    DateTimeOffset CreatedAt,
    SparkTransfer? Transfer)
{
    /// <summary>
    /// The invoice was paid: the SSP shared the preimage, which it only learns by paying, or
    /// claimed the transfer.
    /// </summary>
    public bool IsPaid =>
        Preimage is not null
        || Status == nameof(PreimageRequestStatus.PreimageShared)
        || Transfer?.Status == nameof(TransferStatus.Completed);

    /// <summary>
    /// The send's funds came back to the wallet: the operators returned the transfer, or it
    /// expired unclaimed. While neither this nor <see cref="IsPaid"/> holds, the send is in
    /// flight; both at once contradict the protocol and deserve a look.
    /// </summary>
    public bool IsReturned => Transfer is null
        ? Status == nameof(PreimageRequestStatus.Returned)
        : Transfer.Status is nameof(TransferStatus.Returned) or nameof(TransferStatus.Expired);
}
