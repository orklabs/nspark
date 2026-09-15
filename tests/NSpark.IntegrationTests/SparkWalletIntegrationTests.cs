using Microsoft.Extensions.Options;
using NSpark;
using NSpark.Models;
using NSpark.GraphQL;
using NSpark.Services;

namespace NSpark.Tests;

// =============================================================================
// Wallet Tests (matching JS: wallet.test.ts / Swift: WalletTests)
// =============================================================================

[TestFixture]
[Category("Integration")]
public class WalletTests
{
    private static string MnemonicA => TestSecrets.MnemonicA;
    private static string MnemonicB => TestSecrets.MnemonicB;

    private SparkConnection _client = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _client = new SparkConnection(
            Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet }),
            new HttpClient());
    }

    [OneTimeTearDown]
    public void Teardown() => _client?.Dispose();

    [Test]
    public async Task ShouldInitializeWalletFromMnemonic()
    {
        var wallet = await _client.CreateWalletAsync(MnemonicA);
        Assert.That(wallet.IdentityPublicKeyHex, Is.Not.Empty);
    }

    [Test]
    public async Task DifferentAccountsProduceDifferentKeys()
    {
        var account0 = await _client.CreateWalletAsync(MnemonicA, account: 0);
        var account1 = await _client.CreateWalletAsync(MnemonicA, account: 1);
        Assert.That(account0.IdentityPublicKeyHex, Is.Not.EqualTo(account1.IdentityPublicKeyHex));
    }

    [Test]
    public async Task SameMnemonicProducesSameKey()
    {
        var w1 = await _client.CreateWalletAsync(MnemonicA, account: 0);
        var w2 = await _client.CreateWalletAsync(MnemonicA, account: 0);
        Assert.That(w1.IdentityPublicKeyHex, Is.EqualTo(w2.IdentityPublicKeyHex));
    }

    [Test]
    public async Task DifferentMnemonicsProduceDifferentKeys()
    {
        var wA = await _client.CreateWalletAsync(MnemonicA);
        var wB = await _client.CreateWalletAsync(MnemonicB);
        Assert.That(wA.IdentityPublicKeyHex, Is.Not.EqualTo(wB.IdentityPublicKeyHex));
    }
}

// =============================================================================
// Spark Address Tests (matching JS: address.test.ts / Swift: SparkAddressTests)
// =============================================================================

[TestFixture]
[Category("Integration")]
public class SparkAddressTests
{
    private static string MnemonicA => TestSecrets.MnemonicA;
    private static string MnemonicB => TestSecrets.MnemonicB;

    private SparkConnection _client = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _client = new SparkConnection(
            Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet }),
            new HttpClient());
    }

    [OneTimeTearDown]
    public void Teardown() => _client?.Dispose();

    [Test]
    public async Task ShouldGenerateSparkAddressWithCorrectPrefix()
    {
        var wallet = await _client.CreateWalletAsync(MnemonicA);
        var address = wallet.GetSparkAddress();

        TestContext.Out.WriteLine($"Spark address: {address}");
        Assert.That(address, Does.StartWith("spark1"));
        Assert.That(address.Length, Is.GreaterThan(10));
    }

    [Test]
    public async Task SameWalletProducesSameSparkAddress()
    {
        var w1 = await _client.CreateWalletAsync(MnemonicA);
        var w2 = await _client.CreateWalletAsync(MnemonicA);
        Assert.That(w1.GetSparkAddress(), Is.EqualTo(w2.GetSparkAddress()));
    }

    [Test]
    public async Task DifferentWalletsProduceDifferentSparkAddresses()
    {
        var wA = await _client.CreateWalletAsync(MnemonicA);
        var wB = await _client.CreateWalletAsync(MnemonicB);
        Assert.That(wA.GetSparkAddress(), Is.Not.EqualTo(wB.GetSparkAddress()));
    }
}

// =============================================================================
// Balance Tests (matching Swift: BalanceTests)
// =============================================================================

[TestFixture]
[Category("Integration")]
public class BalanceTests
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
    public async Task ShouldQueryBalance()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var balance = await _wallet.GetBalanceAsync(cts.Token);
        TestContext.Out.WriteLine($"Balance: {balance.SatsBalance.Available} sats, {balance.Leaves.Count} leaves");
        Assert.That(balance.SatsBalance.Available, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public async Task ShouldQueryLeaves()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var leaves = await _wallet.GetLeavesAsync(cts.Token);
        foreach (var leaf in leaves)
        {
            TestContext.Out.WriteLine($"  Leaf {leaf.Id}: {leaf.ValueSats} sats [{leaf.Status}]");
            Assert.That(leaf.ValueSats, Is.GreaterThan(0));
        }
    }
}

// =============================================================================
// Deposit Tests (matching JS: deposit.test.ts / Swift: DepositTests)
// =============================================================================

[TestFixture]
[Category("Integration")]
public class DepositTests
{
    private static string MnemonicA => TestSecrets.MnemonicA;
    private static string MnemonicB => TestSecrets.MnemonicB;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

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
    public async Task ShouldGenerateSingleUseDepositAddress()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var deposit = await _walletA.GetDepositAddressAsync(cts.Token);

        TestContext.Out.WriteLine($"Deposit address: {deposit.Address}");
        Assert.That(deposit.Address, Is.Not.Null.And.Not.Empty);
        Assert.That(deposit.LeafId, Is.Not.Null.And.Not.Empty);
        Assert.That(deposit.Address, Does.StartWith("bc1p").Or.StartWith("bcrt1p"));
    }

    [Test]
    public async Task ShouldGenerateStaticDepositAddress()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var deposit = await _walletA.GetStaticDepositAddressAsync(cts.Token);

        TestContext.Out.WriteLine($"Static deposit address: {deposit.Address}");
        Assert.That(deposit.Address, Is.Not.Null.And.Not.Empty);
        Assert.That(deposit.Address, Does.StartWith("bc1p").Or.StartWith("bcrt1p"));
    }

    [Test]
    public async Task StaticDepositAddressIsDeterministic()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var addr1 = await _walletA.GetStaticDepositAddressAsync(cts.Token);
        var addr2 = await _walletA.GetStaticDepositAddressAsync(cts.Token);
        Assert.That(addr1.Address, Is.EqualTo(addr2.Address));
    }

    [Test]
    public async Task ShouldQueryUnusedDepositAddresses()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var addresses = await _walletA.QueryUnusedDepositAddressesAsync(ct: cts.Token);
        TestContext.Out.WriteLine($"Unused deposit addresses: {addresses.Count}");
        foreach (var addr in addresses)
        {
            Assert.That(addr.Address, Is.Not.Null.And.Not.Empty);
            TestContext.Out.WriteLine($"  {addr.Address} leafId={addr.LeafId}");
        }
    }

    [Test]
    public async Task ShouldGenerateMultipleAndQueryThem()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var countBefore = (await _walletA.QueryUnusedDepositAddressesAsync(ct: cts.Token)).Count;

        await _walletA.GetDepositAddressAsync(cts.Token);
        await _walletA.GetDepositAddressAsync(cts.Token);

        var countAfter = (await _walletA.QueryUnusedDepositAddressesAsync(ct: cts.Token)).Count;
        Assert.That(countAfter, Is.GreaterThanOrEqualTo(countBefore + 2));
    }

    [Test]
    public async Task StaticDepositNotInUnusedAddresses()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        // Use walletB for cleaner state
        var staticAddr = await _walletB.GetStaticDepositAddressAsync(cts.Token);
        var unused = await _walletB.QueryUnusedDepositAddressesAsync(ct: cts.Token);
        var found = unused.Any(a => a.Address == staticAddr.Address);
        Assert.That(found, Is.False, "Static deposit address should not appear in unused deposit addresses");
    }
}

