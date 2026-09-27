using System.Diagnostics;
using System.Globalization;
using Grpc.Core;

namespace NSpark.Connection;

/// <summary>
/// The operators' clock as this process estimates it, like the reference SDK's
/// <c>ServerTimeSync</c>. Every operator answer carries its <c>date</c> (whole seconds) and how
/// long the operator took to process it (<c>x-processing-time-ms</c>); half the remaining round
/// trip is added to the date, and the estimate then advances on the monotonic clock, so a local
/// clock that is wrong — or is changed — does not move it. Until the first answer the local clock
/// is used.
/// </summary>
/// <remarks>
/// Session-token expiry is the operators' time and token transactions are checked against their
/// clock, so both are compared with this estimate rather than <see cref="DateTimeOffset.UtcNow"/>.
/// </remarks>
internal sealed class ServerTimeSync
{
    internal const string DateHeader = "date";
    internal const string ProcessingTimeHeader = "x-processing-time-ms";

    private static readonly string[] s_dateFormats =
    [
        // Go's http.TimeFormat and time.RFC1123, which the operators send.
        "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
        "ddd, dd MMM yyyy HH:mm:ss 'UTC'",
        "ddd, d MMM yyyy HH:mm:ss 'GMT'",
        "ddd, d MMM yyyy HH:mm:ss 'UTC'",
    ];

    private sealed record Sample(DateTimeOffset ServerTime, long Timestamp);

    private Sample? _sample;

    /// <summary>Whether at least one operator answer has been recorded.</summary>
    public bool IsSynced => Volatile.Read(ref _sample) is not null;

    /// <summary>
    /// The operators' current time: the latest estimate advanced by the monotonic clock, or the
    /// local clock before any answer.
    /// </summary>
    public DateTimeOffset ServerNow
    {
        get
        {
            var sample = Volatile.Read(ref _sample);
            return sample is null
                ? DateTimeOffset.UtcNow
                : sample.ServerTime + Stopwatch.GetElapsedTime(sample.Timestamp);
        }
    }

    /// <summary>Unix seconds on the operators' clock.</summary>
    public long GetServerUnixSeconds() => ServerNow.ToUnixTimeSeconds();

    /// <summary>
    /// Records one answer from its headers, for a call sent at <paramref name="sentTimestamp"/> and
    /// answered at <paramref name="receivedTimestamp"/> (<see cref="Stopwatch.GetTimestamp"/>
    /// values). Answers without both headers are ignored.
    /// </summary>
    public void Record(Metadata? headers, long sentTimestamp, long receivedTimestamp)
    {
        if (headers is null)
        {
            return;
        }

        Record(headers.GetValue(DateHeader), headers.GetValue(ProcessingTimeHeader), sentTimestamp, receivedTimestamp);
    }

    /// <summary>
    /// Records one answer: its <c>date</c> and processing time, sent at
    /// <paramref name="sentTimestamp"/> and answered at <paramref name="receivedTimestamp"/>. A
    /// header that does not parse is ignored.
    /// </summary>
    public void Record(string? date, string? processingTimeMs, long sentTimestamp, long receivedTimestamp)
    {
        if (!TryParseDate(date, out var serverDate)
            || !double.TryParse(processingTimeMs, NumberStyles.Float, CultureInfo.InvariantCulture, out var processingMs)
            || processingMs < 0
            || double.IsNaN(processingMs)
            || double.IsInfinity(processingMs))
        {
            return;
        }

        var roundTripMs = Math.Max(0, Stopwatch.GetElapsedTime(sentTimestamp, receivedTimestamp).TotalMilliseconds - processingMs);
        var estimate = new Sample(serverDate.AddMilliseconds(roundTripMs / 2), receivedTimestamp);
        Volatile.Write(ref _sample, estimate);
    }

    /// <summary>Parses an HTTP <c>date</c> header (RFC 1123, GMT or UTC).</summary>
    internal static bool TryParseDate(string? value, out DateTimeOffset date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return DateTimeOffset.TryParseExact(
            value.Trim(),
            s_dateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out date);
    }
}
