using System.Security.Cryptography;
using Google.Protobuf;
using NSpark.Exceptions;
using NSpark.Models;
using NSpark.Proto;
using NSpark.Services;
using NSpark.UnitTests.TestSupport;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace NSpark.UnitTests.Services;

/// <summary>Looking up the wallet's Lightning sends by payment hash or transfer id (<c>query_htlc</c> as the sender).</summary>
[TestFixture]
public class LightningSendLookupTests
{
    private const string PaidId = "0199a8f0-0000-7000-8000-0000000000a1";
    private const string OpenId = "0199a8f0-0000-7000-8000-0000000000a2";
    private const string ReturnedId = "0199a8f0-0000-7000-8000-0000000000a3";
    private const string ExpiredId = "0199a8f0-0000-7000-8000-0000000000a4";
    private static readonly DateTimeOffset s_created = new(2026, 10, 16, 23, 12, 0, TimeSpan.Zero);

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>An invoice's preimage and payment hash.</summary>
    private static (byte[] Preimage, byte[] PaymentHash) Invoice()
    {
        var preimage = RandomNumberGenerator.GetBytes(32);
        return (preimage, SHA256.HashData(preimage));
    }

    /// <summary>The wallet's send of an invoice, as <c>query_htlc</c> reports it in a given state.</summary>
    private static PreimageRequestWithTransfer Send(
        SparkWallet wallet,
        byte[] paymentHash,
        string transferId,
        PreimageRequestStatus status = PreimageRequestStatus.WaitingForPreimage,
        TransferStatus transferStatus = TransferStatus.SenderKeyTweakPending,
        byte[]? preimage = null)
    {
        var send = LightningResumeTests.HeldSend(
            wallet.IdentityPublicKey, wallet.Options.RequireSspIdentityPublicKey(), paymentHash, 250_700, transferId);
        send.Status = status;
        send.CreatedTime = Timestamp.FromDateTimeOffset(s_created);
        send.Transfer.Status = transferStatus;
        send.Transfer.CreatedTime = Timestamp.FromDateTimeOffset(s_created.AddSeconds(2));
        send.Transfer.ExpiryTime = Timestamp.FromDateTimeOffset(s_created.AddDays(16));
        if (preimage is not null)
        {
            send.Preimage = ByteString.CopyFrom(preimage);
        }
        return send;
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Sends_are_found_by_payment_hash_and_tell_paid_in_flight_and_returned_apart(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        var (preimage, paid) = Invoice();
        var (_, open) = Invoice();
        var (_, returned) = Invoice();
        var (_, never) = Invoice();

        var (sends, identity) = await FakeOperator.RunAsync(state, async wallet =>
        {
            state.Hold(Send(wallet, paid, PaidId, PreimageRequestStatus.PreimageShared, TransferStatus.Completed, preimage));
            state.Hold(Send(wallet, open, OpenId));
            state.Hold(Send(wallet, returned, ReturnedId, PreimageRequestStatus.Returned, TransferStatus.Returned));
            var found = await wallet.GetLightningSendsByPaymentHashAsync(
                [Hex(paid).ToUpperInvariant(), Hex(open), Hex(returned), Hex(never), Hex(paid)], ct);
            return (found, wallet.IdentityPublicKey);
        });

        // An invoice never sent to has no entry.
        sends.Should().HaveCount(3);
        var paidSend = sends.Single(s => s.PaymentHash == Hex(paid));
        paidSend.Should().Match<LightningSend>(s => s.IsPaid && !s.IsReturned && s.Preimage == Hex(preimage)
            && s.Status == "PreimageShared" && s.CreatedAt == s_created);
        paidSend.Transfer.Should().BeEquivalentTo(new
        {
            Id = PaidId,
            TotalValueSats = 250_700L,
            Status = "Completed",
            Type = "PreimageSwap",
            CreatedAt = s_created.AddSeconds(2),
            ExpiresAt = (DateTimeOffset?)s_created.AddDays(16),
        });
        sends.Single(s => s.PaymentHash == Hex(open)).Should().Match<LightningSend>(s => !s.IsPaid && !s.IsReturned && s.Preimage == null);
        sends.Single(s => s.PaymentHash == Hex(returned)).Should().Match<LightningSend>(s => s.IsReturned && !s.IsPaid);

        // One request, as the sender, for this wallet, with each distinct hash as raw bytes.
        var query = state.HtlcQueries.Should().ContainSingle().Which;
        query.MatchRole.Should().Be(PreimageRequestRole.Sender);
        query.IdentityPublicKey.ToByteArray().Should().Equal(identity);
        query.Limit.Should().Be(100);
        query.PaymentHashes.Select(h => Hex(h.ToByteArray())).Should().BeEquivalentTo([Hex(paid), Hex(open), Hex(returned), Hex(never)]);
        query.TransferIds.Should().BeEmpty();
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task An_expired_transfer_or_a_returned_HTLC_without_one_counts_as_returned(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        var (_, expired) = Invoice();
        var (_, bare) = Invoice();

        var sends = await FakeOperator.RunAsync(state, async wallet =>
        {
            state.Hold(Send(wallet, expired, ExpiredId, transferStatus: TransferStatus.Expired));
            var withoutTransfer = Send(wallet, bare, ReturnedId, PreimageRequestStatus.Returned);
            withoutTransfer.Transfer = null;
            state.Hold(withoutTransfer);
            return await wallet.GetLightningSendsByPaymentHashAsync([Hex(expired), Hex(bare)], ct);
        });

        sends.Single(s => s.PaymentHash == Hex(expired)).Should().Match<LightningSend>(s => s.IsReturned && !s.IsPaid);
        sends.Single(s => s.PaymentHash == Hex(bare)).Should().Match<LightningSend>(s => s.IsReturned && !s.IsPaid && s.Transfer == null);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Sends_are_found_by_transfer_id_to_tell_which_invoice_each_one_paid(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        var (_, first) = Invoice();
        var (_, second) = Invoice();
        const string unknownId = "0199a8f0-0000-7000-8000-0000000000ff";

        var sends = await FakeOperator.RunAsync(state, async wallet =>
        {
            state.Hold(Send(wallet, first, PaidId));
            state.Hold(Send(wallet, second, OpenId));
            return await wallet.GetLightningSendsByTransferIdAsync([PaidId.ToUpperInvariant(), unknownId, PaidId], ct);
        });

        sends.Should().ContainSingle().Which.Should().Match<LightningSend>(s => s.PaymentHash == Hex(first) && s.Transfer!.Id == PaidId);
        var query = state.HtlcQueries.Should().ContainSingle().Which;
        query.TransferIds.Should().Equal(PaidId, unknownId);
        query.PaymentHashes.Should().BeEmpty();
        query.MatchRole.Should().Be(PreimageRequestRole.Sender);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Every_page_is_read_and_more_than_a_thousand_hashes_go_out_in_several_requests(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        var (_, retried) = Invoice();
        var others = Enumerable.Range(0, 1_200).Select(_ => Hex(Invoice().PaymentHash)).ToList();

        var sends = await FakeOperator.RunAsync(state, async wallet =>
        {
            // 250 attempts at one invoice, every one returned: three pages.
            for (var i = 0; i < 250; i++)
            {
                state.Hold(Send(wallet, retried, $"0199a8f0-0000-7000-8000-{i:x12}", PreimageRequestStatus.Returned, TransferStatus.Returned));
            }
            return await wallet.GetLightningSendsByPaymentHashAsync([Hex(retried), .. others], ct);
        });

        sends.Should().HaveCount(250).And.OnlyContain(s => s.IsReturned);
        state.HtlcQueries.Select(q => (q.PaymentHashes.Count, q.Offset)).Should().Equal((1000, 0L), (1000, 100L), (1000, 200L), (201, 0L));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task An_answer_that_is_not_this_wallets_send_of_an_invoice_asked_about_is_refused(CancellationToken ct)
    {
        var (_, hash) = Invoice();

        async Task<string> RefusedAsync(Action<SparkWallet, FakeOperatorState> arrange)
        {
            var state = FakeOperatorState.Accepting();
            return await FakeOperator.RunAsync(state, async wallet =>
            {
                arrange(wallet, state);
                var act = () => wallet.GetLightningSendsByPaymentHashAsync([Hex(hash)], ct);
                return (await act.Should().ThrowAsync<SparkUntrustedResponseException>()).Which.Message;
            });
        }

        (await RefusedAsync((wallet, state) => state.Hold(Send(wallet, hash, PaidId, PreimageRequestStatus.PreimageShared,
            TransferStatus.Completed, RandomNumberGenerator.GetBytes(32))))).Should().Contain("does not hash to its payment hash");
        (await RefusedAsync((wallet, state) =>
        {
            var send = Send(wallet, hash, PaidId);
            send.SenderIdentityPubkey = ByteString.CopyFrom(new byte[33]);
            state.Hold(send);
        })).Should().Contain("not a Lightning send of this wallet");
        (await RefusedAsync((wallet, state) =>
        {
            var send = Send(wallet, hash, PaidId);
            send.Transfer.Type = TransferType.Transfer;
            state.Hold(send);
        })).Should().Contain("not a Lightning send of this wallet");
        (await RefusedAsync((wallet, state) =>
            state.TamperHtlc = (_, response) => response.PreimageRequests.Add(Send(wallet, Invoice().PaymentHash, OpenId))))
            .Should().Contain("not asked for");
        (await RefusedAsync((wallet, state) =>
        {
            state.Hold(Send(wallet, hash, PaidId));
            state.TamperHtlc = (request, response) => response.Offset = request.Offset;
        })).Should().Contain("offset");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Malformed_hashes_or_ids_are_refused_and_an_empty_list_asks_nothing(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();

        await FakeOperator.RunAsync(state, async wallet =>
        {
            (await wallet.GetLightningSendsByPaymentHashAsync([], ct)).Should().BeEmpty();
            (await wallet.GetLightningSendsByTransferIdAsync([], ct)).Should().BeEmpty();

            var tooShort = () => wallet.GetLightningSendsByPaymentHashAsync(["abcd"], ct);
            (await tooShort.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("paymentHashes");
            var notHex = () => wallet.GetLightningSendsByPaymentHashAsync([new string('z', 64)], ct);
            await notHex.Should().ThrowAsync<ArgumentException>();
            var notUuid = () => wallet.GetLightningSendsByTransferIdAsync(["not-a-uuid"], ct);
            (await notUuid.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("transferIds");
        });

        state.HtlcQueries.Should().BeEmpty();
    }
}