// =============================================================================
// Lightning Tests (matching JS: lightning.test.ts / Swift: LightningTests)
// =============================================================================

[TestFixture]
[Category("Integration")]
public class LightningTests
{
    private static string MnemonicA => TestSecrets.MnemonicA;
    private static string MnemonicB => TestSecrets.MnemonicB;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

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
    public async Task ShouldCreateLightningInvoice()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var invoice = await _walletA.CreateLightningInvoiceAsync(100, memo: "test invoice", ct: cts.Token);

        TestContext.Out.WriteLine($"Invoice: {invoice.PaymentRequest[..50]}...");
        Assert.That(invoice.PaymentRequest, Is.Not.Empty);
        Assert.That(invoice.PaymentHash, Is.Not.Empty);
        Assert.That(invoice.AmountSats, Is.EqualTo(100));
        Assert.That(invoice.ExpiresAt, Is.GreaterThan(DateTimeOffset.UtcNow));
        Assert.That(invoice.PaymentRequest.ToLowerInvariant(), Does.StartWith("lnbc"));
    }

    [Test]
    public async Task ShouldCreateInvoiceWithoutMemo()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var invoice = await _walletA.CreateLightningInvoiceAsync(50, ct: cts.Token);
        Assert.That(invoice.PaymentRequest, Is.Not.Empty);
    }

    [Test]
    public async Task ShouldGetLightningSendFeeEstimate()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var invoice = await _walletB.CreateLightningInvoiceAsync(100, ct: cts.Token);
        var fee = await _walletA.GetLightningSendFeeEstimateAsync(invoice.PaymentRequest, ct: cts.Token);

        TestContext.Out.WriteLine($"Fee estimate: {fee} sats");
        Assert.That(fee, Is.GreaterThanOrEqualTo(0));
    }

    [Test, Explicit("Requires funded Wallet A (>= 100 sats)")]
    public async Task ShouldPayLightningInvoiceBetweenWallets()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var balanceBefore = await _walletA.GetBalanceAsync(cts.Token);
        if (balanceBefore.SatsBalance.Available < 100)
        {
            Assert.Inconclusive($"WalletA needs >= 100 sats (has {balanceBefore.SatsBalance.Available})");
            return;
        }

        var invoice = await _walletB.CreateLightningInvoiceAsync(10, memo: "integration test", ct: cts.Token);
        var paymentId = await _walletA.PayLightningInvoiceAsync(invoice.PaymentRequest, maxFeeSats: 50, ct: cts.Token);

        Assert.That(paymentId, Is.Not.Empty);
        TestContext.Out.WriteLine($"Payment ID: {paymentId}");

        var balanceAfter = await _walletA.GetBalanceAsync(cts.Token);
        Assert.That(balanceAfter.SatsBalance.Available, Is.LessThan(balanceBefore.SatsBalance.Available));
        TestContext.Out.WriteLine($"WalletA: {balanceBefore.SatsBalance.Available} -> {balanceAfter.SatsBalance.Available} sats");
    }

    [Test, Explicit("Requires funded Wallet A (>= 50 sats)")]
    public async Task ShouldPayExternalLightningAddress()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var balance = await _walletA.GetBalanceAsync(cts.Token);
        if (balance.SatsBalance.Available < 50)
        {
            Assert.Inconclusive("WalletA needs >= 50 sats");
            return;
        }

        var paymentId = await _walletA.PayLightningAddressAsync("bub@bub.gg", 10, maxFeeSats: 50, ct: cts.Token);
        Assert.That(paymentId, Is.Not.Empty);
        TestContext.Out.WriteLine($"External payment ID: {paymentId}");
    }

    [Test, Explicit("Requires funded Wallet A (>= 100 sats)")]
    public async Task GetLightningSendStatus_should_track_a_send_to_terminal()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var balanceBefore = await _walletA.GetBalanceAsync(cts.Token);
        if (balanceBefore.SatsBalance.Available < 100)
        {
            Assert.Inconclusive($"WalletA needs >= 100 sats (has {balanceBefore.SatsBalance.Available})");
            return;
        }

        // Sanity: an obviously-bogus request id MUST return null, not throw.
        var bogus = await _walletA.GetLightningSendStatusAsync(Guid.NewGuid().ToString(), cts.Token);
        Assert.That(bogus, Is.Null, "GetLightningSendStatusAsync should return null for unknown request ids");

        // Querying with a *receive* request id MUST also return null — the method is send-only.
        var receiveInvoice = await _walletB.CreateLightningInvoiceAsync(10, memo: "send-status receive-id guard", ct: cts.Token);
        Assert.That(receiveInvoice.RequestId, Is.Not.Null.And.Not.Empty);
        var wrongType = await _walletA.GetLightningSendStatusAsync(receiveInvoice.RequestId!, cts.Token);
        Assert.That(wrongType, Is.Null,
            "GetLightningSendStatusAsync called with a LightningReceiveRequest id must return null");

        // Real send: pay a fresh invoice from A → B and poll until status reaches one of the
        // expected terminal-or-near-terminal values. We don't strictly assert SUCCEEDED because
        // mainnet routing latency varies; reaching any non-pending state proves the query works.
        var invoice = await _walletB.CreateLightningInvoiceAsync(10, memo: "send-status integration", ct: cts.Token);
        var requestId = await _walletA.PayLightningInvoiceAsync(invoice.PaymentRequest, maxFeeSats: 50, ct: cts.Token);
        Assert.That(requestId, Is.Not.Empty);
        TestContext.Out.WriteLine($"Send request id: {requestId}");

        var pendingStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CREATED", "REQUEST_VALIDATED", "LIGHTNING_PAYMENT_INITIATED", "PENDING_USER_SWAP_RETURN",
        };
        var terminalStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LIGHTNING_PAYMENT_SUCCEEDED", "TRANSFER_COMPLETED", "PREIMAGE_PROVIDED",
            "LIGHTNING_PAYMENT_FAILED", "TRANSFER_FAILED", "PREIMAGE_PROVIDING_FAILED",
            "USER_TRANSFER_VALIDATION_FAILED", "USER_SWAP_RETURNED", "USER_SWAP_RETURN_FAILED",
        };

        NSpark.Models.LightningSendStatus? last = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (DateTime.UtcNow < deadline)
        {
            last = await _walletA.GetLightningSendStatusAsync(requestId, cts.Token);
            Assert.That(last, Is.Not.Null, "GetLightningSendStatusAsync should return a status for a real send");
            TestContext.Out.WriteLine($"  status={last!.Status} fee={last.FeeSats?.ToString() ?? "(null)"} preimage={(last.Preimage is null ? "(null)" : "set")}");
            if (terminalStatuses.Contains(last.Status)) break;
            Assert.That(pendingStatuses.Contains(last.Status), Is.True,
                $"Unexpected (non-terminal, non-pending) status '{last.Status}' — Spark may have added a new enum value");
            await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
        }

        Assert.That(last, Is.Not.Null);
        Assert.That(terminalStatuses.Contains(last!.Status), Is.True,
            $"Status never reached a terminal value within the test window (last='{last.Status}')");
    }
}

