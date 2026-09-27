using Microsoft.Extensions.Options;
using NSpark;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for <see cref="TokenService.CollectOperatorIdentityPublicKeys"/>.
/// </summary>
[TestFixture]
public sealed class OperatorKeysTests
{
    [Test]
    public async Task Mainnet_wallet_collects_three_sorted_operator_keys()
    {
        var options = Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet });
        using var connection = new SparkConnection(options, new HttpClient());
        var wallet = await connection.CreateWalletAsync(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");

        var keys = TokenService.CollectOperatorIdentityPublicKeys(wallet);
        keys.Should().HaveCount(3);

        // Lexicographic sort.
        for (int i = 0; i < keys.Count - 1; i++)
        {
            CompareBytes(keys[i], keys[i + 1]).Should().BeLessThan(0,
                because: $"key {i} ({Hex(keys[i])}) must lexicographically precede key {i + 1} ({Hex(keys[i + 1])})");
        }
    }

    [Test]
    public async Task Regtest_wallet_collects_the_hosted_operators_keys()
    {
        // The regtest preset is the hosted operators under their mainnet keys, as in the
        // reference SDK's REGTEST preset.
        var options = Options.Create(new SparkOptions
        {
            Network = SparkNetwork.Regtest,
            SigningOperators = SparkOptions.GetDefaultOperators(SparkNetwork.Regtest),
        });
        using var connection = new SparkConnection(options, new HttpClient());
        var wallet = await connection.CreateWalletAsync(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");

        var keys = TokenService.CollectOperatorIdentityPublicKeys(wallet);
        keys.Select(Hex).Should().BeEquivalentTo(
            SparkOptions.GetDefaultOperators(SparkNetwork.Mainnet).Select(o => o.IdentityPublicKeyHex));
    }

    [Test]
    public async Task Operators_without_an_identity_key_are_skipped()
    {
        var operators = SparkOptions.GetDefaultOperators(SparkNetwork.Regtest);
        operators[1] = operators[1] with { IdentityPublicKeyHex = string.Empty };
        var options = Options.Create(new SparkOptions { Network = SparkNetwork.Regtest, SigningOperators = operators });
        using var connection = new SparkConnection(options, new HttpClient());
        var wallet = await connection.CreateWalletAsync(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");

        TokenService.CollectOperatorIdentityPublicKeys(wallet).Should().HaveCount(2);
    }

    private static int CompareBytes(byte[] a, byte[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            var c = a[i].CompareTo(b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();
}
