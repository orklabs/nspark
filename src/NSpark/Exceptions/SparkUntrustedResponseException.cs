namespace NSpark.Exceptions;

/// <summary>
/// A response from a remote party failed NSpark's client-side verification, so the wallet
/// refused to sign or hand over anything on the strength of it. Raised for an SSP cooperative
/// exit whose transactions do not pay the requested address, an SSP invoice that does not carry
/// the wallet's payment hash, a coordinator token transaction that differs from the one the
/// wallet submitted, or an inbound transfer whose leaves do not carry a valid sender signature.
/// Not retryable: the same response will fail verification again.
/// </summary>
public sealed class SparkUntrustedResponseException : SparkException
{
    /// <inheritdoc />
    public SparkUntrustedResponseException(string operation, string message)
        : base(operation, message)
    {
    }

    /// <inheritdoc />
    public SparkUntrustedResponseException(string operation, string message, Exception innerException)
        : base(operation, message, innerException)
    {
    }
}
