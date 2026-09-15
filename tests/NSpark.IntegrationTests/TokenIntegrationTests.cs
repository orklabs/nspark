using Microsoft.Extensions.Options;
using NSpark;
using NSpark.Models;
using NSpark.Services;

namespace NSpark.Tests;

// =============================================================================
// Token integration tests (matching Swift TokenIntegrationTests).
//
// These exercise the live Spark token endpoints against the configured network.
// They are gated on TestSecrets — set NSPARK_TEST_MNEMONIC_A / _B in
// .env.local (or as env vars) for funded wallets that hold sats and at least
// one token, otherwise the tests are reported as inconclusive (skipped).
// =============================================================================

[TestFixture]
[Category("Integration")]
[Category("Tokens")]
public class TokenReadTests
{
    private static string MnemonicA => TestSecrets.MnemonicA;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private SparkConnection _client = null!;
    private SparkWallet _wallet = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _client = new SparkConnection(
            Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet }),
            new HttpClient());
        _wallet = await _client.CreateWalletAsync(MnemonicA);
    }

    [OneTimeTearDown]
    public void Teardown() => _client?.Dispose();

    [Test]
    public async Task ShouldListTokenOutputs()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var outputs = await _wallet.GetTokenOutputsAsync(ct: cts.Token);
        TestContext.Out.WriteLine($"Token outputs: {outputs.Count}");
        foreach (var o in outputs.Take(5))
        {
            var tokenIdHex = Convert.ToHexString(o.TokenIdentifier).ToLowerInvariant();
            TestContext.Out.WriteLine(
                $"  amount={o.TokenAmount} token={tokenIdHex[..16]}... status={o.Status}");
        }
    }

    [Test]
    public async Task ShouldListTokenBalances()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var balances = await _wallet.GetTokenBalancesAsync(cts.Token);
        TestContext.Out.WriteLine($"Token balances: {balances.Count} tokens");
        foreach (var b in balances)
        {
            TestContext.Out.WriteLine(
                $"  {b.TokenMetadata.TokenName} ({b.TokenMetadata.TokenTicker})");
            TestContext.Out.WriteLine($"    identifier: {b.TokenMetadata.TokenIdentifier}");
            TestContext.Out.WriteLine($"    owned:      {b.OwnedBalance}");
            TestContext.Out.WriteLine($"    available:  {b.AvailableToSendBalance}");
            TestContext.Out.WriteLine($"    decimals:   {b.TokenMetadata.Decimals}");
        }
    }

    [Test]
    public async Task BalanceShouldIncludeTokenBalances()
    {
        // GetBalanceAsync also fetches the per-token aggregations and joins
        // them onto WalletBalance.TokenBalances. Verifies the read-side join.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var balance = await _wallet.GetBalanceAsync(cts.Token);
        TestContext.Out.WriteLine($"sats.available={balance.SatsBalance.Available}");
        TestContext.Out.WriteLine($"sats.owned    ={balance.SatsBalance.Owned}");
        TestContext.Out.WriteLine($"sats.incoming ={balance.SatsBalance.Incoming}");
        TestContext.Out.WriteLine($"tokens        ={balance.TokenBalances.Count}");
        Assert.That(balance.TokenBalances, Is.Not.Null);
    }
}

// -----------------------------------------------------------------------------
// Full token lifecycle: create -> mint -> transfer A->B -> transfer B->A -> burn.
// Ported from Swift TokenIntegrationTests.fullTokenLifecycle. Requires walletA
// to hold enough sats to fund the create + mint + transfer + burn operations.
// -----------------------------------------------------------------------------