// =============================================================================
// Third-Party Invoice Tests (Lightning Address / Delegated Invoice Flow)
// =============================================================================

[TestFixture]
[Category("Integration")]
public class DelegatedInvoiceTests
{
    private static string MnemonicA => TestSecrets.MnemonicA;
    private static string MnemonicB => TestSecrets.MnemonicB;
    private static string MnemonicC => TestSecrets.MnemonicC;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private SparkConnection _client = null!;
    private SparkWallet _walletA = null!;
    private SparkWallet _walletB = null!;
    private SparkWallet _walletC = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _client = new SparkConnection(
            Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet }),
            new HttpClient());
        _walletA = await _client.CreateWalletAsync(MnemonicA);
        _walletB = await _client.CreateWalletAsync(MnemonicB);
        _walletC = await _client.CreateWalletAsync(MnemonicC);
    }

    [OneTimeTearDown]
    public void TearDown() => _client?.Dispose();

    [Test, Explicit("Costs sats: B pays invoice created by A for C")]
    public async Task ShouldPayDelegatedInvoice_BPaysInvoiceCreatedByAForC()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        // Ensure B has funds
        await _walletB.ClaimPendingTransfersAsync(cts.Token);
        var balB = await _walletB.GetBalanceAsync(cts.Token);
        TestContext.Out.WriteLine($"WalletB balance: {balB.SatsBalance.Available} sats");
        if (balB.SatsBalance.Available < 50) { Assert.Inconclusive("WalletB needs >= 50 sats"); return; }

        // Get C's balance before
        await _walletC.ClaimPendingTransfersAsync(cts.Token);
        var balCBefore = await _walletC.GetBalanceAsync(cts.Token);
        TestContext.Out.WriteLine($"WalletC balance before: {balCBefore.SatsBalance.Available} sats");

        // Step 1: A creates invoice on behalf of C (server issues invoice for user C)
        var cIdentityPubKey = _walletC.IdentityPublicKey;
        var invoice = await _walletA.CreateLightningInvoiceAsync(
            amountSats: 10,
            memo: "delegated invoice for C",
            receiverIdentityPublicKey: cIdentityPubKey,
            ct: cts.Token);

        TestContext.Out.WriteLine($"Invoice created by A for C: {invoice.PaymentRequest[..40]}...");
        TestContext.Out.WriteLine($"Request ID: {invoice.RequestId}");
        Assert.That(invoice.RequestId, Is.Not.Null.And.Not.Empty, "Request ID should be returned");

        // Step 2: B pays the invoice
        var paymentId = await _walletB.PayLightningInvoiceAsync(invoice.PaymentRequest, maxFeeSats: 50, ct: cts.Token);
        TestContext.Out.WriteLine($"B paid invoice, payment ID: {paymentId}");

        // Step 3: A monitors the request status (server polls SSP)
        string? status = null;
        for (int i = 0; i < 30; i++)
        {
            status = await _walletA.GetLightningReceiveRequestStatusAsync(invoice.RequestId!, cts.Token);
            TestContext.Out.WriteLine($"  Poll {i + 1}: status = {status}");
            if (status is "TRANSFER_COMPLETED" or "COMPLETED") break;
            await Task.Delay(2000, cts.Token);
        }
        Assert.That(status, Is.EqualTo("TRANSFER_COMPLETED").Or.EqualTo("COMPLETED"),
            "Lightning receive request should complete after payment");

        // Step 4: C claims the pending transfer
        var claimed = await _walletC.ClaimPendingTransfersAsync(cts.Token);
        TestContext.Out.WriteLine($"C claimed {claimed.Count} transfer(s)");

        var balCAfter = await _walletC.GetBalanceAsync(cts.Token);
        TestContext.Out.WriteLine($"WalletC balance after: {balCAfter.SatsBalance.Available} sats");
        Assert.That(balCAfter.SatsBalance.Available, Is.GreaterThan(balCBefore.SatsBalance.Available),
            "C's balance should increase after claiming");
    }

    [Test, Explicit("Costs sats: same as above but with privacy enabled on C")]
    public async Task ShouldPayDelegatedInvoice_WithPrivacyEnabledOnC()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        // Enable privacy on C and verify via separate query
        await _walletC.SetPrivacyEnabledAsync(true, cts.Token);
        var settings = await _walletC.GetWalletSettingsAsync(cts.Token);
        TestContext.Out.WriteLine($"Privacy enabled on C: {settings.PrivateEnabled}");
        Assert.That(settings.PrivateEnabled, Is.True);

        try
        {
            // Ensure B has funds
            await _walletB.ClaimPendingTransfersAsync(cts.Token);
            var balB = await _walletB.GetBalanceAsync(cts.Token);
            TestContext.Out.WriteLine($"WalletB balance: {balB.SatsBalance.Available} sats");
            if (balB.SatsBalance.Available < 50) { Assert.Inconclusive("WalletB needs >= 50 sats"); return; }

            // Get C's balance before
            await _walletC.ClaimPendingTransfersAsync(cts.Token);
            var balCBefore = await _walletC.GetBalanceAsync(cts.Token);
            TestContext.Out.WriteLine($"WalletC balance before: {balCBefore.SatsBalance.Available} sats");

            // A creates invoice on behalf of C
            var cIdentityPubKey = _walletC.IdentityPublicKey;
            var invoice = await _walletA.CreateLightningInvoiceAsync(
                amountSats: 10,
                memo: "delegated invoice for C (private)",
                receiverIdentityPublicKey: cIdentityPubKey,
                ct: cts.Token);

            TestContext.Out.WriteLine($"Invoice created by A for C: {invoice.PaymentRequest[..40]}...");
            TestContext.Out.WriteLine($"Request ID: {invoice.RequestId}");

            // B pays the invoice
            var paymentId = await _walletB.PayLightningInvoiceAsync(invoice.PaymentRequest, maxFeeSats: 50, ct: cts.Token);
            TestContext.Out.WriteLine($"B paid invoice, payment ID: {paymentId}");

            // A monitors the request status
            string? status = null;
            for (int i = 0; i < 30; i++)
            {
                status = await _walletA.GetLightningReceiveRequestStatusAsync(invoice.RequestId!, cts.Token);
                TestContext.Out.WriteLine($"  Poll {i + 1}: status = {status}");
                if (status is "TRANSFER_COMPLETED" or "COMPLETED") break;
                await Task.Delay(2000, cts.Token);
            }
            Assert.That(status, Is.EqualTo("TRANSFER_COMPLETED").Or.EqualTo("COMPLETED"),
                "Lightning receive request should complete after payment");

            // C claims the pending transfer
            var claimed = await _walletC.ClaimPendingTransfersAsync(cts.Token);
            TestContext.Out.WriteLine($"C claimed {claimed.Count} transfer(s)");

            var balCAfter = await _walletC.GetBalanceAsync(cts.Token);
            TestContext.Out.WriteLine($"WalletC balance after: {balCAfter.SatsBalance.Available} sats");
            Assert.That(balCAfter.SatsBalance.Available, Is.GreaterThan(balCBefore.SatsBalance.Available),
                "C's balance should increase after claiming");
        }
        finally
        {
            // Restore privacy setting
            await _walletC.SetPrivacyEnabledAsync(false, CancellationToken.None);
            TestContext.Out.WriteLine("Privacy disabled on C (restored)");
        }
    }
}

