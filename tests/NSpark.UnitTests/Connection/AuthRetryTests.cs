using Grpc.Core;
using Grpc.Core.Interceptors;
using NSpark.Connection;
using NSpark.Exceptions;
using NSpark.Models;
using NSpark.Services;
using NSpark.UnitTests.Services;
using NSpark.UnitTests.TestSupport;

namespace NSpark.UnitTests.Connection;

/// <summary>
/// The reference SDK's auth middleware drops a token the operator rejects, authenticates again and
/// re-issues the call. These run the wallet's real transport stack against the operator stand-in.
/// </summary>
[TestFixture]
public class AuthRetryTests
{
    [Test]
    [CancelAfter(60_000)]
    public async Task A_token_rejected_before_the_answer_is_dropped_and_the_call_reissued_with_a_fresh_one(CancellationToken ct)
    {
        var state = new FakeOperatorState(token => token == "session-1");

        var leaves = await FakeOperator.RunAsync(state, wallet => wallet.GetLeavesAsync(ct));

        leaves.Should().BeEmpty();
        state.IssuedTokens.Should().Equal("session-1", "session-2");
        state.Calls.Should().Equal("query_nodes Bearer session-1", "query_nodes Bearer session-2");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_token_rejected_after_the_response_headers_is_dropped_and_the_call_reissued_too(CancellationToken ct)
    {
        // The operators answer UNAUTHENTICATED only from their pre-handler interceptors — some of
        // which set headers first — so nothing ran and re-issuing is safe either way.
        var state = new FakeOperatorState(token => token == "session-1", FakeOperatorState.Rejection.AfterHeaders);

        var leaves = await FakeOperator.RunAsync(state, wallet => wallet.GetLeavesAsync(ct));

        leaves.Should().BeEmpty();
        state.IssuedTokens.Should().Equal("session-1", "session-2");
        state.Calls.Should().Equal("query_nodes Bearer session-1", "query_nodes Bearer session-2");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_rejected_event_subscription_is_retried_with_a_fresh_token(CancellationToken ct)
    {
        var state = new FakeOperatorState(token => token == "session-1");

        var events = await FakeOperator.RunAsync(
            state,
            wallet => EventStreamConnectionTests.EventsUntilConnectionAsync(wallet.SubscribeEventsAsync(ct), 1));

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<ReconnectingEvent>()
            .Which.Should().Match<ReconnectingEvent>(e => e.Attempt == 1 && e.RetryIn == TimeSpan.FromSeconds(1));
        events[1].Should().BeOfType<ConnectedEvent>();
        state.IssuedTokens.Should().Equal("session-1", "session-2");
        state.Calls.Take(2).Should().Equal("subscribe_to_events Bearer session-1", "subscribe_to_events Bearer session-2");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_token_that_is_never_accepted_fails_after_the_interceptor_attempts(CancellationToken ct)
    {
        var state = new FakeOperatorState(_ => true);

        await FakeOperator.RunAsync(state, async wallet =>
        {
            var act = () => wallet.GetLeavesAsync(ct);
            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
        });

        state.IssuedTokens.Should().Equal("session-1", "session-2", "session-3");
        state.Calls.Should().HaveCount(AuthRetryInterceptor.MaxAttempts);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Concurrent_calls_share_one_authentication(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        state.VerifyDelay = TimeSpan.FromMilliseconds(300);

        await FakeOperator.RunAsync(state, wallet => Task.WhenAll(Enumerable.Range(0, 5).Select(_ => wallet.GetLeavesAsync(ct))));

        state.ChallengesIssued.Should().Be(1);
        state.IssuedTokens.Should().Equal("session-1");
        state.Calls.Should().HaveCount(5);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task An_expired_or_used_challenge_is_replaced_by_a_fresh_one(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        state.FailNextVerifications(
            new Status(StatusCode.FailedPrecondition, "challenge validation failed: expired: challenge expired 3 seconds ago"),
            new Status(StatusCode.FailedPrecondition, "challenge validation failed: challenge reused: nonce already used"));

        await FakeOperator.RunAsync(state, wallet => wallet.GetLeavesAsync(ct));

        state.ChallengesIssued.Should().Be(3);
        state.IssuedTokens.Should().Equal("session-1");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Any_other_refusal_ends_authentication(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        state.FailNextVerifications(
            new Status(StatusCode.FailedPrecondition, "signature verification failed under both ECDSA and Schnorr"));

        await FakeOperator.RunAsync(state, async wallet =>
        {
            var act = () => wallet.GetLeavesAsync(ct);
            await act.Should().ThrowAsync<SparkConnectionException>();
        });

        state.ChallengesIssued.Should().Be(1);
        state.IssuedTokens.Should().BeEmpty();
        state.Calls.Should().BeEmpty();
    }

    [Test]
    public async Task Calls_without_a_token_and_the_token_issuing_service_pass_through_untouched()
    {
        var interceptor = new AuthRetryInterceptor(
            _ => throw new AssertionException("no token should be fetched"),
            _ => throw new AssertionException("nothing should be invalidated"));
        var authn = Method(GrpcConnectionPool.AuthnService, "verify_challenge");
        var spark = Method(GrpcConnectionPool.SparkServiceName, "query_nodes");

        foreach (var (method, headers) in new[]
                 {
                     (authn, new Metadata { { "authorization", "Bearer mine" } }),
                     (spark, new Metadata()),
                 })
        {
            var seen = new List<Metadata?>();
            var call = interceptor.AsyncUnaryCall(
                "request",
                new ClientInterceptorContext<string, string>(method, null, new CallOptions(headers)),
                (request, context) =>
                {
                    seen.Add(context.Options.Headers);
                    return new AsyncUnaryCall<string>(
                        Task.FromException<string>(new RpcException(new Status(StatusCode.Unauthenticated, "no"))),
                        Task.FromResult(new Metadata()),
                        () => new Status(StatusCode.Unauthenticated, "no"),
                        () => new Metadata(),
                        () => { });
                });

            var act = async () => await call.ResponseAsync;
            await act.Should().ThrowAsync<RpcException>();
            seen.Should().ContainSingle().Which.Should().BeSameAs(headers);
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Every_attempt_carries_the_current_token_and_a_late_rejection_never_evicts_a_newer_one(CancellationToken ct)
    {
        var current = "old";
        var invalidated = new List<string>();
        var interceptor = new AuthRetryInterceptor(
            _ => Task.FromResult(current),
            token =>
            {
                invalidated.Add(token);
                if (token == current)
                {
                    current = "fresh";
                }
            });
        var sent = new List<string?>();

        var call = interceptor.AsyncUnaryCall(
            "request",
            new ClientInterceptorContext<string, string>(
                Method(GrpcConnectionPool.SparkServiceName, "query_nodes"),
                null,
                new CallOptions(new Metadata { { "authorization", "Bearer stale-at-call-site" } }, cancellationToken: ct)),
            (request, context) =>
            {
                var token = AuthRetryInterceptor.BearerToken(context.Options.Headers);
                sent.Add(token);
                return token == "fresh"
                    ? new AsyncUnaryCall<string>(Task.FromResult("ok"), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { })
                    : new AsyncUnaryCall<string>(
                        Task.FromException<string>(new RpcException(new Status(StatusCode.Unauthenticated, "expired"))),
                        Task.FromResult(new Metadata()),
                        () => new Status(StatusCode.Unauthenticated, "expired"),
                        () => new Metadata(),
                        () => { });
            });

        (await call.ResponseAsync).Should().Be("ok");
        sent.Should().Equal("old", "fresh");
        invalidated.Should().Equal("old");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_dropped_token_stays_dropped_while_concurrent_calls_hit_the_cache(CancellationToken ct)
    {
        // A cache hit refreshes the entry's LRU stamp. Done blindly, a hit that read the token just
        // before it was dropped puts it back; and a drop that compares the whole entry misses it
        // once a hit has restamped it. Either way the rejected token would be served again.
        var state = FakeOperatorState.Accepting();
        using var connection = FakeOperator.Connect(state);
        var wallet = await connection.CreateWalletAsync(FakeOperator.Mnemonic, account: 0);
        var auth = connection.Authenticator;
        Task<string> Token() => auth.GetTokenAsync(connection.Pool, FakeOperator.Address, wallet.Signer, wallet.IdentityPublicKey, ct);

        for (var round = 0; round < 50; round++)
        {
            var rejected = await Token();
            using var stop = new CancellationTokenSource();
            using var running = new CountdownEvent(4);
            var hits = Enumerable.Range(0, 4).Select(_ => Task.Run(
                async () =>
                {
                    await Token();
                    running.Signal();
                    while (!stop.IsCancellationRequested)
                    {
                        await Token();
                    }
                },
                ct)).ToArray();
            running.Wait(ct);

            auth.Invalidate(FakeOperator.Address, wallet.IdentityPublicKey, rejected);
            await stop.CancelAsync();
            await Task.WhenAll(hits);

            (await Token()).Should().NotBe(rejected, $"round {round} dropped it");
        }
    }

    private static Method<string, string> Method(string service, string name) =>
        new(MethodType.Unary, service, name, Marshallers.StringMarshaller, Marshallers.StringMarshaller);
}