[TestFixture]
[Category("Integration")]
[Category("Tokens")]
public class TokenLifecycleTests
{
    private static string MnemonicA => TestSecrets.MnemonicA;
    private static string MnemonicB => TestSecrets.MnemonicB;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private SparkConnection _client = null!;
    private SparkWallet _walletA = null!;
    private SparkWallet _walletB = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _client = new SparkConnection(
            Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet }),
            new HttpClient());
        _walletA = await _client.CreateWalletAsync(MnemonicA);
        _walletB = await _client.CreateWalletAsync(MnemonicB);
    }

    [OneTimeTearDown]
    public void Teardown() => _client?.Dispose();

    [Test]
    public async Task FullTokenLifecycle_create_mint_transferAB_transferBA_burn()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        // --- Phase 1: Create or reuse token issued by wallet A ---
        TestContext.Out.WriteLine("\n--- Phase 1: Create or reuse token ---");
        var issuer = Convert.FromHexString(_walletA.IdentityPublicKeyHex);
        var existing = await _walletA.QueryTokenMetadataAsync(
            issuerPublicKeys: [issuer], ct: cts.Token);

        string tokenIdentifier;
        if (existing.Count > 0)
        {
            tokenIdentifier = existing[0].TokenIdentifier;
            TestContext.Out.WriteLine(
                $"Reusing existing token: {existing[0].TokenName} ({existing[0].TokenTicker})");
            TestContext.Out.WriteLine($"Token identifier: {tokenIdentifier}");
        }
        else
        {
            var creation = await _walletA.CreateTokenAsync(
                tokenName: "NSpark",
                tokenTicker: "NSPK",
                decimals: 2,
                maxSupply: (UInt128)1_000_000,
                isFreezable: false,
                ct: cts.Token);
            Assert.That(creation.TransactionHash, Is.Not.Empty);
            TestContext.Out.WriteLine($"Token created, tx: {creation.TransactionHash}");

            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);

            var metas = await _walletA.QueryTokenMetadataAsync(
                issuerPublicKeys: [issuer], ct: cts.Token);
            if (metas.Count == 0)
            {
                Assert.Fail("Token metadata not found after creation");
                return;
            }
            tokenIdentifier = metas[0].TokenIdentifier;
            TestContext.Out.WriteLine($"Token identifier: {tokenIdentifier}");
        }
        Assert.That(tokenIdentifier, Does.StartWith("btkn1"));

        // --- Phase 2: Mint ---
        TestContext.Out.WriteLine("\n--- Phase 2: Mint 10,000 tokens ---");
        UInt128 mintAmount = 10_000;
        var mintTx = await _walletA.MintTokensAsync(tokenIdentifier, mintAmount, cts.Token);
        Assert.That(mintTx.TransactionHash, Is.Not.Empty);
        TestContext.Out.WriteLine($"Mint tx: {mintTx.TransactionHash}");
        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);

        var afterMint = await _walletA.GetTokenBalancesAsync(cts.Token);
        var afterMintForToken = afterMint.FirstOrDefault(b => b.TokenMetadata.TokenIdentifier == tokenIdentifier);
        Assert.That(afterMintForToken, Is.Not.Null, "Minted token not found in balances");
        TestContext.Out.WriteLine($"WalletA NSPK balance after mint: {afterMintForToken!.OwnedBalance}");
        Assert.That(afterMintForToken.OwnedBalance, Is.GreaterThanOrEqualTo(mintAmount));

        // --- Phase 3: Transfer A -> B (5,000 tokens) ---
        TestContext.Out.WriteLine("\n--- Phase 3: Transfer 5,000 NSPK A -> B ---");
        UInt128 transferAmount = 5_000;
        var sparkAddressB = _walletB.GetSparkAddress();

        // B may still hold NSPK from an earlier (interrupted) run: assert the round trip
        // relative to that starting balance rather than assuming B starts empty.
        var balancesBBefore = await _walletB.GetTokenBalancesAsync(cts.Token);
        var bBefore = balancesBBefore.FirstOrDefault(b => b.TokenMetadata.TokenIdentifier == tokenIdentifier)?.OwnedBalance ?? UInt128.Zero;
        TestContext.Out.WriteLine($"WalletB NSPK balance before: {bBefore}");
        var transferTx = await _walletA.TransferTokensAsync(
            tokenIdentifier, transferAmount, sparkAddressB, ct: cts.Token);
        Assert.That(transferTx.TransactionHash, Is.Not.Empty);
        TestContext.Out.WriteLine($"Transfer A->B tx: {transferTx.TransactionHash}");
        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);

        var balancesB = await _walletB.GetTokenBalancesAsync(cts.Token);
        var bForToken = balancesB.FirstOrDefault(b => b.TokenMetadata.TokenIdentifier == tokenIdentifier);
        TestContext.Out.WriteLine($"WalletB NSPK balance: {bForToken?.OwnedBalance ?? UInt128.Zero}");
        Assert.That(bForToken, Is.Not.Null);
        Assert.That(bForToken!.OwnedBalance, Is.EqualTo(bBefore + transferAmount));

        var balancesA2 = await _walletA.GetTokenBalancesAsync(cts.Token);
        var a2ForToken = balancesA2.FirstOrDefault(b => b.TokenMetadata.TokenIdentifier == tokenIdentifier);
        TestContext.Out.WriteLine($"WalletA NSPK balance after transfer: {a2ForToken?.OwnedBalance ?? UInt128.Zero}");

        // --- Phase 4: Transfer B -> A (return all) ---
        TestContext.Out.WriteLine("\n--- Phase 4: Transfer 5,000 NSPK B -> A (return) ---");
        var sparkAddressA = _walletA.GetSparkAddress();
        var returnTx = await _walletB.TransferTokensAsync(
            tokenIdentifier, transferAmount, sparkAddressA, ct: cts.Token);
        Assert.That(returnTx.TransactionHash, Is.Not.Empty);
        TestContext.Out.WriteLine($"Transfer B->A tx: {returnTx.TransactionHash}");
        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);

        var finalB = await _walletB.GetTokenBalancesAsync(cts.Token);
        var finalBForToken = finalB.FirstOrDefault(b => b.TokenMetadata.TokenIdentifier == tokenIdentifier);
        TestContext.Out.WriteLine(
            $"WalletB final NSPK balance: {finalBForToken?.OwnedBalance ?? UInt128.Zero}");
        Assert.That(
            (finalBForToken?.OwnedBalance ?? UInt128.Zero) == bBefore,
            $"Expected WalletB to be back at {bBefore} NSPK, got {finalBForToken?.OwnedBalance}");

        var finalA = await _walletA.GetTokenBalancesAsync(cts.Token);
        var finalAForToken = finalA.FirstOrDefault(b => b.TokenMetadata.TokenIdentifier == tokenIdentifier);
        TestContext.Out.WriteLine($"WalletA final NSPK balance: {finalAForToken?.OwnedBalance ?? UInt128.Zero}");
        Assert.That(finalAForToken, Is.Not.Null);
        Assert.That(finalAForToken!.OwnedBalance, Is.GreaterThanOrEqualTo(mintAmount));

        // --- Phase 5: Burn ---
        TestContext.Out.WriteLine("\n--- Phase 5: Burn 1,000 NSPK ---");
        UInt128 burnAmount = 1_000;
        var burnTx = await _walletA.BurnTokensAsync(tokenIdentifier, burnAmount, ct: cts.Token);
        Assert.That(burnTx.TransactionHash, Is.Not.Empty);
        TestContext.Out.WriteLine($"Burn tx: {burnTx.TransactionHash}");
        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);

        var afterBurn = await _walletA.GetTokenBalancesAsync(cts.Token);
        var afterBurnForToken = afterBurn.FirstOrDefault(b => b.TokenMetadata.TokenIdentifier == tokenIdentifier);
        TestContext.Out.WriteLine(
            $"WalletA NSPK balance after burn: {afterBurnForToken?.OwnedBalance ?? UInt128.Zero}");

        TestContext.Out.WriteLine("\nFull token lifecycle complete.");
    }

    [Test]
    public async Task TransferTokens_should_fail_cleanly_when_token_unknown()
    {
        // No-funds scenario: transferring a token with no held outputs should
        // surface a clean SparkConfigurationException (not a generic gRPC failure).
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        // Construct a random-looking token identifier the wallet definitely
        // doesn't hold any outputs of.
        var random = new byte[32];
        new Random(1234).NextBytes(random);
        var fakeTokenId = TokenIdentifier.Encode(random, SparkNetwork.Mainnet);

        Assert.ThrowsAsync<NSpark.Exceptions.SparkConfigurationException>(
            async () => await _walletA.TransferTokensAsync(
                fakeTokenId,
                amount: 1,
                receiverSparkAddress: _walletB.GetSparkAddress(),
                ct: cts.Token));
    }
}
