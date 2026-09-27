using System.Diagnostics;
using System.Globalization;
using System.Net;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSpark.Connection;
using NSpark.Exceptions;
using NSpark.Proto;
using NSpark.Services;
using NSpark.UnitTests.Services;
using NSpark.UnitTests.TestSupport;

namespace NSpark.UnitTests.Connection;

/// <summary>
/// Pins the transport behaviour that stops a wedged connection or a rejected session token from
/// parking every call until the host process restarts. The values mirror the reference SDK's
/// connection manager: a 60 s deadline on unary calls; 3 attempts, 1 s → 10 s backoff on
/// UNAVAILABLE and CANCELLED; 20 MB messages.
/// </summary>
[TestFixture]
public class TransportHardeningTests
{
    private static MethodConfig Config(string service, string method = "") =>
        GrpcConnectionPool.CreateServiceConfig().MethodConfigs.Single(c =>
            c.Names.Any(n => (n.Service ?? string.Empty) == service && (n.Method ?? string.Empty) == method));

    internal static TreeNode AvailableNode(int index, int payload = 0) => new()
    {
        Id = $"node-{index:D4}",
        Status = "AVAILABLE",
        Value = 1,
        NodeTx = ByteString.CopyFrom(Enumerable.Repeat((byte)0xAB, payload).ToArray()),
    };

    /// <summary>A <c>grpc-timeout</c> header's value.</summary>
    private static TimeSpan ParseGrpcTimeout(string value)
    {
        var amount = long.Parse(value[..^1], CultureInfo.InvariantCulture);
        return value[^1] switch
        {
            'H' => TimeSpan.FromHours(amount),
            'M' => TimeSpan.FromMinutes(amount),
            'S' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMilliseconds(amount),
            'u' => TimeSpan.FromTicks(amount * 10),
            'n' => TimeSpan.FromTicks(amount / 100),
            _ => throw new FormatException(value),
        };
    }

    [Test]
    public void Every_operator_call_retries_like_the_reference_SDK()
    {
        var policy = Config(string.Empty).RetryPolicy;

        policy.Should().NotBeNull();
        policy!.MaxAttempts.Should().Be(3);
        policy.InitialBackoff.Should().Be(TimeSpan.FromSeconds(1));
        policy.MaxBackoff.Should().Be(TimeSpan.FromSeconds(10));
        policy.BackoffMultiplier.Should().Be(2);
        policy.RetryableStatusCodes.Should().BeEquivalentTo(new[] { StatusCode.Unavailable, StatusCode.Cancelled });
        policy.RetryableStatusCodes.Should().NotContain(StatusCode.DeadlineExceeded);
    }

