namespace NSpark.Models;

/// <summary>
/// Base type for events emitted by the Signing Operator event stream.
/// Pattern-match concrete subclasses to handle specific event categories.
/// </summary>
public abstract record SparkEvent;

/// <summary>
/// A payment to this wallet arrived. The stream claims it first (best effort; one it cannot claim
/// yet stays pending for the next claim pass), and on every connection it claims and reports the
/// payments that arrived while it was down. The counter-transfers of the wallet's own swaps and
/// transfers to itself are not reported: the operation that made them claims them.
/// </summary>
/// <param name="Transfer">The transfer descriptor.</param>
public sealed record TransferReceivedEvent(SparkTransfer Transfer) : SparkEvent;

/// <summary>
/// An outgoing transfer changed status — initiated, awaiting or applied the sender's key tweak, or
/// returned — so one transfer is reported several times; <see cref="SparkTransfer.Status"/> says
/// which.
/// </summary>
/// <param name="Transfer">The transfer descriptor.</param>
public sealed record TransferSentEvent(SparkTransfer Transfer) : SparkEvent;

/// <summary>A deposit's leaf became available.</summary>
/// <param name="TreeId">Identifier of the resulting Spark tree.</param>
public sealed record DepositConfirmedEvent(string TreeId) : SparkEvent;

/// <summary>The event stream connection has been established.</summary>
public sealed record ConnectedEvent : SparkEvent;

/// <summary>
/// The event stream failed, or the operator ended it; it subscribes again after
/// <paramref name="RetryIn"/>.
/// </summary>
/// <param name="Attempt">The attempts since the stream was last connected.</param>
/// <param name="RetryIn">How long the stream waits before subscribing again.</param>
/// <param name="Reason">Why the previous subscription ended.</param>
public sealed record ReconnectingEvent(int Attempt, TimeSpan RetryIn, string Reason) : SparkEvent;
