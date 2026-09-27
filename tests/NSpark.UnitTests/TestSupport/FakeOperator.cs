using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSpark.Proto;
using NSpark.Proto.Authn;
using NSpark.Proto.Token;
using NSpark.Services;

namespace NSpark.UnitTests.TestSupport;

/// <summary>
/// What a <see cref="FakeOperatorHandler"/> answers, and what it was asked.
/// </summary>
/// <remarks>
/// It issues session tokens through the real authn RPCs (<c>session-1</c>, <c>session-2</c>, …)
/// and answers SparkService and SparkTokenService calls. Tokens the <c>rejects</c> predicate
/// names are refused with UNAUTHENTICATED the way an operator does: before any response headers
/// (its auth interceptor), or after them when an earlier operator interceptor already set a header
/// such as <c>x-trace-id</c>.
/// </remarks>
internal sealed class FakeOperatorState
{
    internal enum Rejection
    {
        BeforeHeaders,
        AfterHeaders,
    }

    /// <summary>How the event subscription behaves after its <c>connected</c> event.</summary>
    internal enum Subscription
    {
        /// <summary>The stream ends.</summary>
        End,

        /// <summary>One heartbeat, then 3 s of silence.</summary>
        HeartbeatThenSilence,

        /// <summary>3 s of silence, without heartbeats.</summary>
        Silence,
    }

    /// <summary>What <c>broadcast_transaction</c> does with a V3 transaction.</summary>
    internal enum Broadcast
    {
        /// <summary>Refuses it, as <c>start_transaction</c> does.</summary>
        Refuse,

        /// <summary>
        /// Finalizes it as the operators do (<see cref="FakeOperatorHandler.Finalize"/>), then alters
        /// the answer with <see cref="Tamper"/>, as a dishonest coordinator would.
        /// </summary>
        Finalize,
    }

    private readonly object _gate = new();
    private readonly Func<string, bool> _rejects;
    private readonly List<string> _issuedTokens = [];
    private readonly Queue<Status> _verifyFailures = new();
    private readonly List<string> _calls = [];
    private readonly List<(string Method, string? Timeout)> _timeouts = [];
    private readonly List<PreimageRequestWithTransfer> _heldSends = [];
    private readonly List<Transfer> _knownTransfers = [];
    private readonly List<TransferFilter> _transferFilters = [];
    private readonly List<Transfer> _pendingTransfers = [];
    private readonly List<OutputWithPreviousTransactionData> _tokenOutputs = [];
    private readonly List<int> _metadataRequestSizes = [];
    private readonly List<IReadOnlyList<string>> _startedSpends = [];
    private readonly List<(TokenTransaction Transaction, string? IdempotencyKey)> _startedTransactions = [];
    private readonly List<(BroadcastTransactionRequest Request, string? IdempotencyKey)> _broadcasts = [];
    private readonly List<(long Limit, long Offset)> _nodePages = [];
    private readonly List<(long Limit, long Offset)> _unusedAddressPages = [];
    private readonly List<string> _preimageSwapIdempotencyKeys = [];
    private readonly Dictionary<string, Queue<Status>> _callFailures = new(StringComparer.Ordinal);
    private List<TreeNode> _nodes = [];
    private List<(DepositAddressQueryResult Address, bool Used)> _singleUseAddresses = [];
    private int _challengesIssued;

    public FakeOperatorState(Func<string, bool> rejects, Rejection rejection = Rejection.BeforeHeaders, Address? depositAddress = null)
    {
        _rejects = rejects;
        RejectionMode = rejection;
        DepositAddress = depositAddress ?? new Address();
    }

    /// <summary>A state that accepts every token.</summary>
    public static FakeOperatorState Accepting() => new(_ => false);

    public Rejection RejectionMode { get; }

    /// <summary>What <c>generate_deposit_address</c> and <c>generate_static_deposit_address</c> hand out.</summary>
    public Address DepositAddress { get; }

