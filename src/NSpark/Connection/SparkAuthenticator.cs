using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSpark.Diagnostics;
using NSpark.Exceptions;
using NSpark.Proto.Authn;
using NSpark.Signer;

namespace NSpark.Connection;

/// <summary>
/// Challenge-response authentication with Spark Signing Operators.
/// Tokens are cached per (soAddress, identityPubKey) with TTL + size eviction.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><description>Concurrent callers share one authentication per operator and identity
///   (the reference SDK's <c>authInflight</c>) instead of each running a challenge.</description></item>
///   <item><description>Up to <see cref="MaxAttempts"/> challenge exchanges are made, as in the
///   reference SDK: a fresh challenge at once when the last one expired or was already used, after
///   250 ms when the connection failed; any other failure ends authentication. The transport does
///   not retry the authentication service itself (a retried <c>verify_challenge</c> would re-send a
///   consumed challenge).</description></item>
///   <item><description>Token expiry is the operators' time, so it is compared with their clock
///   (<see cref="ServerTimeSync"/>), not the local one.</description></item>
///   <item><description>A token the operator rejects is dropped with <see cref="Invalidate"/>, so the
///   next call authenticates again.</description></item>
/// </list>
/// The cache is capped at <see cref="DefaultMaxCacheEntries"/> entries and evicts the least recently
/// used tokens when full.
/// </remarks>
internal sealed class SparkAuthenticator
{
    /// <summary>Default maximum number of cached tokens.</summary>
    public const int DefaultMaxCacheEntries = 1024;

    /// <summary>Challenge exchanges tried before authentication fails, as in the reference SDK.</summary>
    public const int MaxAttempts = 8;

    /// <summary>Default buffer subtracted from token expiration to force early refresh.</summary>
    public static readonly TimeSpan DefaultRefreshBuffer = TimeSpan.FromMinutes(1);

    /// <summary>Wait before another exchange after a connection failure (250 ms, as in the reference SDK).</summary>
    internal TimeSpan ConnectionRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<string, CachedToken> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<CachedToken>>> _inFlight = new(StringComparer.Ordinal);
    private readonly ILogger<SparkAuthenticator> _logger;
    private readonly int _maxCacheEntries;
    private readonly TimeSpan _refreshBuffer;
    private readonly ServerTimeSync _clock;

    private sealed record CachedToken(string Token, DateTimeOffset ExpiresAt, long LastUsedTicks);

    /// <inheritdoc cref="SparkAuthenticator(ILogger{SparkAuthenticator}, int, TimeSpan?, ServerTimeSync?)"/>
    public SparkAuthenticator()
        : this(NullLogger<SparkAuthenticator>.Instance, DefaultMaxCacheEntries, DefaultRefreshBuffer)
    {
    }

    /// <summary>
    /// Create an authenticator with explicit cache configuration.
    /// </summary>
    /// <param name="logger">Logger for telemetry events.</param>
    /// <param name="maxCacheEntries">Soft cap on cached tokens; LRU-evicted on overflow.</param>
    /// <param name="refreshBuffer">How long before expiration to force a refresh.</param>
    /// <param name="clock">The operators' clock token expiry is compared with.</param>
    public SparkAuthenticator(
        ILogger<SparkAuthenticator> logger,
        int maxCacheEntries = DefaultMaxCacheEntries,
        TimeSpan? refreshBuffer = null,
        ServerTimeSync? clock = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxCacheEntries = maxCacheEntries > 0
            ? maxCacheEntries
            : throw new ArgumentOutOfRangeException(nameof(maxCacheEntries));
        _refreshBuffer = refreshBuffer ?? DefaultRefreshBuffer;
        _clock = clock ?? new ServerTimeSync();
    }

