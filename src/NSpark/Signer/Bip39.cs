using System.Security.Cryptography;
using System.Text;
using NBitcoin;
using NSpark.Exceptions;

namespace NSpark.Signer;

/// <summary>
/// BIP-39 mnemonic validation (English wordlist).
/// </summary>
/// <remarks>
/// A mnemonic must be 12, 15, 18, 21 or 24 lower-case English words separated by single spaces,
/// and its embedded checksum must match. Without this check a mistyped phrase silently derives a
/// different, empty wallet — and funds deposited to it can only be recovered with the exact typo.
/// NBitcoin's <c>Mnemonic</c> accepts a phrase whose checksum does not match.
/// </remarks>
public static class Bip39
{
    private const string Operation = "mnemonic.validate";

    private static readonly int[] s_validWordCounts = [12, 15, 18, 21, 24];

    /// <summary>Whether <paramref name="mnemonic"/> passes <see cref="Validate"/>.</summary>
    public static bool IsValid(string? mnemonic)
    {
        try
        {
            Validate(mnemonic);
            return true;
        }
        catch (SparkConfigurationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Throws <see cref="SparkConfigurationException"/> describing the first problem found.
    /// </summary>
    public static void Validate(string? mnemonic)
    {
        var normalized = (mnemonic ?? string.Empty).Normalize(NormalizationForm.FormKD);
        var words = normalized.Split(' ');
        if (Array.IndexOf(s_validWordCounts, words.Length) < 0)
        {
            throw Invalid($"expected 12, 15, 18, 21 or 24 words separated by single spaces, got {words.Length}");
        }

        var wordlist = Wordlist.English;
        var indices = new int[words.Length];
        for (var position = 0; position < words.Length; position++)
        {
            var word = words[position];
            if (wordlist.WordExists(word, out var index) && string.Equals(wordlist.GetWordAtIndex(index), word, StringComparison.Ordinal))
            {
                indices[position] = index;
                continue;
            }

            if (word.Length == 0)
            {
                throw Invalid($"empty word at position {position + 1} (double space or leading/trailing space)");
            }

            if (wordlist.WordExists(word.ToLowerInvariant(), out _))
            {
                throw Invalid($"word {position + 1} must be lower case");
            }

            throw Invalid($"word {position + 1} is not in the BIP-39 English wordlist");
        }

        // Concatenate the 11-bit indices; the last count/3 bits are the checksum of the entropy.
        var totalBits = words.Length * 11;
        var checksumBits = words.Length / 3;
        var entropyBits = totalBits - checksumBits;
        var bits = new bool[totalBits];
        for (var i = 0; i < indices.Length; i++)
        {
            for (var shift = 10; shift >= 0; shift--)
            {
                bits[(i * 11) + (10 - shift)] = ((indices[i] >> shift) & 1) == 1;
            }
        }

        var entropy = new byte[entropyBits / 8];
        for (var bit = 0; bit < entropyBits; bit++)
        {
            if (bits[bit])
            {
                entropy[bit / 8] |= (byte)(0x80 >> (bit % 8));
            }
        }

        var hash = SHA256.HashData(entropy);
        for (var bit = 0; bit < checksumBits; bit++)
        {
            var expected = ((hash[bit / 8] >> (7 - (bit % 8))) & 1) == 1;
            if (bits[entropyBits + bit] != expected)
            {
                throw Invalid("checksum mismatch — one or more words are wrong");
            }
        }
    }

    private static SparkConfigurationException Invalid(string reason) =>
        new(Operation, $"Invalid mnemonic: {reason}.");
}
