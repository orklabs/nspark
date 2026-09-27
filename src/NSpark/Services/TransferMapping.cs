using NSpark.Models;
using NSpark.Proto;

namespace NSpark.Services;

/// <summary>Shared conversions from operator data to the public models.</summary>
internal static class TransferMapping
{
    /// <summary>Every bitcoin there will ever be, in sats.</summary>
    internal const long MaxSupplySats = 21_000_000L * 100_000_000L;

    /// <summary>
    /// A sats amount an operator, the SSP or a block explorer reported, capped at the bitcoin
    /// supply: a plain cast wraps 2^63 and above to a negative number, so a hostile or corrupt
    /// response would report garbage, and a cap this low keeps sums of such amounts far from
    /// overflowing.
    /// </summary>
    internal static long ReportedSats(ulong value) => (long)Math.Min(value, (ulong)MaxSupplySats);

    /// <summary>The transfer as an operator reported it.</summary>
    internal static SparkTransfer ToModel(Transfer transfer) => new(
        Id: transfer.Id,
        SenderIdentityPublicKey: Convert.ToHexString(transfer.SenderIdentityPublicKey.Span).ToLowerInvariant(),
        ReceiverIdentityPublicKey: Convert.ToHexString(transfer.ReceiverIdentityPublicKey.Span).ToLowerInvariant(),
        TotalValueSats: ReportedSats(transfer.TotalValue),
        Status: transfer.Status.ToString(),
        CreatedAt: transfer.CreatedTime?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch,
        Type: transfer.Type.ToString())
    {
        SparkInvoice = string.IsNullOrEmpty(transfer.SparkInvoice) ? null : transfer.SparkInvoice,
    };
}

/// <summary>Network spellings derived from <see cref="SparkOptions"/>.</summary>
internal static class SparkNetworkExtensions
{
    /// <summary>The operators' network enum.</summary>
    internal static Network ProtoNetwork(this SparkOptions options) => options.Network.ToProto();

    /// <summary>The operators' network enum.</summary>
    internal static Network ToProto(this SparkNetwork network) => network switch
    {
        SparkNetwork.Mainnet => Network.Mainnet,
        SparkNetwork.Regtest => Network.Regtest,
        _ => throw new ArgumentOutOfRangeException(nameof(network)),
    };

    /// <summary>The SSP's GraphQL spelling: <c>MAINNET</c> or <c>REGTEST</c>.</summary>
    internal static string GraphQLName(this SparkNetwork network) => network switch
    {
        SparkNetwork.Mainnet => "MAINNET",
        SparkNetwork.Regtest => "REGTEST",
        _ => throw new ArgumentOutOfRangeException(nameof(network)),
    };

    /// <summary>Lower-case name, as the operators spell it in signed statements.</summary>
    internal static string LowerName(this SparkNetwork network) => network switch
    {
        SparkNetwork.Mainnet => "mainnet",
        SparkNetwork.Regtest => "regtest",
        _ => throw new ArgumentOutOfRangeException(nameof(network)),
    };
}