    public Subscription SubscriptionMode { get; set; } = Subscription.End;

    /// <summary>How far the operator's clock is from this machine's; its answers carry its <c>date</c>.</summary>
    public TimeSpan ClockOffset { get; set; }

    /// <summary>How long <c>verify_challenge</c> takes.</summary>
    public TimeSpan VerifyDelay { get; set; }

    /// <summary>Whether <c>query_token_metadata</c> fails.</summary>
    public bool FailsTokenMetadata { get; set; }

    public Broadcast BroadcastMode { get; set; } = Broadcast.Refuse;

    /// <summary>How a finalized broadcast answer is altered before it is sent.</summary>
    public Action<FinalTokenTransaction> Tamper { get; set; } = _ => { };

    /// <summary>What every <c>initiate_preimage_swap_v3</c> fails with.</summary>
    public Status PreimageSwapError { get; set; } = new(StatusCode.Internal, "preimage swap failed");

    /// <summary>The operator's current time, as its date header and token expiries state it.</summary>
    public DateTimeOffset Now => DateTimeOffset.UtcNow + ClockOffset;

    public int ChallengesIssued => Volatile.Read(ref _challengesIssued);

    /// <summary>Session tokens handed out by <c>verify_challenge</c>, in order.</summary>
    public IReadOnlyList<string> IssuedTokens => Snapshot(_issuedTokens);

    /// <summary><c>"&lt;method&gt; &lt;authorization header&gt;"</c> for every service call received.</summary>
    public IReadOnlyList<string> Calls => Snapshot(_calls);

    /// <summary>The service methods called, in order.</summary>
    public IReadOnlyList<string> Methods => Calls.Select(c => c.Split(' ')[0]).ToList();

    /// <summary>The <c>grpc-timeout</c> header of every call, authn included, in order.</summary>
    public IReadOnlyList<(string Method, string? Timeout)> Timeouts => Snapshot(_timeouts);

    public IReadOnlyList<TransferFilter> TransferFilters => Snapshot(_transferFilters);

    public IReadOnlyList<int> MetadataRequestSizes => Snapshot(_metadataRequestSizes);

    /// <summary>The outputs (<see cref="TokenOutputLocks.Key"/>) each token transaction spends, in order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> StartedSpends => Snapshot(_startedSpends);

    /// <summary>The partial transaction and <c>x-idempotency-key</c> of each V2 <c>start_transaction</c>.</summary>
    public IReadOnlyList<(TokenTransaction Transaction, string? IdempotencyKey)> StartedTransactions => Snapshot(_startedTransactions);

    /// <summary>Each <c>broadcast_transaction</c> request and its <c>x-idempotency-key</c>, in order.</summary>
    public IReadOnlyList<(BroadcastTransactionRequest Request, string? IdempotencyKey)> Broadcasts => Snapshot(_broadcasts);

    /// <summary><c>(limit, offset)</c> of every <c>query_nodes</c> call.</summary>
    public IReadOnlyList<(long Limit, long Offset)> NodePages => Snapshot(_nodePages);

    /// <summary><c>(limit, offset)</c> of every <c>query_unused_deposit_addresses</c> call.</summary>
    public IReadOnlyList<(long Limit, long Offset)> UnusedAddressPages => Snapshot(_unusedAddressPages);

    /// <summary>The <c>x-idempotency-key</c> of every <c>initiate_preimage_swap_v3</c>, in order.</summary>
    public IReadOnlyList<string> PreimageSwapIdempotencyKeys => Snapshot(_preimageSwapIdempotencyKeys);

    /// <summary>The errors the next <c>verify_challenge</c> calls fail with, in order.</summary>
    public void FailNextVerifications(params Status[] errors)
    {
        lock (_gate)
        {
            _verifyFailures.Clear();
            foreach (var error in errors)
            {
                _verifyFailures.Enqueue(error);
            }
        }
    }

