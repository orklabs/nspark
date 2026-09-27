using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using NBitcoin;
using NSpark.Bitcoin;
using NSpark.Exceptions;
using NSpark.Models;
using NSpark.Proto;
using NSpark.Services;
using ConnectedEvent = NSpark.Models.ConnectedEvent;
using SparkAddress = NSpark.Services.SparkAddress;

namespace NSpark.Tests;

// =============================================================================
// Hardening (matching Swift: HardeningIntegrationTests)
// =============================================================================

/// <summary>
/// Mainnet checks for the 0.3 hardening: exact-amount Spark transfers to a Spark address,
/// claim passes, fee-capped Lightning payments against verified invoices and their resume path,
/// the self-healing event stream, frozen-leaf accounting, and argument validation that must fail
/// before any leaf is touched. Whichever of wallets A and B can spend more acts as the sender, so
/// the suite works whichever wallet was funded last.
/// </summary>
/// <remarks>
/// Tests that move sats between the test wallets (a few to a few dozen, plus Lightning fees) are
/// <c>Explicit</c>: run them with a filter that names them. On-chain operations are further opt-in
/// through environment variables and are never part of a plain run.
/// </remarks>
[TestFixture]
[Category("Integration")]
[Category("Hardening")]
[NonParallelizable]
public class HardeningIntegrationTests
{
    private const string MovesSats = "Moves sats between test wallets A and B";

    private SparkConnection _client = null!;
    private SparkWallet _walletA = null!;
    private SparkWallet _walletB = null!;

    private sealed record Pair(SparkWallet Sender, SparkWallet Receiver, string SenderLabel, long SenderSpendable);