    /// <summary>
    /// Get a valid auth token for the given SO address, authenticating if needed.
    /// </summary>
    public async Task<string> GetTokenAsync(
        GrpcConnectionPool pool,
        string soAddress,
        ISparkSigner signer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signer);
        var identityPubKey = await signer.GetIdentityPublicKeyAsync(ct).ConfigureAwait(false);
        return await GetTokenAsync(pool, soAddress, signer, identityPubKey, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Get a valid auth token for the given SO address and identity, authenticating if needed.
    /// Concurrent callers for the same operator and identity share one authentication.
    /// </summary>
    public async Task<string> GetTokenAsync(
        GrpcConnectionPool pool,
        string soAddress,
        ISparkSigner signer,
        byte[] identityPubKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentException.ThrowIfNullOrEmpty(soAddress);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(identityPubKey);

        var cacheKey = MakeCacheKey(soAddress, identityPubKey);
        if (_cache.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresAt > _clock.ServerNow + _refreshBuffer)
        {
            // Restamp only the entry that was read: a blind write would put back a token
            // Invalidate just dropped, or replace the fresh one that followed it.
            _cache.TryUpdate(cacheKey, cached with { LastUsedTicks = DateTime.UtcNow.Ticks }, cached);
            SparkMeter.AuthTokenCacheHits.Add(1);
            return cached.Token;
        }

        SparkMeter.AuthTokenCacheMisses.Add(1);

        // One authentication per operator and identity at a time. It runs on its own, so one
        // caller giving up does not cancel it for the others.
        var candidate = new Lazy<Task<CachedToken>>(
            () => AuthenticateAndCacheAsync(pool, soAddress, signer, identityPubKey, cacheKey),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var shared = _inFlight.GetOrAdd(cacheKey, candidate);
        if (ReferenceEquals(shared, candidate))
        {
            _ = candidate.Value.ContinueWith(
                task =>
                {
                    // Observed here: every waiting caller may have given up on a failed attempt.
                    _ = task.Exception;
                    _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<CachedToken>>>(cacheKey, candidate));
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        var token = await shared.Value.WaitAsync(ct).ConfigureAwait(false);
        return token.Token;
    }

    /// <summary>
    /// Create gRPC call headers with the auth token.
    /// </summary>
    public async Task<Metadata> GetAuthMetadataAsync(
        GrpcConnectionPool pool,
        string soAddress,
        ISparkSigner signer,
        CancellationToken ct = default)
    {
        var token = await GetTokenAsync(pool, soAddress, signer, ct).ConfigureAwait(false);
        return new Metadata { { "authorization", $"Bearer {token}" } };
    }

    /// <summary>
    /// Forget this operator's cached session if it is still <paramref name="token"/>. Called when
    /// the operator answers UNAUTHENTICATED: a token the operator no longer honours stays "valid" by
    /// its own expiry, and reusing it would fail every call until then. Only the rejected token is
    /// dropped — a concurrent call may already have replaced it with a fresh one.
    /// </summary>
    public void Invalidate(string soAddress, byte[] identityPubKey, string token)
    {
        var cacheKey = MakeCacheKey(soAddress, identityPubKey);

        // The entry compares whole, LRU stamp included, so a cache hit restamping it in between
        // makes the removal miss; look again until the token is gone or has been replaced.
        while (_cache.TryGetValue(cacheKey, out var cached) && string.Equals(cached.Token, token, StringComparison.Ordinal))
        {
            if (_cache.TryRemove(new KeyValuePair<string, CachedToken>(cacheKey, cached)))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Drop every cached token (for example on signer rotation or shutdown).
    /// </summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>The operator refused the challenge as expired or already used; a fresh one will do.</summary>
    internal static bool IsStaleChallenge(RpcException error) =>
        error.StatusCode == StatusCode.FailedPrecondition
        && (error.Status.Detail.Contains("challenge expired", StringComparison.Ordinal)
            || error.Status.Detail.Contains("challenge reused", StringComparison.Ordinal));

    /// <summary>The exchange failed on the way rather than on its content.</summary>
    internal static bool IsConnectionFailure(RpcException error) => error.StatusCode is
        StatusCode.Unavailable or
        StatusCode.Internal or
        StatusCode.Unknown or
        StatusCode.Cancelled or
        StatusCode.DeadlineExceeded;

    private static string MakeCacheKey(string soAddress, byte[] identityPubKey) =>
        $"{soAddress}:{Convert.ToHexString(identityPubKey)}";

    private void EvictIfFull()
    {
        if (_cache.Count < _maxCacheEntries)
        {
            return;
        }

        // Simple LRU eviction: drop ~10% of the least recently used entries.
        var dropCount = Math.Max(1, _maxCacheEntries / 10);
        foreach (var kvp in _cache
                     .OrderBy(e => e.Value.LastUsedTicks)
                     .Take(dropCount))
        {
            _cache.TryRemove(kvp.Key, out _);
        }
    }

    private async Task<CachedToken> AuthenticateAndCacheAsync(
        GrpcConnectionPool pool,
        string soAddress,
        ISparkSigner signer,
        byte[] identityPubKey,
        string cacheKey)
    {
        var fresh = await AuthenticateAsync(pool, soAddress, signer, identityPubKey).ConfigureAwait(false);
        EvictIfFull();
        _cache[cacheKey] = fresh;
        return fresh;
    }

    private async Task<CachedToken> AuthenticateAsync(
        GrpcConnectionPool pool,
        string soAddress,
        ISparkSigner signer,
        byte[] identityPubKey)
    {
        using var activity = SparkActivitySource.Start(SparkActivitySource.Spans.SoAuthenticate);
        activity?.SetTag(SparkActivitySource.Tags.SoAddress, soAddress);

        var stopwatch = Stopwatch.StartNew();
        RpcException? lastError = null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                var token = await ExchangeChallengeAsync(pool, soAddress, signer, identityPubKey).ConfigureAwait(false);
                stopwatch.Stop();
                SparkMeter.SoRttMs.Record(stopwatch.Elapsed.TotalMilliseconds);
                _logger.LogDebug(
                    LogEvents.AuthTokenRefreshed,
                    "Refreshed auth token from signing operator {SoAddress}; expires at {ExpiresAt}.",
                    soAddress,
                    token.ExpiresAt);
                return token;
            }
            catch (RpcException rpc) when (IsStaleChallenge(rpc))
            {
                lastError = rpc;
            }
            catch (RpcException rpc) when (IsConnectionFailure(rpc))
            {
                lastError = rpc;
                await Task.Delay(ConnectionRetryDelay).ConfigureAwait(false);
            }
            catch (RpcException rpc)
            {
                throw Failed(soAddress, rpc, activity);
            }
        }

        throw Failed(soAddress, lastError!, activity);
    }

    private SparkConnectionException Failed(string soAddress, RpcException rpc, Activity? activity)
    {
        SparkMeter.SoGrpcErrors.Add(1);
        activity?.SetTag(SparkActivitySource.Tags.GrpcStatus, rpc.StatusCode.ToString());
        activity?.SetStatus(ActivityStatusCode.Error, rpc.Status.Detail);
        _logger.LogWarning(
            LogEvents.GrpcCallFailed,
            rpc,
            "Authentication gRPC call to {SoAddress} failed with status {GrpcStatus}.",
            soAddress,
            rpc.StatusCode);
        return SparkConnectionException.FromRpc("so.authenticate", soAddress, rpc);
    }

    private static async Task<CachedToken> ExchangeChallengeAsync(
        GrpcConnectionPool pool,
        string soAddress,
        ISparkSigner signer,
        byte[] identityPubKey)
    {
        var authnClient = pool.GetAuthnClient(soAddress);

        // Step 1: Request challenge.
        var challengeResponse = await authnClient.get_challengeAsync(
            new GetChallengeRequest
            {
                PublicKey = ByteString.CopyFrom(identityPubKey),
            }).ConfigureAwait(false);

        // Step 2: Sign the challenge.
        var challengeBytes = challengeResponse.ProtectedChallenge.Challenge.ToByteArray();
        var challengeHash = SHA256.HashData(challengeBytes);
        var signature = await signer.SignWithIdentityKeyAsync(challengeHash).ConfigureAwait(false);

        // Step 3: Verify challenge and receive a session token.
        var verifyResponse = await authnClient.verify_challengeAsync(
            new VerifyChallengeRequest
            {
                ProtectedChallenge = challengeResponse.ProtectedChallenge,
                Signature = ByteString.CopyFrom(signature),
                PublicKey = ByteString.CopyFrom(identityPubKey),
            }).ConfigureAwait(false);

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(verifyResponse.ExpirationTimestamp);
        return new CachedToken(verifyResponse.SessionToken, expiresAt, DateTime.UtcNow.Ticks);
    }
}
