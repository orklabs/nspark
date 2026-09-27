using System.Collections.Concurrent;
using System.Collections.Frozen;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Grpc.Net.Client.Configuration;
using NSpark.Exceptions;
using NSpark.Proto;
using NSpark.Proto.Authn;
using NSpark.Proto.Token;

namespace NSpark.Connection;

/// <summary>
/// Maintains one GrpcChannel per Signing Operator address. Channels are shared across all wallets.
/// HTTP/2 multiplexing means one channel per SO is sufficient.
/// </summary>
/// <remarks>
/// <para>
/// Every call goes through <see cref="OperatorTransportInterceptor"/> (a 60 s default deadline,
/// and the operators' clock from their answers) and the channel's retry policy
/// (<see cref="RetryPolicy"/>): UNAVAILABLE and CANCELLED are retried, which is how a pooled
/// connection the operator closed while idle heals. The authentication service and the event
/// subscription are not retried here: a retried <c>verify_challenge</c> re-sends a challenge the
/// operator may already have consumed, and the event stream reconnects by itself.
/// </para>
/// <para>
/// On mainnet an operator is only reached over TLS: an address that is not <c>https://</c> is
/// refused, since operator traffic carries session tokens and signing material.
/// </para>
/// </remarks>
internal sealed class GrpcConnectionPool : IDisposable
{
    /// <summary>
    /// Largest message sent or received: the reference SDK's 20 MB, raised from gRPC's 4 MiB
    /// default after <c>start_transfer_v2</c> answers of ~5 MB.
    /// </summary>
    internal const int MaxMessageBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Size cap for the dedicated recovery-query channels. query_nodes with
    /// include_parents returns every leaf's full ancestor chain, and long-lived
    /// wallets exceed smaller limits (seen live: 6.9 MB → ResourceExhausted).
    /// </summary>
    private const int LargeMessageMaxBytes = 128 * 1024 * 1024;

    internal const string AuthnService = "spark_authn.SparkAuthnService";
    internal const string SparkServiceName = "spark.SparkService";
    internal const string SubscribeToEventsMethod = "subscribe_to_events";

    /// <summary>
    /// The reference SDK's retry policy: up to 3 attempts, 1 s → 10 s exponential backoff, on
    /// UNAVAILABLE and CANCELLED. A deadline is deliberately not retryable.
    /// </summary>
    internal static readonly RetryPolicy RetryPolicy = new()
    {
        MaxAttempts = 3,
        InitialBackoff = TimeSpan.FromSeconds(1),
        MaxBackoff = TimeSpan.FromSeconds(10),
        BackoffMultiplier = 2,
        RetryableStatusCodes = { StatusCode.Unavailable, StatusCode.Cancelled },
    };

    private readonly FrozenDictionary<string, CallInvoker> _invokers;
    private readonly FrozenDictionary<string, GrpcChannel> _channels;
    private readonly ConcurrentDictionary<string, Lazy<(GrpcChannel Channel, CallInvoker Invoker)>> _largeMessage = new();
    private readonly ServerTimeSync _clock;
    private readonly OperatorTransportInterceptor _transportInterceptor;
    private readonly HttpMessageHandler? _httpHandler;

    /// <summary>Pool of TLS-or-plaintext channels; plaintext only when <paramref name="allowPlaintext"/>.</summary>
    public GrpcConnectionPool(IReadOnlyList<string> soAddresses, bool allowPlaintext = false, ServerTimeSync? clock = null)
        : this(soAddresses, allowPlaintext, clock, httpHandler: null)
    {
    }

    /// <summary>
    /// Pool whose channels send over <paramref name="httpHandler"/> instead of their own sockets —
    /// the seam unit tests use to stand in for operators with the real channel stack (service
    /// config, retries, deadlines, message limits) in place. The handler is not disposed with the
    /// pool.
    /// </summary>
    internal GrpcConnectionPool(
        IReadOnlyList<string> soAddresses,
        bool allowPlaintext,
        ServerTimeSync? clock,
        HttpMessageHandler? httpHandler)
    {
        ArgumentNullException.ThrowIfNull(soAddresses);
        _clock = clock ?? new ServerTimeSync();
        _transportInterceptor = new OperatorTransportInterceptor(_clock);
        _httpHandler = httpHandler;

        var channels = new Dictionary<string, GrpcChannel>(soAddresses.Count, StringComparer.Ordinal);
        var invokers = new Dictionary<string, CallInvoker>(soAddresses.Count, StringComparer.Ordinal);
        foreach (var address in soAddresses)
        {
            ValidateAddress(address, allowPlaintext);
            if (channels.ContainsKey(address))
            {
                continue;
            }

            var channel = GrpcChannel.ForAddress(address, CreateChannelOptions(MaxMessageBytes));
            channels[address] = channel;
            invokers[address] = channel.CreateCallInvoker().Intercept(_transportInterceptor);
        }

        _channels = channels.ToFrozenDictionary(StringComparer.Ordinal);
        _invokers = invokers.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Pool over the given call invokers, one per operator address — the seam unit tests use to
    /// stand in for operators. The transport interceptor is applied on top, as for real channels.
    /// </summary>
    internal GrpcConnectionPool(IReadOnlyDictionary<string, CallInvoker> invokers, ServerTimeSync? clock = null)
    {
        ArgumentNullException.ThrowIfNull(invokers);
        _clock = clock ?? new ServerTimeSync();
        _transportInterceptor = new OperatorTransportInterceptor(_clock);
        _channels = FrozenDictionary<string, GrpcChannel>.Empty;
        _invokers = invokers.ToFrozenDictionary(
            kv => kv.Key,
            kv => kv.Value.Intercept(_transportInterceptor),
            StringComparer.Ordinal);
    }

    /// <summary>The operators' clock, fed by every answer that passes through the pool.</summary>
    public ServerTimeSync Clock => _clock;

    public IReadOnlyList<string> Addresses => [.. _invokers.Keys];

    /// <summary>
    /// Refuse an operator address that is not <c>https://</c>, unless plaintext is allowed and it
    /// is <c>http://</c>.
    /// </summary>
    internal static void ValidateAddress(string address, bool allowPlaintext)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            throw new SparkConfigurationException("transport.address", $"Invalid signing operator address: '{address}'.");
        }