    /// <summary>The errors the next admitted calls of <paramref name="method"/> fail with, in order.</summary>
    public void FailNextCalls(string method, params Status[] errors)
    {
        lock (_gate)
        {
            _callFailures[method] = new Queue<Status>(errors);
        }
    }

    /// <summary>Nodes <c>query_nodes</c> pages through by id, as the operators page an owner query.</summary>
    public void SetNodes(IEnumerable<TreeNode> nodes)
    {
        lock (_gate)
        {
            _nodes = nodes.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>The wallet's single-use deposit addresses, newest first, and whether each was used.</summary>
    public void SetSingleUseAddresses(IEnumerable<(DepositAddressQueryResult Address, bool Used)> addresses)
    {
        lock (_gate)
        {
            _singleUseAddresses = addresses.ToList();
        }
    }

    /// <summary>Token outputs <c>query_token_outputs</c> returns, in one page.</summary>
    public void SetTokenOutputs(IEnumerable<OutputWithPreviousTransactionData> outputs)
    {
        lock (_gate)
        {
            _tokenOutputs.Clear();
            _tokenOutputs.AddRange(outputs);
        }
    }

    /// <summary>A Lightning send <c>query_htlc</c> reports as held, matched by transfer id.</summary>
    public void Hold(PreimageRequestWithTransfer send)
    {
        lock (_gate)
        {
            _heldSends.Add(send);
        }
    }

    /// <summary>A transfer <c>query_transfers_by_id</c> knows, matched by id.</summary>
    public void Know(Transfer transfer)
    {
        lock (_gate)
        {
            _knownTransfers.Add(transfer);
        }
    }

    /// <summary>A transfer <c>query_pending_transfers</c> reports until it is claimed.</summary>
    public void AddPending(Transfer transfer)
    {
        lock (_gate)
        {
            _pendingTransfers.Add(transfer);
        }
    }

    internal void IssueChallenge() => Interlocked.Increment(ref _challengesIssued);

    internal Status? NextVerifyFailure()
    {
        lock (_gate)
        {
            return _verifyFailures.Count > 0 ? _verifyFailures.Dequeue() : null;
        }
    }

    internal string IssueToken()
    {
        lock (_gate)
        {
            var token = $"session-{_issuedTokens.Count + 1}";
            _issuedTokens.Add(token);
            return token;
        }
    }

    internal Status? NextCallFailure(string method)
    {
        lock (_gate)
        {
            return _callFailures.TryGetValue(method, out var failures) && failures.Count > 0 ? failures.Dequeue() : null;
        }
    }

    internal void RecordTimeout(string method, string? timeout)
    {
        lock (_gate)
        {
            _timeouts.Add((method, timeout));
        }
    }

    /// <summary>Records the call; returns whether its token is accepted.</summary>
    internal bool Admit(string method, string authorization)
    {
        lock (_gate)
        {
            _calls.Add($"{method} {authorization}");
        }

        var token = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : authorization;
        return token.Length > 0 && !_rejects(token);
    }

    /// <summary>The page the operators return: every node without a limit, else at most 100 from <c>offset</c>.</summary>
    internal IReadOnlyList<TreeNode> NodesPage(long limit, long offset)
    {
        lock (_gate)
        {
            _nodePages.Add((limit, offset));
            var start = (int)Math.Min(offset, _nodes.Count);
            var end = limit > 0 ? (int)Math.Min(start + Math.Min(limit, 100), _nodes.Count) : _nodes.Count;
            return _nodes.GetRange(start, end - start);
        }
    }

    /// <summary>
    /// What <c>tree_query_handler.go</c> answers: without a limit or offset, every unused address;
    /// with one, a page of at most 100 addresses, used ones included, from which it drops the used
    /// ones, and a next offset only when the page is still full after that.
    /// </summary>
    internal QueryUnusedDepositAddressesResponse UnusedDepositAddresses(long limit, long offset)
    {
        lock (_gate)
        {
            _unusedAddressPages.Add((limit, offset));
            var paged = limit > 0 || offset > 0;
            var pageSize = limit is > 0 and < 100 ? (int)limit : 100;
            var page = paged ? _singleUseAddresses.Skip((int)offset).Take(pageSize) : _singleUseAddresses;
            var response = new QueryUnusedDepositAddressesResponse();
            response.DepositAddresses.AddRange(page.Where(a => !a.Used).Select(a => a.Address));
            response.Offset = paged && response.DepositAddresses.Count == pageSize ? offset + pageSize : -1;
            return response;
        }
    }

    internal IReadOnlyList<PreimageRequestWithTransfer> HeldSends(IEnumerable<string> transferIds)
    {
        var ids = transferIds.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            return _heldSends.Where(s => s.Transfer is not null && ids.Contains(s.Transfer.Id)).ToList();
        }
    }

    internal IReadOnlyList<Transfer> KnownTransfers(IEnumerable<string> transferIds)
    {
        var ids = transferIds.Select(id => id.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            return _knownTransfers.Where(t => ids.Contains(t.Id)).ToList();
        }
    }

    internal IReadOnlyList<Transfer> PendingTransfers(long limit, long offset)
    {
        lock (_gate)
        {
            var start = (int)Math.Min(offset, _pendingTransfers.Count);
            var end = limit > 0 ? (int)Math.Min(start + limit, _pendingTransfers.Count) : _pendingTransfers.Count;
            return _pendingTransfers.GetRange(start, end - start);
        }
    }

    internal void RecordTransferFilter(TransferFilter filter)
    {
        lock (_gate)
        {
            _transferFilters.Add(filter);
        }
    }

    internal IReadOnlyList<OutputWithPreviousTransactionData> TokenOutputs() => Snapshot(_tokenOutputs);

    internal void RecordMetadataRequest(int size)
    {
        lock (_gate)
        {
            _metadataRequestSizes.Add(size);
        }
    }

    internal void RecordStart(TokenTransaction transaction, string? idempotencyKey)
    {
        lock (_gate)
        {
            _startedSpends.Add(Spends(transaction.TransferInput));
            _startedTransactions.Add((transaction, idempotencyKey));
        }
    }

    internal void RecordBroadcast(BroadcastTransactionRequest request, string? idempotencyKey)
    {
        lock (_gate)
        {
            _startedSpends.Add(Spends(request.PartialTokenTransaction.TransferInput));
            _broadcasts.Add((request, idempotencyKey));
        }
    }

    internal void RecordPreimageSwap(string idempotencyKey)
    {
        lock (_gate)
        {
            _preimageSwapIdempotencyKeys.Add(idempotencyKey);
        }
    }

    private static List<string> Spends(TokenTransferInput? input) =>
        input is null
            ? []
            : input.OutputsToSpend
                .Select(o => $"{Convert.ToHexString(o.PrevTokenTransactionHash.Span)}:{o.PrevTokenTransactionVout}")
                .ToList();

    private List<T> Snapshot<T>(List<T> list)
    {
        lock (_gate)
        {
            return [.. list];
        }
    }
}

/// <summary>
/// A signing-operator stand-in under a real <c>GrpcChannel</c>: the channel's HTTP handler,
/// speaking gRPC's framing, trailers and status headers. The SDK's whole transport stack — service
/// config and retries, deadlines, message limits, interceptors, authenticator — runs as it does
/// against the operators; only the network is replaced.
/// </summary>
internal sealed class FakeOperatorHandler : HttpMessageHandler
{
    internal static readonly Status Unauthenticated = new(StatusCode.Unauthenticated, "failed to verify token: token has expired");

