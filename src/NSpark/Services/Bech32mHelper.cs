using System.Text;
using NSpark.Exceptions;

namespace NSpark.Services;

/// <summary>Which checksum constant a bech32-family string was encoded with.</summary>
internal enum Bech32Variant
{
    /// <summary>BIP-173 bech32 (constant 1): segwit v0 addresses, BOLT-11 invoices.</summary>
    Bech32,

    /// <summary>BIP-350 bech32m (constant 0x2bc830a3): segwit v1+ addresses, Spark addresses, token ids.</summary>
    Bech32m,
}

/// <summary>
/// Bech32m encoder / decoder used by Spark addresses, Bitcoin segwit
/// addresses, and Spark token identifiers. Implements BIP-350 with arbitrary
/// human-readable parts. <see cref="DecodeWords(string)"/> additionally accepts plain
/// BIP-173 bech32, which BOLT-11 invoices use.
/// </summary>
public static class Bech32mHelper
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private const uint Bech32Const = 1;
    private const uint Bech32mConst = 0x2bc830a3;

    private static readonly int[] CharsetLookup = BuildCharsetLookup();

    /// <summary>
    /// Encode raw bytes as a Bech32m string under the given HRP.
    /// </summary>
    public static string Encode(string hrp, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(hrp);
        ArgumentNullException.ThrowIfNull(data);

        var words = ConvertBits(data, fromBits: 8, toBits: 5, pad: true)
            ?? throw new SparkConfigurationException("bech32m.encode", "Failed to convert bytes to 5-bit words.");
        return EncodeWords(hrp, words, Bech32Variant.Bech32m);
    }

    /// <summary>
    /// Encode a segwit v1+ (bech32m) address. Per BIP-350 the witness version is
    /// a single raw 5-bit word prepended to the 8→5-bit converted witness
    /// program — unlike <see cref="Encode"/>, which converts the whole payload.
    /// </summary>
    internal static string EncodeSegwit(string hrp, byte witnessVersion, ReadOnlySpan<byte> program)
    {
        var programWords = ConvertBits(program, fromBits: 8, toBits: 5, pad: true)
            ?? throw new SparkConfigurationException(
                "bech32m.encode", "Failed to convert witness program to 5-bit words.");

        var words = new byte[programWords.Length + 1];
        words[0] = witnessVersion;
        programWords.CopyTo(words.AsSpan(1));

        return EncodeWords(hrp, words, Bech32Variant.Bech32m);
    }

    /// <summary>
    /// Encode raw 5-bit words (no checksum) under the given HRP with the checksum of
    /// <paramref name="variant"/>. The inverse of <see cref="DecodeWords(string)"/>.
    /// </summary>
    internal static string EncodeWords(string hrp, ReadOnlySpan<byte> words, Bech32Variant variant)
    {
        ArgumentNullException.ThrowIfNull(hrp);

        var checksum = CreateChecksum(hrp, words, variant == Bech32Variant.Bech32m ? Bech32mConst : Bech32Const);

        var sb = new StringBuilder(hrp.Length + 1 + words.Length + 6);
        sb.Append(hrp);
        sb.Append('1');
        foreach (var w in words)
        {
            sb.Append(Charset[w]);
        }
        foreach (var c in checksum)
        {
            sb.Append(Charset[c]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Decode a Bech32m string into its HRP and raw byte payload.
    /// </summary>
    /// <param name="bech32m">The encoded string (case-insensitive).</param>
    /// <param name="limit">Maximum allowed encoded length. Defaults to 90 for BIP-350; pass 500 for Spark token identifiers.</param>
    /// <exception cref="SparkConfigurationException">
    /// Thrown when the string is malformed, exceeds <paramref name="limit"/>,
    /// contains invalid characters, or fails the checksum.
    /// </exception>
    public static (string Hrp, byte[] Data) Decode(string bech32m, int limit = 90)
    {
        ArgumentException.ThrowIfNullOrEmpty(bech32m);

        if (bech32m.Length > limit)
        {
            throw new SparkConfigurationException(
                "bech32m.decode",
                $"Bech32m string is {bech32m.Length} chars, max is {limit}.");
        }

        var (hrp, words, variant) = DecodeWords(bech32m, "bech32m.decode");
        if (variant != Bech32Variant.Bech32m)
        {
            throw new SparkConfigurationException("bech32m.decode", "Invalid bech32m checksum.");
        }

        // Convert the 5-bit payload words back to bytes.
        var bytes = ConvertBits(words, fromBits: 5, toBits: 8, pad: false)
            ?? throw new SparkConfigurationException("bech32m.decode", "Bech32m payload contains illegal padding.");

        return (hrp, bytes);
    }

    /// <summary>
    /// Decode a bech32 (BIP-173) or bech32m (BIP-350) string into its HRP and the raw 5-bit
    /// data words with the six checksum words stripped, reporting which checksum matched.
    /// There is no length limit — BOLT-11 invoices routinely exceed 90 characters — and the
    /// words are returned unconverted because BOLT-11 tagged fields are laid out in 5-bit words.
    /// Mixed-case input is rejected, as BIP-173 requires.
    /// </summary>
    internal static (string Hrp, byte[] Words, Bech32Variant Variant) DecodeWords(string encoded)
        => DecodeWords(encoded, "bech32.decode");

    private static (string Hrp, byte[] Words, Bech32Variant Variant) DecodeWords(string encoded, string operation)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoded);

        var hasLower = false;
        var hasUpper = false;
        foreach (var c in encoded)
        {
            if (c < 33 || c > 126)
            {
                throw new SparkConfigurationException(operation, $"Invalid character (code {(int)c}) in bech32 string.");
            }
            hasLower |= char.IsLower(c);
            hasUpper |= char.IsUpper(c);
        }
        if (hasLower && hasUpper)
        {
            throw new SparkConfigurationException(operation, "Bech32 strings must not mix upper and lower case.");
        }

        var lower = encoded.ToLowerInvariant();
        var sepIdx = lower.LastIndexOf('1');
        if (sepIdx < 1 || sepIdx + 7 > lower.Length)
        {
            throw new SparkConfigurationException(operation, "Bech32 string missing or misplaced '1' separator.");
        }

        var hrp = lower[..sepIdx];
        var dataStr = lower[(sepIdx + 1)..];

        var words = new byte[dataStr.Length];
        for (int i = 0; i < dataStr.Length; i++)
        {
            var idx = (int)dataStr[i] < CharsetLookup.Length ? CharsetLookup[dataStr[i]] : -1;
            if (idx < 0)
            {
                throw new SparkConfigurationException(
                    operation,
                    $"Invalid bech32 character '{dataStr[i]}' at position {sepIdx + 1 + i}.");
            }
            words[i] = (byte)idx;
        }

        var hrpExpanded = HrpExpand(hrp);
        var values = new byte[hrpExpanded.Length + words.Length];
        Array.Copy(hrpExpanded, 0, values, 0, hrpExpanded.Length);
        Array.Copy(words, 0, values, hrpExpanded.Length, words.Length);
        var variant = Polymod(values) switch
        {
            Bech32Const => Bech32Variant.Bech32,
            Bech32mConst => Bech32Variant.Bech32m,
            _ => throw new SparkConfigurationException(operation, "Invalid bech32 checksum."),
        };

        // Strip the 6-word checksum suffix.
        return (hrp, words[..^6], variant);
    }

    /// <summary>
    /// Convert a byte array between two bit-widths (e.g., 8-bit to 5-bit for
    /// Bech32m encoding). Returns <c>null</c> when <paramref name="pad"/> is
    /// false and the input cannot be evenly converted (i.e., the source
    /// contained illegal padding bits).
    /// </summary>
    internal static byte[]? ConvertBits(ReadOnlySpan<byte> data, int fromBits, int toBits, bool pad)
    {
        var acc = 0;
        var bits = 0;
        var maxv = (1 << toBits) - 1;
        var result = new List<byte>(data.Length * fromBits / toBits + 1);
        foreach (var value in data)
        {
            acc = (acc << fromBits) | value;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                result.Add((byte)((acc >> bits) & maxv));
            }
        }
        if (pad)
        {
            if (bits > 0)
            {
                result.Add((byte)((acc << (toBits - bits)) & maxv));
            }
        }
        else if (bits >= fromBits || ((acc << (toBits - bits)) & maxv) != 0)
        {
            return null;
        }
        return result.ToArray();
    }

    private static byte[] CreateChecksum(string hrp, ReadOnlySpan<byte> data, uint constant)
    {
        var hrpExpanded = HrpExpand(hrp);
        var values = new byte[hrpExpanded.Length + data.Length + 6];
        Array.Copy(hrpExpanded, 0, values, 0, hrpExpanded.Length);
        data.CopyTo(values.AsSpan(hrpExpanded.Length));

        var polymod = Polymod(values) ^ constant;
        var checksum = new byte[6];
        for (int i = 0; i < 6; i++)
        {
            checksum[i] = (byte)((polymod >> (5 * (5 - i))) & 0x1F);
        }
        return checksum;
    }

    private static byte[] HrpExpand(string hrp)
    {
        var result = new byte[(hrp.Length * 2) + 1];
        for (int i = 0; i < hrp.Length; i++)
        {
            result[i] = (byte)(hrp[i] >> 5);
        }
        result[hrp.Length] = 0;
        for (int i = 0; i < hrp.Length; i++)
        {
            result[hrp.Length + 1 + i] = (byte)(hrp[i] & 0x1F);
        }
        return result;
    }

    private static uint Polymod(byte[] values)
    {
        uint chk = 1;
        ReadOnlySpan<uint> gen = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];
        foreach (var v in values)
        {
            var top = chk >> 25;
            chk = ((chk & 0x1FFFFFF) << 5) ^ v;
            for (int i = 0; i < 5; i++)
            {
                if (((top >> i) & 1) == 1)
                {
                    chk ^= gen[i];
                }
            }
        }
        return chk;
    }

    private static int[] BuildCharsetLookup()
    {
        var lookup = new int[128];
        Array.Fill(lookup, -1);
        for (int i = 0; i < Charset.Length; i++)
        {
            lookup[Charset[i]] = i;
        }
        return lookup;
    }
}