        var secure = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var plaintext = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (!secure && !(plaintext && allowPlaintext))
        {
            throw new SparkConfigurationException(
                "transport.address",
                $"Signing operator {address} must be reached over https.");
        }
    }

    /// <summary>
    /// Service config shared by every operator channel: the retry policy for every method except
    /// the authentication service and the event subscription.
    /// </summary>
    internal static ServiceConfig CreateServiceConfig() => new()
    {
        MethodConfigs =
        {
            new MethodConfig
            {
                Names = { MethodName.Default },
                RetryPolicy = RetryPolicy,
            },
            new MethodConfig
            {
                Names = { new MethodName { Service = SparkServiceName, Method = SubscribeToEventsMethod } },
            },
            new MethodConfig
            {
                Names = { new MethodName { Service = AuthnService } },
            },
        },
    };

    private GrpcChannelOptions CreateChannelOptions(int maxMessageBytes) => new()
    {
        MaxReceiveMessageSize = maxMessageBytes,
        MaxSendMessageSize = maxMessageBytes,
        ServiceConfig = CreateServiceConfig(),
        HttpHandler = _httpHandler,
        DisposeHttpClient = false,
    };

    /// <summary>The call invoker for an operator, with the transport interceptor applied.</summary>
    public CallInvoker GetCallInvoker(string address)
    {
        if (_invokers.TryGetValue(address, out var invoker))
        {
            return invoker;
        }

        throw new ArgumentException($"Unknown SO address: {address}", nameof(address));
    }

    /// <summary>
    /// Call invoker on a dedicated channel with 128 MiB send/receive caps, created lazily per
    /// address. Grpc.Net.Client only supports message size limits at the channel level, so
    /// recovery-snapshot queries use these channels instead of raising the limit for every call
    /// in the pool. Falls back to the regular invoker for a pool built over test invokers.
    /// </summary>
    public CallInvoker GetLargeMessageCallInvoker(string address)
    {
        if (!_channels.ContainsKey(address))
        {
            return GetCallInvoker(address);
        }

        var entry = _largeMessage.GetOrAdd(
            address,
            a => new Lazy<(GrpcChannel, CallInvoker)>(() =>
            {
                var channel = GrpcChannel.ForAddress(a, CreateChannelOptions(LargeMessageMaxBytes));
                return (channel, channel.CreateCallInvoker().Intercept(_transportInterceptor));
            }));
        return entry.Value.Invoker;
    }

    /// <summary>
    /// The authentication client for an operator. It carries no session token, so it is used
    /// without the wallet's auth interceptor.
    /// </summary>
    public SparkAuthnService.SparkAuthnServiceClient GetAuthnClient(string address)
    {
        return new SparkAuthnService.SparkAuthnServiceClient(GetCallInvoker(address));
    }

    /// <summary>A SparkService client without the wallet's auth interceptor.</summary>
    public SparkService.SparkServiceClient GetSparkClient(string address)
    {
        return new SparkService.SparkServiceClient(GetCallInvoker(address));
    }

    /// <summary>A SparkTokenService client without the wallet's auth interceptor.</summary>
    public SparkTokenService.SparkTokenServiceClient GetTokenClient(string address)
    {
        return new SparkTokenService.SparkTokenServiceClient(GetCallInvoker(address));
    }

    public void Dispose()
    {
        foreach (var channel in _channels.Values)
        {
            channel.Dispose();
        }

        foreach (var lazy in _largeMessage.Values)
        {
            if (lazy.IsValueCreated)
            {
                lazy.Value.Channel.Dispose();
            }
        }
    }
}
