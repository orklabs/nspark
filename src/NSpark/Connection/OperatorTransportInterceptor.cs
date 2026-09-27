using System.Diagnostics;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace NSpark.Connection;

/// <summary>
/// Innermost interceptor on every operator call, authenticated or not:
/// <list type="bullet">
///   <item><description>a unary call without a deadline gets <see cref="DefaultTimeout"/> — the
///   reference SDK's last-resort cap. Without any deadline, a connection that looks alive but
///   never answers parks the caller until the process restarts;</description></item>
///   <item><description>the operators' clock (<see cref="ServerTimeSync"/>) is fed from the
///   <c>date</c> and <c>x-processing-time-ms</c> headers of every answer. Innermost, so the
///   measured round trip excludes any authentication the call waited for first.</description></item>
/// </list>
/// The event subscription is a long-lived stream and gets no deadline.
/// </summary>
internal sealed class OperatorTransportInterceptor : Interceptor
{
    /// <summary>Deadline applied to a unary call that has none.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly ServerTimeSync _clock;

    public OperatorTransportInterceptor(ServerTimeSync clock)
    {
        _clock = clock;
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var sent = Stopwatch.GetTimestamp();
        var call = continuation(request, WithDefaultDeadline(context));
        ObserveHeaders(call.ResponseHeadersAsync, sent);
        return call;
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var sent = Stopwatch.GetTimestamp();
        var call = continuation(request, context);
        ObserveHeaders(call.ResponseHeadersAsync, sent);
        return call;
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        BlockingUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(request, WithDefaultDeadline(context));
    }

    internal static ClientInterceptorContext<TRequest, TResponse> WithDefaultDeadline<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        if (context.Options.Deadline is not null)
        {
            return context;
        }

        return new ClientInterceptorContext<TRequest, TResponse>(
            context.Method,
            context.Host,
            context.Options.WithDeadline(DateTime.UtcNow + DefaultTimeout));
    }

    private void ObserveHeaders(Task<Metadata> headers, long sent)
    {
        headers.ContinueWith(
            (task, state) =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    ((ServerTimeSync)state!).Record(task.Result, sent, Stopwatch.GetTimestamp());
                }
                else
                {
                    // Observed so a failed call does not surface as an unobserved task exception.
                    _ = task.Exception;
                }
            },
            _clock,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