// =============================================================================
// Transfer Tests (matching JS: transfer.test.ts / Swift: TransferTests)
// =============================================================================

[TestFixture]
[Category("Integration")]
public class TransferTests
{
    private static string MnemonicA => TestSecrets.MnemonicA;
    private static string MnemonicB => TestSecrets.MnemonicB;
    private static string MnemonicC => TestSecrets.MnemonicC;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

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
    public async Task ShouldClaimPendingTransfers()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var claimed = await _walletA.ClaimPendingTransfersAsync(cts.Token);
        TestContext.Out.WriteLine($"Claimed {claimed.Count} pending transfers");
    }

    [Test]
    public async Task ShouldGetTransfers()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var page = await _walletA.GetTransfersAsync(limit: 10, ct: cts.Token);

        TestContext.Out.WriteLine($"Transfers: {page.Transfers.Count}, Offset: {page.Offset}");
        foreach (var t in page.Transfers.Take(5))
            TestContext.Out.WriteLine($"  {t.Id}: {t.TotalValueSats} sats — {t.Status} — {t.Type}");

        Assert.That(page.Transfers, Is.Not.Null);
    }

    [Test]
    public async Task ShouldGetSingleTransfer()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        // First get a transfer ID from the list
        var page = await _walletA.GetTransfersAsync(limit: 1, ct: cts.Token);
        if (page.Transfers.Count == 0)
        {
            TestContext.Out.WriteLine("No transfers found — skipping single transfer test");
            return;
        }

        var transferId = page.Transfers[0].Id;
        var transfer = await _walletA.GetTransferAsync(transferId, cts.Token);

        Assert.That(transfer, Is.Not.Null);
        Assert.That(transfer!.Id, Is.EqualTo(transferId));
        TestContext.Out.WriteLine($"Transfer {transfer.Id}: {transfer.TotalValueSats} sats — {transfer.Status}");
    }

    [Test, Explicit("Requires funded Wallet A (>= 100 sats)")]
    public async Task ShouldTransferBetweenWallets()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var balanceA = await _walletA.GetBalanceAsync(cts.Token);
        if (balanceA.SatsBalance.Available < 100)
        {
            Assert.Inconclusive($"WalletA needs >= 100 sats (has {balanceA.SatsBalance.Available})");
            return;
        }

        var receiverPubKey = Convert.FromHexString(_walletB.IdentityPublicKeyHex);
        var transfer = await _walletA.SendAsync(receiverPubKey, 10, null, cts.Token);

        Assert.That(transfer.Id, Is.Not.Null.And.Not.Empty);
        TestContext.Out.WriteLine($"Transfer sent: {transfer.Id} status={transfer.Status}");

        // Wait for propagation then claim on B
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
        var claimed = await _walletB.ClaimPendingTransfersAsync(cts.Token);
        Assert.That(claimed.Count, Is.GreaterThanOrEqualTo(1));

        var balanceB = await _walletB.GetBalanceAsync(cts.Token);
        Assert.That(balanceB.SatsBalance.Available, Is.GreaterThanOrEqualTo(10));
        TestContext.Out.WriteLine($"WalletB balance: {balanceB.SatsBalance.Available} sats");
    }

    [Test, Explicit("Requires funded Wallet A (>= 50 sats)")]
    public async Task ShouldRoundTripTransfer()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var balanceA = await _walletA.GetBalanceAsync(cts.Token);
        if (balanceA.SatsBalance.Available < 50)
        {
            Assert.Inconclusive("WalletA needs >= 50 sats");
            return;
        }

        // Send A -> B
        var pubB = Convert.FromHexString(_walletB.IdentityPublicKeyHex);
        await _walletA.SendAsync(pubB, 20, null, cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
        await _walletB.ClaimPendingTransfersAsync(cts.Token);

        // Send all B -> A
        var balB = await _walletB.GetBalanceAsync(cts.Token);
        Assert.That(balB.SatsBalance.Available, Is.GreaterThan(0));

        // Drain pattern: renew any floor-timelock leaves first so the full
        // balance can move (a leaf at timelock <= 100 cannot be spent and
        // would fail the send with SparkLeafTimelockExhaustedException).
        var renewal = await _walletB.RenewExhaustedLeavesAsync(cts.Token);
        TestContext.Out.WriteLine(
            $"WalletB renewal: checked {renewal.Checked}, renewed {renewal.Renewed}, " +
            $"failures: [{string.Join("; ", renewal.Failures)}]");

        var pubA = Convert.FromHexString(_walletA.IdentityPublicKeyHex);
        var transfer = await _walletB.SendAsync(pubA, balB.SatsBalance.Available, null, cts.Token);
        TestContext.Out.WriteLine($"Return transfer: {transfer.Id}");

        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
        await _walletA.ClaimPendingTransfersAsync(cts.Token);

        // B should be empty
        var finalB = await _walletB.GetBalanceAsync(cts.Token);
        Assert.That(finalB.SatsBalance.Available, Is.EqualTo(0));
        TestContext.Out.WriteLine($"WalletB final balance: {finalB.SatsBalance.Available} sats");
    }

    [Test, Explicit("A sends to C, C claims")]
    public async Task ShouldTransferToThirdWallet()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var walletC = await _client.CreateWalletAsync(MnemonicC);

        var balanceA = await _walletA.GetBalanceAsync(cts.Token);
        if (balanceA.SatsBalance.Available < 100)
        {
            Assert.Inconclusive("WalletA needs >= 100 sats");
            return;
        }

        var cPubKey = Convert.FromHexString(walletC.IdentityPublicKeyHex);
        var transfer = await _walletA.SendAsync(cPubKey, 100, null, cts.Token);
        TestContext.Out.WriteLine($"Transfer: {transfer.Id} — {transfer.TotalValueSats} sats");

        for (int i = 0; i < 24; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
            var claimed = await walletC.ClaimPendingTransfersAsync(cts.Token);
            if (claimed.Count > 0)
            {
                TestContext.Out.WriteLine($"C claimed: {claimed[0].TotalValueSats} sats");
                break;
            }
        }

        var balanceC = await walletC.GetBalanceAsync(cts.Token);
        Assert.That(balanceC.SatsBalance.Available, Is.GreaterThan(0));
    }
}