    [OneTimeSetUp]
    public async Task Setup()
    {
        _client = new SparkConnection(Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet }), new HttpClient());
        _walletA = await _client.CreateWalletAsync(TestSecrets.MnemonicA);
        _walletB = await _client.CreateWalletAsync(TestSecrets.MnemonicB);
    }

    [OneTimeTearDown]
    public void Teardown() => _client?.Dispose();

    private static CancellationTokenSource Timeout(TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cts.CancelAfter(timeout);
        return cts;
    }

    /// <summary>Sats the wallet can send right now (renews what the coordinator will renew, skips frozen leaves).</summary>
    private static async Task<long> SpendableAsync(SparkWallet wallet, CancellationToken ct)
    {
        var leaves = await wallet.GetSpendableLeavesAsync(ct);
        Assert.That(leaves.All(l => l.IsSpendable), Is.True);
        return leaves.Sum(l => l.ValueSats);
    }

    private async Task<Pair> MakePairAsync(CancellationToken ct)
    {
        var spendableA = await SpendableAsync(_walletA, ct);
        var spendableB = await SpendableAsync(_walletB, ct);
        return spendableA >= spendableB
            ? new Pair(_walletA, _walletB, "A", spendableA)
            : new Pair(_walletB, _walletA, "B", spendableB);
    }

    private static void RequireSpendable(Pair pair, long needed)
    {
        if (pair.SenderSpendable < needed)
        {
            Assert.Inconclusive($"The sender needs {needed} spendable sats, has {pair.SenderSpendable}.");
        }
    }

    /// <summary>Claims on <paramref name="receiver"/> until its owned sats reach <paramref name="target"/> or 30 s pass.</summary>
    private static async Task<SatsBalance> ClaimUntilAsync(SparkWallet receiver, long target, CancellationToken ct)
    {
        var balance = (await receiver.GetBalanceAsync(ct)).SatsBalance;
        for (var attempt = 0; attempt < 10 && balance.Owned < target; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            await receiver.ClaimPendingTransfersAsync(ct);
            balance = (await receiver.GetBalanceAsync(ct)).SatsBalance;
        }

        return balance;
    }

    [Test, Explicit(MovesSats)]
    public async Task A_Spark_transfer_to_a_Spark_address_is_claimed_after_sender_signature_verification()
    {
        using var cts = Timeout(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        var pair = await MakePairAsync(ct);
        const long amount = 10;
        RequireSpendable(pair, amount + 5);
        var receiverBefore = (await pair.Receiver.GetBalanceAsync(ct)).SatsBalance;
        var senderBefore = (await pair.Sender.GetBalanceAsync(ct)).SatsBalance;

        var transfer = await pair.Sender.SendAsync(pair.Receiver.GetSparkAddress(), amount, ct);
        Assert.That(transfer.Id, Is.Not.Empty);
        Assert.That(transfer.TotalValueSats, Is.EqualTo(amount));
        Assert.That(transfer.ReceiverIdentityPublicKey, Is.EqualTo(pair.Receiver.IdentityPublicKeyHex));
        TestContext.Out.WriteLine($"[{pair.SenderLabel}] sent {amount} sats, transfer {transfer.Id}");

        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        var claim = await pair.Receiver.ClaimPendingTransfersAsync(ct);
        Assert.That(claim.ClaimedTransferIds, Does.Contain(transfer.Id));
        Assert.That(claim.Failures, Is.Empty);

        var receiverAfter = (await pair.Receiver.GetBalanceAsync(ct)).SatsBalance;
        var senderAfter = (await pair.Sender.GetBalanceAsync(ct)).SatsBalance;
        Assert.That(receiverAfter.Owned, Is.EqualTo(receiverBefore.Owned + receiverBefore.Incoming + amount));
        Assert.That(senderAfter.Owned, Is.EqualTo(senderBefore.Owned - amount));
        TestContext.Out.WriteLine($"receiver {receiverBefore.Owned} -> {receiverAfter.Owned}, sender {senderBefore.Owned} -> {senderAfter.Owned}");
    }

    [Test]
    public async Task Bad_arguments_are_rejected_before_any_network_call_moves_a_leaf()
    {
        using var cts = Timeout(TimeSpan.FromMinutes(3));
        var ct = cts.Token;
        var pair = await MakePairAsync(ct);
        var before = (await pair.Sender.GetBalanceAsync(ct)).SatsBalance;
        var address = pair.Receiver.GetSparkAddress();
        var invoice = SparkInvoiceFor(pair.Receiver.IdentityPublicKey, 10);

        Assert.That(() => pair.Sender.SendAsync(address, 0, ct), Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(() => pair.Sender.SendAsync(address, -1, ct), Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(() => pair.Sender.SendAsync("sparkrt1qq", 1, ct), Throws.InstanceOf<SparkConfigurationException>());
        Assert.That(() => pair.Sender.SendAsync(invoice, 10, ct), Throws.InstanceOf<SparkConfigurationException>());
        Assert.That(() => pair.Sender.SendAsync(address, 1_000_000_000, ct), Throws.InstanceOf<SparkTransferException>());
        Assert.That(() => pair.Sender.WithdrawAsync("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4", 0, ct: ct), Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(() => pair.Sender.WithdrawAsync("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", 1_000, ct: ct), Throws.InstanceOf<SparkConfigurationException>());
        Assert.That(() => pair.Sender.WithdrawAsync("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4", 1_000_000_000, ct: ct), Throws.InstanceOf<SparkTransferException>());
        Assert.That(
            () => pair.Sender.PayLightningInvoiceAsync(
                "lntb20m1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygshp58yjmdan79s6qqdhdzgynm4zwqd5d7xmw5fk98klysy043l2ahrqspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqfpp3x9et2e20v6pu37c5d9vax37wxq72un989qrsgqdj545axuxtnfemtpwkc45hx9d2ft7x04mt8q7y6t0k2dge9e7h8kpy9p34ytyslj3yu569aalz2xdk8xkd7ltxqld94u8h2esmsmacgpghe9k8",
                maxFeeSats: 10,
                ct: ct),
            Throws.InstanceOf<InvalidBolt11Exception>());

        var after = (await pair.Sender.GetBalanceAsync(ct)).SatsBalance;
        Assert.That(after.Owned, Is.EqualTo(before.Owned));
        Assert.That(after.Available, Is.EqualTo(before.Available));
    }

    /// <summary>An unsigned mainnet Spark invoice for <paramref name="amountSats"/> to <paramref name="identityPublicKey"/>.</summary>
    private static string SparkInvoiceFor(byte[] identityPublicKey, ulong amountSats)
    {
        var payload = new NSpark.Proto.SparkAddress
        {
            IdentityPublicKey = Google.Protobuf.ByteString.CopyFrom(identityPublicKey),
            SparkInvoiceFields = new SparkInvoiceFields
            {
                Version = 1,
                Id = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                SatsPayment = new SatsPayment { Amount = amountSats },
            },
        };
        return Bech32mHelper.Encode("spark", Google.Protobuf.MessageExtensions.ToByteArray(payload));
    }

    [Test, Explicit(MovesSats)]
    public async Task One_claim_pass_takes_every_pending_transfer_and_reports_no_failures()
    {
        using var cts = Timeout(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        var pair = await MakePairAsync(ct);
        long[] amounts = [1, 2, 8];
        RequireSpendable(pair, amounts.Sum() + 5);
        await pair.Receiver.ClaimPendingTransfersAsync(ct);

        var sent = new List<string>();
        foreach (var amount in amounts)
        {
            sent.Add((await pair.Sender.SendAsync(pair.Receiver.GetSparkAddress(), amount, ct)).Id);
        }

        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        var result = await pair.Receiver.ClaimPendingTransfersAsync(ct);
        TestContext.Out.WriteLine($"[{pair.SenderLabel}] sent {string.Join(", ", sent)}; claimed {string.Join(", ", result.ClaimedTransferIds)}; failures {result.Failures.Count}");
        Assert.That(result.ClaimedTransferIds, Is.SupersetOf(sent));
        Assert.That(result.Failures, Is.Empty);
        var pending = await pair.Receiver.QueryPendingTransfersAsync(100, 0, ct);
        Assert.That(pending.Select(t => t.Id).Intersect(sent), Is.Empty);
    }

    [Test, Explicit(MovesSats)]
    public async Task Claiming_a_transfer_this_wallet_already_claimed_counts_as_claimed()
    {
        using var cts = Timeout(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        var pair = await MakePairAsync(ct);
        RequireSpendable(pair, 6);
        await pair.Receiver.ClaimPendingTransfersAsync(ct);

        var sent = await pair.Sender.SendAsync(pair.Receiver.GetSparkAddress(), 1, ct);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        var pending = (await pair.Receiver.QueryPendingTransfersAsync(100, 0, ct)).First(t => t.Id == sent.Id);

        await ClaimService.ClaimTransferAsync(pair.Receiver, pending, ct);
        // The operators now answer ALREADY_EXISTS; the reference SDK treats a completed transfer as
        // claimed, and so must we (a swap and a claim pass can race for the same transfer).
        await ClaimService.ClaimTransferAsync(pair.Receiver, pending, ct);
        Assert.That((await pair.Receiver.QueryTransferByIdAsync(sent.Id, ct)).Status, Is.EqualTo(TransferStatus.Completed));
    }

    [Test, Explicit(MovesSats)]
    public async Task A_send_no_leaf_combination_can_pay_exactly_swaps_for_change_first()
    {
        using var cts = Timeout(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        Pair? pair = null;
        long amount = 0;
        foreach (var (sender, receiver, label) in new[] { (_walletA, _walletB, "A"), (_walletB, _walletA, "B") })
        {
            var leaves = await sender.GetSpendableLeavesAsync(ct);
            var total = leaves.Sum(l => l.ValueSats);
            var gap = Enumerable.Range(1, (int)Math.Min(total, 2_000)).FirstOrDefault(a => SwapService.TryExactSelection(leaves, a) is null);
            if (gap > 0)
            {
                (pair, amount) = (new Pair(sender, receiver, label, total), gap);
                break;
            }
        }

        if (pair is null)
        {
            Assert.Inconclusive("Both wallets have an exact leaf combination for every amount up to 2,000 sats.");
            return;
        }

        await pair.Receiver.ClaimPendingTransfersAsync(ct);
        var receiverBefore = (await pair.Receiver.GetBalanceAsync(ct)).SatsBalance;
        var transfer = await pair.Sender.SendAsync(pair.Receiver.GetSparkAddress(), amount, ct);
        TestContext.Out.WriteLine($"[{pair.SenderLabel}] sent {amount} sats through a swap, transfer {transfer.Id}");
        Assert.That(transfer.TotalValueSats, Is.EqualTo(amount));

        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        var claim = await pair.Receiver.ClaimPendingTransfersAsync(ct);
        Assert.That(claim.ClaimedTransferIds, Does.Contain(transfer.Id));
        var receiverAfter = (await pair.Receiver.GetBalanceAsync(ct)).SatsBalance;
        Assert.That(receiverAfter.Owned, Is.EqualTo(receiverBefore.Owned + receiverBefore.Incoming + amount));
    }

    [Test, Explicit(MovesSats)]
    public async Task Sent_sats_leave_owned_once_the_transfer_is_committed_before_the_receiver_claims()
    {
        using var cts = Timeout(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        var pair = await MakePairAsync(ct);
        RequireSpendable(pair, 6);
        await pair.Receiver.ClaimPendingTransfersAsync(ct);
        // Sats of other in-flight operations (a cooperative exit waiting for its confirmations)
        // may be locked already; the send must add none.
        var before = (await pair.Sender.GetBalanceAsync(ct)).SatsBalance;
        var receiverBefore = (await pair.Receiver.GetBalanceAsync(ct)).SatsBalance;

        var transfer = await pair.Sender.SendAsync(pair.Receiver.GetSparkAddress(), 1, ct);
        Assert.That(transfer.Status, Is.EqualTo(nameof(TransferStatus.SenderKeyTweaked)));

        // The operators applied the sender's key tweak: the sat belongs to the receiver now, even
        // though its leaf stays TRANSFER_LOCKED under the sender's key until the claim.
        var sent = (await pair.Sender.GetBalanceAsync(ct)).SatsBalance;
        TestContext.Out.WriteLine($"[{pair.SenderLabel}] owned {before.Owned} -> {sent.Owned}, locked {before.Locked} -> {sent.Locked}");
        Assert.That(sent.Owned, Is.EqualTo(before.Owned - 1));
        Assert.That(sent.Locked, Is.EqualTo(before.Locked));
        var receiverPending = (await pair.Receiver.GetBalanceAsync(ct)).SatsBalance;
        Assert.That(receiverPending.Incoming, Is.EqualTo(receiverBefore.Incoming + 1));
        Assert.That(receiverPending.Owned, Is.EqualTo(receiverBefore.Owned));
        await pair.Receiver.ClaimPendingTransfersAsync(ct);
    }

    [Test]
    public async Task Frozen_sats_are_exactly_the_leaves_below_the_renewal_minimum_and_the_drain_quote_agrees()
    {
        using var cts = Timeout(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        foreach (var (label, wallet, other) in new[] { ("A", _walletA, _walletB), ("B", _walletB, _walletA) })
        {
            var balance = await wallet.GetBalanceAsync(ct);
            var frozenLeaves = balance.Leaves.Where(l => l.IsFrozen).ToList();
            Assert.That(balance.SatsBalance.Frozen, Is.EqualTo(frozenLeaves.Sum(l => l.ValueSats)), label);
            Assert.That(balance.SatsBalance.Available, Is.EqualTo(balance.Leaves.Where(l => !l.IsFrozen).Sum(l => l.ValueSats)), label);
            Assert.That(frozenLeaves.All(l => l.RefundTimelockBlocks < TimelockHelper.TimeLockInterval), Is.True, label);

            // The quote claims, renews what the operators will renew, and fetches a fee quote;
            // nothing leaves the wallet.
            var destination = (await other.GetStaticDepositAddressAsync(ct)).Address;
            var quote = await wallet.QuoteWithdrawAllAsync(destination, ct);
            var after = (await wallet.GetBalanceAsync(ct)).SatsBalance;
            TestContext.Out.WriteLine(
                $"[{label}] available {balance.SatsBalance.Available} frozen {balance.SatsBalance.Frozen} -> quote spendable {quote.SpendableSats} " +
                $"fee {quote.QuotedFeeSats} frozen {quote.FrozenSats} unrenewed {quote.UnrenewedSats} locked {quote.LockedSats}");
            Assert.That(quote.FrozenSats, Is.EqualTo(after.Frozen), label);
            Assert.That(quote.UnrenewedSats, Is.Zero, $"{label}: every renewable leaf should have been renewed");
            Assert.That(quote.SpendableSats + quote.UnrenewedSats, Is.EqualTo(after.Available), label);
        }
    }

    /// <summary>
    /// Bounces one small leaf between the test wallets until a transfer delivers it in the renewal
    /// range (each Spark transfer takes 100 blocks off it); the receiver's claim pass must renew it
    /// to a fresh timelock right away. Opt-in (<c>NSPARK_TEST_RENEWAL=1</c>): one transfer per 100
    /// blocks of timelock.
    /// </summary>
    [Test, Explicit("Opt-in: NSPARK_TEST_RENEWAL=1; bounces one leaf up to 20 times")]
    public async Task A_leaf_that_arrives_in_the_renewal_range_is_renewed_by_the_claim()
    {
        if (TestSecrets.TryGet("NSPARK_TEST_RENEWAL") != "1")
        {
            Assert.Inconclusive("Set NSPARK_TEST_RENEWAL=1 to run the renewal round trip.");
            return;
        }

        using var cts = Timeout(TimeSpan.FromMinutes(20));
        var ct = cts.Token;
        var candidates = new List<(SparkWallet Holder, SparkWallet Other, SparkLeaf Leaf)>();
        foreach (var (holder, other) in new[] { (_walletA, _walletB), (_walletB, _walletA) })
        {
            candidates.AddRange((await holder.GetLeavesAsync(ct)).Where(l => l.IsSpendable && l.ValueSats <= 64).Select(l => (holder, other, l)));
        }

        if (candidates.Count == 0)
        {
            Assert.Inconclusive("No small spendable leaf to bounce.");
            return;
        }

        var (current, next, leaf) = candidates.MinBy(c => c.Leaf.RefundTimelockBlocks);
        var leafId = leaf.Id;
        TestContext.Out.WriteLine($"bouncing leaf {leafId} ({leaf.ValueSats} sats) from refund timelock {leaf.RefundTimelockBlocks}");
        while (true)
        {
            var arrivesAt = TimelockHelper.RoundedTimelock(leaf.RefundTimelockBlocks) - TimelockHelper.TimeLockInterval;
            await current.TransferLeavesAsync([leaf], next.IdentityPublicKey, transferId: null, ct);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            var claim = await next.ClaimPendingTransfersAsync(ct);
            Assert.That(claim.Failures, Is.Empty);
            (current, next) = (next, current);
            leaf = (await current.GetLeavesAsync(ct)).First(l => l.Id == leafId);
            TestContext.Out.WriteLine($"  delivered at {arrivesAt}, after the claim {leaf.RefundTimelockBlocks}");
            if (arrivesAt < RenewalService.RenewalThreshold)
            {
                // Delivered in the renewal range: the claim pass renewed it.
                Assert.That(leaf.RefundTimelockBlocks, Is.EqualTo(2000u));
                Assert.That(leaf.IsSpendable, Is.True);
                break;
            }

            Assert.That(leaf.RefundTimelockBlocks, Is.EqualTo(arrivesAt));
        }
    }

    /// <summary>
    /// Drains the sender wallet with <c>WithdrawAllAsync</c>. Opt-in (<c>NSPARK_TEST_ALLOW_WITHDRAW_ALL=1</c>):
    /// irreversible, pays the SSP's exit fee. Destination: <c>NSPARK_TEST_WITHDRAW_DESTINATION</c>
    /// (an address, or <c>receiver-static-deposit</c> for the other wallet's static deposit address).
    /// </summary>
    [Test, Explicit("Destructive: drains a wallet on-chain; set NSPARK_TEST_ALLOW_WITHDRAW_ALL=1")]
    public async Task WithdrawAll_drains_every_spendable_sat_with_a_verified_payout_and_reports_what_stays()
    {
        if (TestSecrets.TryGet("NSPARK_TEST_ALLOW_WITHDRAW_ALL") != "1")
        {
            Assert.Inconclusive("Set NSPARK_TEST_ALLOW_WITHDRAW_ALL=1 to drain a test wallet on-chain.");
            return;
        }

        using var cts = Timeout(TimeSpan.FromMinutes(10));
        var ct = cts.Token;
        var pair = await MakePairAsync(ct);
        var destination = await DestinationAsync(pair, ct);
        var quote = await pair.Sender.QuoteWithdrawAllAsync(destination, ct);
        TestContext.Out.WriteLine(
            $"[{pair.SenderLabel}] quote: spendable {quote.SpendableSats} fee {quote.QuotedFeeSats} payout≈{quote.EstimatedPayoutSats} " +
            $"frozen {quote.FrozenSats} locked {quote.LockedSats} incoming {quote.IncomingSats} leaves {quote.LeafCount}");
        Assert.That(quote.SpendableSats, Is.EqualTo(pair.SenderSpendable));
        if (!quote.CoversFee)
        {
            Assert.Inconclusive($"The fee {quote.QuotedFeeSats} is not covered by {quote.SpendableSats} spendable sats.");
            return;
        }

        var result = await pair.Sender.WithdrawAllAsync(destination, ct: ct);
        TestContext.Out.WriteLine($"[{pair.SenderLabel}] withdrawAll: txid {result.Txid} sent {result.SentSats} payout {result.PayoutSats} fee {result.FeeSats}");
        Assert.That(result.Txid, Has.Length.EqualTo(64));
        Assert.That(result.SentSats, Is.EqualTo(quote.SpendableSats));
        Assert.That(result.PayoutSats, Is.GreaterThanOrEqualTo(quote.SpendableSats - quote.QuotedFeeSats));
        Assert.That(result.PayoutSats, Is.LessThan(result.SentSats));
        Assert.That(result.FrozenSats, Is.EqualTo(quote.FrozenSats));

        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        var after = (await pair.Sender.GetBalanceAsync(ct)).SatsBalance;
        Assert.That(after.Available, Is.Zero);
        Assert.That(after.Frozen, Is.EqualTo(result.FrozenSats));
    }

    private static async Task<string> DestinationAsync(Pair pair, CancellationToken ct)
    {
        var configured = TestSecrets.TryGet("NSPARK_TEST_WITHDRAW_DESTINATION");
        if (configured is null or "receiver-static-deposit")
        {
            return (await pair.Receiver.GetStaticDepositAddressAsync(ct)).Address;
        }

        return configured;
    }

    /// <summary>
    /// Claims confirmed UTXOs at a wallet's static deposit address back into Spark. Opt-in
    /// (<c>NSPARK_TEST_CLAIM_STATIC=A|B</c>) because the SSP charges a fee for the claim.
    /// </summary>
    [Test, Explicit("Opt-in: NSPARK_TEST_CLAIM_STATIC=A|B; the SSP charges a fee")]
    public async Task Confirmed_static_deposits_are_claimed_back_into_the_wallet_for_the_checked_quote()
    {
        var which = TestSecrets.TryGet("NSPARK_TEST_CLAIM_STATIC");
        if (which is not ("A" or "B"))
        {
            Assert.Inconclusive("Set NSPARK_TEST_CLAIM_STATIC=A or B to claim that wallet's static deposits.");
            return;
        }

        using var cts = Timeout(TimeSpan.FromMinutes(10));
        var ct = cts.Token;
        var wallet = which == "A" ? _walletA : _walletB;
        var address = (await wallet.GetStaticDepositAddressAsync(ct)).Address;
        var utxos = await wallet.GetUtxosForDepositAddressAsync(address, ct: ct);
        TestContext.Out.WriteLine($"[{which}] static deposit address {address}: {utxos.Count} unclaimed utxo(s)");
        if (utxos.Count == 0)
        {
            Assert.Inconclusive($"No unclaimed utxo at {address} yet (unconfirmed, or already claimed).");
            return;
        }

        var before = (await wallet.GetBalanceAsync(ct)).SatsBalance;
        foreach (var utxo in utxos)
        {
            var quote = await wallet.GetDepositFeeEstimateAsync(utxo.Txid, utxo.Vout, ct);
            TestContext.Out.WriteLine($"  {utxo.Txid}:{utxo.Vout} credits {quote.CreditAmountSats} sats after the SSP fee");
            var transferId = await wallet.ClaimStaticDepositAsync(utxo.Txid, quote, utxo.Vout, ct);
            Assert.That(transferId, Is.Not.Empty);
        }

        var after = await ClaimUntilAsync(wallet, before.Owned + 1, ct);
        TestContext.Out.WriteLine($"  balance {before.Owned} -> {after.Owned}");
        Assert.That(after.Owned, Is.GreaterThan(before.Owned));
    }

    /// <summary>
    /// Refunds an unclaimed static deposit of either test wallet to that wallet's own static deposit
    /// address, checks the operators' co-signature against the deposit output's key, and broadcasts
    /// it. Opt-in (<c>NSPARK_TEST_ALLOW_REFUND=1</c>): it needs a confirmed unclaimed deposit and
    /// pays the on-chain fee; the new output can be claimed later.
    /// </summary>
    [Test, Explicit("Destructive: broadcasts an on-chain refund; set NSPARK_TEST_ALLOW_REFUND=1")]
    public async Task An_unclaimed_static_deposit_is_refunded_to_the_wallets_own_static_address()
    {
        if (TestSecrets.TryGet("NSPARK_TEST_ALLOW_REFUND") != "1")
        {
            Assert.Inconclusive("Set NSPARK_TEST_ALLOW_REFUND=1 to refund a static deposit on-chain.");
            return;
        }

        using var cts = Timeout(TimeSpan.FromMinutes(10));
        var ct = cts.Token;
        foreach (var (label, wallet) in new[] { ("B", _walletB), ("A", _walletA) })
        {
            var address = (await wallet.GetStaticDepositAddressAsync(ct)).Address;
            var utxo = (await wallet.GetUtxosForDepositAddressAsync(address, ct: ct)).FirstOrDefault();
            if (utxo is null)
            {
                continue;
            }

            var outpoint = new DepositOutpoint(utxo.Txid, utxo.Vout);
            var txHex = await wallet.RefundStaticDepositAsync(utxo.Txid, address, 2, utxo.Vout, ct);
            var signed = RawTransaction.ParseHex(txHex, "refund");
            Assert.That(signed.Inputs, Has.Count.EqualTo(1));
            Assert.That(signed.Inputs[0].PreviousTxid, Is.EqualTo(outpoint.InternalOrderTxid));
            Assert.That(signed.Outputs.Single().ScriptPubKey, Is.EqualTo(CoopExitValidator.ScriptPubKeyFor(address, SparkNetwork.Mainnet).ToBytes()));

            // The aggregated signature must verify against the deposit output's key.
            var deposit = RawTransaction.Parse(await DepositService.FetchRawTransactionAsync(wallet, outpoint.Txid, ct));
            var prevout = deposit.OutputAt(outpoint.Vout);
            var unsigned = RawTransaction.ParseHex(txHex, "refund");
            unsigned.Inputs[0].Witness = [];
            unsigned.HasWitnessSerialization = false;
            var sighash = SparkTxBuilder.ComputeMultiInputSighash(
                unsigned.Serialize(includeWitness: false), 0, [prevout.ScriptPubKey], [prevout.Value]);
            var outputKey = new TaprootPubKey(prevout.ScriptPubKey[2..]);
            Assert.That(
                outputKey.VerifySignature(new uint256(sighash), NBitcoin.Crypto.SchnorrSignature.TryParse(signed.Inputs[0].Witness[0], out var schnorr) ? schnorr : null!),
                Is.True);

            var txid = await wallet.BroadcastTransactionAsync(txHex, ct);
            TestContext.Out.WriteLine($"[{label}] refunded {outpoint.Txid}:{outpoint.Vout} ({prevout.Value} sats) to its static address: {txid}");
            return;
        }

        Assert.Inconclusive("Neither test wallet has an unclaimed static deposit to refund.");
    }

    // ── Lightning sends ──

    [Test, Explicit(MovesSats)]
    public async Task A_Lightning_payment_of_a_verified_invoice_under_a_fee_cap_is_claimed()
    {
        using var cts = Timeout(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        var pair = await MakePairAsync(ct);
        const long amount = 10;
        var invoice = await pair.Receiver.CreateLightningInvoiceAsync(amount, "hardening test", ct: ct);
        Assert.That(invoice.AmountSats, Is.EqualTo(amount));
        var decoded = Bolt11Invoice.Decode(invoice.PaymentRequest);
        Assert.That(decoded.PaymentHashHex, Is.EqualTo(invoice.PaymentHash));
        Assert.That(decoded.AmountMsat, Is.EqualTo((ulong)amount * 1000));
        Assert.That(decoded.Network, Is.EqualTo(Bolt11Network.Mainnet));

        var fee = await pair.Sender.GetLightningSendFeeEstimateAsync(invoice.PaymentRequest, ct: ct);
        TestContext.Out.WriteLine($"[{pair.SenderLabel}] fee estimate {fee} sats for {amount} sats");
        var maxFee = Math.Max(fee, 1) + 5;
        RequireSpendable(pair, amount + maxFee + 5);
        await pair.Receiver.ClaimPendingTransfersAsync(ct);
        var receiverBefore = (await pair.Receiver.GetBalanceAsync(ct)).SatsBalance;
        var senderBefore = (await pair.Sender.GetBalanceAsync(ct)).SatsBalance;

        var requestId = await pair.Sender.PayLightningInvoiceAsync(invoice.PaymentRequest, maxFee, ct: ct);
        Assert.That(requestId, Is.Not.Empty);
        TestContext.Out.WriteLine($"lightning send request {requestId}");

        var receiverAfter = await ClaimUntilAsync(pair.Receiver, receiverBefore.Owned + amount, ct);
        var senderAfter = (await pair.Sender.GetBalanceAsync(ct)).SatsBalance;
        Assert.That(receiverAfter.Owned, Is.EqualTo(receiverBefore.Owned + amount));
        Assert.That(senderAfter.Owned, Is.LessThanOrEqualTo(senderBefore.Owned - amount));
        Assert.That(senderAfter.Owned, Is.GreaterThanOrEqualTo(senderBefore.Owned - amount - maxFee));
        TestContext.Out.WriteLine($"receiver {receiverBefore.Owned} -> {receiverAfter.Owned}, sender {senderBefore.Owned} -> {senderAfter.Owned}");
    }

    [Test, Explicit(MovesSats)]
    public async Task An_amountless_Lightning_invoice_is_paid_with_the_callers_amount()
    {
        using var cts = Timeout(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        var pair = await MakePairAsync(ct);
        const long amount = 12;
        var invoice = await pair.Receiver.CreateLightningInvoiceAsync(0, "amountless invoice test", ct: ct);
        Assert.That(Bolt11Invoice.Decode(invoice.PaymentRequest).AmountMsat, Is.Null);
        var fee = await pair.Sender.GetLightningSendFeeEstimateAsync(invoice.PaymentRequest, amount, ct);
        var maxFee = Math.Max(fee, 1) + 5;
        RequireSpendable(pair, amount + maxFee + 5);
        await pair.Receiver.ClaimPendingTransfersAsync(ct);
        var receiverBefore = (await pair.Receiver.GetBalanceAsync(ct)).SatsBalance;

        var requestId = await pair.Sender.PayLightningInvoiceAsync(invoice.PaymentRequest, maxFee, amountSats: amount, ct: ct);
        TestContext.Out.WriteLine($"[{pair.SenderLabel}] paid an amountless invoice with {amount} sats (fee estimate {fee}): {requestId}");

        var receiverAfter = await ClaimUntilAsync(pair.Receiver, receiverBefore.Owned + amount, ct);
        Assert.That(receiverAfter.Owned, Is.EqualTo(receiverBefore.Owned + amount));
    }

    [Test]
    public async Task A_fee_cap_below_the_SSP_estimate_is_refused_before_any_leaf_is_locked()
    {
        using var cts = Timeout(TimeSpan.FromMinutes(3));
        var ct = cts.Token;
        var pair = await MakePairAsync(ct);
        var invoice = await pair.Receiver.CreateLightningInvoiceAsync(10, "fee cap test", ct: ct);
        var fee = await pair.Sender.GetLightningSendFeeEstimateAsync(invoice.PaymentRequest, ct: ct);
        if (fee < 1)
        {
            Assert.Inconclusive("The SSP quotes no fee for this invoice, so no cap can fall below it.");
            return;
        }

        var before = (await pair.Sender.GetBalanceAsync(ct)).SatsBalance;
        var refusal = Assert.ThrowsAsync<FeeExceedsLimitException>(() => pair.Sender.PayLightningInvoiceAsync(invoice.PaymentRequest, fee - 1, ct: ct));
        Assert.That(refusal!.MaxFeeSats, Is.EqualTo(fee - 1));
        Assert.That(refusal.FeeSats, Is.GreaterThanOrEqualTo(fee));

        var after = (await pair.Sender.GetBalanceAsync(ct)).SatsBalance;
        Assert.That(after.Owned, Is.EqualTo(before.Owned));
        Assert.That(after.Available, Is.EqualTo(before.Available));
    }

    [Test, Explicit(MovesSats)]
    public async Task An_invoice_pasted_in_upper_case_with_surrounding_whitespace_is_paid_as_validated()
    {
        using var cts = Timeout(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        var pair = await MakePairAsync(ct);
        const long amount = 10;
        var invoice = await pair.Receiver.CreateLightningInvoiceAsync(amount, "pasted invoice test", ct: ct);
        var pasted = $"  {invoice.PaymentRequest.ToUpperInvariant()}\n";
        var maxFee = Math.Max(await pair.Sender.GetLightningSendFeeEstimateAsync(invoice.PaymentRequest, ct: ct), 1) + 5;
        RequireSpendable(pair, amount + maxFee + 5);
        await pair.Receiver.ClaimPendingTransfersAsync(ct);
        var receiverBefore = (await pair.Receiver.GetBalanceAsync(ct)).SatsBalance;

        var requestId = await pair.Sender.PayLightningInvoiceAsync(pasted, maxFee, ct: ct);
        TestContext.Out.WriteLine($"[{pair.SenderLabel}] paid a pasted invoice: {requestId}");

        var receiverAfter = await ClaimUntilAsync(pair.Receiver, receiverBefore.Owned + amount, ct);
        Assert.That(receiverAfter.Owned, Is.EqualTo(receiverBefore.Owned + amount));
    }

    [Test, Explicit(MovesSats)]
    public async Task An_interrupted_Lightning_send_resumes_from_the_transfer_the_coordinator_holds_and_pays_once()
    {
        using var cts = Timeout(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        var pair = await MakePairAsync(ct);
        const long amount = 10;
        var invoice = await pair.Receiver.CreateLightningInvoiceAsync(amount, "resume test", ct: ct);
        var maxFee = Math.Max(await pair.Sender.GetLightningSendFeeEstimateAsync(invoice.PaymentRequest, ct: ct), 1) + 5;
        RequireSpendable(pair, (2 * (amount + maxFee)) + 5);
        await pair.Receiver.ClaimPendingTransfersAsync(ct);
        var receiverBefore = (await pair.Receiver.GetBalanceAsync(ct)).SatsBalance;
        var payment = new LightningPayment(invoice.PaymentRequest, maxFee, null, null, SparkNetwork.Mainnet);
        var transferId = Guid.NewGuid().ToString("D");

        // The first attempt ends after the swap, as when the SSP request fails: the coordinator
        // holds the leaves under the transfer id.
        var held = await pair.Sender.StartLightningSendAsync(payment, transferId, ct);
        Assert.That(held.Id, Is.EqualTo(transferId));
        Assert.That(held.TotalValue, Is.GreaterThanOrEqualTo((ulong)amount));
        // The same swap again: the coordinator answers with the transfer it holds and locks nothing more.
        var availableBefore = (await pair.Sender.GetBalanceAsync(ct)).SatsBalance.Available;
        var repeated = await pair.Sender.StartLightningSendAsync(payment, transferId, ct);
        Assert.That(repeated.Id, Is.EqualTo(transferId));
        Assert.That(repeated.Leaves.Select(l => l.Leaf.Id), Is.EquivalentTo(held.Leaves.Select(l => l.Leaf.Id)));
        Assert.That((await pair.Sender.GetBalanceAsync(ct)).SatsBalance.Available, Is.EqualTo(availableBefore));

        // Resuming selects no leaf: the SSP pays from the held transfer.
        var requestId = await pair.Sender.PayLightningInvoiceAsync(invoice.PaymentRequest, maxFee, transferId: transferId, ct: ct);
        Assert.That((await pair.Sender.GetBalanceAsync(ct)).SatsBalance.Available, Is.EqualTo(availableBefore));
        // Paying again under the same id pays nothing twice: the SSP answers with its request.
        var again = await pair.Sender.PayLightningInvoiceAsync(invoice.PaymentRequest, maxFee, transferId: transferId, ct: ct);
        Assert.That(again, Is.EqualTo(requestId));
        // Another invoice cannot be paid from that transfer.
        var other = await pair.Receiver.CreateLightningInvoiceAsync(amount, "resume test, other invoice", ct: ct);
        Assert.That(
            () => pair.Sender.PayLightningInvoiceAsync(other.PaymentRequest, maxFee, transferId: transferId, ct: ct),
            Throws.InstanceOf<ArgumentException>());
        TestContext.Out.WriteLine($"[{pair.SenderLabel}] resumed transfer {transferId} ({held.TotalValue} sats) as {requestId}");

        var receiverAfter = await ClaimUntilAsync(pair.Receiver, receiverBefore.Owned + amount, ct);
        Assert.That(receiverAfter.Owned, Is.EqualTo(receiverBefore.Owned + amount));
    }

    // ── Events ──

    /// <summary>Collects a wallet's events in the background until the returned source is cancelled.</summary>
    private static (List<SparkEvent> Log, CancellationTokenSource Stop, Task Reader) Record(SparkWallet wallet, CancellationToken ct)
    {
        var log = new List<SparkEvent>();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var sparkEvent in wallet.SubscribeEventsAsync(stop.Token))
                {
                    lock (log)
                    {
                        log.Add(sparkEvent);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
        });
        return (log, stop, reader);
    }

    private static bool Contains(List<SparkEvent> log, Func<SparkEvent, bool> predicate)
    {
        lock (log)
        {
            return log.Any(predicate);
        }
    }

    private static async Task<bool> EventuallyAsync(TimeSpan within, Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(500, ct);
        }

        return condition();
    }

    [Test]
    public async Task The_operators_heartbeats_keep_a_subscription_alive_and_one_silent_past_the_timeout_is_resubscribed()
    {
        using var cts = Timeout(TimeSpan.FromMinutes(3));
        var ct = cts.Token;

        async Task<List<string>> ReconnectsAsync(TimeSpan timeout, TimeSpan listen)
        {
            var reasons = new List<string>();
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(listen);
            try
            {
                await foreach (var sparkEvent in EventService.SubscribeEventsAsync(_walletB, timeout, window.Token))
                {
                    if (sparkEvent is ReconnectingEvent reconnecting)
                    {
                        reasons.Add(reconnecting.Reason);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The window closed.
            }

            return reasons;
        }

        // Heartbeats every 5 s: an 8 s allowance is never exceeded.
        Assert.That(await ReconnectsAsync(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(30)), Is.Empty);
        // A 3 s allowance is exceeded between two heartbeats.
        var tight = await ReconnectsAsync(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20));
        TestContext.Out.WriteLine($"with a 3 s allowance the stream was resubscribed {tight.Count} time(s)");
        Assert.That(tight.Any(r => r.Contains("heartbeat", StringComparison.Ordinal)), Is.True);
    }

    [Test, Explicit(MovesSats)]
    public async Task A_payment_is_reported_to_its_receiver_and_a_swaps_counter_transfer_is_not_reported_as_received()
    {
        using var cts = Timeout(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        var pair = await MakePairAsync(ct);
        RequireSpendable(pair, 15);
        var smallest = (await pair.Sender.GetSpendableLeavesAsync(ct)).MinBy(l => l.ValueSats)!;
        var sender = Record(pair.Sender, ct);
        var receiver = Record(pair.Receiver, ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        // A swap of the sender's own: the SSP's counter-transfer reaches the sender.
        await pair.Sender.RequestLeavesSwapAsync([smallest.ValueSats], ct);
        // A payment to the receiver.
        var transfer = await pair.Sender.SendAsync(pair.Receiver.GetSparkAddress(), 10, ct);
        TestContext.Out.WriteLine($"[{pair.SenderLabel}] swapped a {smallest.ValueSats}-sat leaf and sent 10 sats, transfer {transfer.Id}");
        await Task.Delay(TimeSpan.FromSeconds(8), ct);
        await sender.Stop.CancelAsync();
        await receiver.Stop.CancelAsync();
        await Task.WhenAll(sender.Reader, receiver.Reader);

        Assert.That(Contains(receiver.Log, e => e is TransferReceivedEvent received && received.Transfer.Id == transfer.Id), Is.True);
        // The swap's counter-transfer is not a payment.
        Assert.That(Contains(sender.Log, e => e is TransferReceivedEvent), Is.False);
        Assert.That(Contains(sender.Log, e => e is TransferSentEvent sent && sent.Transfer.Id == transfer.Id), Is.True);
        TestContext.Out.WriteLine($"sender events: {sender.Log.Count}, receiver events: {receiver.Log.Count}");
        await pair.Receiver.ClaimPendingTransfersAsync(ct);
    }

    [Test, Explicit(MovesSats)]
    public async Task The_event_stream_claims_a_payment_that_arrived_while_it_was_down_and_a_payment_as_it_arrives()
    {
        using var cts = Timeout(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        var pair = await MakePairAsync(ct);
        RequireSpendable(pair, 25);
        await pair.Receiver.ClaimPendingTransfersAsync(ct);
        var before = (await pair.Receiver.GetBalanceAsync(ct)).SatsBalance;
        var address = pair.Receiver.GetSparkAddress();
        static Func<SparkEvent, bool> Received(string id) => e => e is TransferReceivedEvent received && received.Transfer.Id == id;

        // Sent while the receiver has no stream: claimed and reported when the stream connects.
        var whileDown = await pair.Sender.SendAsync(address, 10, ct);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        var stream = Record(pair.Receiver, ct);
        Assert.That(await EventuallyAsync(TimeSpan.FromSeconds(60), () => Contains(stream.Log, Received(whileDown.Id)), ct), Is.True);

        // Sent while it is connected: claimed on arrival, then reported.
        var whileUp = await pair.Sender.SendAsync(address, 11, ct);
        Assert.That(await EventuallyAsync(TimeSpan.FromSeconds(60), () => Contains(stream.Log, Received(whileUp.Id)), ct), Is.True);
        await stream.Stop.CancelAsync();
        await stream.Reader;
        Assert.That(Contains(stream.Log, e => e is ConnectedEvent), Is.True);

        // Nobody else claimed them.
        Assert.That((await pair.Receiver.GetTransferAsync(whileDown.Id, ct))!.Status, Is.EqualTo(nameof(TransferStatus.Completed)));
        Assert.That((await pair.Receiver.GetTransferAsync(whileUp.Id, ct))!.Status, Is.EqualTo(nameof(TransferStatus.Completed)));
        var after = (await pair.Receiver.GetBalanceAsync(ct)).SatsBalance;
        Assert.That(after.Owned, Is.EqualTo(before.Owned + 21));
        TestContext.Out.WriteLine($"[{pair.SenderLabel}] the receiver's stream claimed {whileDown.Id} on connection and {whileUp.Id} on arrival");
    }
}

// =============================================================================
// Transport (matching Swift: TransportHardeningTests' live checks)
// =============================================================================

[TestFixture]
[Category("Integration")]
public class TransportIntegrationTests
{
    [Test]
    public async Task On_mainnet_the_operators_answers_set_the_clock_close_to_this_machines()
    {
        using var client = new SparkConnection(Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet }), new HttpClient());
        var wallet = await client.CreateWalletAsync(TestSecrets.MnemonicB);

        await wallet.GetLeavesAsync();

        Assert.That(wallet.Clock.IsSynced, Is.True);
        var skew = wallet.Clock.ServerNow - DateTimeOffset.UtcNow;
        TestContext.Out.WriteLine($"operators' clock is {skew.TotalSeconds:0.00} s from this machine's");
        Assert.That(Math.Abs(skew.TotalSeconds), Is.LessThan(10));
    }

    [Test]
    public async Task A_new_regtest_wallet_on_the_default_preset_authenticates_and_reads_its_leaves()
    {
        _ = TestSecrets.MnemonicA; // live tests only
        var options = new SparkOptions
        {
            Network = SparkNetwork.Regtest,
            SigningOperators = SparkOptions.GetDefaultOperators(SparkNetwork.Regtest),
        };
        using var client = new SparkConnection(Options.Create(options), new HttpClient());
        // A throwaway account: random seed.
        var wallet = await client.CreateWalletAsync(new NSpark.Signer.SparkSigner(NSpark.Signer.KeyDerivation.FromSeed(RandomNumberGenerator.GetBytes(64))));

        Assert.That(await wallet.GetLeavesAsync(), Is.Empty);
        Assert.That(wallet.Clock.IsSynced, Is.True);
    }

    [Test]
    public async Task A_mainnet_transaction_fetched_by_an_upper_case_txid_hashes_to_it()
    {
        // The first bitcoin transaction between people: Satoshi to Hal Finney, block 170.
        const string txid = "f4184fc596403b9d638783cf57adfe4c75c605f6356fbc91338530e9831e9e16";
        using var client = new SparkConnection(Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet }), new HttpClient());
        var wallet = await client.CreateWalletAsync(TestSecrets.MnemonicA);

        var raw = await DepositService.FetchRawTransactionAsync(wallet, txid.ToUpperInvariant(), CancellationToken.None);

        Assert.That(RawTransaction.Parse(raw).TxidHex, Is.EqualTo(txid));
    }

    [Test]
    public async Task A_past_deposit_to_a_test_wallets_static_address_is_found_at_the_output_the_operators_report()
    {
        using var client = new SparkConnection(Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet }), new HttpClient());
        foreach (var mnemonic in new[] { TestSecrets.MnemonicB, TestSecrets.MnemonicA })
        {
            var wallet = await client.CreateWalletAsync(mnemonic);
            var address = (await wallet.GetStaticDepositAddressAsync()).Address;
            var utxo = (await wallet.GetUtxosForDepositAddressAsync(address, excludeClaimed: false)).FirstOrDefault();
            if (utxo is null)
            {
                continue;
            }

            Assert.That(await wallet.StaticDepositVoutAsync(utxo.Txid, null, null, CancellationToken.None), Is.EqualTo(utxo.Vout));
            TestContext.Out.WriteLine($"static deposit {utxo.Txid}:{utxo.Vout} found by its address");
            return;
        }

        Assert.Inconclusive("Neither test wallet has ever received a static deposit.");
    }

    [Test]
    public async Task Deposit_addresses_from_the_operators_carry_valid_proofs()
    {
        using var client = new SparkConnection(Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet }), new HttpClient());
        var wallet = await client.CreateWalletAsync(TestSecrets.MnemonicA);

        // Both are verified before they are returned: proof of possession, operator signatures,
        // and the address paying the verifying key.
        var single = await wallet.GetDepositAddressAsync();
        var reusable = await wallet.GetStaticDepositAddressAsync();

        Assert.That(single.Address, Does.StartWith("bc1p"));
        Assert.That(reusable.Address, Does.StartWith("bc1p"));
        Assert.That(RenewalService.LeafNodeAddress(single.VerifyingKey, SparkNetwork.Mainnet), Is.EqualTo(single.Address));
        Assert.That(RenewalService.LeafNodeAddress(reusable.VerifyingKey, SparkNetwork.Mainnet), Is.EqualTo(reusable.Address));
    }
}
