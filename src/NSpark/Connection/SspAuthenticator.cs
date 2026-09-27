using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using NSpark.Exceptions;
using NSpark.GraphQL;
using NSpark.Signer;

namespace NSpark.Connection;

/// <summary>
/// Challenge-response authentication with the Spark Service Provider (SSP) via GraphQL.
/// Tokens are cached per identity public key until shortly before their <c>valid_until</c>;
/// concurrent callers for one identity share one authentication.
/// </summary>
internal sealed class SspAuthenticator
{
    private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromMinutes(1);

    private readonly HttpClient _httpClient;
    private readonly string _sspUrl;
    private readonly SspRetryPolicy _retry;
    private readonly ConcurrentDictionary<string, CachedToken> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<CachedToken>>> _inFlight = new(StringComparer.Ordinal);

    private sealed record CachedToken(string Token, DateTimeOffset ExpiresAt);

    public SspAuthenticator(HttpClient httpClient, string sspUrl, SspRetryPolicy? retry = null)
    {
        _httpClient = httpClient;
        _sspUrl = sspUrl;
        _retry = retry ?? SspRetryPolicy.Standard;
    }

    public async Task<string> GetTokenAsync(ISparkSigner signer, byte[] identityPubKey, CancellationToken ct = default)
    {
        var cacheKey = Convert.ToHexString(identityPubKey);
        if (_cache.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresAt > DateTimeOffset.UtcNow + TokenRefreshBuffer)
        {
            return cached.Token;
        }

        var candidate = new Lazy<Task<CachedToken>>(
            () => AuthenticateAndCacheAsync(signer, identityPubKey, cacheKey),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var shared = _inFlight.GetOrAdd(cacheKey, candidate);
        if (ReferenceEquals(shared, candidate))
        {
            _ = candidate.Value.ContinueWith(
                task =>
                {
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
    /// Forget the cached session if it is still <paramref name="token"/>. Called when the SSP
    /// rejects the token before its <c>valid_until</c> (rotation, restart): the next call
    /// authenticates afresh instead of replaying the rejected token until the process restarts.
    /// </summary>
    public void Invalidate(byte[] identityPubKey, string token)
    {
        var cacheKey = Convert.ToHexString(identityPubKey);
        if (_cache.TryGetValue(cacheKey, out var cached) && string.Equals(cached.Token, token, StringComparison.Ordinal))
        {
            _cache.TryRemove(new KeyValuePair<string, CachedToken>(cacheKey, cached));
        }
    }

    private async Task<CachedToken> AuthenticateAndCacheAsync(ISparkSigner signer, byte[] identityPubKey, string cacheKey)
    {
        var token = await AuthenticateAsync(signer, identityPubKey).ConfigureAwait(false);
        _cache[cacheKey] = token;
        return token;
    }

    private async Task<CachedToken> AuthenticateAsync(ISparkSigner signer, byte[] identityPubKey)
    {
        var identityPubKeyHex = Convert.ToHexString(identityPubKey).ToLowerInvariant();

        // Step 1: Get challenge (no auth required)
        var challengeResponse = await SspGraphQL.PostAsync<GetChallengeResponse>(
            _httpClient, _sspUrl, token: null, Mutations.GetChallenge,
            new { public_key = identityPubKeyHex }, _retry, CancellationToken.None).ConfigureAwait(false);

        var protectedChallenge = challengeResponse.GetChallenge?.ProtectedChallenge
            ?? throw new SparkAuthenticationException("ssp.authenticate", "The SSP returned no challenge.");

        // Step 2: Sign the challenge (SSP uses base64url encoding)
        var challengeBytes = DecodeBase64Url(protectedChallenge);
        var challengeHash = SHA256.HashData(challengeBytes);
        var signature = await signer.SignWithIdentityKeyAsync(challengeHash).ConfigureAwait(false);
        var signatureBase64 = Convert.ToBase64String(signature);

        // Step 3: Verify challenge and get token
        var verifyResponse = await SspGraphQL.PostAsync<VerifyChallengeResponse>(
            _httpClient, _sspUrl, token: null, Mutations.VerifyChallenge,
            new
            {
                protected_challenge = protectedChallenge,
                signature = signatureBase64,
                identity_public_key = identityPubKeyHex,
            }, _retry, CancellationToken.None).ConfigureAwait(false);

        var data = verifyResponse.VerifyChallenge
            ?? throw new SparkAuthenticationException("ssp.authenticate", "The SSP returned no session.");
        var expiresAt = DateTimeOffset.TryParse(
            data.ValidUntil, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var validUntil)
            ? validUntil
            : DateTimeOffset.UtcNow.AddHours(1);
        return new CachedToken(data.SessionToken, expiresAt);
    }

    private static byte[] DecodeBase64Url(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException ex)
        {
            throw new SparkAuthenticationException("ssp.authenticate", "The SSP challenge is not base64url.", ex);
        }
    }
}
