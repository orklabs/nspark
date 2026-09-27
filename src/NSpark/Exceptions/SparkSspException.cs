namespace NSpark.Exceptions;

/// <summary>
/// The Spark Service Provider (SSP) refused a request: an HTTP error status, or GraphQL errors in
/// its answer. Transient HTTP failures (502, 503, 504) and lost connections were already retried
/// before this is thrown.
/// </summary>
public sealed class SparkSspException : SparkException
{
    /// <summary>HTTP status of the SSP's answer, when the failure is an HTTP error.</summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>The GraphQL error messages the SSP returned, if any.</summary>
    public IReadOnlyList<string> GraphQLErrors { get; init; } = [];

    /// <inheritdoc />
    public override bool IsRetryable => HttpStatusCode is 502 or 503 or 504;

    /// <inheritdoc />
    public SparkSspException(string operation, string message)
        : base(operation, message)
    {
    }

    /// <inheritdoc />
    public SparkSspException(string operation, string message, Exception innerException)
        : base(operation, message, innerException)
    {
    }
}
