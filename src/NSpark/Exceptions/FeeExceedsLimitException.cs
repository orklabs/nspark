namespace NSpark.Exceptions;

/// <summary>
/// The fee an operation would pay is above the limit the caller set (or, for a withdrawal with
/// no explicit limit, would consume the whole amount). Nothing has been signed or moved. Not
/// retryable as-is: raise the limit, lower the amount, or wait for cheaper conditions.
/// </summary>
public sealed class FeeExceedsLimitException : SparkException
{
    /// <summary>The fee that was quoted, in satoshis.</summary>
    public long FeeSats { get; }

    /// <summary>The highest fee the operation would accept, in satoshis.</summary>
    public long MaxFeeSats { get; }

    /// <inheritdoc />
    public FeeExceedsLimitException(string operation, long feeSats, long maxFeeSats)
        : base(operation, $"The quoted fee of {feeSats} sats exceeds the limit of {maxFeeSats} sats.")
    {
        FeeSats = feeSats;
        MaxFeeSats = maxFeeSats;
    }
}