    internal static readonly Status StartRefusal = new(StatusCode.FailedPrecondition, "the fake operator starts nothing");

    /// <summary>The identifier <c>broadcast_transaction</c> reports for a created token.</summary>
    internal static readonly byte[] CreatedTokenIdentifier = Enumerable.Repeat((byte)0x07, 32).ToArray();

    private readonly FakeOperatorState _state;

    public FakeOperatorHandler(FakeOperatorState state)
    {
        _state = state;
    }

    /// <summary>
    /// What the operators answer a V3 transaction with: the partial transaction, with a revocation
    /// commitment per output and, for a create, the creation entity key.
    /// </summary>
    internal static FinalTokenTransaction Finalize(PartialTokenTransaction partial)
    {
        var final = new FinalTokenTransaction
        {
            Version = partial.Version,
            TokenTransactionMetadata = partial.TokenTransactionMetadata?.Clone(),
        };
        switch (partial.TokenInputsCase)
        {
            case PartialTokenTransaction.TokenInputsOneofCase.MintInput:
                final.MintInput = partial.MintInput.Clone();
                break;
            case PartialTokenTransaction.TokenInputsOneofCase.TransferInput:
                final.TransferInput = partial.TransferInput.Clone();
                break;
            case PartialTokenTransaction.TokenInputsOneofCase.CreateInput:
                var create = partial.CreateInput.Clone();
                create.CreationEntityPublicKey = ByteString.CopyFrom([0x02, .. Enumerable.Repeat((byte)0x0E, 32)]);
                final.CreateInput = create;
                break;
        }

        for (var index = 0; index < partial.PartialTokenOutputs.Count; index++)
        {
            final.FinalTokenOutputs.Add(new FinalTokenOutput
            {
                PartialTokenOutput = partial.PartialTokenOutputs[index].Clone(),
                RevocationCommitment = ByteString.CopyFrom([0x03, .. Enumerable.Repeat((byte)index, 32)]),
            });
        }

        return final;
    }

