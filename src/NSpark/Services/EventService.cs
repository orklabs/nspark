using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using NSpark.Models;
using NSpark.Proto;

namespace NSpark.Services;

/// <inheritdoc/>
public static class EventService
{
    /// <summary>
    /// How long a subscription that sends heartbeats (every 5 s from the operators) may stay
    /// silent before it is dropped and resubscribed — the reference SDK's
    /// <c>STREAM_HEARTBEAT_TIMEOUT_MS</c>.
    /// </summary>
    internal static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Events for this wallet, until the caller stops iterating, cancels <paramref name="ct"/>, or
    /// the <see cref="SparkConnection"/> is disposed; then the sequence ends. Like the reference
    /// SDK's background stream it stays up on its own:
    /// <list type="bullet">
    ///   <item><description>a subscription that fails, or that the operator ends, is retried
    ///   forever — 1 s doubling to 15 s between attempts — with a <see cref="ReconnectingEvent"/>
    ///   before each wait;</description></item>
    ///   <item><description>on every connection the wallet's pending transfers are claimed, so
    ///   payments that arrived while the stream was down are not left waiting, and each is reported
    ///   as a <see cref="TransferReceivedEvent"/>;</description></item>
    ///   <item><description>a payment that arrives while connected is claimed, then reported;</description></item>
    ///   <item><description>once the operator sends heartbeats, a subscription silent for 15 s — a
    ///   connection that died without closing, as after a network change — is dropped and
    ///   resubscribed.</description></item>
    /// </list>
    /// </summary>
    public static IAsyncEnumerable<SparkEvent> SubscribeEventsAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        ObjectDisposedException.ThrowIf(wallet.Client.Lifetime.IsCancellationRequested, wallet.Client);
        return SubscribeEventsAsync(wallet, HeartbeatTimeout, ct);
    }

    internal static async IAsyncEnumerable<SparkEvent> SubscribeEventsAsync(
        SparkWallet wallet,
        TimeSpan heartbeatTimeout,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, wallet.Client.Lifetime);
        var channel = Channel.CreateUnbounded<SparkEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        var producer = Task.Run(() => RunEventStreamAsync(wallet, channel.Writer, heartbeatTimeout, stop.Token), CancellationToken.None);
        try
        {
            while (true)
            {
                bool more;
                try
                {
                    more = await channel.Reader.WaitToReadAsync(stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    more = false;
                }

                if (!more)
                {
                    break;
                }

                while (channel.Reader.TryRead(out var sparkEvent))
                {
                    yield return sparkEvent;
                }
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The producer ends with the stream; nothing is left to report.
            }
        }
    }

    /// <summary>
    /// Wait before event-stream attempt <paramref name="attempt"/> + 1 after
    /// <paramref name="attempt"/> failures in a row: 1 s doubling to 15 s, the reference SDK's
    /// background-stream backoff.
    /// </summary>
    internal static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(15, 1 << Math.Min(Math.Max(attempt, 1) - 1, 4)));

    /// <summary>Subscribes, reports, and subscribes again — until <paramref name="ct"/> is cancelled.</summary>
    private static async Task RunEventStreamAsync(
        SparkWallet wallet,
        ChannelWriter<SparkEvent> writer,
        TimeSpan heartbeatTimeout,
        CancellationToken ct)
    {
        var attempt = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (connected, reason) = await SubscribeOnceAsync(wallet, writer, heartbeatTimeout, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                attempt = connected ? 1 : attempt + 1;
                var delay = Backoff(attempt);
                writer.TryWrite(new ReconnectingEvent(attempt, delay, reason));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        finally
        {
            writer.TryComplete();
        }
    }

    /// <summary>
    /// One subscription, reported until the operator ends it, it fails, or it goes silent after
    /// sending heartbeats. Returns whether it connected and why it ended.
    /// </summary>
    private static async Task<(bool Connected, string Reason)> SubscribeOnceAsync(
        SparkWallet wallet,
        ChannelWriter<SparkEvent> writer,
        TimeSpan heartbeatTimeout,
        CancellationToken ct)
    {
        var activity = new EventStreamActivity();
        using var subscription = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var client = wallet.GetCoordinatorClient();
            var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
            using var call = client.subscribe_to_events(
                new SubscribeToEventsRequest { IdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey) },
                headers,
                cancellationToken: subscription.Token);

            var reporting = ReportAsync(wallet, call.ResponseStream, writer, activity, subscription.Token);
            var silence = activity.SilenceAsync(heartbeatTimeout, subscription.Token);

            // The first to finish decides: the stream ended, failed or went silent.
            var first = await Task.WhenAny(reporting, silence).ConfigureAwait(false);
            await subscription.CancelAsync().ConfigureAwait(false);
            if (first == silence && silence.IsCompletedSuccessfully)
            {
                await Observe(reporting).ConfigureAwait(false);
                return (activity.Connected, $"no heartbeat for {heartbeatTimeout.TotalSeconds:0} s");
            }

            await Observe(silence).ConfigureAwait(false);
            await reporting.ConfigureAwait(false);
            return (activity.Connected, "the operator ended the event stream");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (activity.Connected, ex is RpcException rpc ? $"{rpc.StatusCode}: {rpc.Status.Detail}" : ex.Message);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            return (activity.Connected, "stopped");
        }
    }

    private static async Task Observe(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Already decided by the other task.
        }
    }

    /// <summary>Reports one subscription's messages until it ends.</summary>
    private static async Task ReportAsync(
        SparkWallet wallet,
        IAsyncStreamReader<SubscribeToEventsResponse> messages,
        ChannelWriter<SparkEvent> writer,
        EventStreamActivity activity,
        CancellationToken ct)
    {
        var claimedOnConnect = new HashSet<string>(StringComparer.Ordinal);
        while (await messages.MoveNext(ct).ConfigureAwait(false))
        {
            var message = messages.Current;
            activity.Received(heartbeat: message.EventCase == SubscribeToEventsResponse.EventOneofCase.Heartbeat);
            try
            {
                switch (message.EventCase)
                {
                    case SubscribeToEventsResponse.EventOneofCase.Connected:
                        activity.MarkConnected();
                        writer.TryWrite(new Models.ConnectedEvent());
                        claimedOnConnect = await ClaimPendingOnConnectAsync(wallet, writer, ct).ConfigureAwait(false);
                        break;
                    case SubscribeToEventsResponse.EventOneofCase.ReceiverTransfer:
                        var transfer = message.ReceiverTransfer.Transfer;
                        if (transfer is null || claimedOnConnect.Contains(transfer.Id))
                        {
                            break;
                        }

                        await ClaimOnArrivalAsync(wallet, transfer, ct).ConfigureAwait(false);
                        if (MapEvent(message) is { } received)
                        {
                            writer.TryWrite(received);
                        }

                        break;
                    default:
                        if (MapEvent(message) is { } mapped)
                        {
                            writer.TryWrite(mapped);
                        }

                        break;
                }
            }
            finally
            {
                activity.Handled();
            }
        }
    }

    /// <summary>
    /// Claims every pending transfer once the stream is up — payments that arrived while it was
    /// down included — and reports the claimed payments. Returns the ids claimed, so that their
    /// stream events, if the operators send those too, are not handled twice.
    /// </summary>
    private static async Task<HashSet<string>> ClaimPendingOnConnectAsync(
        SparkWallet wallet,
        ChannelWriter<SparkEvent> writer,
        CancellationToken ct)
    {
        PendingTransferDrain.Result claim;
        try
        {
            claim = await wallet.ClaimPendingTransfersCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        foreach (var transfer in claim.Claimed)
        {
            if (IsIncomingPayment(transfer))
            {
                writer.TryWrite(new TransferReceivedEvent(TransferMapping.ToModel(transfer)));
            }
        }

        return claim.Claimed.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Claims a payment that arrived on the stream before it is reported, as the reference SDK
    /// does. Best effort: one that cannot be claimed now stays pending for the next claim pass.
    /// </summary>
    private static async Task ClaimOnArrivalAsync(SparkWallet wallet, Transfer transfer, CancellationToken ct)
    {
        if (!IsIncomingPayment(transfer) || !PendingTransferDrain.ClaimableStatuses.Contains(transfer.Status))
        {
            return;
        }

        try
        {
            await ClaimService.ClaimTransferAsync(wallet, transfer, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return;
        }

        await wallet.RenewClaimedLeavesAsync(transfer.Leaves.Where(l => l.Leaf is not null).Select(l => l.Leaf.Id).ToList(), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The event an operator's stream message is reported as, if any. As in the reference SDK, a
    /// counter-transfer of the wallet's own swap and a self-transfer are not reported as received
    /// — the operation that made them claims them — and a deposit is reported once its leaf is
    /// available.
    /// </summary>
    internal static SparkEvent? MapEvent(SubscribeToEventsResponse response) =>
        response.EventCase switch
        {
            SubscribeToEventsResponse.EventOneofCase.Connected => new Models.ConnectedEvent(),
            SubscribeToEventsResponse.EventOneofCase.ReceiverTransfer
                when response.ReceiverTransfer.Transfer is { } transfer && IsIncomingPayment(transfer)
                => new TransferReceivedEvent(TransferMapping.ToModel(transfer)),
            SubscribeToEventsResponse.EventOneofCase.SenderTransfer
                when response.SenderTransfer.Transfer is { } transfer
                => new TransferSentEvent(TransferMapping.ToModel(transfer)),
            SubscribeToEventsResponse.EventOneofCase.Deposit
                when response.Deposit.Deposit is { Status: "AVAILABLE" } deposit
                => new DepositConfirmedEvent(deposit.TreeId),
            _ => null,
        };

    /// <summary>
    /// Whether a transfer to this wallet is a payment someone made to it, rather than the
    /// counter-transfer of one of its own swaps or a transfer to itself.
    /// </summary>
    internal static bool IsIncomingPayment(Transfer transfer) =>
        transfer.Type is not (TransferType.CounterSwap or TransferType.CounterSwapV3)
        && !transfer.SenderIdentityPublicKey.Span.SequenceEqual(transfer.ReceiverIdentityPublicKey.Span);
}

/// <summary>
/// One subscription's activity, for the heartbeat watchdog. As in the reference SDK the watchdog
/// arms on the first heartbeat — a coordinator that sends none never times out — and pauses while
/// an event is being handled, since handling one can claim a transfer.
/// </summary>
internal sealed class EventStreamActivity
{
    private readonly object _gate = new();
    private bool _connected;
    private bool _heartbeats;
    private bool _handling;
    private long _lastSeen = System.Diagnostics.Stopwatch.GetTimestamp();

    public bool Connected
    {
        get
        {
            lock (_gate)
            {
                return _connected;
            }
        }
    }

    public void MarkConnected()
    {
        lock (_gate)
        {
            _connected = true;
        }
    }

    /// <summary>A message arrived: the watchdog pauses until <see cref="Handled"/>; a heartbeat arms it.</summary>
    public void Received(bool heartbeat)
    {
        lock (_gate)
        {
            _handling = true;
            _heartbeats |= heartbeat;
        }
    }

    /// <summary>The message is handled: the silence starts counting again.</summary>
    public void Handled()
    {
        lock (_gate)
        {
            _handling = false;
            _lastSeen = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Completes once heartbeats are armed and the stream has been silent for
    /// <paramref name="timeout"/> outside event handling; otherwise waits until cancelled.
    /// </summary>
    public async Task SilenceAsync(TimeSpan timeout, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan? remaining;
            lock (_gate)
            {
                remaining = _heartbeats && !_handling
                    ? timeout - System.Diagnostics.Stopwatch.GetElapsedTime(_lastSeen)
                    : null;
            }

            if (remaining is { } left && left <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(remaining ?? timeout, ct).ConfigureAwait(false);
        }
    }
}
