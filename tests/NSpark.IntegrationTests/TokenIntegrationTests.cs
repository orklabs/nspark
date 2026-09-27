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
        await ExpectOperatorsKnowAsync(mintTx.TransactionHash, _walletA, 3, cts.Token);
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
        await ExpectOperatorsKnowAsync(transferTx.TransactionHash, _walletA, 3, cts.Token);
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
        await ExpectOperatorsKnowAsync(returnTx.TransactionHash, _walletB, 3, cts.Token);
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
        await ExpectOperatorsKnowAsync(burnTx.TransactionHash, _walletA, 3, cts.Token);
        TestContext.Out.WriteLine($"Burn tx: {burnTx.TransactionHash}");
        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);

        var afterBurn = await _walletA.GetTokenBalancesAsync(cts.Token);
        var afterBurnForToken = afterBurn.FirstOrDefault(b => b.TokenMetadata.TokenIdentifier == tokenIdentifier);
        TestContext.Out.WriteLine(
            $"WalletA NSPK balance after burn: {afterBurnForToken?.OwnedBalance ?? UInt128.Zero}");

        TestContext.Out.WriteLine("\nFull token lifecycle complete.");
    }

    /// <summary>
    /// The operators hold a finalized transaction of <paramref name="version"/> under
    /// <paramref name="hash"/>, the hash the SDK reported for it.
    /// </summary>
    private static async Task ExpectOperatorsKnowAsync(string hash, SparkWallet wallet, uint version, CancellationToken ct)
    {
        var request = new NSpark.Proto.Token.QueryTokenTransactionsRequest
        {
            ByTxHash = new NSpark.Proto.Token.QueryTokenTransactionsByTxHash(),
        };
        request.ByTxHash.TokenTransactionHashes.Add(Google.Protobuf.ByteString.CopyFrom(Convert.FromHexString(hash)));
        var response = await wallet.GetTokenClient(wallet.CoordinatorAddress).query_token_transactionsAsync(
            request, await wallet.GetCoordinatorAuthMetadataAsync(ct), cancellationToken: ct);
        var found = response.TokenTransactionsWithStatus;
        Assert.That(found.Select(t => Convert.ToHexString(t.TokenTransactionHash.Span).ToLowerInvariant()), Is.EqualTo(new[] { hash }),
            $"the operators hold no transaction {hash}");
        Assert.That(found[0].Status, Is.EqualTo(NSpark.Proto.Token.TokenTransactionStatus.TokenTransactionFinalized));
        Assert.That(found[0].TokenTransaction.Version, Is.EqualTo(version));
    }

    private async Task<string> IssuedTokenAsync(CancellationToken ct)
    {
        var issued = await _walletA.QueryTokenMetadataAsync(issuerPublicKeys: [_walletA.IdentityPublicKey], ct: ct);
        if (issued.Count == 0)
        {
            Assert.Inconclusive("Wallet A has issued no token; the lifecycle test creates one.");
        }

        return issued[0].TokenIdentifier;
    }

    private static async Task<UInt128> TokenBalanceAsync(SparkWallet wallet, string token, CancellationToken ct) =>
        (await wallet.GetTokenBalancesAsync(ct)).FirstOrDefault(b => b.TokenMetadata.TokenIdentifier == token)?.OwnedBalance ?? UInt128.Zero;

    [Test]
    public async Task Two_concurrent_sends_from_one_wallet_both_land_on_different_outputs()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);
        var ct = cts.Token;
        var token = await IssuedTokenAsync(ct);
        var available = (await _walletA.GetTokenOutputsAsync(token, ct)).Where(o => o.Status == "AVAILABLE").ToList();
        if (available.Count < 2)
        {
            for (var i = available.Count; i < 2; i++)
            {
                await _walletA.MintTokensAsync(token, 10, ct);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            available = (await _walletA.GetTokenOutputsAsync(token, ct)).Where(o => o.Status == "AVAILABLE").ToList();
        }

        // The smallest output's amount: each send then spends a single output, and without locks
        // both would pick the same one and the operators would refuse one as pre-empted.
        var amount = available.Min(o => o.TokenAmount);
        var before = await TokenBalanceAsync(_walletB, token, ct);

        var addressB = _walletB.GetSparkAddress();
        var hashes = await Task.WhenAll(
            _walletA.TransferTokensAsync(token, amount, addressB, ct: ct),
            _walletA.TransferTokensAsync(token, amount, addressB, ct: ct));
        TestContext.Out.WriteLine($"Concurrent sends of {amount}: {string.Join(", ", hashes.Select(h => h.TransactionHash))}");
        Assert.That(hashes.Select(h => h.TransactionHash).Distinct().Count(), Is.EqualTo(2));
        foreach (var hash in hashes)
        {
            await ExpectOperatorsKnowAsync(hash.TransactionHash, _walletA, 3, ct);
        }

        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        Assert.That(await TokenBalanceAsync(_walletB, token, ct), Is.EqualTo(before + (2 * amount)));

        // Back to A.
        await _walletB.TransferTokensAsync(token, 2 * amount, _walletA.GetSparkAddress(), ct: ct);
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        Assert.That(await TokenBalanceAsync(_walletB, token, ct), Is.EqualTo(before));
    }

    [Test]
    public async Task A_send_retried_with_its_idempotency_key_is_made_once_and_returns_the_same_hash()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);
        var ct = cts.Token;
        var token = await IssuedTokenAsync(ct);
        var before = await TokenBalanceAsync(_walletB, token, ct);

        var key = Guid.NewGuid().ToString();
        var addressB = _walletB.GetSparkAddress();
        var first = await _walletA.TransferTokensAsync(token, 7, addressB, idempotencyKey: key, ct: ct);
        var retry = await _walletA.TransferTokensAsync(token, 7, addressB, idempotencyKey: key, ct: ct);
        TestContext.Out.WriteLine($"Keyed send: {first.TransactionHash}, retried: {retry.TransactionHash}");
        Assert.That(retry.TransactionHash, Is.EqualTo(first.TransactionHash));
        await ExpectOperatorsKnowAsync(first.TransactionHash, _walletA, 3, ct);

        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        Assert.That(await TokenBalanceAsync(_walletB, token, ct), Is.EqualTo(before + 7));

        // Back to A.
        await _walletB.TransferTokensAsync(token, 7, _walletA.GetSparkAddress(), ct: ct);
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        Assert.That(await TokenBalanceAsync(_walletB, token, ct), Is.EqualTo(before));
    }

    [Test]
    public async Task V2_token_transactions_still_work_when_configured()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);
        var ct = cts.Token;
        var token = await IssuedTokenAsync(ct);
        using var v2Client = new SparkConnection(
            Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet, TokenTransactionVersion = TokenTransactionVersion.V2 }),
            new HttpClient());
        var walletAv2 = await v2Client.CreateWalletAsync(MnemonicA, ct: ct);
        var before = await TokenBalanceAsync(_walletB, token, ct);

        var hash = await walletAv2.TransferTokensAsync(token, 3, _walletB.GetSparkAddress(), ct: ct);
        TestContext.Out.WriteLine($"V2 send: {hash.TransactionHash}");
        await ExpectOperatorsKnowAsync(hash.TransactionHash, walletAv2, 2, ct);
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        Assert.That(await TokenBalanceAsync(_walletB, token, ct), Is.EqualTo(before + 3));

        // Back to A, as V3.
        await _walletB.TransferTokensAsync(token, 3, _walletA.GetSparkAddress(), ct: ct);
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        Assert.That(await TokenBalanceAsync(_walletB, token, ct), Is.EqualTo(before));
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
