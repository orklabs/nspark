using Microsoft.Extensions.Options;
using NSpark;
using NSpark.Exceptions;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Validation-path tests for the write-side TokenService methods. These never
/// hit the network — each test forces an argument-validation failure before
/// any gRPC call would be made, so they're safe to run without funded test
/// wallets.
/// </summary>
[TestFixture]
public sealed class TokenValidationTests
{
    private SparkConnection _connection = null!;
    private SparkWallet _wallet = null!;

    [SetUp]
    public async Task Setup()
    {
        var options = Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet });
        _connection = new SparkConnection(options, new HttpClient());
        _wallet = await _connection.CreateWalletAsync(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");
    }

    [TearDown]
    public void Teardown() => _connection.Dispose();

    [Test]
    public async Task CreateTokenAsync_throws_on_name_too_long()
    {
        Func<Task> act = () => _wallet.CreateTokenAsync(
            tokenName: new string('A', 21),
            tokenTicker: "TST",
            decimals: 8,
            maxSupply: UInt128.Zero,
            isFreezable: false);
        await act.Should().ThrowAsync<SparkConfigurationException>()
            .WithMessage("*3-20 UTF-8 bytes*");
    }

    [Test]
    public async Task CreateTokenAsync_throws_on_empty_name()
    {
        Func<Task> act = () => _wallet.CreateTokenAsync(
            tokenName: "",
            tokenTicker: "TST",
            decimals: 8,
            maxSupply: UInt128.Zero,
            isFreezable: false);
        await act.Should().ThrowAsync<SparkConfigurationException>()
            .WithMessage("*3-20 UTF-8 bytes*");
    }

    [Test]
    public async Task CreateTokenAsync_throws_on_ticker_too_long()
    {
        Func<Task> act = () => _wallet.CreateTokenAsync(
            tokenName: "Test",
            tokenTicker: "TOOLONG",
            decimals: 8,
            maxSupply: UInt128.Zero,
            isFreezable: false);
        await act.Should().ThrowAsync<SparkConfigurationException>()
            .WithMessage("*3-6 UTF-8 bytes*");
    }

    [Test]
    public async Task CreateTokenAsync_throws_on_decimals_above_255()
    {
        Func<Task> act = () => _wallet.CreateTokenAsync(
            tokenName: "Test",
            tokenTicker: "TST",
            decimals: 256,
            maxSupply: UInt128.Zero,
            isFreezable: false);
        await act.Should().ThrowAsync<SparkConfigurationException>()
            .WithMessage("*<= 255*");
    }

    [Test]
    public async Task CreateTokenAsync_throws_on_oversized_extra_metadata()
    {
        Func<Task> act = () => _wallet.CreateTokenAsync(
            tokenName: "Test",
            tokenTicker: "TST",
            decimals: 8,
            maxSupply: UInt128.Zero,
            isFreezable: false,
            extraMetadata: new byte[1025]);
        await act.Should().ThrowAsync<SparkConfigurationException>()
            .WithMessage("*<= 1024 bytes*");
    }

    // The reference SDK's token-create.test.ts, with the operators' NFC rule (TokenMetadata.ValidatePartial).
    [TestCase("abc", "AAA", TestName = "Accepts the shortest name")]
    [TestCase("12345678901234567890", "AAA", TestName = "Accepts the longest name")]
    [TestCase("Token", "ABC", TestName = "Accepts the shortest ticker")]
    [TestCase("Token", "ABCDEF", TestName = "Accepts the longest ticker")]
    [TestCase("ABCDEFGHIJKLMNOPQ", "AAA", TestName = "Accepts a 17-byte name")]
    [TestCase("Tok\U0001F680n", "TOK", TestName = "Accepts a 4-byte character in the name")]
    [TestCase("Caf\u00E9", "\u00C9CU", TestName = "Accepts precomposed characters")]
    public void ValidateTokenParameters_accepts(string name, string ticker)
    {
        TokenService.ValidateTokenParameters(name, ticker, 0, null);
    }

    [TestCase("ab", "AAA", TestName = "Refuses a name that is too short")]
    [TestCase("123456789012345678901", "AAA", TestName = "Refuses a name that is too long")]
    [TestCase("Token", "AB", TestName = "Refuses a ticker that is too short")]
    [TestCase("Token", "ABCDEFG", TestName = "Refuses a ticker that is too long")]
    [TestCase("Cafe\u0301", "TOK", TestName = "Refuses a name that is not NFC")]
    [TestCase("Token", "E\u0301CU", TestName = "Refuses a ticker that is not NFC")]
    public void ValidateTokenParameters_refuses(string name, string ticker)
    {
        var act = () => TokenService.ValidateTokenParameters(name, ticker, 0, null);

        act.Should().Throw<SparkConfigurationException>();
    }

    [Test]
    public void ValidateTokenParameters_allows_decimals_up_to_255_and_extra_metadata_up_to_1024_bytes()
    {
        TokenService.ValidateTokenParameters("Token", "TOK", 255, new byte[1024]);
        var decimals = () => TokenService.ValidateTokenParameters("Token", "TOK", 256, null);
        decimals.Should().Throw<SparkConfigurationException>();
        var extra = () => TokenService.ValidateTokenParameters("Token", "TOK", 0, new byte[1025]);
        extra.Should().Throw<SparkConfigurationException>();
    }

    [Test]
    public async Task MintTokensAsync_throws_on_zero_amount()
    {
        var fakeTokenId = TokenIdentifier.Encode(new byte[32], SparkNetwork.Mainnet);
        Func<Task> act = () => _wallet.MintTokensAsync(fakeTokenId, UInt128.Zero);
        await act.Should().ThrowAsync<SparkConfigurationException>()
            .WithMessage("*greater than 0*");
    }
}
