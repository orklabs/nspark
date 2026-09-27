using System.Buffers.Binary;
using Google.Protobuf;
using Grpc.Core;
using NSpark.Exceptions;
using NSpark.Models;
using NSpark.Proto.Token;
using NSpark.Services;
using NSpark.UnitTests.TestSupport;
using SparkAddress = NSpark.Services.SparkAddress;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Which token outputs a send may pick, as the reference SDK's <c>TokenOutputManager</c> decides
/// it: only AVAILABLE ones, and none another send from the wallet picked in the last 30 s.
/// </summary>
[TestFixture]
public class TokenOutputLockTests
{
    internal static readonly byte[] TokenId = Enumerable.Repeat((byte)0x01, 32).ToArray();

    internal static OutputWithPreviousTransactionData Output(
        uint vout,
        ulong amount = 100,
        TokenOutputStatus status = TokenOutputStatus.Available,
        byte[]? owner = null) => new()
    {
        Output = new TokenOutput
        {
            OwnerPublicKey = ByteString.CopyFrom(owner ?? Enumerable.Repeat((byte)0x02, 33).ToArray()),
            TokenIdentifier = ByteString.CopyFrom(TokenId),
            TokenAmount = TokenService.EncodeUInt128(amount),
            Status = status,
        },
        PreviousTransactionHash = ByteString.CopyFrom(Enumerable.Repeat((byte)0xAA, 32).ToArray()),
        PreviousTransactionVout = vout,
    };

    internal static List<string> Keys(IEnumerable<OutputWithPreviousTransactionData> outputs) => outputs.Select(TokenOutputLocks.Key).ToList();

    /// <summary>Outputs of <paramref name="amounts"/> owned by <paramref name="wallet"/>, with <paramref name="statuses"/> (AVAILABLE when not given).</summary>
    internal static List<OutputWithPreviousTransactionData> WalletOutputs(
        SparkWallet wallet,
        ulong[] amounts,
        TokenOutputStatus[]? statuses = null) =>
        amounts.Select((amount, index) => Output(
                (uint)index,
                amount,
                statuses is not null && index < statuses.Length ? statuses[index] : TokenOutputStatus.Available,
                wallet.IdentityPublicKey))
            .ToList();

    internal static string Token => TokenIdentifier.Encode(TokenId, SparkNetwork.Regtest);

    /// <summary>Sends 100 of the test token from <paramref name="wallet"/> to itself.</summary>
    internal static Task<TokenTransferResult> SendToSelfAsync(SparkWallet wallet, CancellationToken ct, string? idempotencyKey = null, ulong amount = 100) =>
        wallet.TransferTokensAsync(Token, amount, wallet.GetSparkAddress(), idempotencyKey: idempotencyKey, ct: ct);

    [Test]
    public void Only_AVAILABLE_outputs_are_offered_and_the_picked_ones_are_not_offered_again()
    {
        var locks = new TokenOutputLocks();
        var outputs = new[] { Output(0), Output(1, status: TokenOutputStatus.PendingOutbound), Output(2) };

        Keys(locks.Acquire(outputs, o => o.Take(1).ToList())).Should().Equal(Keys([outputs[0]]));
        Keys(locks.Acquire(outputs, o => o.ToList())).Should().Equal(Keys([outputs[2]]));
        locks.Acquire(outputs, o => o.ToList()).Should().BeEmpty();
    }

    [Test]
    public async Task A_lock_ends_after_its_expiry()
    {
        var locks = new TokenOutputLocks(TimeSpan.FromMilliseconds(100));
        var outputs = new[] { Output(0) };

        locks.Acquire(outputs, o => o.ToList()).Should().HaveCount(1);
        locks.Acquire(outputs, o => o.ToList()).Should().BeEmpty();
        await Task.Delay(150);
        locks.Acquire(outputs, o => o.ToList()).Should().HaveCount(1);
    }

