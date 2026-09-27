using System.Security.Cryptography;
using System.Text;
using NBitcoin;

namespace NSpark.Signer;

/// <summary>
/// BIP-39/32 key derivation for Spark wallets.
/// Derives 5 keys at m/8797555'/{account}'/0'-4'.
/// </summary>
public sealed class KeyDerivation
{
    /// <summary>BIP-44 purpose number used by the Spark protocol.</summary>
    public const int SparkPurpose = 8797555;

    private const uint HardenedOffset = 0x80000000;

    /// <summary>Identity key (`m/8797555'/{account}'/0'`), used for authentication and ECIES.</summary>
    public ExtKey IdentityKey { get; }

    /// <summary>Signing key (`m/8797555'/{account}'/1'`), parent of per-leaf signing keys.</summary>
    public ExtKey SigningKey { get; }

    /// <summary>Deposit key (`m/8797555'/{account}'/2'`), used for on-chain deposit addresses.</summary>
    public ExtKey DepositKey { get; }

    /// <summary>Static deposit key (`m/8797555'/{account}'/3'`), parent of per-index static deposits.</summary>
    public ExtKey StaticDepositKey { get; }

    /// <summary>HTLC preimage key (`m/8797555'/{account}'/4'`), HMAC key for deterministic preimages.</summary>
    public ExtKey HtlcPreimageKey { get; }

    private KeyDerivation(ExtKey identity, ExtKey signing, ExtKey deposit, ExtKey staticDeposit, ExtKey htlcPreimage)
    {
        IdentityKey = identity;
        SigningKey = signing;
        DepositKey = deposit;
        StaticDepositKey = staticDeposit;
        HtlcPreimageKey = htlcPreimage;
    }

    /// <summary>
    /// Derive all Spark keys from a BIP-39 mnemonic.
    /// </summary>
    /// <param name="mnemonic">The BIP-39 phrase.</param>
    /// <param name="account">BIP-32 account index, 0 to 2^31 − 1.</param>
    /// <param name="passphrase">Optional BIP-39 passphrase.</param>
    /// <param name="validateMnemonic">
    /// Refuse a phrase that fails the BIP-39 English wordlist or checksum (see <see cref="Bip39"/>)
    /// instead of silently deriving a different wallet. Pass <c>false</c> only for phrases known to
    /// be non-standard: the seed is then derived from the phrase as BIP-39 specifies it (PBKDF2 over
    /// its NFKD form), whatever its words, as the Swift SDK derives it.
    /// </param>
    /// <exception cref="Exceptions.SparkConfigurationException">
    /// The mnemonic fails validation, or the account index is out of range.
    /// </exception>
    public static KeyDerivation FromMnemonic(
        string mnemonic,
        int account = 0,
        string? passphrase = null,
        bool validateMnemonic = true)
    {
        ArgumentNullException.ThrowIfNull(mnemonic);
        ValidateAccount(account);
        if (validateMnemonic)
        {
            Bip39.Validate(mnemonic);
        }

        var seed = MnemonicToSeed(mnemonic, passphrase);
        try
        {
            return FromSeed(seed, account);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>
    /// The BIP-39 seed of <paramref name="mnemonic"/>. A phrase NBitcoin parses gets its seed, as
    /// before; any other phrase (words outside the wordlist, which only an unvalidated load lets
    /// through) gets the specification's PBKDF2-HMAC-SHA512 over the NFKD phrase with the salt
    /// "mnemonic" + passphrase, 2048 rounds — the same seed for every phrase NBitcoin accepts.
    /// </summary>
    internal static byte[] MnemonicToSeed(string mnemonic, string? passphrase)
    {
        try
        {
            return new Mnemonic(mnemonic).DeriveSeed(passphrase);
        }
        catch (FormatException)
        {
            return Bip39Seed(mnemonic, passphrase);
        }
    }

    /// <summary>BIP-39's seed function: PBKDF2-HMAC-SHA512, 2048 rounds, over the NFKD phrase and "mnemonic" + passphrase.</summary>
    internal static byte[] Bip39Seed(string mnemonic, string? passphrase) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(mnemonic.Normalize(NormalizationForm.FormKD)),
            Encoding.UTF8.GetBytes(("mnemonic" + (passphrase ?? string.Empty)).Normalize(NormalizationForm.FormKD)),
            2048,
            HashAlgorithmName.SHA512,
            64);

    /// <summary>
    /// Derive all Spark keys from a raw seed.
    /// </summary>
    /// <exception cref="Exceptions.SparkConfigurationException">The account index is out of range.</exception>
    public static KeyDerivation FromSeed(byte[] seed, int account = 0)
    {
        ValidateAccount(account);
        var master = ExtKey.CreateFromSeed(seed);

        // m/8797555'/{account}'
        var accountKey = master
            .Derive(SparkPurpose, hardened: true)
            .Derive(account, hardened: true);

        return new KeyDerivation(
            identity: accountKey.Derive(0, hardened: true),
            signing: accountKey.Derive(1, hardened: true),
            deposit: accountKey.Derive(2, hardened: true),
            staticDeposit: accountKey.Derive(3, hardened: true),
            htlcPreimage: accountKey.Derive(4, hardened: true)
        );
    }

    /// <summary>A hardened BIP-32 index: 0 to 2^31 − 1.</summary>
    private static void ValidateAccount(int account)
    {
        if (account < 0)
        {
            throw new Exceptions.SparkConfigurationException(
                "mnemonic.account", $"The account index must be between 0 and 2^31-1, got {account}.");
        }
    }

    /// <summary>
    /// Derive a per-leaf signing key from a leaf node ID.
    /// sha256(leafId) → first 4 bytes as uint32 BE → (value % 2^31) + 2^31 → hardened child.
    /// </summary>
    public ExtKey DeriveLeafKey(string leafId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(leafId));
        var index = (uint)((hash[0] << 24) | (hash[1] << 16) | (hash[2] << 8) | hash[3]);
        var childIndex = index % HardenedOffset;
        return SigningKey.Derive((int)childIndex, hardened: true);
    }

    /// <summary>
    /// Derive a static deposit key for a given index (hardened child).
    /// </summary>
    public ExtKey DeriveStaticDepositChildKey(int index)
    {
        return StaticDepositKey.Derive(index, hardened: true);
    }

    /// <summary>
    /// Generate a deterministic HTLC preimage: HMAC-SHA256(htlcPrivateKey, transferId).
    /// </summary>
    /// <remarks>
    /// The local copy of the HTLC private key is zeroed before this method returns.
    /// The underlying NBitcoin <c>ExtKey</c> still retains the key for the lifetime
    /// of the <see cref="KeyDerivation"/> instance — that is a deliberate trade-off
    /// (FROST signing requires repeated access). Callers handling especially
    /// sensitive material should treat the entire process memory as the trust
    /// boundary; see <c>docs/trust-model.md</c> for the threat model.
    /// </remarks>
    public byte[] ComputePreimage(string transferId)
    {
        var key = HtlcPreimageKey.PrivateKey.ToBytes();
        try
        {
            var data = Encoding.UTF8.GetBytes(transferId);
            return HMACSHA256.HashData(key, data);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
