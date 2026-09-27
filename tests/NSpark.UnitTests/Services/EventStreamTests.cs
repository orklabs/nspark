using Google.Protobuf;
using NSpark.Models;
using NSpark.Proto;
using NSpark.Services;
using NSpark.UnitTests.TestSupport;
using ConnectedEvent = NSpark.Models.ConnectedEvent;

namespace NSpark.UnitTests.Services;

/// <summary>
/// How operator stream messages become <see cref="SparkEvent"/>s, following the reference SDK's
/// <c>handleStreamEvent</c>.
/// </summary>
[TestFixture]
public class EventStreamTests
{
    private static readonly byte[] s_wallet = [0x02, .. Enumerable.Repeat((byte)0x11, 32)];
    private static readonly byte[] s_other = [0x03, .. Enumerable.Repeat((byte)0x22, 32)];

    private static SubscribeToEventsResponse TransferMessage(bool receiver, TransferType type, byte[]? from = null, byte[]? to = null)
    {
        var transferEvent = new TransferEvent
        {
            Transfer = new Transfer
            {
                Id = "0199a8f0-0000-7000-8000-000000000001",
                Type = type,
                Status = TransferStatus.SenderKeyTweaked,
                SenderIdentityPublicKey = ByteString.CopyFrom(from ?? s_other),
                ReceiverIdentityPublicKey = ByteString.CopyFrom(to ?? s_wallet),
                TotalValue = 10,
            },
        };
        return receiver
            ? new SubscribeToEventsResponse { ReceiverTransfer = transferEvent }
            : new SubscribeToEventsResponse { SenderTransfer = transferEvent };
    }

    private static SubscribeToEventsResponse DepositMessage(string status) => new()
    {
        Deposit = new DepositEvent { Deposit = new TreeNode { TreeId = "tree", Status = status } },
    };

    [Test]
    public void A_payment_to_the_wallet_is_reported_but_not_its_own_swaps_counter_transfers_or_self_transfers()
    {
        foreach (var type in new[] { TransferType.Transfer, TransferType.PreimageSwap, TransferType.UtxoSwap })
        {
            EventService.MapEvent(TransferMessage(receiver: true, type)).Should().BeOfType<TransferReceivedEvent>()
                .Which.Transfer.TotalValueSats.Should().Be(10, "a {0} is a payment", type);
        }

        foreach (var type in new[] { TransferType.CounterSwap, TransferType.CounterSwapV3 })
        {
            EventService.MapEvent(TransferMessage(receiver: true, type)).Should().BeNull();
        }

        EventService.MapEvent(TransferMessage(receiver: true, TransferType.Transfer, from: s_wallet, to: s_wallet)).Should().BeNull();
    }

    [Test]
    public void Outgoing_transfers_are_reported_with_their_status_swaps_included()
    {
        foreach (var type in new[] { TransferType.Transfer, TransferType.PrimarySwapV3 })
        {
            EventService.MapEvent(TransferMessage(receiver: false, type, from: s_wallet, to: s_other))
                .Should().BeOfType<TransferSentEvent>()
                .Which.Transfer.Status.Should().Be(nameof(TransferStatus.SenderKeyTweaked));
        }
    }

    [Test]
    public void A_deposit_is_reported_once_its_leaf_is_available_and_connection_events_pass_through()
    {
        EventService.MapEvent(DepositMessage("AVAILABLE")).Should().BeOfType<DepositConfirmedEvent>()
            .Which.TreeId.Should().Be("tree");
        EventService.MapEvent(DepositMessage("CREATING")).Should().BeNull();
        EventService.MapEvent(new SubscribeToEventsResponse { Connected = new NSpark.Proto.ConnectedEvent() })
            .Should().BeOfType<ConnectedEvent>();
        EventService.MapEvent(new SubscribeToEventsResponse()).Should().BeNull();
    }
}

/// <summary>
/// The event stream's connection handling against the operator stand-in, whose subscription sends
/// <c>connected</c> and then ends (or stays silent).
/// </summary>
[TestFixture]
public class EventStreamConnectionTests
{
    /// <summary>Events up to and including the <paramref name="count"/>-th connection; stops iterating there.</summary>
    internal static async Task<List<SparkEvent>> EventsUntilConnectionAsync(IAsyncEnumerable<SparkEvent> stream, int count)
    {
        var events = new List<SparkEvent>();
        var connections = 0;
        await foreach (var sparkEvent in stream)
        {
            events.Add(sparkEvent);
            if (sparkEvent is ConnectedEvent && ++connections == count)
            {
                break;
            }
        }

        return events;
    }