    [Test]
    public void An_output_the_operators_report_pending_loses_its_lock_and_can_be_picked_once_available_again()
    {
        var locks = new TokenOutputLocks();

        locks.Acquire([Output(0)], o => o.ToList()).Should().HaveCount(1);
        locks.Acquire([Output(0, status: TokenOutputStatus.PendingOutbound)], o => o.ToList()).Should().BeEmpty();
        // The pending transaction expired: the operators report the output AVAILABLE again.
        locks.Acquire([Output(0)], o => o.ToList()).Should().HaveCount(1);
    }

    [Test]
    public void A_pick_that_fails_locks_nothing()
    {
        var locks = new TokenOutputLocks();
        var outputs = new[] { Output(0) };

        var act = () => locks.Acquire(outputs, o => TokenService.SelectTokenOutputs(o, 500, TokenSelectionStrategy.SmallFirst));

        act.Should().Throw<SparkConfigurationException>();
        locks.Acquire(outputs, o => o.ToList()).Should().HaveCount(1);
    }

    [Test]
    public async Task Concurrent_picks_never_share_an_output()
    {
        var locks = new TokenOutputLocks();
        var outputs = Enumerable.Range(0, 50).Select(i => Output((uint)i)).ToList();

        var picks = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => Keys(locks.Acquire(outputs, o => o.Take(1).ToList())))));

        var picked = picks.SelectMany(p => p).ToList();
        picked.Should().HaveCount(50).And.OnlyHaveUniqueItems();
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_send_skips_outputs_the_operators_report_pending(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();

        await FakeOperator.RunAsync(state, async wallet =>
        {
            var outputs = WalletOutputs(wallet, [100, 100], [TokenOutputStatus.PendingOutbound, TokenOutputStatus.Available]);
            state.SetTokenOutputs(outputs);

            await ((Func<Task>)(() => SendToSelfAsync(wallet, ct))).Should().ThrowAsync<RpcException>();

            state.StartedSpends.Should().ContainSingle().Which.Should().Equal(Keys([outputs[1]]));
        });
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_send_does_not_pick_the_outputs_of_one_that_may_still_be_in_flight(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();

        await FakeOperator.RunAsync(state, async wallet =>
        {
            var outputs = WalletOutputs(wallet, [100, 100]);
            state.SetTokenOutputs(outputs);

            // The first send is refused, but the operators may hold it: its output stays locked.
            await ((Func<Task>)(() => SendToSelfAsync(wallet, ct))).Should().ThrowAsync<RpcException>();
            await ((Func<Task>)(() => wallet.BurnTokensAsync(Token, 100, ct: ct))).Should().ThrowAsync<RpcException>();
            state.StartedSpends.Should().HaveCount(2);
            state.StartedSpends[0].Should().Equal(Keys([outputs[0]]));
            state.StartedSpends[1].Should().Equal(Keys([outputs[1]]));

            // Nothing left to pick: refused before anything reaches the operators.
            await ((Func<Task>)(() => SendToSelfAsync(wallet, ct))).Should().ThrowAsync<SparkConfigurationException>();
            state.StartedSpends.Should().HaveCount(2);
        });
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Concurrent_sends_spend_different_outputs(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();

        await FakeOperator.RunAsync(state, async wallet =>
        {
            state.SetTokenOutputs(WalletOutputs(wallet, [100, 100]));

            await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                try
                {
                    await SendToSelfAsync(wallet, ct);
                }
                catch (RpcException)
                {
                    // The stand-in refuses every transaction.
                }
            }));

            var spends = state.StartedSpends;
            spends.Should().HaveCount(2);
            spends.SelectMany(s => s).Should().HaveCount(2).And.OnlyHaveUniqueItems();
        });
    }
}