// =============================================================================
// Swap / Leaf Splitting Tests (matching Swift: SwapService)
// =============================================================================

[TestFixture]
[Category("Integration")]
public class SwapTests
{
    private static string MnemonicA => TestSecrets.MnemonicA;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

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

    /// <summary>
    /// A node whose refund transaction carries a fresh timelock, so the leaf counts as
    /// spendable. Selection ignores leaves at the timelock floor (and leaves without a
    /// parseable refund transaction), so a bare <c>TreeNode</c> would never be picked.
    /// </summary>
    private static NSpark.Proto.TreeNode SpendableNode()
    {
        var tx = new byte[4 + 1 + 36 + 1 + 4];
        tx[0] = 0x02; // version
        tx[4] = 0x01; // one input; prevout, empty script
        BitConverter.GetBytes(2000u).CopyTo(tx, 42); // nSequence = initial timelock
        return new NSpark.Proto.TreeNode
        {
            NodeTx = Google.Protobuf.ByteString.CopyFrom(tx),
            RefundTx = Google.Protobuf.ByteString.CopyFrom(tx),
        };
    }

    [Test]
    public void TryExactSelection_SingleLeafMatch()
    {
        var node = SpendableNode();
        var leaves = new List<SparkLeaf>
        {
            new("a", "t1", 100, "AVAILABLE") { Node = node },
            new("b", "t1", 200, "AVAILABLE") { Node = node },
            new("c", "t1", 300, "AVAILABLE") { Node = node },
        };
        var result = SwapService.TryExactSelection(leaves, 200);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Count, Is.EqualTo(1));
        Assert.That(result[0].ValueSats, Is.EqualTo(200));
    }

    [Test]
    public void TryExactSelection_MultiLeafMatch()
    {
        var node = SpendableNode();
        var leaves = new List<SparkLeaf>
        {
            new("a", "t1", 100, "AVAILABLE") { Node = node },
            new("b", "t1", 200, "AVAILABLE") { Node = node },
            new("c", "t1", 300, "AVAILABLE") { Node = node },
        };
        var result = SwapService.TryExactSelection(leaves, 300);
        Assert.That(result, Is.Not.Null);
        // Could be either single 300 leaf or 100+200
        Assert.That(result!.Sum(l => l.ValueSats), Is.EqualTo(300));
    }

    [Test]
    public void TryExactSelection_NoMatch()
    {
        var node = SpendableNode();
        var leaves = new List<SparkLeaf>
        {
            new("a", "t1", 100, "AVAILABLE") { Node = node },
            new("b", "t1", 300, "AVAILABLE") { Node = node },
        };
        var result = SwapService.TryExactSelection(leaves, 250);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void TryExactSelection_SkipsNonAvailable()
    {
        var node = SpendableNode();
        var leaves = new List<SparkLeaf>
        {
            new("a", "t1", 200, "LOCKED") { Node = node },
            new("b", "t1", 100, "AVAILABLE") { Node = node },
        };
        var result = SwapService.TryExactSelection(leaves, 200);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void SelectLeaves_GreedySelection()
    {
        var node = new NSpark.Proto.TreeNode();
        var leaves = new List<SparkLeaf>
        {
            new("1", "t1", 100, "AVAILABLE") { Node = node },
            new("2", "t2", 500, "AVAILABLE") { Node = node },
            new("3", "t3", 200, "AVAILABLE") { Node = node },
        };

        var selected = TransferService.SelectLeaves(leaves, 600);
        Assert.That(selected.Count, Is.EqualTo(2));
        // Greedy largest-first: 500 + 200
        Assert.That(selected[0].ValueSats, Is.EqualTo(500));
        Assert.That(selected[1].ValueSats, Is.EqualTo(200));
    }

    [Test]
    public void SelectLeaves_InsufficientBalance_Throws()
    {
        var node = new NSpark.Proto.TreeNode();
        var leaves = new List<SparkLeaf>
        {
            new("1", "t1", 100, "AVAILABLE") { Node = node },
            new("2", "t2", 500, "AVAILABLE") { Node = node },
            new("3", "t3", 200, "AVAILABLE") { Node = node },
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            TransferService.SelectLeaves(leaves, 1000));
        Assert.That(ex!.Message, Does.Contain("800"));
        Assert.That(ex.Message, Does.Contain("1000"));
    }

    [Test, Explicit("Requires funded Wallet A (>= 100 sats, multiple leaves or > 100 sats leaf)")]
    public async Task ShouldSwapLeavesToMatchTarget()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var balance = await _wallet.GetBalanceAsync(cts.Token);
        if (balance.SatsBalance.Available < 100)
        {
            Assert.Inconclusive($"Wallet needs >= 100 sats (has {balance.SatsBalance.Available})");
            return;
        }

        TestContext.Out.WriteLine($"Balance before swap: {balance.SatsBalance.Available} sats ({balance.Leaves.Count} leaves)");
        foreach (var leaf in balance.Leaves)
            TestContext.Out.WriteLine($"  Leaf {leaf.Id}: {leaf.ValueSats} sats");

        // Request swap to split into a target amount
        var targetAmount = Math.Min(50, balance.SatsBalance.Available / 2);
        var newLeaves = await _wallet.RequestLeavesSwapAsync([targetAmount], cts.Token);

        TestContext.Out.WriteLine($"\nLeaves after swap: {newLeaves.Count}");
        foreach (var leaf in newLeaves)
            TestContext.Out.WriteLine($"  Leaf {leaf.Id}: {leaf.ValueSats} sats");

        // Should have a leaf matching the target amount
        var totalAfter = newLeaves.Where(l => l.Status == "AVAILABLE").Sum(l => l.ValueSats);
        Assert.That(totalAfter, Is.GreaterThanOrEqualTo(targetAmount),
            "Total available balance after swap should cover the target");
    }

    [Test, Explicit("Requires funded Wallet A (>= 100 sats)")]
    public async Task SelectLeavesWithSwap_ShouldReturnExactMatch()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var balance = await _wallet.GetBalanceAsync(cts.Token);
        if (balance.SatsBalance.Available < 100)
        {
            Assert.Inconclusive($"Wallet needs >= 100 sats (has {balance.SatsBalance.Available})");
            return;
        }

        var targetAmount = Math.Min(50, balance.SatsBalance.Available / 2);
        var selected = await _wallet.SelectLeavesWithSwapAsync(targetAmount, cts.Token);

        var total = selected.Sum(l => l.ValueSats);
        TestContext.Out.WriteLine($"Selected {selected.Count} leaves totaling {total} sats for target {targetAmount}");
        Assert.That(total, Is.GreaterThanOrEqualTo(targetAmount));
    }
}

// =============================================================================
// Withdrawal Tests (matching JS: coop-exit.test.ts / Swift: WithdrawalTests)
// =============================================================================