    internal static byte[] Frame(IMessage message)
    {
        var body = message.ToByteArray();
        var framed = new byte[5 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(1), (uint)body.Length);
        body.CopyTo(framed, 5);
        return framed;
    }

    internal static byte[] Unframe(byte[] body)
    {
        if (body.Length < 5)
        {
            return [];
        }

        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1));
        return body.AsSpan(5, length).ToArray();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var separator = path.IndexOf('/', StringComparison.Ordinal);
        var service = path[..separator];
        var method = path[(separator + 1)..];
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var payload = Unframe(body);
        _state.RecordTimeout(method, Header(request, "grpc-timeout"));

        if (service == "spark_authn.SparkAuthnService")
        {
            return await AuthnAsync(method, cancellationToken);
        }

        if (!_state.Admit(method, Header(request, "authorization") ?? string.Empty))
        {
            return Reject();
        }

        if (_state.NextCallFailure(method) is { } failure)
        {
            return Error(failure);
        }

        var idempotencyKey = Header(request, "x-idempotency-key");
        return (service, method) switch
        {
            ("spark.SparkService", _) => SparkService(method, payload, idempotencyKey, cancellationToken),
            ("spark_token.SparkTokenService", _) => TokenService(method, payload, idempotencyKey),
            _ => Error(new Status(StatusCode.Unimplemented, $"unknown service {service}")),
        };
    }

    private async Task<HttpResponseMessage> AuthnAsync(string method, CancellationToken ct)
    {
        switch (method)
        {
            case "get_challenge":
                _state.IssueChallenge();
                return Ok(new GetChallengeResponse
                {
                    ProtectedChallenge = new ProtectedChallenge
                    {
                        Challenge = new Challenge
                        {
                            Nonce = ByteString.CopyFrom(Enumerable.Repeat((byte)7, 32).ToArray()),
                            Timestamp = _state.Now.ToUnixTimeSeconds(),
                        },
                    },
                });
            case "verify_challenge":
                if (_state.VerifyDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_state.VerifyDelay, ct);
                }

                if (_state.NextVerifyFailure() is { } failure)
                {
                    return Error(failure);
                }

                return Ok(new VerifyChallengeResponse
                {
                    SessionToken = _state.IssueToken(),
                    ExpirationTimestamp = _state.Now.AddHours(1).ToUnixTimeSeconds(),
                });
            default:
                return Error(new Status(StatusCode.Unimplemented, $"the fake operator does not implement {method}"));
        }
    }

    private HttpResponseMessage SparkService(string method, byte[] payload, string? idempotencyKey, CancellationToken ct)
    {
        switch (method)
        {
            case "query_nodes":
            {
                var query = QueryNodesRequest.Parser.ParseFrom(payload);
                var response = new QueryNodesResponse { Offset = -1 };
                foreach (var node in _state.NodesPage(query.Limit, query.Offset))
                {
                    response.Nodes[node.Id] = node;
                }

                return Ok(response);
            }

            case "query_unused_deposit_addresses":
            {
                var query = QueryUnusedDepositAddressesRequest.Parser.ParseFrom(payload);
                return Ok(_state.UnusedDepositAddresses(query.Limit, query.Offset));
            }

            case "generate_deposit_address":
                return Ok(new GenerateDepositAddressResponse { DepositAddress = _state.DepositAddress });
            case "generate_static_deposit_address":
                return Ok(new GenerateStaticDepositAddressResponse { DepositAddress = _state.DepositAddress });
            case "query_htlc":
            {
                var query = QueryHtlcRequest.Parser.ParseFrom(payload);
                var response = new QueryHtlcResponse { Offset = -1 };
                response.PreimageRequests.AddRange(_state.HeldSends(query.TransferIds));
                return Ok(response);
            }

            case "query_all_transfers":
                _state.RecordTransferFilter(TransferFilter.Parser.ParseFrom(payload));
                return Ok(new QueryTransfersResponse { Offset = -1 });
            case "query_pending_transfers":
            {
                var filter = TransferFilter.Parser.ParseFrom(payload);
                var response = new QueryTransfersResponse { Offset = -1 };
                response.Transfers.AddRange(_state.PendingTransfers(filter.Limit, filter.Offset));
                return Ok(response);
            }

            case "query_transfers_by_id":
            {
                var query = QueryTransfersByIdRequest.Parser.ParseFrom(payload);
                var response = new QueryTransfersResponse { Offset = -1 };
                response.Transfers.AddRange(_state.KnownTransfers(query.TransferIds));
                return Ok(response);
            }

            case "initiate_preimage_swap_v3":
                _state.RecordPreimageSwap(idempotencyKey ?? string.Empty);
                return Error(_state.PreimageSwapError);
            case "subscribe_to_events":
                return Subscribe(_state.SubscriptionMode, ct);
            default:
                return Error(new Status(StatusCode.Unimplemented, $"the fake operator does not implement {method}"));
        }
    }

    private HttpResponseMessage TokenService(string method, byte[] payload, string? idempotencyKey)
    {
        switch (method)
        {
            case "query_token_outputs":
            {
                var response = new QueryTokenOutputsResponse();
                response.OutputsWithPreviousTransactionData.AddRange(_state.TokenOutputs());
                return Ok(response);
            }

            case "query_token_metadata":
            {
                var ids = QueryTokenMetadataRequest.Parser.ParseFrom(payload).TokenIdentifiers;
                _state.RecordMetadataRequest(ids.Count);
                if (_state.FailsTokenMetadata)
                {
                    return Error(new Status(StatusCode.Internal, "metadata unavailable"));
                }

                if (ids.Count > 500)
                {
                    return Error(new Status(
                        StatusCode.InvalidArgument,
                        $"too many token identifiers in filter: got {ids.Count}, max 500"));
                }

                var response = new QueryTokenMetadataResponse();
                response.TokenMetadata.AddRange(ids.Select(id => new TokenMetadata
                {
                    TokenIdentifier = id,
                    TokenName = "Spam",
                    TokenTicker = "SPM",
                    MaxSupply = ByteString.CopyFrom(new byte[16]),
                }));
                return Ok(response);
            }

            case "start_transaction":
                _state.RecordStart(StartTransactionRequest.Parser.ParseFrom(payload).PartialTokenTransaction, idempotencyKey);
                return Error(StartRefusal);
            case "broadcast_transaction":
            {
                var request = BroadcastTransactionRequest.Parser.ParseFrom(payload);
                _state.RecordBroadcast(request, idempotencyKey);
                if (_state.BroadcastMode == FakeOperatorState.Broadcast.Refuse)
                {
                    return Error(StartRefusal);
                }

                var final = Finalize(request.PartialTokenTransaction);
                _state.Tamper(final);
                var response = new BroadcastTransactionResponse
                {
                    FinalTokenTransaction = final,
                    CommitStatus = CommitStatus.CommitFinalized,
                };
                if (request.PartialTokenTransaction.TokenInputsCase == PartialTokenTransaction.TokenInputsOneofCase.CreateInput)
                {
                    response.TokenIdentifier = ByteString.CopyFrom(CreatedTokenIdentifier);
                }

                return Ok(response);
            }

            default:
                return Error(new Status(StatusCode.Unimplemented, $"the fake operator does not implement {method}"));
        }
    }

    /// <summary>The event subscription: <c>connected</c>, then as <paramref name="mode"/> says.</summary>
    private HttpResponseMessage Subscribe(FakeOperatorState.Subscription mode, CancellationToken ct)
    {
        return Streaming(
            async (write, token) =>
            {
                await write(new SubscribeToEventsResponse { Connected = new NSpark.Proto.ConnectedEvent() });
                if (mode == FakeOperatorState.Subscription.HeartbeatThenSilence)
                {
                    await write(new SubscribeToEventsResponse { Heartbeat = new HeartbeatEvent() });
                }

                if (mode != FakeOperatorState.Subscription.End)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), token);
                }
            },
            ct);
    }

    private HttpResponseMessage Reject()
    {
        if (_state.RejectionMode == FakeOperatorState.Rejection.BeforeHeaders)
        {
            return Error(Unauthenticated);
        }

        var response = GrpcResponse(new ByteArrayContent([]));
        response.Headers.Add("x-trace-id", "0af7651916cd43dd8448eb211c80319c");
        AddStatus(response.TrailingHeaders, Unauthenticated);
        return response;
    }

    /// <summary>A successful answer, with the headers the operators' timestamp interceptor adds.</summary>
    private HttpResponseMessage Ok(IMessage message)
    {
        var response = GrpcResponse(new ByteArrayContent(Frame(message)));
        AddTimeHeaders(response);
        AddStatus(response.TrailingHeaders, Status.DefaultSuccess);
        return response;
    }

    /// <summary>A trailers-only answer: the status in the headers, no message.</summary>
    private static HttpResponseMessage Error(Status status)
    {
        var response = GrpcResponse(new ByteArrayContent([]));
        AddStatus(response.Headers, status);
        return response;
    }

    /// <summary>A server stream whose messages <paramref name="produce"/> writes, then an OK status.</summary>
    private HttpResponseMessage Streaming(Func<Func<IMessage, Task>, CancellationToken, Task> produce, CancellationToken ct)
    {
        var frames = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var response = GrpcResponse(new StreamContent(new ChunkStream(frames.Reader)));
        AddTimeHeaders(response);
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await produce(
                        message =>
                        {
                            frames.Writer.TryWrite(Frame(message));
                            return Task.CompletedTask;
                        },
                        ct);
                    AddStatus(response.TrailingHeaders, Status.DefaultSuccess);
                }
                catch (OperationCanceledException)
                {
                    // The client went away.
                }
                finally
                {
                    frames.Writer.TryComplete();
                }
            },
            CancellationToken.None);
        return response;
    }

    private static HttpResponseMessage GrpcResponse(HttpContent content)
    {
        content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        return new HttpResponseMessage(HttpStatusCode.OK) { Version = HttpVersion.Version20, Content = content };
    }

    private void AddTimeHeaders(HttpResponseMessage response)
    {
        response.Headers.Date = _state.Now;
        response.Headers.Add("x-processing-time-ms", "1");
    }

    private static void AddStatus(HttpHeaders headers, Status status)
    {
        headers.Add("grpc-status", ((int)status.StatusCode).ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(status.Detail))
        {
            headers.Add("grpc-message", status.Detail);
        }
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}

