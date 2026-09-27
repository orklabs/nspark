using Google.Protobuf;
using NBitcoin;
using NSpark.Exceptions;
using NSpark.Proto;

namespace NSpark.Services;

/// <summary>
/// Spark addresses: a bech32m encoding of the protobuf <c>SparkAddress</c> payload under a
/// network-specific human-readable part (<c>spark1...</c> on mainnet, <c>sparkrt1...</c> on
/// regtest; the legacy <c>sp1...</c> and <c>sprt1...</c> prefixes are accepted too). A plain
/// address carries only the receiver's identity public key; a Spark invoice also carries what to
/// pay, until when, from whom, and the receiver's signature.
/// </summary>
public static class SparkAddress
{
    private const string Operation = "spark.address.decode";

    /// <summary>Longest string decoded, as in the reference SDK.</summary>
    private const int MaxLength = 1024;

    /// <summary>A decoded Spark address or Spark invoice.</summary>
    /// <param name="IdentityPublicKey">The receiver's 33-byte identity public key.</param>
    /// <param name="InvoiceFields">Present when the string is a Spark invoice.</param>
    /// <param name="Signature">The receiver's signature over an invoice.</param>
    internal sealed record Payload(byte[] IdentityPublicKey, SparkInvoiceFields? InvoiceFields, byte[]? Signature);

    /// <summary>The current and legacy human-readable parts for a network.</summary>
    internal static (string Current, string Legacy) Prefixes(SparkNetwork network) => network switch
    {
        SparkNetwork.Mainnet => ("spark", "sp"),
        SparkNetwork.Regtest => ("sparkrt", "sprt"),
        _ => throw new ArgumentOutOfRangeException(nameof(network)),
    };

    /// <summary>Encode an identity public key as a Spark address for <paramref name="network"/>.</summary>
    public static string Encode(byte[] identityPublicKey, SparkNetwork network)
    {
        ArgumentNullException.ThrowIfNull(identityPublicKey);
        var payload = new Proto.SparkAddress { IdentityPublicKey = ByteString.CopyFrom(identityPublicKey) };
        return Bech32mHelper.Encode(Prefixes(network).Current, payload.ToByteArray());
    }

    /// <summary>
    /// The identity public key a plain Spark address for <paramref name="network"/> encodes.
    /// </summary>
    /// <exception cref="SparkConfigurationException">
    /// The address is malformed, belongs to another network, does not carry a valid compressed
    /// secp256k1 identity key, or is a Spark invoice: paying an invoice as if it were an address
    /// ignores its amount, expiry and sender restriction, and the transfer is not linked to it, so
    /// the payee never sees it paid (the reference SDK refuses invoices there too).
    /// </exception>
    public static byte[] Decode(string sparkAddress, SparkNetwork network)
    {
        var payload = DecodePayload(sparkAddress, network);
        if (payload.InvoiceFields is not null)
        {
            throw new SparkConfigurationException(
                Operation,
                "This is a Spark invoice, not a Spark address; paying it as an address would ignore its amount, expiry and sender.");
        }

        return payload.IdentityPublicKey;
    }

    /// <summary>
    /// Decode a Spark address and extract the 33-byte identity public key it carries. The network
    /// is taken from the address's prefix.
    /// </summary>
    /// <exception cref="SparkConfigurationException">See <see cref="Decode(string, SparkNetwork)"/>.</exception>
    [Obsolete("Use Decode(sparkAddress, network), which also checks that the address is for the wallet's network.")]
    public static byte[] DecodeIdentityPublicKey(string sparkAddress)
    {
        ArgumentException.ThrowIfNullOrEmpty(sparkAddress);
        var trimmed = sparkAddress.Trim().ToLowerInvariant();
        var network = trimmed.StartsWith("sparkrt1", StringComparison.Ordinal) || trimmed.StartsWith("sprt1", StringComparison.Ordinal)
            ? SparkNetwork.Regtest
            : SparkNetwork.Mainnet;
        return Decode(sparkAddress, network);
    }

    /// <summary>
    /// Decode the whole <c>SparkAddress</c> payload of a Spark address or Spark invoice, as the
    /// reference SDK's <c>decodeSparkAddress</c> does.
    /// </summary>
    /// <exception cref="SparkConfigurationException">
    /// A malformed string, one for another network, or an identity key that is not a compressed
    /// secp256k1 point.
    /// </exception>
    internal static Payload DecodePayload(string sparkAddress, SparkNetwork network)
    {
        ArgumentException.ThrowIfNullOrEmpty(sparkAddress);
        var trimmed = sparkAddress.Trim();

        string hrp;
        byte[] data;
        try
        {
            (hrp, data) = Bech32mHelper.Decode(trimmed, MaxLength);
        }
        catch (SparkConfigurationException ex)
        {
            throw new SparkConfigurationException(Operation, $"'{trimmed}' is not a Spark address: {ex.Message}", ex);
        }

        var (current, legacy) = Prefixes(network);
        if (!string.Equals(hrp, current, StringComparison.Ordinal) && !string.Equals(hrp, legacy, StringComparison.Ordinal))
        {
            throw new SparkConfigurationException(
                Operation, $"'{trimmed}' is not a {network} Spark address (prefix '{hrp}').");
        }

        Proto.SparkAddress payload;
        try
        {
            payload = Proto.SparkAddress.Parser.ParseFrom(data);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new SparkConfigurationException(Operation, $"'{trimmed}' has an invalid payload encoding.", ex);
        }

        var identityKey = payload.IdentityPublicKey.ToByteArray();
        if (!IsCompressedPublicKey(identityKey))
        {
            throw new SparkConfigurationException(
                Operation, $"'{trimmed}' does not carry a valid 33-byte identity public key.");
        }

        return new Payload(
            identityKey,
            payload.SparkInvoiceFields,
            payload.HasSignature ? payload.Signature.ToByteArray() : null);
    }

    /// <summary>Whether <paramref name="key"/> is a 33-byte compressed secp256k1 point on the curve.</summary>
    internal static bool IsCompressedPublicKey(byte[]? key)
    {
        if (key is not { Length: 33 } || (key[0] != 0x02 && key[0] != 0x03))
        {
            return false;
        }

        return PubKey.TryCreatePubKey(key, out _);
    }
}