[TestFixture]
[Category("Integration")]
public class WithdrawalTests
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

    [Test, Explicit("Requires funded Wallet A with leaves")]
    public async Task ShouldGetWithdrawalFeeEstimate()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var leaves = await _wallet.GetLeavesAsync(cts.Token);
        if (leaves.Count == 0)
        {
            Assert.Inconclusive("WalletA has no leaves");
            return;
        }

        var fee = await _wallet.GetFeeQuoteAsync(
            leaves.Select(l => l.Id).ToArray(),
            "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4",
            cts.Token);

        // Guard against the alpha.5 unit-mismatch regression: a cooperative exit on mainnet
        // always carries an L1 miner fee that's at least a couple hundred sats. If the quote
        // comes back at 2 sats the conversion logic has misinterpreted MILLISATOSHI vs SATOSHI.
        Assert.That(fee.FeeSats, Is.GreaterThan(100),
            $"Withdrawal fee estimate {fee.FeeSats} sats looks suspiciously low — " +
            "WithdrawalService.GetFeeQuoteAsync may be mishandling the CurrencyAmount unit.");
        TestContext.Out.WriteLine($"Withdrawal fee estimate: {fee.FeeSats} sats");
    }

    /// <summary>
    /// Requests a cooperative exit from the SSP for every spendable leaf and runs exactly the
    /// checks <c>WithdrawAsync</c> runs before it signs anything, on the real response. Nothing
    /// is signed or handed over and the SSP request simply expires unused, so this proves the
    /// request shape, the response fields, and the validator on mainnet without moving funds.
    /// </summary>
    [Test]
    public async Task CoopExitResponseFromTheSspValidatesWithoutSigning()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var leaves = await _wallet.GetSpendableLeavesAsync(cts.Token);
        if (leaves.Count == 0)
        {
            Assert.Inconclusive("WalletA has no spendable leaves");
            return;
        }

        var amount = leaves.Sum(l => l.ValueSats);
        var leafIds = leaves.Select(l => l.Id).ToArray();
        var destination = (await _wallet.GetStaticDepositAddressAsync(cts.Token)).Address;
        var quote = await _wallet.GetFeeQuoteAsync(leafIds, destination, cts.Token);
        TestContext.Out.WriteLine($"{leaves.Count} leaves, {amount} sats, SSP quote {quote.FeeSats} sats, destination {destination}");
        if (quote.FeeSats >= amount)
        {
            Assert.Inconclusive($"SSP fee {quote.FeeSats} sats is not covered by {amount} spendable sats");
            return;
        }

        var feeCap = CoopExitValidator.ResolveFeeCap(quote.FeeSats, null, amount);
        var response = await _wallet.SspClient.ExecuteAsync<RequestCoopExitResponse>(
            Mutations.RequestCoopExit,
            new Dictionary<string, object>
            {
                ["leaf_external_ids"] = leafIds,
                ["withdrawal_address"] = destination,
                ["exit_speed"] = "FAST",
                ["withdraw_all"] = true,
                ["user_outbound_transfer_external_id"] = Guid.NewGuid().ToString("D"),
            },
            cts.Token);
        var request = response.RequestCoopExit.Request;
        Assert.That(request.RawCoopExitTransaction, Is.Not.Null.And.Not.Empty, "the SSP must return raw_coop_exit_transaction");
        Assert.That(request.RawConnectorTransaction, Is.Not.Empty);
        Assert.That(request.CoopExitTxid, Is.Not.Empty);

        var validated = CoopExitValidator.Validate(
            request.RawCoopExitTransaction,
            request.RawConnectorTransaction,
            request.CoopExitTxid,
            destination,
            amount - feeCap,
            leaves.Count,
            _client.Options.Network);

        TestContext.Out.WriteLine(
            $"exit txid {validated.ExitTxidHex} (SSP field {request.CoopExitTxid}), payout {validated.PayoutSats} sats at vout {validated.PayoutVout}, " +
            $"connector txid {Convert.ToHexString(validated.ConnectorTxidInternal).ToLowerInvariant()} with {validated.ConnectorTx.Outputs.Count} outputs");
        Assert.That(validated.PayoutSats, Is.GreaterThanOrEqualTo((ulong)(amount - feeCap)));
        Assert.That(validated.ConnectorTx.Outputs.Count, Is.EqualTo(leaves.Count + 1));

        // Build (but never sign) the connector refunds for the first leaf: proves the raw-tx helpers
        // accept the SSP's real transactions and produce the two-input sighashes the exit would sign.
        var sspPubKey = Convert.FromHexString(_client.Options.SspIdentityPublicKeyHex);
        var connectorOutput = validated.ConnectorTx.Outputs[0];
        var refunds = WithdrawalService.BuildConnectorRefunds(
            leaves[0].Node, sspPubKey, validated.ConnectorTxidInternal,
            connectorOutput.ScriptPubKey.ToBytes(), (ulong)connectorOutput.Value.Satoshi, 0,
            FrostSigningHelper.GetNetworkString(_client.Options.Network));
        Assert.That(refunds.Cpfp.Sighash, Has.Length.EqualTo(32));
        Assert.That(refunds.DirectFromCpfp.Sighash, Has.Length.EqualTo(32));
        Assert.That(NBitcoin.Transaction.Load(refunds.Cpfp.Tx, NBitcoin.Network.Main).Inputs, Has.Count.EqualTo(2));
        TestContext.Out.WriteLine("connector refunds built for leaf " + leaves[0].Id + " (cpfp, directFromCpfp" + (refunds.Direct is null ? ")" : ", direct)"));
    }

    /// <summary>
    /// The real thing: spends balance on-chain. Opt in with <c>NSPARK_TEST_ALLOW_WITHDRAW=1</c>.
    /// Optional: <c>NSPARK_TEST_WITHDRAW_SATS</c> (default: every spendable sat),
    /// <c>NSPARK_TEST_WITHDRAW_DESTINATION</c> (default: this wallet's own static deposit
    /// address, so the payout can be claimed back with <c>ClaimStaticDepositAsync</c>),
    /// <c>NSPARK_TEST_WITHDRAW_MAX_FEE_SATS</c> (default: the SSP's quote).
    /// </summary>
    [Test, Explicit("Destructive: spends balance on-chain; set NSPARK_TEST_ALLOW_WITHDRAW=1")]
    public async Task ShouldWithdrawToOnChainAddress()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(10));

        if (TestSecrets.TryGet("NSPARK_TEST_ALLOW_WITHDRAW") != "1")
        {
            Assert.Inconclusive("Set NSPARK_TEST_ALLOW_WITHDRAW=1 to run the on-chain withdrawal.");
            return;
        }

        var destination = TestSecrets.TryGet("NSPARK_TEST_WITHDRAW_DESTINATION")
            ?? (await _wallet.GetStaticDepositAddressAsync(cts.Token)).Address;
        var spendable = await _wallet.GetSpendableLeavesAsync(cts.Token);
        var amount = long.TryParse(TestSecrets.TryGet("NSPARK_TEST_WITHDRAW_SATS"), out var configuredAmount)
            ? configuredAmount
            : spendable.Sum(l => l.ValueSats);
        long? maxFeeSats = long.TryParse(TestSecrets.TryGet("NSPARK_TEST_WITHDRAW_MAX_FEE_SATS"), out var cap) ? cap : null;
        if (amount <= 0)
        {
            Assert.Inconclusive("No spendable balance to withdraw");
            return;
        }

        var before = await _wallet.GetBalanceAsync(cts.Token);
        TestContext.Out.WriteLine($"Withdrawing {amount} of {before.SatsBalance.Available} available sats to {destination} (fee cap {maxFeeSats?.ToString() ?? "SSP quote"})");

        var txid = await _wallet.WithdrawAsync(destination, amount, maxFeeSats, cts.Token);

        Assert.That(txid, Has.Length.EqualTo(64));
        TestContext.Out.WriteLine($"Withdrawal txid: {txid}");

        // The exited leaves stay transfer-locked (still owned) until the exit confirms on-chain;
        // only the spendable balance drops immediately.
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
        var after = await _wallet.GetBalanceAsync(cts.Token);
        Assert.That(after.SatsBalance.Available, Is.EqualTo(before.SatsBalance.Available - amount));
        Assert.That(after.SatsBalance.Owned, Is.GreaterThanOrEqualTo(before.SatsBalance.Owned - amount));
    }
}

