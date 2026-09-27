namespace NSpark.GraphQL;

/// <summary>
/// Helpers for converting Lightspark <c>CurrencyAmount</c> values (the
/// <c>{ original_value, original_unit }</c> envelope the SSP returns for every
/// fee / balance field) into whole satoshis.
/// </summary>
/// <remarks>
/// All call sites that consume an SSP fee or amount MUST go through these
/// helpers — earlier NSpark builds inlined a blind <c>/1000</c>, which
/// silently 1000× under-reported any field the SSP answered in
/// <c>SATOSHI</c> instead of <c>MILLISATOSHI</c>. The conversion factors
/// here mirror Lightspark's reference <c>amount_as_msats</c>, scaled down
/// by 1000 to land in sats. Sub-sat values round UP — under-quoting a fee
/// would cause the operation to be rejected at execute time.
/// </remarks>
internal static class CurrencyAmountExtensions
{
    /// <summary>
    /// Convert a Lightspark CurrencyAmount (original_value + original_unit string)
    /// to whole satoshis, rounding sub-sat units UP so the caller never
    /// under-quotes the fee.
    /// </summary>
    /// <param name="originalValue">The raw numeric value as the SSP returned it.</param>
    /// <param name="originalUnit">
    /// The Lightspark <c>CurrencyUnit</c> enum value (string form). Known values:
    /// <c>SATOSHI</c>, <c>MILLISATOSHI</c>, <c>BITCOIN</c>, <c>MILLIBITCOIN</c>,
    /// <c>MICROBITCOIN</c>, <c>NANOBITCOIN</c>. Unknown / null defaults to
    /// <c>SATOSHI</c> — the SSP never returns fiat units for fee fields in
    /// practice and treating an unknown unit as sats produces the smallest
    /// surprise.
    /// </param>
    public static long ToSats(long originalValue, string? originalUnit)
    {
        return (originalUnit ?? string.Empty).ToUpperInvariant() switch
        {
            "SATOSHI" => originalValue,
            "MILLISATOSHI" => (originalValue + 999) / 1000,
            "BITCOIN" => originalValue * 100_000_000L,
            "MILLIBITCOIN" => originalValue * 100_000L,
            "MICROBITCOIN" => originalValue * 100L,
            "NANOBITCOIN" => (originalValue + 9) / 10,
            // Unknown unit — default to sats so we still produce *some* answer.
            // The SSP doesn't return fiat units for fee fields in practice.
            _ => originalValue,
        };
    }

    /// <summary>
    /// An SSP fee (<c>original_value</c> in <c>original_unit</c>) in whole sats, strictly: SATOSHI
    /// as is, MILLISATOSHI rounded up; any other unit, a missing unit or a negative value is
    /// refused, as the reference SDK refuses a fee estimate in another unit. A fee read in the
    /// wrong unit would be capped against the wrong number and, for Lightning, underpaid after the
    /// leaves were locked.
    /// </summary>
    /// <exception cref="Exceptions.SparkUntrustedResponseException">The amount is not one of those.</exception>
    public static long ToFeeSats(long originalValue, string? originalUnit, string field)
    {
        if (originalValue < 0)
        {
            throw new Exceptions.SparkUntrustedResponseException(
                "ssp.amount", $"The SSP's {field} is negative ({originalValue}).");
        }

        return originalUnit switch
        {
            "SATOSHI" => originalValue,
            "MILLISATOSHI" => (originalValue / 1000) + (originalValue % 1000 == 0 ? 0 : 1),
            null => throw new Exceptions.SparkUntrustedResponseException(
                "ssp.amount", $"The SSP's {field} has no unit."),
            _ => throw new Exceptions.SparkUntrustedResponseException(
                "ssp.amount", $"The SSP's {field} is in an unsupported unit: {originalUnit}."),
        };
    }
}