    [Test]
    public void The_event_subscription_and_the_token_issuing_service_are_never_retried_by_the_transport()
    {
        Config(GrpcConnectionPool.SparkServiceName, GrpcConnectionPool.SubscribeToEventsMethod).RetryPolicy.Should().BeNull();
        Config(GrpcConnectionPool.AuthnService).RetryPolicy.Should().BeNull();
        GrpcConnectionPool.AuthnService.Should().Be("spark_authn.SparkAuthnService");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Unary_calls_carry_the_60_s_deadline_and_the_event_subscription_none(CancellationToken ct)
    {
        OperatorTransportInterceptor.DefaultTimeout.Should().Be(TimeSpan.FromSeconds(60));
        var state = FakeOperatorState.Accepting();

        await FakeOperator.RunAsync(state, async wallet =>
        {
            await wallet.GetLeavesAsync(ct);
            await EventStreamConnectionTests.EventsUntilConnectionAsync(wallet.SubscribeEventsAsync(ct), 1);
        });

        var timeouts = state.Timeouts;
        foreach (var method in new[] { "get_challenge", "verify_challenge", "query_nodes" })
        {
            var timeout = timeouts.First(t => t.Method == method).Timeout;
            timeout.Should().NotBeNull(method);
            ParseGrpcTimeout(timeout!).Should().BeCloseTo(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5), method);
        }

        timeouts.First(t => t.Method == "subscribe_to_events").Timeout.Should().BeNull();
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task An_unavailable_operator_is_retried_by_the_channel(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        state.FailNextCalls("query_nodes", new Status(StatusCode.Unavailable, "connection reset"));

        var leaves = await FakeOperator.RunAsync(state, wallet => wallet.GetLeavesAsync(ct));

        leaves.Should().BeEmpty();
        state.Methods.Should().Equal("query_nodes", "query_nodes");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Other_failures_are_not_retried(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        state.FailNextCalls("query_nodes", new Status(StatusCode.Internal, "boom"));

        await FakeOperator.RunAsync(state, async wallet =>
        {
            var act = () => wallet.GetLeavesAsync(ct);
            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Internal);
        });

        state.Methods.Should().Equal("query_nodes");
    }

    [Test]
    public void Messages_up_to_the_reference_SDKs_20_MB_are_allowed()
    {
        GrpcConnectionPool.MaxMessageBytes.Should().Be(20 * 1024 * 1024);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_wallets_nodes_are_read_a_page_of_100_at_a_time_until_a_short_page(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        state.SetNodes(Enumerable.Range(0, 250).Select(i => AvailableNode(i)));

        var leaves = await FakeOperator.RunAsync(state, wallet => wallet.GetLeavesAsync(ct));

        leaves.Should().HaveCount(250);
        state.NodePages.Should().Equal((100L, 0L), (100L, 100L), (100L, 200L));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_page_larger_than_gRPCs_4_MiB_default_is_received(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        // 100 nodes of 50 kB: one 5 MB page, then an empty one.
        state.SetNodes(Enumerable.Range(0, 100).Select(i => AvailableNode(i, payload: 50_000)));

        var leaves = await FakeOperator.RunAsync(state, wallet => wallet.GetLeavesAsync(ct));

        leaves.Should().HaveCount(100);
        state.NodePages.Should().Equal((100L, 0L), (100L, 100L));
    }

    [Test]
    public void Operators_are_reached_over_TLS_with_plaintext_only_where_allowed_and_never_on_mainnet()
    {
        var plaintextOnMainnet = () => GrpcConnectionPool.ValidateAddress("http://operator.example", allowPlaintext: false);
        plaintextOnMainnet.Should().Throw<SparkConfigurationException>();
        GrpcConnectionPool.ValidateAddress("https://operator.example", allowPlaintext: false);
        GrpcConnectionPool.ValidateAddress("http://127.0.0.1:9001", allowPlaintext: true);
        var otherScheme = () => GrpcConnectionPool.ValidateAddress("ftp://operator.example", allowPlaintext: true);
        otherScheme.Should().Throw<SparkConfigurationException>();
        var notAnAddress = () => GrpcConnectionPool.ValidateAddress("operator.example", allowPlaintext: true);
        notAnAddress.Should().Throw<SparkConfigurationException>();

        var options = new SparkOptions
        {
            Network = SparkNetwork.Mainnet,
            SigningOperators = [new SigningOperatorConfig("http://operator.example", FakeOperator.Identifier, FakeOperator.IdentityPublicKeyHex)],
        };
        using var http = new HttpClient();
        var connect = () => new SparkConnection(Options.Create(options), http, NullLoggerFactory.Instance);
        connect.Should().Throw<SparkConfigurationException>();
    }

    [Test]
    public void The_SSP_identity_key_follows_the_SSP()
    {
        new SparkOptions().EffectiveSspIdentityPublicKeyHex
            .Should().Be("023e33e2920326f64ea31058d44777442d97d7d5cbfcf54e3060bc1695e5261c93");
        new SparkOptions { Network = SparkNetwork.Regtest }.EffectiveSspIdentityPublicKeyHex
            .Should().Be("022bf283544b16c0622daecb79422007d167eca6ce9f0c98c0c49833b1f7170bfe");
        new SparkOptions { SspUrl = SparkOptions.DefaultSspUrl }.EffectiveSspIdentityPublicKeyHex
            .Should().Be(new SparkOptions().EffectiveSspIdentityPublicKeyHex);

        // A custom SSP without its key: no key, and transfers to the SSP refuse to run.
        var custom = new SparkOptions { SspUrl = "https://ssp.example/graphql" };
        custom.EffectiveSspIdentityPublicKeyHex.Should().BeNull();
        custom.Invoking(o => o.RequireSspIdentityPublicKey()).Should().Throw<SparkConfigurationException>();

        var key = "03" + string.Concat(Enumerable.Repeat("ab", 32));
        var configured = new SparkOptions { SspUrl = "https://ssp.example/graphql", SspIdentityPublicKeyHex = key };
        Convert.ToHexString(configured.RequireSspIdentityPublicKey()).ToLowerInvariant().Should().Be(key);

        new SparkOptions { SspIdentityPublicKeyHex = "zz" }
            .Invoking(o => o.RequireSspIdentityPublicKey()).Should().Throw<SparkConfigurationException>();
        new SparkOptions { SspIdentityPublicKeyHex = "04" + string.Concat(Enumerable.Repeat("ab", 32)) }
            .Invoking(o => o.RequireSspIdentityPublicKey()).Should().Throw<SparkConfigurationException>();
    }

    [Test]
    public void The_regtest_preset_uses_the_hosted_operators_and_keys_as_the_reference_SDKs_REGTEST_preset()
    {
        var regtest = SparkOptions.GetDefaultOperators(SparkNetwork.Regtest);
        var mainnet = SparkOptions.GetDefaultOperators(SparkNetwork.Mainnet);

        regtest.Select(o => o.Address).Should().Equal(mainnet.Select(o => o.Address));
        regtest.Select(o => o.IdentityPublicKeyHex).Should().Equal(mainnet.Select(o => o.IdentityPublicKeyHex));
        regtest.Should().OnlyContain(o => o.Address.StartsWith("https://", StringComparison.Ordinal) && o.IdentityPublicKeyHex.Length == 66);
        new SparkOptions { Network = SparkNetwork.Regtest, SigningOperators = regtest }.EffectiveSigningThreshold.Should().Be(2);
    }

    [Test]
    public void SSP_requests_back_off_like_the_reference_SDKs_1_s_doubling_to_10_s()
    {
        Enumerable.Range(0, 7).Select(SspRetryPolicy.Standard.DelayAfter)
            .Should().Equal(new[] { 1, 2, 4, 8, 10, 10, 10 }.Select(s => TimeSpan.FromSeconds(s)));
        SspRetryPolicy.Standard.MaxRetries.Should().Be(5);
    }

    [Test]
    public async Task SSP_requests_are_retried_on_502_503_504_and_lost_connections_only()
    {
        var instant = new SspRetryPolicy(5, TimeSpan.Zero, TimeSpan.Zero);

        async Task<(int Attempts, HttpStatusCode? Status, Exception? Error)> RunAsync(Func<int, HttpResponseMessage> answer)
        {
            var handler = new ScriptedHttpHandler(answer);
            using var http = new HttpClient(handler);
            try
            {
                using var response = await instant.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Post, "https://ssp.example/graphql"), CancellationToken.None);
                return (handler.Attempts, response.StatusCode, null);
            }
            catch (Exception ex) when (ex is not AssertionException)
            {
                return (handler.Attempts, null, ex);
            }
        }

        var statuses = new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.BadGateway };
        var recovered = await RunAsync(attempt => new HttpResponseMessage(attempt <= 2 ? statuses[attempt - 1] : HttpStatusCode.OK));
        recovered.Should().Be((3, HttpStatusCode.OK, null));

        var reconnected = await RunAsync(attempt => attempt == 1
            ? throw new HttpRequestException("connection lost")
            : new HttpResponseMessage(HttpStatusCode.OK));
        reconnected.Should().Be((2, HttpStatusCode.OK, null));

        // Out of retries, the last answer stands; other statuses and timeouts are not retried.
        (await RunAsync(_ => new HttpResponseMessage(HttpStatusCode.GatewayTimeout))).Should().Be((6, HttpStatusCode.GatewayTimeout, null));
        (await RunAsync(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))).Should().Be((1, HttpStatusCode.InternalServerError, null));
        var timedOut = await RunAsync(_ => throw new TaskCanceledException("timed out", new TimeoutException()));
        timedOut.Attempts.Should().Be(1);
        timedOut.Error.Should().BeOfType<TaskCanceledException>();
    }

    [Test]
    public void An_SSP_auth_rejection_is_recognised_and_other_failures_are_not()
    {
        static SparkSspException Http(int status) => new("ssp.graphql", $"HTTP {status}") { HttpStatusCode = status };
        static SparkSspException GraphQL(string message) => new("ssp.graphql", message) { GraphQLErrors = [message] };

        SspGraphQLClient.IsAuthFailure(Http(401)).Should().BeTrue();
        SspGraphQLClient.IsAuthFailure(Http(403)).Should().BeTrue();
        SspGraphQLClient.IsAuthFailure(GraphQL("Unauthorized")).Should().BeTrue();
        SspGraphQLClient.IsAuthFailure(GraphQL("Not authenticated: token expired")).Should().BeTrue();
        SspGraphQLClient.IsAuthFailure(Http(500)).Should().BeFalse();
        SspGraphQLClient.IsAuthFailure(GraphQL("Insufficient funds for coop exit")).Should().BeFalse();
        SspGraphQLClient.IsAuthFailure(new SparkSspException("ssp.graphql", "Invalid fee estimate response")).Should().BeFalse();
    }

    [Test]
    public void The_operators_clock_is_estimated_from_their_date_and_processing_time_headers()
    {
        var clock = new ServerTimeSync();
        clock.IsSynced.Should().BeFalse();
        clock.ServerNow.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));

        // Garbage headers are ignored.
        var now = Stopwatch.GetTimestamp();
        clock.Record("yesterday", "1", now, now);
        clock.Record("Mon, 02 Jan 2006 15:04:05 UTC", "-5", now, now);
        clock.Record("Mon, 02 Jan 2006 15:04:05 UTC", "NaN", now, now);
        clock.IsSynced.Should().BeFalse();

        // Answered 200 ms after sending, 100 ms of it processing: 50 ms each way.
        var received = Stopwatch.GetTimestamp();
        var sent = received - (Stopwatch.Frequency / 5);
        clock.Record("Mon, 02 Jan 2006 15:04:05 UTC", "100", sent, received);

        clock.IsSynced.Should().BeTrue();
        ServerTimeSync.TryParseDate("Mon, 02 Jan 2006 15:04:05 GMT", out var stated).Should().BeTrue();
        stated.ToUnixTimeSeconds().Should().Be(1_136_214_245);
        var offset = clock.ServerNow - stated;
        offset.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(50)).And.BeLessThan(TimeSpan.FromMilliseconds(1_050));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Session_tokens_are_kept_by_the_operators_clock_so_a_device_clock_two_hours_ahead_does_not_reauthenticate(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        state.ClockOffset = TimeSpan.FromHours(-2);

        await FakeOperator.RunAsync(state, async wallet =>
        {
            for (var i = 0; i < 3; i++)
            {
                await wallet.GetLeavesAsync(ct);
            }

            wallet.Clock.IsSynced.Should().BeTrue();
            wallet.Clock.ServerNow.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(-2), TimeSpan.FromSeconds(5));
        });

        state.IssuedTokens.Should().Equal("session-1");
    }

    /// <summary>Answers each attempt with <c>answer(attempt)</c>, counting from 1.</summary>
    private sealed class ScriptedHttpHandler(Func<int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            try
            {
                return Task.FromResult(answer(Attempts));
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }
}