/// <summary>A read-only stream of the chunks a channel delivers; it ends when the channel completes.</summary>
internal sealed class ChunkStream : Stream
{
    private readonly ChannelReader<byte[]> _chunks;
    private byte[] _current = [];
    private int _offset;

    public ChunkStream(ChannelReader<byte[]> chunks)
    {
        _chunks = chunks;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_offset >= _current.Length)
        {
            if (!await _chunks.WaitToReadAsync(cancellationToken))
            {
                return 0;
            }

            if (_chunks.TryRead(out var next))
            {
                (_current, _offset) = (next, 0);
            }
        }

        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>An SSP that cannot be reached: every request fails at once, without retries.</summary>
internal sealed class UnreachableSspHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new InvalidOperationException("The unit tests' SSP is unreachable."));
}

/// <summary>Regtest wallets whose only operator is a <see cref="FakeOperatorHandler"/>.</summary>
internal static class FakeOperator
{
    /// <summary>Never dialed: the channel sends through the fake handler.</summary>
    internal const string Address = "http://127.0.0.1:9";

    internal const string Identifier = "0000000000000000000000000000000000000000000000000000000000000001";

    internal const string IdentityPublicKeyHex = "03dfbdff4b6332c220f8fa2ba8ed496c698ceada563fa01b67d9983bfc5c95e763";