    /// <summary>Every event the stream yields within <paramref name="duration"/>.</summary>
    private static async Task<List<SparkEvent>> EventsForAsync(IAsyncEnumerable<SparkEvent> stream, TimeSpan duration)
    {
        var events = new List<SparkEvent>();
        using var stop = new CancellationTokenSource(duration);
        try
        {
            await foreach (var sparkEvent in stream.WithCancellation(stop.Token))
            {
                lock (events)
                {
                    events.Add(sparkEvent);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }

        return events;
    }

    [Test]
    public void Attempts_back_off_from_1_s_doubling_to_15_s_as_the_reference_SDKs_stream_does()
    {
        Enumerable.Range(0, 8).Select(EventService.Backoff)
            .Should().Equal(new[] { 1, 1, 2, 4, 8, 15, 15, 15 }.Select(s => TimeSpan.FromSeconds(s)));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task An_ended_subscription_is_resumed_and_every_connection_claims_the_pending_transfers(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();

        var events = await FakeOperator.RunAsync(state, wallet => EventsUntilConnectionAsync(wallet.SubscribeEventsAsync(ct), 2));

        events.Should().HaveCount(3);
        events[0].Should().BeOfType<ConnectedEvent>();
        var reconnecting = events[1].Should().BeOfType<ReconnectingEvent>().Subject;
        reconnecting.Attempt.Should().Be(1);
        reconnecting.RetryIn.Should().Be(TimeSpan.FromSeconds(1));
        reconnecting.Reason.Should().Contain("ended");
        events[2].Should().BeOfType<ConnectedEvent>();
        state.Methods.Take(3).Should().Equal("subscribe_to_events", "query_pending_transfers", "subscribe_to_events");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Disposing_the_connection_ends_its_event_streams_and_refuses_new_ones(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        state.SubscriptionMode = FakeOperatorState.Subscription.Silence;
        var connection = FakeOperator.Connect(state);
        var wallet = await connection.CreateWalletAsync(FakeOperator.Mnemonic, account: 0, ct: ct);

        await using var events = wallet.SubscribeEventsAsync(ct).GetAsyncEnumerator(ct);
        (await events.MoveNextAsync()).Should().BeTrue();
        events.Current.Should().BeOfType<ConnectedEvent>();

        connection.Dispose();
        while (await events.MoveNextAsync())
        {
            // Drained: the stream ends with the connection.
        }

        var subscribe = () => wallet.SubscribeEventsAsync(ct);
        subscribe.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_subscription_that_goes_silent_after_heartbeats_is_dropped_and_resubscribed(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        state.SubscriptionMode = FakeOperatorState.Subscription.HeartbeatThenSilence;

        var events = await FakeOperator.RunAsync(
            state,
            wallet => EventsUntilConnectionAsync(EventService.SubscribeEventsAsync(wallet, TimeSpan.FromMilliseconds(300), ct), 2));

        events.Should().HaveCount(3);
        events[0].Should().BeOfType<ConnectedEvent>();
        var reconnecting = events[1].Should().BeOfType<ReconnectingEvent>().Subject;
        reconnecting.Attempt.Should().Be(1);
        reconnecting.Reason.Should().Contain("heartbeat");
        events[2].Should().BeOfType<ConnectedEvent>();
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_quiet_subscription_that_never_sent_a_heartbeat_is_kept(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        state.SubscriptionMode = FakeOperatorState.Subscription.Silence;

        var events = await FakeOperator.RunAsync(
            state,
            wallet => EventsForAsync(EventService.SubscribeEventsAsync(wallet, TimeSpan.FromMilliseconds(200), ct), TimeSpan.FromSeconds(1)));

        events.Should().ContainSingle().Which.Should().BeOfType<ConnectedEvent>();
    }

    [Test]
    public async Task The_watchdog_arms_on_a_heartbeat_and_pauses_while_an_event_is_handled()
    {
        static async Task<bool> FiresAsync(EventStreamActivity activity, TimeSpan within)
        {
            using var window = new CancellationTokenSource(within);
            try
            {
                await activity.SilenceAsync(TimeSpan.FromMilliseconds(100), window.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        var unarmed = new EventStreamActivity();
        unarmed.Received(heartbeat: false);
        unarmed.Handled();
        (await FiresAsync(unarmed, TimeSpan.FromMilliseconds(400))).Should().BeFalse();

        var armed = new EventStreamActivity();
        armed.Received(heartbeat: true);
        armed.Handled();
        (await FiresAsync(armed, TimeSpan.FromSeconds(5))).Should().BeTrue();

        var busy = new EventStreamActivity();
        busy.Received(heartbeat: true);
        (await FiresAsync(busy, TimeSpan.FromMilliseconds(400))).Should().BeFalse();
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_pending_transfer_that_cannot_be_claimed_is_looked_at_on_connect_and_not_reported(CancellationToken ct)
    {
        // An expired transfer is not claimable: the pass looks at it and moves on, and nothing is
        // reported for it.
        var state = FakeOperatorState.Accepting();
        state.AddPending(new Transfer
        {
            Id = "0199a8f0-0000-7000-8000-00000000000e",
            Type = TransferType.Transfer,
            Status = TransferStatus.Expired,
        });

        // Until the second connection: the first connection's claim pass has finished by then.
        var events = await FakeOperator.RunAsync(state, wallet => EventsUntilConnectionAsync(wallet.SubscribeEventsAsync(ct), 2));

        events.Select(e => e.GetType()).Should().Equal(typeof(ConnectedEvent), typeof(ReconnectingEvent), typeof(ConnectedEvent));
        state.Methods.Take(2).Should().Equal("subscribe_to_events", "query_pending_transfers");
    }
}