/// <summary>Retries of a token transfer sent with an idempotency key.</summary>
[TestFixture]
public class TokenTransferIdempotencyTests
{
    [Test]
    [CancelAfter(60_000)]
    public async Task A_retry_with_the_key_resends_the_first_transaction_unchanged(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();

        await FakeOperator.RunAsync(state, async wallet =>
        {
            state.SetTokenOutputs(TokenOutputLockTests.WalletOutputs(wallet, [100, 100]));
            for (var i = 0; i < 2; i++)
            {
                await ((Func<Task>)(() => TokenOutputLockTests.SendToSelfAsync(wallet, ct, "retry-1"))).Should().ThrowAsync<RpcException>();
            }
        });

        var broadcasts = state.Broadcasts;
        broadcasts.Should().HaveCount(2);
        broadcasts.Select(b => b.IdempotencyKey).Should().Equal("retry-1", "retry-1");
        broadcasts[1].Request.PartialTokenTransaction.Should().Be(broadcasts[0].Request.PartialTokenTransaction);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_key_used_for_another_transfer_is_refused_before_anything_is_sent(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();

        await FakeOperator.RunAsync(state, async wallet =>
        {
            state.SetTokenOutputs(TokenOutputLockTests.WalletOutputs(wallet, [100, 100]));
            await ((Func<Task>)(() => TokenOutputLockTests.SendToSelfAsync(wallet, ct, "k"))).Should().ThrowAsync<RpcException>();
            await ((Func<Task>)(() => TokenOutputLockTests.SendToSelfAsync(wallet, ct, "k", amount: 50))).Should().ThrowAsync<ArgumentException>();
        });

        state.Broadcasts.Should().HaveCount(1);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Without_a_key_nothing_is_remembered_and_no_idempotency_header_is_sent(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();

        await FakeOperator.RunAsync(state, async wallet =>
        {
            state.SetTokenOutputs(TokenOutputLockTests.WalletOutputs(wallet, [100, 100]));
            await ((Func<Task>)(() => TokenOutputLockTests.SendToSelfAsync(wallet, ct))).Should().ThrowAsync<RpcException>();
            await ((Func<Task>)(() => TokenOutputLockTests.SendToSelfAsync(wallet, ct))).Should().ThrowAsync<RpcException>();
        });

        state.Broadcasts.Select(b => b.IdempotencyKey).Should().Equal(null, null);
        state.StartedSpends.SelectMany(s => s).Should().OnlyHaveUniqueItems("each send picks its own output");
    }

    [Test]
    public void The_oldest_keys_are_forgotten_beyond_the_capacity()
    {
        var attempts = new TokenTransferAttempts();
        var request = new TokenTransferAttempts.Request(new byte[32], 1, new byte[33]);
        var attempt = new TokenTransferAttempts.Attempt(request, new TokenTransactionDraft.V3(new PartialTokenTransaction()), []);

        for (var index = 0; index <= TokenTransferAttempts.Capacity; index++)
        {
            attempts.Remember(attempt, $"key-{index}");
        }

        attempts.Get("key-0").Should().BeNull();
        attempts.Get("key-1").Should().NotBeNull();
        attempts.Get($"key-{TokenTransferAttempts.Capacity}").Should().NotBeNull();
    }
}

/// <summary>
/// V3 token transactions against the operator stand-in: what the wallet broadcasts, how it signs,
/// and which answers it accepts. The hashes themselves are checked against the operators' vectors
/// in <see cref="ProtoHashTests"/>.
/// </summary>
[TestFixture]
public class TokenTransactionV3Tests
{
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string FinalHash(PartialTokenTransaction partial) => Hex(ProtoHash.Hash(FakeOperatorHandler.Finalize(partial)));

    private static readonly string s_receiver = SparkAddress.Encode(TokenHashVectorTests.Key(25), SparkNetwork.Regtest);

    /// <summary>Runs <paramref name="body"/> on a wallet holding one 100-token output, against a stand-in that finalizes broadcasts, altered by <paramref name="tamper"/>.</summary>
    private static async Task WithFinalizingOperatorAsync(Func<SparkWallet, FakeOperatorState, Task> body, Action<FinalTokenTransaction>? tamper = null)
    {
        var state = FakeOperatorState.Accepting();
        state.BroadcastMode = FakeOperatorState.Broadcast.Finalize;
        state.Tamper = tamper ?? (_ => { });
        await FakeOperator.RunAsync(state, async wallet =>
        {
            state.SetTokenOutputs(TokenOutputLockTests.WalletOutputs(wallet, [100]));
            await body(wallet, state);
        });
    }

    private static BroadcastTransactionRequest OnlyBroadcast(FakeOperatorState state)
    {
        state.Broadcasts.Should().HaveCount(1);
        return state.Broadcasts[0].Request;
    }

    /// <summary>The owner signatures are the wallet's, over the protohash of the partial transaction, in <c>single_signature</c>.</summary>
    private static void ExpectSigned(BroadcastTransactionRequest request, SparkWallet wallet, int inputs)
    {
        var hash = ProtoHash.Hash(request.PartialTokenTransaction);
        request.IdentityPublicKey.ToByteArray().Should().Equal(wallet.IdentityPublicKey);
        request.TokenTransactionOwnerSignatures.Select(s => (int)s.InputIndex).Should().Equal(Enumerable.Range(0, inputs));
        foreach (var signature in request.TokenTransactionOwnerSignatures)
        {
            signature.HasSignature.Should().BeFalse("the deprecated field is not used");
            signature.AuthoritySignaturesCase.Should().Be(SignatureWithIndex.AuthoritySignaturesOneofCase.SingleSignature);
            signature.SingleSignature.PublicKey.ToByteArray().Should().Equal(wallet.IdentityPublicKey);
            TransferLeafVerifier.VerifyLegacySignature(signature.SingleSignature.Signature.Span, hash, wallet.IdentityPublicKey)
                .Should().BeTrue();
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_transfer_broadcasts_one_signed_V3_transaction_and_returns_the_final_transactions_hash(CancellationToken ct)
    {
        await WithFinalizingOperatorAsync(async (wallet, state) =>
        {
            var result = await wallet.TransferTokensAsync(TokenOutputLockTests.Token, 40, s_receiver, ct: ct);

            var request = OnlyBroadcast(state);
            var partial = request.PartialTokenTransaction;
            result.TransactionHash.Should().Be(FinalHash(partial));
            state.StartedTransactions.Should().BeEmpty();

            partial.Version.Should().Be(3u);
            var metadata = partial.TokenTransactionMetadata;
            metadata.ValidityDurationSeconds.Should().Be(180UL);
            metadata.Network.Should().Be(NSpark.Proto.Network.Regtest);
            metadata.SparkOperatorIdentityPublicKeys.Select(k => k.ToByteArray())
                .Should().BeEquivalentTo(TokenService.CollectOperatorIdentityPublicKeys(wallet), o => o.WithStrictOrdering());
            (metadata.ClientCreatedTimestamp.Nanos % 1_000).Should().Be(0, "timestamps are microsecond-precise");
            metadata.InvoiceAttachments.Should().BeEmpty();
            partial.TransferInput.OutputsToSpend.Select(o => o.PrevTokenTransactionVout).Should().Equal(0u);

            partial.PartialTokenOutputs.Select(o => Hex(o.OwnerPublicKey.ToByteArray()))
                .Should().Equal(Hex(TokenHashVectorTests.Key(25)), wallet.IdentityPublicKeyHex);
            partial.PartialTokenOutputs.Select(o => TokenService.DecodeUInt128(o.TokenAmount)).Should().Equal((UInt128)40, (UInt128)60);
            foreach (var output in partial.PartialTokenOutputs)
            {
                output.TokenIdentifier.ToByteArray().Should().Equal(TokenOutputLockTests.TokenId);
                output.WithdrawBondSats.Should().Be(10_000UL);
                output.WithdrawRelativeBlockLocktime.Should().Be(1_000UL);
            }

            ExpectSigned(request, wallet, inputs: 1);
        });
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_burn_pays_the_burn_key_signed_for_the_output_it_spends(CancellationToken ct)
    {
        await WithFinalizingOperatorAsync(async (wallet, state) =>
        {
            await wallet.BurnTokensAsync(TokenOutputLockTests.Token, 100, ct: ct);

            var request = OnlyBroadcast(state);
            request.PartialTokenTransaction.PartialTokenOutputs.Select(o => o.OwnerPublicKey.ToByteArray())
                .Should().ContainSingle().Which.Should().Equal(Enumerable.Repeat((byte)0x02, 33));
            ExpectSigned(request, wallet, inputs: 1);
        });
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_mint_pays_the_issuer_signed_by_the_issuer(CancellationToken ct)
    {
        await WithFinalizingOperatorAsync(async (wallet, state) =>
        {
            var result = await wallet.MintTokensAsync(TokenOutputLockTests.Token, 5, ct);

            var request = OnlyBroadcast(state);
            var partial = request.PartialTokenTransaction;
            result.TransactionHash.Should().Be(FinalHash(partial));
            partial.MintInput.IssuerPublicKey.ToByteArray().Should().Equal(wallet.IdentityPublicKey);
            partial.MintInput.TokenIdentifier.ToByteArray().Should().Equal(TokenOutputLockTests.TokenId);
            partial.PartialTokenOutputs.Select(o => o.OwnerPublicKey.ToByteArray()).Should().ContainSingle()
                .Which.Should().Equal(wallet.IdentityPublicKey);
            partial.PartialTokenOutputs.Select(o => TokenService.DecodeUInt128(o.TokenAmount)).Should().Equal((UInt128)5);
            ExpectSigned(request, wallet, inputs: 1);
        });
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_create_carries_the_tokens_parameters_and_returns_the_identifier_the_operators_report(CancellationToken ct)
    {
        await WithFinalizingOperatorAsync(async (wallet, state) =>
        {
            var result = await wallet.CreateTokenAsync("Piggy", "PIG", 2, 1_000, isFreezable: false, ct: ct);

            var request = OnlyBroadcast(state);
            var create = request.PartialTokenTransaction.CreateInput;
            create.TokenName.Should().Be("Piggy");
            create.TokenTicker.Should().Be("PIG");
            create.Decimals.Should().Be(2u);
            TokenService.DecodeUInt128(create.MaxSupply).Should().Be((UInt128)1_000);
            create.HasCreationEntityPublicKey.Should().BeFalse("the operators set it");
            request.PartialTokenTransaction.PartialTokenOutputs.Should().BeEmpty();
            result.TokenIdentifier.Should().Be(TokenIdentifier.Encode(FakeOperatorHandler.CreatedTokenIdentifier, SparkNetwork.Regtest));
            result.TransactionHash.Should().Be(FinalHash(request.PartialTokenTransaction));
            ExpectSigned(request, wallet, inputs: 1);
        });
    }

    private static readonly (string Label, Action<FinalTokenTransaction> Tamper)[] s_tamperings =
    [
        ("output owner redirected", f => f.FinalTokenOutputs[0].PartialTokenOutput.OwnerPublicKey = ByteString.CopyFrom(TokenHashVectorTests.Key(99))),
        ("output amount changed", f => f.FinalTokenOutputs[0].PartialTokenOutput.TokenAmount = TokenService.EncodeUInt128(41)),
        ("withdraw bond changed", f => f.FinalTokenOutputs[1].PartialTokenOutput.WithdrawBondSats = 1),
        ("revocation commitment missing", f => f.FinalTokenOutputs[1].RevocationCommitment = ByteString.Empty),
        ("output dropped", f => f.FinalTokenOutputs.RemoveAt(f.FinalTokenOutputs.Count - 1)),
        ("input changed", f => f.TransferInput.OutputsToSpend[0].PrevTokenTransactionVout = 7),
        ("metadata changed", f => f.TokenTransactionMetadata.ValidityDurationSeconds = 300),
        ("version changed", f => f.Version = 2),
        ("type changed", f => f.MintInput = new TokenMintInput()),
    ];

    [Test]
    [CancelAfter(60_000)]
    public async Task A_final_transaction_that_is_not_the_one_signed_is_refused([Range(0, 8)] int index, CancellationToken ct)
    {
        var (label, tamper) = s_tamperings[index];

        await WithFinalizingOperatorAsync(
            async (wallet, _) =>
            {
                var act = () => wallet.TransferTokensAsync(TokenOutputLockTests.Token, 40, s_receiver, ct: ct);
                await act.Should().ThrowAsync<SparkUntrustedResponseException>(label);
            },
            tamper);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task V2_is_still_used_when_configured(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();

        await FakeOperator.RunAsync(
            state,
            async wallet =>
            {
                state.SetTokenOutputs(TokenOutputLockTests.WalletOutputs(wallet, [100]));
                await ((Func<Task>)(() => TokenOutputLockTests.SendToSelfAsync(wallet, ct))).Should().ThrowAsync<RpcException>();
            },
            TokenTransactionVersion.V2);

        state.StartedTransactions.Should().ContainSingle().Which.Transaction.Version.Should().Be(2u);
        state.Broadcasts.Should().BeEmpty();
    }
}

/// <summary>Token balances against the operator stand-in, which refuses metadata queries for more than 500 tokens as the operators do.</summary>
[TestFixture]
public class TokenBalanceTests
{
    private static List<OutputWithPreviousTransactionData> Outputs(int kinds) =>
        Enumerable.Range(0, kinds).Select(index =>
        {
            var id = new byte[32];
            BinaryPrimitives.WriteUInt32BigEndian(id.AsSpan(28), (uint)index);
            return new OutputWithPreviousTransactionData
            {
                Output = new TokenOutput
                {
                    TokenIdentifier = ByteString.CopyFrom(id),
                    TokenAmount = TokenService.EncodeUInt128(1),
                    Status = TokenOutputStatus.Available,
                },
            };
        }).ToList();

    [Test]
    [CancelAfter(60_000)]
    public async Task Metadata_is_asked_for_500_tokens_at_a_time_so_any_number_of_tokens_can_be_listed(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        state.SetTokenOutputs(Outputs(1_200));

        var balances = await FakeOperator.RunAsync(state, wallet => wallet.GetTokenBalancesAsync(ct));

        balances.Should().HaveCount(1_200);
        balances.Should().OnlyContain(b => b.OwnedBalance == 1 && b.AvailableToSendBalance == 1);
        state.MetadataRequestSizes.Should().Equal(500, 500, 200);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Tokens_that_cannot_be_read_cost_GetBalance_its_token_balances_not_the_sats(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        state.SetNodes([NSpark.UnitTests.Connection.TransportHardeningTests.AvailableNode(0), NSpark.UnitTests.Connection.TransportHardeningTests.AvailableNode(1)]);
        state.SetTokenOutputs(Outputs(3));
        state.FailsTokenMetadata = true;

        await FakeOperator.RunAsync(state, async wallet =>
        {
            var balance = await wallet.GetBalanceAsync(ct);
            balance.SatsBalance.Owned.Should().Be(2);
            balance.TokenBalances.Should().BeEmpty();

            var act = () => wallet.GetTokenBalancesAsync(ct);
            await act.Should().ThrowAsync<RpcException>();
        });
    }
}

/// <summary>Token outputs as the public API reports them.</summary>
[TestFixture]
public class TokenOutputInfoTests
{
    [Test]
    public void Output_statuses_are_reported_in_the_documented_operator_spelling()
    {
        TokenService.StatusName(TokenOutputStatus.Available).Should().Be("AVAILABLE");
        TokenService.StatusName(TokenOutputStatus.PendingOutbound).Should().Be("PENDING_OUTBOUND");
        TokenService.StatusName(TokenOutputStatus.Unspecified).Should().Be("UNSPECIFIED");
        TokenService.StatusName((TokenOutputStatus)7).Should().Be("7");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task GetTokenOutputsAsync_reports_status_and_amounts(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();

        var outputs = await FakeOperator.RunAsync(state, async wallet =>
        {
            state.SetTokenOutputs(TokenOutputLockTests.WalletOutputs(wallet, [100, 5], [TokenOutputStatus.Available, TokenOutputStatus.PendingOutbound]));
            return await wallet.GetTokenOutputsAsync(TokenOutputLockTests.Token, ct);
        });

        outputs.Select(o => o.Status).Should().Equal("AVAILABLE", "PENDING_OUTBOUND");
        outputs.Select(o => o.TokenAmount).Should().Equal((UInt128)100, (UInt128)5);
    }
}