    internal const string SspIdentityPublicKeyHex = "022bf283544b16c0622daecb79422007d167eca6ce9f0c98c0c49833b1f7170bfe";

    internal const string Mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    internal static SparkOptions Options(TokenTransactionVersion tokenTransactionVersion = TokenTransactionVersion.V3) => new()
    {
        Network = SparkNetwork.Regtest,
        SigningOperators = [new SigningOperatorConfig(Address, Identifier, IdentityPublicKeyHex)],
        SspUrl = "https://ssp.invalid/graphql",
        SspIdentityPublicKeyHex = SspIdentityPublicKeyHex,
        SigningThreshold = 1,
        TokenTransactionVersion = tokenTransactionVersion,
    };

    /// <summary>A connection whose operator is <paramref name="state"/>'s stand-in and whose SSP is unreachable.</summary>
    internal static SparkConnection Connect(
        FakeOperatorState state,
        TokenTransactionVersion tokenTransactionVersion = TokenTransactionVersion.V3,
        HttpMessageHandler? ssp = null)
    {
        return new SparkConnection(
            Microsoft.Extensions.Options.Options.Create(Options(tokenTransactionVersion)),
            new HttpClient(ssp ?? new UnreachableSspHandler()),
            NullLoggerFactory.Instance,
            new FakeOperatorHandler(state));
    }

    /// <summary>Runs <paramref name="body"/> on a wallet of <see cref="Connect"/>, then disposes the connection.</summary>
    internal static async Task<T> RunAsync<T>(
        FakeOperatorState state,
        Func<SparkWallet, Task<T>> body,
        TokenTransactionVersion tokenTransactionVersion = TokenTransactionVersion.V3)
    {
        using var connection = Connect(state, tokenTransactionVersion);
        var wallet = await connection.CreateWalletAsync(Mnemonic, account: 0);
        return await body(wallet);
    }

    /// <inheritdoc cref="RunAsync{T}"/>
    internal static Task RunAsync(
        FakeOperatorState state,
        Func<SparkWallet, Task> body,
        TokenTransactionVersion tokenTransactionVersion = TokenTransactionVersion.V3) =>
        RunAsync(
            state,
            async wallet =>
            {
                await body(wallet);
                return true;
            },
            tokenTransactionVersion);
}
