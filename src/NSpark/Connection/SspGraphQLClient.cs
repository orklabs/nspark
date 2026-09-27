using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSpark.Exceptions;

namespace NSpark.Connection;

/// <summary>
/// GraphQL client for the Spark Service Provider (SSP).
/// Uses a shared HttpClient with per-wallet auth tokens.
/// </summary>
/// <remarks>
/// Requests are retried on transient failures (<see cref="SspRetryPolicy"/>), and a session the
/// SSP no longer honours is dropped and the request retried once with a fresh one.
/// </remarks>
internal sealed class SspGraphQLClient
{
    private readonly HttpClient _httpClient;
    private readonly string _sspUrl;
    private readonly Func<CancellationToken, Task<string>> _getToken;
    private readonly Action<string> _invalidateToken;
    private readonly SspRetryPolicy _retry;

    public SspGraphQLClient(
        HttpClient httpClient,
        string sspUrl,
        Func<CancellationToken, Task<string>> getToken,
        Action<string>? invalidateToken = null,
        SspRetryPolicy? retry = null)
    {
        _httpClient = httpClient;
        _sspUrl = sspUrl;
        _getToken = getToken;
        _invalidateToken = invalidateToken ?? (_ => { });
        _retry = retry ?? SspRetryPolicy.Standard;
    }

    public async Task<T> ExecuteAsync<T>(string query, object? variables = null, CancellationToken ct = default)
    {
        var token = await _getToken(ct).ConfigureAwait(false);
        try
        {
            return await SspGraphQL.PostAsync<T>(_httpClient, _sspUrl, token, query, variables, _retry, ct).ConfigureAwait(false);
        }
        catch (SparkSspException ex) when (IsAuthFailure(ex))
        {
            // The SSP no longer honours the cached session (it stays "valid" by its own
            // valid_until for hours): drop it and retry ONCE with a fresh one, instead of failing
            // every SSP call — fee quotes, coop exits, invoices — until the process restarts.
            _invalidateToken(token);
            token = await _getToken(ct).ConfigureAwait(false);
            return await SspGraphQL.PostAsync<T>(_httpClient, _sspUrl, token, query, variables, _retry, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An HTTP 401/403, or a GraphQL error that names authentication. A false positive only costs
    /// one re-authentication and one retry.
    /// </summary>
    internal static bool IsAuthFailure(SparkSspException error)
    {
        if (error.HttpStatusCode is 401 or 403)
        {
            return true;
        }

        string[] markers =
        [
            "unauthenticated", "unauthorized", "not authorized", "authentication",
            "invalid token", "expired token", "token expired",
        ];
        return error.GraphQLErrors.Any(message =>
            markers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>
/// How SSP requests are retried: the reference SDK's fetch wrapper — up to 5 more attempts, 1 s
/// doubling to 10 s between them, on HTTP 502, 503 and 504 and on a connection that failed, but
/// not on a timeout or a cancellation. Mutations are retried too; the ones that move funds are
/// keyed by the transfer they pay from, and the SSP answers a repeat with its first answer.
/// </summary>
internal sealed record SspRetryPolicy(int MaxRetries, TimeSpan BaseDelay, TimeSpan MaxDelay)
{
    public static SspRetryPolicy Standard { get; } = new(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

    /// <summary>A single attempt.</summary>
    public static SspRetryPolicy None { get; } = new(0, TimeSpan.Zero, TimeSpan.Zero);

    public static bool IsRetryableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    public TimeSpan DelayAfter(int attempt)
    {
        var factor = Math.Pow(2, Math.Min(attempt, 20));
        var delay = TimeSpan.FromTicks((long)Math.Min(BaseDelay.Ticks * factor, MaxDelay.Ticks));
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    /// <summary>
    /// Sends a request built by <paramref name="build"/> (a request cannot be sent twice),
    /// repeated per the policy. A retryable status on the last attempt is returned as is.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        Func<HttpRequestMessage> build,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = build();
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < MaxRetries && !ct.IsCancellationRequested)
            {
                await Task.Delay(DelayAfter(attempt), ct).ConfigureAwait(false);
                continue;
            }

            if (IsRetryableStatus(response.StatusCode) && attempt < MaxRetries)
            {
                response.Dispose();
                await Task.Delay(DelayAfter(attempt), ct).ConfigureAwait(false);
                continue;
            }

            return response;
        }
    }
}

/// <summary>One GraphQL request to the SSP: POST, JSON, retried per <see cref="SspRetryPolicy"/>.</summary>
internal static class SspGraphQL
{
    private const string Operation = "ssp.graphql";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<T> PostAsync<T>(
        HttpClient http,
        string sspUrl,
        string? token,
        string query,
        object? variables,
        SspRetryPolicy retry,
        CancellationToken ct)
    {
        using var response = await retry.SendAsync(
            http,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, sspUrl)
                {
                    Content = JsonContent.Create(new GraphQLRequest(query, variables), options: s_jsonOptions),
                };
                if (token is not null)
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }
                return request;
            },
            ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            throw new SparkSspException(Operation, $"The SSP answered HTTP {status}.")
            {
                HttpStatusCode = status,
            };
        }

        GraphQLResponse<T>? result;
        try
        {
            result = await response.Content.ReadFromJsonAsync<GraphQLResponse<T>>(s_jsonOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new SparkSspException(Operation, "The SSP answered with invalid JSON.", ex);
        }

        if (result is null)
        {
            throw new SparkSspException(Operation, "The SSP returned an empty response.");
        }

        if (result.Errors is { Count: > 0 })
        {
            var messages = result.Errors.Select(e => e.Message ?? string.Empty).ToList();
            throw new SparkSspException(Operation, $"SSP GraphQL errors: {string.Join("; ", messages)}")
            {
                GraphQLErrors = messages,
            };
        }

        return result.Data ?? throw new SparkSspException(Operation, "The SSP returned no data.");
    }

    private sealed record GraphQLRequest(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("variables")] object? Variables);

    private sealed record GraphQLResponse<TData>(
        [property: JsonPropertyName("data")] TData? Data,
        [property: JsonPropertyName("errors")] List<GraphQLError>? Errors);

    private sealed record GraphQLError(
        [property: JsonPropertyName("message")] string? Message);
}
