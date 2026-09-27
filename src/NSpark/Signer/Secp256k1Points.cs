using System.Globalization;
using System.Numerics;

namespace NSpark.Signer;

/// <summary>
/// Point arithmetic on secp256k1 public keys — negation and addition of compressed points. Only
/// ever applied to public data (verifying keys, signing public keys), so the variable-time
/// big-integer arithmetic here leaks nothing secret. NBitcoin keeps its own group-element types
/// internal.
/// </summary>
internal static class Secp256k1Points
{
    private static readonly BigInteger P = BigInteger.Parse(
        "0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC2F",
        NumberStyles.HexNumber,
        CultureInfo.InvariantCulture);

    private readonly record struct Point(BigInteger X, BigInteger Y);

    /// <summary><c>a - b</c> for two 33-byte compressed points; <c>null</c> when either is not a point or the result is infinity.</summary>
    public static byte[]? Subtract(byte[] a, byte[] b)
    {
        if (!TryDecompress(a, out var lhs) || !TryDecompress(b, out var rhs))
        {
            return null;
        }

        var negated = new Point(rhs.X, Mod(-rhs.Y));
        return Add(lhs, negated) is { } sum ? Compress(sum) : null;
    }

    /// <summary><c>a + b</c> for two 33-byte compressed points; <c>null</c> when either is not a point or the result is infinity.</summary>
    public static byte[]? Add(byte[] a, byte[] b)
    {
        if (!TryDecompress(a, out var lhs) || !TryDecompress(b, out var rhs))
        {
            return null;
        }

        return Add(lhs, rhs) is { } sum ? Compress(sum) : null;
    }

    private static Point? Add(Point a, Point b)
    {
        BigInteger lambda;
        if (a.X == b.X)
        {
            if (Mod(a.Y + b.Y) == 0)
            {
                return null; // a == -b
            }

            // Doubling: λ = 3x² / 2y (the curve's a coefficient is 0).
            lambda = Mod(3 * a.X * a.X * Inverse(2 * a.Y));
        }
        else
        {
            lambda = Mod((b.Y - a.Y) * Inverse(b.X - a.X));
        }

        var x = Mod((lambda * lambda) - a.X - b.X);
        var y = Mod((lambda * (a.X - x)) - a.Y);
        return new Point(x, y);
    }

    private static bool TryDecompress(byte[]? encoded, out Point point)
    {
        point = default;
        if (encoded is not { Length: 33 } || (encoded[0] != 0x02 && encoded[0] != 0x03))
        {
            return false;
        }

        var x = new BigInteger(encoded.AsSpan(1), isUnsigned: true, isBigEndian: true);
        if (x >= P)
        {
            return false;
        }

        var ySquared = Mod((x * x * x) + 7);
        var y = BigInteger.ModPow(ySquared, (P + 1) / 4, P);
        if (Mod(y * y) != ySquared)
        {
            return false; // x is not on the curve
        }

        var wantOdd = encoded[0] == 0x03;
        if (y.IsEven == wantOdd)
        {
            y = P - y;
        }

        point = new Point(x, y);
        return true;
    }

    private static byte[] Compress(Point point)
    {
        var result = new byte[33];
        result[0] = point.Y.IsEven ? (byte)0x02 : (byte)0x03;
        var x = point.X.ToByteArray(isUnsigned: true, isBigEndian: true);
        x.CopyTo(result, 33 - x.Length);
        return result;
    }

    private static BigInteger Mod(BigInteger value)
    {
        var r = value % P;
        return r.Sign < 0 ? r + P : r;
    }

    private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);
}
