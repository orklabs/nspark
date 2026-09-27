using Grpc.Core;
using Grpc.Core.Interceptors;

namespace NSpark.Connection;

/// <summary>
/// Keeps a wallet's operator calls on a live session token, like the reference SDK's auth
/// middleware.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><description>Every attempt of a unary call that carries an <c>authorization</c> header is
///   sent with the operator's current token: the cached one, or a freshly authenticated one after
///   an invalidation.</description></item>
///   <item><description>An UNAUTHENTICATED answer drops that token (if it is still the cached one)
///   and the call is re-issued with a new one, up to <see cref="MaxAttempts"/> attempts in all.
///   The operators answer UNAUTHENTICATED only from their pre-handler interceptors, so nothing ran
///   server-side and re-issuing is safe.</description></item>
///   <item><description>A streaming call keeps the token it was started with; an UNAUTHENTICATED
///   answer drops the token so the next subscription authenticates again.</description></item>
/// </list>
/// Calls to the authentication service and calls without a bearer token pass through unchanged.
/// </remarks>
internal sealed class AuthRetryInterceptor : Interceptor
{
    /// <summary>Attempts of an authenticated unary call, the first included.</summary>
    internal const int MaxAttempts = 3;

    internal const string AuthorizationHeader = "authorization";
    private const string BearerPrefix = "Bearer ";

    private readonly Func<CancellationToken, Task<string>> _currentToken;
    private readonly Action<string> _invalidate;

    /// <param name="currentToken">The operator's current session token (cached or fresh).</param>
    /// <param name="invalidate">
    /// Drops the given token from the cache if it is still the operator's current one: a late
    /// rejection of an older token must not evict a newer one another call already obtained.
    /// </param>
    public AuthRetryInterceptor(Func<CancellationToken, Task<string>> currentToken, Action<string> invalidate)
    {
        _currentToken = currentToken;
        _invalidate = invalidate;
    }

    /// <summary>Whether the call site asked for an authenticated call (it attached a bearer token).</summary>
    internal static bool IsAuthenticated(Metadata? headers) => BearerToken(headers) is not null;

    /// <summary>The bearer token in <paramref name="headers"/>, if any.</summary>
    internal static string? BearerToken(Metadata? headers)
    {
        if (headers is null)
        {
            return null;
        }

        foreach (var entry in headers)
        {
            if (!entry.IsBinary
                && string.Equals(entry.Key, AuthorizationHeader, StringComparison.OrdinalIgnoreCase)
                && entry.Value.StartsWith(BearerPrefix, StringComparison.Ordinal))
            {
                return entry.Value[BearerPrefix.Length..];
            }
        }

        return null;
    }

    /// <summary><paramref name="headers"/> with the authorization header replaced by <paramref name="token"/>.</summary>
    internal static Metadata WithToken(Metadata? headers, string token)
    {
        var result = new Metadata();
        if (headers is not null)
        {
            foreach (var entry in headers)
            {
                if (!string.Equals(entry.Key, AuthorizationHeader, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(entry);
                }
            }
        }

        result.Add(AuthorizationHeader, BearerPrefix + token);
        return result;
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        if (string.Equals(context.Method.ServiceName, GrpcConnectionPool.AuthnService, StringComparison.Ordinal)
            || !IsAuthenticated(context.Options.Headers))
        {
            return continuation(request, context);
        }

        var attempts = new UnaryAttempts<TResponse>();
        var response = RunAsync(request, context, continuation, attempts);
        return new AsyncUnaryCall<TResponse>(
            response,
            attempts.Headers,
            attempts.GetStatus,
            attempts.GetTrailers,
            attempts.Dispose);
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var token = BearerToken(context.Options.Headers);
        var call = continuation(request, context);
        if (token is null)
        {
            return call;
        }

        return new AsyncServerStreamingCall<TResponse>(
            new UnauthenticatedObserver<TResponse>(call.ResponseStream, () => _invalidate(token)),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    private async Task<TResponse> RunAsync<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation,
        UnaryAttempts<TResponse> attempts)
        where TRequest : class
        where TResponse : class
    {
        var ct = context.Options.CancellationToken;
        for (var attempt = 1; ; attempt++)
        {
            string token;
            try
            {
                token = await _currentToken(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                attempts.Fail(ex);
                throw;
            }

            var attemptContext = new ClientInterceptorContext<TRequest, TResponse>(
                context.Method,
                context.Host,
                context.Options.WithHeaders(WithToken(context.Options.Headers, token)));
            var call = continuation(request, attemptContext);
            attempts.Start(call);
            var retry = false;
            try
            {
                return await call.ResponseAsync.ConfigureAwait(false);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
            {
                _invalidate(token);
                if (attempt >= MaxAttempts || ct.IsCancellationRequested)
                {
                    throw;
                }

                retry = true;
            }
            finally
            {
                if (retry)
                {
                    // An attempt being replaced: its outcome is no longer the call's.
                    call.Dispose();
                }
                else
                {
                    attempts.Complete(call);
                }
            }
        }
    }

    /// <summary>
    /// The attempts of one intercepted unary call: headers, status and trailers are the latest
    /// attempt's.
    /// </summary>
    private sealed class UnaryAttempts<TResponse> : IDisposable
    {
        private readonly TaskCompletionSource<Metadata> _headers = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private AsyncUnaryCall<TResponse>? _current;

        public UnaryAttempts()
        {
            // Observed so an unread failure does not surface as an unobserved task exception.
            _headers.Task.ContinueWith(
                t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public Task<Metadata> Headers => _headers.Task;

        public void Start(AsyncUnaryCall<TResponse> call)
        {
            _current = call;
        }

        /// <summary>The call's final attempt: its headers become the call's.</summary>
        public void Complete(AsyncUnaryCall<TResponse> call)
        {
            call.ResponseHeadersAsync.ContinueWith(
                (task, state) =>
                {
                    var headers = (TaskCompletionSource<Metadata>)state!;
                    if (task.IsCompletedSuccessfully)
                    {
                        headers.TrySetResult(task.Result);
                    }
                    else if (task.IsCanceled)
                    {
                        headers.TrySetCanceled();
                    }
                    else
                    {
                        headers.TrySetException(task.Exception!.InnerExceptions);
                    }
                },
                _headers,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>The call ended before an attempt could be made (no token).</summary>
        public void Fail(Exception ex)
        {
            _headers.TrySetException(ex);
        }

        public Status GetStatus() =>
            _current?.GetStatus() ?? throw new InvalidOperationException("The call has not started.");

        public Metadata GetTrailers() =>
            _current?.GetTrailers() ?? throw new InvalidOperationException("The call has not started.");

        public void Dispose() => _current?.Dispose();
    }

    /// <summary>
    /// Passes a response stream through unchanged and calls <c>onUnauthenticated</c> when it ends
    /// with an UNAUTHENTICATED status.
    /// </summary>
    private sealed class UnauthenticatedObserver<T> : IAsyncStreamReader<T>
    {
        private readonly IAsyncStreamReader<T> _inner;
        private readonly Action _onUnauthenticated;

        public UnauthenticatedObserver(IAsyncStreamReader<T> inner, Action onUnauthenticated)
        {
            _inner = inner;
            _onUnauthenticated = onUnauthenticated;
        }

        public T Current => _inner.Current;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            try
            {
                return await _inner.MoveNext(cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
            {
                _onUnauthenticated();
                throw;
            }
        }
    }
}