// =============================================================================
// Static Deposit Tests (matching JS: static_deposit.test.ts / Swift: StaticDepositTests)
// =============================================================================

[TestFixture]
[Category("Integration")]
public class StaticDepositTests
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

    [Test, Explicit("Requires real static deposit txid")]
    public async Task ShouldGetDepositFeeEstimate()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var txId = "33205a3babb25b4e8a0b15a496dd74b31739d225afb1365fc55052da6c58c86e";
        uint outputIndex = 4;

        var estimate = await _wallet.GetDepositFeeEstimateAsync(txId, outputIndex: outputIndex, ct: cts.Token);
        Assert.That(estimate.CreditAmountSats, Is.GreaterThan(0));
        Assert.That(estimate.Signature, Is.Not.Empty);
        TestContext.Out.WriteLine($"Credit amount: {estimate.CreditAmountSats} sats (from 10500 sats deposit)");
    }

    [Test, Explicit("Requires funded static deposit")]
    public async Task ShouldClaimStaticDeposit()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var txId = "33205a3babb25b4e8a0b15a496dd74b31739d225afb1365fc55052da6c58c86e";
        uint outputIndex = 4;

        var balanceBefore = await _wallet.GetBalanceAsync(cts.Token);
        TestContext.Out.WriteLine($"Balance before claim: {balanceBefore.SatsBalance.Available} sats");

        await _wallet.ClaimStaticDepositAsync(txId, outputIndex: outputIndex, ct: cts.Token);
        var balance = await _wallet.GetBalanceAsync(cts.Token);
        TestContext.Out.WriteLine($"Balance after claim: {balance.SatsBalance.Available} sats");
        Assert.That(balance.SatsBalance.Available, Is.GreaterThan(balanceBefore.SatsBalance.Available));
    }

    [Test, Explicit("Requires funded static deposit — cannot run after claim")]
    public async Task ShouldRefundStaticDeposit()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var txId = "33205a3babb25b4e8a0b15a496dd74b31739d225afb1365fc55052da6c58c86e";
        uint outputIndex = 4;

        var txHex = await _wallet.RefundStaticDepositAsync(
            txId,
            "bc1q4cmzdldcdp43h2r7nde44rh8rcjr9vzlmvj5lq",
            satsPerVbyte: 5,
            outputIndex: outputIndex,
            ct: cts.Token);

        Assert.That(txHex, Is.Not.Empty);
        TestContext.Out.WriteLine($"Refund tx hex: {txHex[..Math.Min(80, txHex.Length)]}...");
    }
}

// =============================================================================
// On-Chain Deposit Tests (matching Swift: OnChainDepositTests)
// =============================================================================

[TestFixture]
[Category("Integration")]
public class OnChainDepositTests
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

    [Test, Explicit("Requires funded deposit address")]
    public async Task ShouldClaimOnChainDeposit()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var txId = "";
        if (string.IsNullOrEmpty(txId))
        {
            Assert.Inconclusive("Set txId to test on-chain deposit claim");
            return;
        }

        await _wallet.ClaimDepositAsync(txId, ct: cts.Token);
        var balance = await _wallet.GetBalanceAsync(cts.Token);
        TestContext.Out.WriteLine($"Balance after deposit claim: {balance.SatsBalance.Available} sats");
        Assert.That(balance.SatsBalance.Available, Is.GreaterThan(0));
    }
}

// =============================================================================
// Third-Party Invoice Tests
// =============================================================================

[TestFixture]
[Category("Integration")]
public class ThirdPartyInvoiceTests
{
    private static string MnemonicA => TestSecrets.MnemonicA;
    private static string MnemonicC => TestSecrets.MnemonicC;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    private SparkConnection _client = null!;
    private SparkWallet _walletA = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _client = new SparkConnection(
            Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet }),
            new HttpClient());
        _walletA = await _client.CreateWalletAsync(MnemonicA);
    }

    [OneTimeTearDown]
    public void Teardown() => _client?.Dispose();

    [Test, Explicit("A creates invoice for C, external payment, C claims")]
    public async Task ShouldCreateInvoiceForThirdParty()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var walletC = await _client.CreateWalletAsync(MnemonicC);
        var cPubKey = Convert.FromHexString(walletC.IdentityPublicKeyHex);

        var invoice = await _walletA.CreateLightningInvoiceAsync(
            200, memo: "invoice for C", receiverIdentityPublicKey: cPubKey, ct: cts.Token);
        TestContext.Out.WriteLine($"=== PAY THIS INVOICE (200 sats, for wallet C) ===");
        TestContext.Out.WriteLine(invoice.PaymentRequest);

        // Poll for payment
        bool paid = false;
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
            var transfers = await _walletA.QueryTransfersForReceiverAsync(cPubKey, cts.Token);
            if (transfers.Count > 0) { paid = true; break; }
        }
        Assert.That(paid, Is.True, "Invoice was not paid in time");

        var claimed = await walletC.ClaimPendingTransfersAsync(cts.Token);
        Assert.That(claimed.Count, Is.GreaterThan(0));

        var balanceC = await walletC.GetBalanceAsync(cts.Token);
        Assert.That(balanceC.SatsBalance.Available, Is.GreaterThan(0));
    }
}

// =============================================================================
// Debug / Utility Tests (matching Swift: DebugTests)
// =============================================================================

