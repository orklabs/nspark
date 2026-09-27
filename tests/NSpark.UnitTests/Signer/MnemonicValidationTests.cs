using Microsoft.Extensions.Options;
using NBitcoin;
using NSpark.Exceptions;
using NSpark.Signer;

namespace NSpark.UnitTests.Signer;

/// <summary>BIP-39 validation: a mistyped phrase must not silently derive another, empty wallet.</summary>
[TestFixture]
public sealed class MnemonicValidationTests
{
    private const string Vector12a = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
    private const string Vector12b = "ozone drill grab fiber curtain grace pudding thank cruise elder eight picnic";
    private const string Vector18 = "legal winner thank year wave sausage worth useful legal winner thank year wave sausage worth useful legal will";
    private const string Vector24a = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon art";
    private const string Vector24b = "zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo vote";
    private const string Typo = "ozone drill grab fiber curtain grace pudding thank cruise elder eight picnics";

    [TestCase(Vector12a)]
    [TestCase(Vector12b)]
    [TestCase(Vector18)]
    [TestCase(Vector24a)]
    [TestCase(Vector24b)]
    public void Official_test_vectors_validate(string mnemonic)
    {
        Bip39.Validate(mnemonic);
        Bip39.IsValid(mnemonic).Should().BeTrue();
    }

    [TestCase("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon", TestName = "checksum: last word swapped")]
    [TestCase(Typo, TestName = "checksum: one word changed")]
    [TestCase("drill ozone grab fiber curtain grace pudding thank cruise elder eight picnic", TestName = "checksum: two words swapped")]
    [TestCase("ozone drill grab fibre curtain grace pudding thank cruise elder eight picnic", TestName = "unknown word")]
    [TestCase("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about", TestName = "eleven words")]
    [TestCase("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about abandon", TestName = "thirteen words")]
    [TestCase("Abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about", TestName = "upper case")]
    [TestCase("abandon  abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about", TestName = "double space")]
    [TestCase(Vector12a + " ", TestName = "trailing space")]
    [TestCase("", TestName = "empty")]
    [TestCase("abababababababababababababababababababababababababababababababab", TestName = "hex seed")]
    public void Typos_unknown_words_wrong_counts_case_and_spacing_are_rejected(string mnemonic)
    {
        Bip39.IsValid(mnemonic).Should().BeFalse();
        var act = () => Bip39.Validate(mnemonic);
        act.Should().Throw<SparkConfigurationException>();
    }

    [Test]
    public async Task Wallet_and_signer_refuse_an_invalid_mnemonic_unless_validation_is_explicitly_disabled()
    {
        using var connection = new SparkConnection(Options.Create(new SparkOptions()), new HttpClient());

        var wallet = () => connection.CreateWalletAsync(Typo);
        await wallet.Should().ThrowAsync<SparkConfigurationException>();
        ((Action)(() => SparkSigner.FromMnemonic(Typo))).Should().Throw<SparkConfigurationException>();
        ((Action)(() => KeyDerivation.FromMnemonic(Typo))).Should().Throw<SparkConfigurationException>();

        // The escape hatch keeps the previous behaviour for phrases known to be non-standard.
        var lenient = await connection.CreateWalletAsync(SparkSigner.FromMnemonic(Typo, 1, validateMnemonic: false));
        lenient.IdentityPublicKeyHex.Should().HaveLength(66);

        // Valid phrases still derive the same keys as before (mainnet's default account is 1).
        var valid = await connection.CreateWalletAsync(Vector12b);
        var identity = await SparkSigner.FromMnemonic(Vector12b, 1).GetIdentityPublicKeyAsync();
        valid.IdentityPublicKey.Should().Equal(identity);
    }

    [Test]
    public void The_seed_is_BIP_39s_whether_or_not_the_phrase_is_in_the_wordlist()
    {
        // The specification's vector (Trezor's): 12 × abandon … about, passphrase "TREZOR".
        Convert.ToHexString(KeyDerivation.Bip39Seed(Vector12a, "TREZOR")).ToLowerInvariant().Should().Be(
            "c55257c360c07c72029aebc1b53c05ed0362ada38ead3e3e9efa3708e53495531f09a6987599d18264c1e1c92f2cf141630c7a3c4ab7c81b2f001698e7463b04");
        foreach (var phrase in new[] { Vector12a, Vector12b, Vector18, Vector24a, Vector24b })
        {
            foreach (var passphrase in new[] { null, "TREZOR", "pässphrase" })
            {
                KeyDerivation.MnemonicToSeed(phrase, passphrase).Should().Equal(new Mnemonic(phrase).DeriveSeed(passphrase));
                KeyDerivation.Bip39Seed(phrase, passphrase).Should().Equal(new Mnemonic(phrase).DeriveSeed(passphrase));
            }
        }

        // A word outside the wordlist still derives a seed when validation is off.
        KeyDerivation.MnemonicToSeed(Typo, null).Should().Equal(KeyDerivation.Bip39Seed(Typo, null));
    }

    [Test]
    public void Account_indexes_outside_the_hardened_range_are_refused_instead_of_wrapping()
    {
        ((Action)(() => KeyDerivation.FromMnemonic(Vector12a, -1))).Should().Throw<SparkConfigurationException>();
        ((Action)(() => KeyDerivation.FromMnemonic(Vector12a, int.MinValue))).Should().Throw<SparkConfigurationException>();
        ((Action)(() => KeyDerivation.FromSeed(new Mnemonic(Vector12a).DeriveSeed(), -7))).Should().Throw<SparkConfigurationException>();
        ((Action)(() => KeyDerivation.FromMnemonic(Vector12a, int.MaxValue))).Should().NotThrow();
    }
}