[TestFixture]
[Category("Integration")]
public class DebugTests
{
    private static string MnemonicA => TestSecrets.MnemonicA;
    private static string MnemonicB => TestSecrets.MnemonicB;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private SparkConnection _client = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _client = new SparkConnection(
            Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet }),
            new HttpClient());
    }

    [OneTimeTearDown]
    public void Teardown() => _client?.Dispose();

    [Test]
    public async Task ShowWalletInfo()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var walletA = await _client.CreateWalletAsync(MnemonicA);
        var walletB = await _client.CreateWalletAsync(MnemonicB);

        var balA = await walletA.GetBalanceAsync(cts.Token);
        var balB = await walletB.GetBalanceAsync(cts.Token);

        TestContext.Out.WriteLine("=== Wallet A ===");
        TestContext.Out.WriteLine($"  Identity: {walletA.IdentityPublicKeyHex}");
        TestContext.Out.WriteLine($"  Spark:    {walletA.GetSparkAddress()}");
        TestContext.Out.WriteLine($"  Balance:  {balA.SatsBalance.Available} sats ({balA.Leaves.Count} leaves)");

        TestContext.Out.WriteLine("=== Wallet B ===");
        TestContext.Out.WriteLine($"  Identity: {walletB.IdentityPublicKeyHex}");
        TestContext.Out.WriteLine($"  Spark:    {walletB.GetSparkAddress()}");
        TestContext.Out.WriteLine($"  Balance:  {balB.SatsBalance.Available} sats ({balB.Leaves.Count} leaves)");
    }
}

// =============================================================================
// Funding Helpers (matching Swift: FundingTests)
// =============================================================================

[TestFixture]
[Category("Integration")]
public class FundingTests
{
    private static string MnemonicA => TestSecrets.MnemonicA;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

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

    [Test, Explicit("Creates invoice for manual funding")]
    public async Task CreateFundingInvoice()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var invoice = await _wallet.CreateLightningInvoiceAsync(
            5000, memo: "Fund walletA for tests", ct: cts.Token);
        TestContext.Out.WriteLine("=== PAY THIS INVOICE TO FUND WALLET A ===");
        TestContext.Out.WriteLine(invoice.PaymentRequest);
        TestContext.Out.WriteLine($"Amount: 5000 sats | Hash: {invoice.PaymentHash}");
    }

    [Test, Explicit("Claims all pending transfers")]
    public async Task ClaimAllForWalletA()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var claimed = await _wallet.ClaimPendingTransfersAsync(cts.Token);
        var balance = await _wallet.GetBalanceAsync(cts.Token);
        TestContext.Out.WriteLine($"Claimed {claimed.Count} transfers. Balance: {balance.SatsBalance.Available} sats");
    }

    [Test, Explicit("Requires manual Lightning payment, polls for balance")]
    public async Task FundAndPoll()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        var beforeBalance = await _wallet.GetBalanceAsync(cts.Token);
        var invoice = await _wallet.CreateLightningInvoiceAsync(1_000, memo: "fund integration test wallet", ct: cts.Token);

        TestContext.Out.WriteLine("=== PAY THIS INVOICE ===");
        TestContext.Out.WriteLine(invoice.PaymentRequest);
        TestContext.Out.WriteLine($"Amount: {invoice.AmountSats} sats — waiting up to 5 minutes...");

        while (!cts.Token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
            var current = await _wallet.GetBalanceAsync(cts.Token);
            TestContext.Out.WriteLine($"  Balance: {current.SatsBalance.Available} sats");
            if (current.SatsBalance.Available > beforeBalance.SatsBalance.Available)
            {
                TestContext.Out.WriteLine($"Funded! +{current.SatsBalance.Available - beforeBalance.SatsBalance.Available} sats");
                return;
            }
        }
        Assert.Fail("Funding timed out");
    }
}

// =============================================================================
// Full Integration Flow (matching JS e2e / Swift: FullFlowTests)
// =============================================================================

[TestFixture]
[Category("Integration")]
public class FullFlowTests
{
    private static string MnemonicA => TestSecrets.MnemonicA;
    private static string MnemonicB => TestSecrets.MnemonicB;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);
    private const long MinimumTestBalance = 500;

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

    [Test, Explicit("Full round trip: Lightning A->B, Spark B->A, external Lightning pay")]
    public async Task FullRoundTrip()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(Timeout);

        void Log(string msg) { Console.Error.WriteLine(msg); Console.Error.Flush(); }

        // Check walletA has funds
        var initialBalance = await _walletA.GetBalanceAsync(cts.Token);
        Log($"WalletA initial balance: {initialBalance.SatsBalance.Available} sats");
        if (initialBalance.SatsBalance.Available < MinimumTestBalance)
        {
            var deposit = await _walletA.GetDepositAddressAsync(cts.Token);
            Assert.Inconclusive($"WalletA needs >= {MinimumTestBalance} sats. Deposit to: {deposit.Address}");
            return;
        }

        // --- Phase 1: Lightning A -> B ---
        Log("\n--- Phase 1: Lightning A -> B (100 sats) ---");
        var invoice = await _walletB.CreateLightningInvoiceAsync(100, memo: "full flow test", ct: cts.Token);
        Assert.That(invoice.AmountSats, Is.EqualTo(100));

        var payId = await _walletA.PayLightningInvoiceAsync(invoice.PaymentRequest, maxFeeSats: 50, ct: cts.Token);
        Assert.That(payId, Is.Not.Empty);
        Log($"Payment sent: {payId}");

        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
        var claimedB = await _walletB.ClaimPendingTransfersAsync(cts.Token);
        Log($"WalletB claimed {claimedB.Count} transfers");

        var balB = await _walletB.GetBalanceAsync(cts.Token);
        Assert.That(balB.SatsBalance.Available, Is.GreaterThanOrEqualTo(100));
        Log($"WalletB balance: {balB.SatsBalance.Available} sats");

        // --- Phase 2: Spark Transfer B -> A ---
        Log($"\n--- Phase 2: Spark B -> A ({balB.SatsBalance.Available} sats) ---");
        var pubA = Convert.FromHexString(_walletA.IdentityPublicKeyHex);
        var transfer = await _walletB.SendAsync(pubA, balB.SatsBalance.Available, null, cts.Token);
        Assert.That(transfer.Id, Is.Not.Empty);
        Log($"Transfer: {transfer.Id}");

        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
        var claimedA = await _walletA.ClaimPendingTransfersAsync(cts.Token);
        Assert.That(claimedA.Count, Is.GreaterThanOrEqualTo(1));

        var balA2 = await _walletA.GetBalanceAsync(cts.Token);
        Log($"WalletA balance: {balA2.SatsBalance.Available} sats");

        // B should be empty
        var finalB = await _walletB.GetBalanceAsync(cts.Token);
        Assert.That(finalB.SatsBalance.Available, Is.EqualTo(0));

        // --- Phase 3: External Lightning A -> bub@bub.gg ---
        Log("\n--- Phase 3: Lightning A -> bub@bub.gg (10 sats) ---");
        var extPayId = await _walletA.PayLightningAddressAsync("bub@bub.gg", 10, maxFeeSats: 50, ct: cts.Token);
        Assert.That(extPayId, Is.Not.Empty);
        Log($"External payment: {extPayId}");

        var finalA = await _walletA.GetBalanceAsync(cts.Token);
        Log($"\nFinal WalletA balance: {finalA.SatsBalance.Available} sats");
        Log("Full flow complete!");
    }
}

