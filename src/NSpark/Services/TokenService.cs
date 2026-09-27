using System.Buffers.Binary;
using System.Text;
using Google.Protobuf;
using NSpark.Exceptions;
using NSpark.Models;
using NSpark.Proto;
using NSpark.Proto.Multisig;
using NSpark.Proto.Token;
using ProtoTokenMetadata = NSpark.Proto.Token.TokenMetadata;
using TokenMetadata = NSpark.Models.TokenMetadata;

namespace NSpark.Services;

/// <summary>
/// Extension methods on <see cref="SparkWallet"/> for working with Spark
/// tokens (LRC-20-style fungible assets settled on Spark).
/// </summary>
/// <remarks>
/// Token transactions use the operators' V3 format by default, as the reference SDK does: one
/// <c>broadcast_transaction</c> call, signed over the protohash of the partial transaction.
/// <see cref="SparkOptions.TokenTransactionVersion"/> set to
/// <see cref="TokenTransactionVersion.V2"/> keeps the older two-step flow while the operators
/// accept it.
/// </remarks>
public static class TokenService
{
    private const uint QueryTokenOutputsPageSize = 100;
    private const int MaxTokenOutputsPerTx = 500;

    /// <summary>Token identifiers per metadata query: the operators' <c>MaxTokenMetadataFilterValues</c>.</summary>
    internal const int TokenMetadataBatchSize = 500;

    /// <summary>
    /// How long the operators may take to carry out a V3 token transaction: the reference SDK's
    /// default (the operators accept 1 to 300 seconds).
    /// </summary>
    internal const ulong TokenValidityDurationSeconds = 180;

    /// <summary>
    /// Aggregate the wallet's token holdings into one <see cref="TokenBalance"/>
    /// per token type, joining output sums with token metadata.
    /// </summary>
    public static async Task<IReadOnlyList<TokenBalance>> GetTokenBalancesAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);

        var outputs = await FetchTokenOutputsAsync(wallet, tokenIdentifiers: null, ct).ConfigureAwait(false);
        if (outputs.Count == 0)
        {
            return Array.Empty<TokenBalance>();
        }

        // Aggregate by raw token identifier.
        var byToken = new Dictionary<ByteString, (UInt128 Owned, UInt128 Available)>();
        foreach (var entry in outputs)
        {
            var output = entry.Output;
            var id = output.TokenIdentifier;
            var amount = DecodeUInt128(output.TokenAmount);

            byToken.TryGetValue(id, out var existing);
            existing.Owned += amount;
            if (TokenOutputLocks.IsAvailable(entry))
            {
                existing.Available += amount;
            }
            byToken[id] = existing;
        }

        var metadataMap = await FetchTokenMetadataMapAsync(wallet, [.. byToken.Keys], ct).ConfigureAwait(false);

        var balances = new List<TokenBalance>(byToken.Count);
        foreach (var (rawId, (owned, available)) in byToken)
        {
            if (metadataMap.TryGetValue(rawId, out var meta))
            {
                balances.Add(new TokenBalance(meta, owned, available));
            }
        }
        return balances;
    }

    /// <summary>
    /// Enumerate every token output owned by this wallet, optionally filtered
    /// to a single token type by Bech32m identifier.
    /// </summary>
    public static async Task<IReadOnlyList<TokenOutputInfo>> GetTokenOutputsAsync(
        this SparkWallet wallet,
        string? bech32mTokenIdentifier = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);

        ByteString[]? filter = null;
        if (bech32mTokenIdentifier is not null)
        {
            var (raw, _) = TokenIdentifier.Decode(bech32mTokenIdentifier, wallet.Options.Network);
            filter = [ByteString.CopyFrom(raw)];
        }

        var outputs = await FetchTokenOutputsAsync(wallet, filter, ct).ConfigureAwait(false);
        return outputs.Select(MapOutput).ToList();
    }

    /// <summary>
    /// Query the token registry for metadata, filtered either by Bech32m
    /// token identifiers or by issuer public keys.
    /// </summary>
    public static async Task<IReadOnlyList<TokenMetadata>> QueryTokenMetadataAsync(
        this SparkWallet wallet,
        IReadOnlyList<string>? bech32mTokenIdentifiers = null,
        IReadOnlyList<byte[]>? issuerPublicKeys = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);

        var client = wallet.GetTokenClient(wallet.CoordinatorAddress);
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);

        var request = new QueryTokenMetadataRequest();
        if (bech32mTokenIdentifiers is not null)
        {
            foreach (var id in bech32mTokenIdentifiers)
            {
                var (raw, _) = TokenIdentifier.Decode(id, wallet.Options.Network);
                request.TokenIdentifiers.Add(ByteString.CopyFrom(raw));
            }
        }
        if (issuerPublicKeys is not null)
        {
            foreach (var key in issuerPublicKeys)
            {
                request.IssuerPublicKeys.Add(ByteString.CopyFrom(key));
            }
        }

        var response = await client.query_token_metadataAsync(
            request, headers, cancellationToken: ct).ConfigureAwait(false);

        return response.TokenMetadata.Select(m => ToModel(m, wallet.Options.Network)).ToList();
    }

    // ───────────────────────────────── Write-side ─────────────────────────────────

    /// <summary>
    /// Transfer tokens to a receiver identified by their Spark address. Returns the transaction
    /// hash (hex): the final transaction's protohash for V3, its V2 hash otherwise.
    /// </summary>
    /// <param name="wallet">The sending wallet.</param>
    /// <param name="bech32mTokenIdentifier">Bech32m token id (e.g. <c>"btkn1..."</c>).</param>
    /// <param name="amount">Token units to send.</param>
    /// <param name="receiverSparkAddress">
    /// Receiver's Spark address (e.g. <c>"spark1..."</c>) for the wallet's network. A Spark invoice
    /// is refused with <see cref="SparkConfigurationException"/>, as in <c>SendAsync</c>.
    /// </param>
    /// <param name="strategy">Output-selection strategy.</param>
    /// <param name="idempotencyKey">
    /// Makes retries safe. A retry with the same key, on the same wallet, resends the transaction
    /// the first call built, so the transfer is made at most once: a retry after it went through
    /// returns its hash again, and a retry after it failed completes it if it can still be sent,
    /// else fails again. A key used for another token, amount or receiver is refused with
    /// <see cref="ArgumentException"/>. The wallet remembers the last 1,000 keys; use a new key
    /// for a new transfer.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<TokenTransferResult> TransferTokensAsync(
        this SparkWallet wallet,
        string bech32mTokenIdentifier,
        UInt128 amount,
        string receiverSparkAddress,
        TokenSelectionStrategy strategy = TokenSelectionStrategy.SmallFirst,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentException.ThrowIfNullOrEmpty(bech32mTokenIdentifier);
        ArgumentException.ThrowIfNullOrEmpty(receiverSparkAddress);

        var (rawTokenId, _) = TokenIdentifier.Decode(bech32mTokenIdentifier, wallet.Options.Network);
        // The receiver's identity key, from a Spark address for this network (a Spark invoice is
        // refused), before any output is fetched.
        var receiver = SparkAddress.Decode(receiverSparkAddress, wallet.Options.Network);
        var request = new TokenTransferAttempts.Request(rawTokenId, amount, receiver);

        TokenTransferAttempts.Attempt attempt;
        if (idempotencyKey is not null && wallet.TokenTransferAttempts.Get(idempotencyKey) is { } earlier)
        {
            if (!earlier.Request.Matches(request))
            {
                throw new ArgumentException(
                    $"Idempotency key {idempotencyKey} was used for a different token transfer.", nameof(idempotencyKey));
            }

            attempt = earlier;
        }
        else
        {
            attempt = await NewTokenTransferAsync(wallet, request, bech32mTokenIdentifier, strategy, ct).ConfigureAwait(false);
            if (idempotencyKey is not null)
            {
                wallet.TokenTransferAttempts.Remember(attempt, idempotencyKey);
            }
        }

        var (hash, _) = await SendTokenTransactionAsync(
            wallet, attempt.Transaction, attempt.SpentOutputs, idempotencyKey, ct).ConfigureAwait(false);
        return new TokenTransferResult(hash);
    }

    /// <summary>Picks outputs for <paramref name="request"/> and builds its transaction, with change back to the wallet.</summary>
    private static async Task<TokenTransferAttempts.Attempt> NewTokenTransferAsync(
        SparkWallet wallet,
        TokenTransferAttempts.Request request,
        string bech32mTokenIdentifier,
        TokenSelectionStrategy strategy,
        CancellationToken ct)
    {
        var outputs = await FetchTokenOutputsAsync(wallet, [ByteString.CopyFrom(request.TokenIdentifier)], ct).ConfigureAwait(false);
        if (outputs.Count == 0)
        {
            throw new SparkConfigurationException(
                "token.transfer",
                $"Insufficient token balance for {bech32mTokenIdentifier}: need {request.Amount}, have 0.");
        }

        // Only available outputs no other send from this wallet has picked (see TokenOutputLocks).
        var selected = wallet.TokenOutputLocks.Acquire(outputs, spendable => SelectTokenOutputs(spendable, request.Amount, strategy));
        var receiverOutput = new TokenOutputSpec(request.ReceiverIdentityPublicKey, request.TokenIdentifier, request.Amount);
        var draft = TransferDraft(wallet, selected, TransferOutputs(selected, [receiverOutput], wallet.IdentityPublicKey));
        return new TokenTransferAttempts.Attempt(request, draft, selected);
    }

    /// <summary>
    /// Create a new token on Spark — the caller's identity key becomes the issuer.
    /// </summary>
    /// <remarks>
    /// The parameters are checked as the operators check them (<see cref="ValidateTokenParameters"/>).
    /// </remarks>
    public static async Task<TokenCreationResult> CreateTokenAsync(
        this SparkWallet wallet,
        string tokenName,
        string tokenTicker,
        uint decimals,
        UInt128 maxSupply,
        bool isFreezable,
        byte[]? extraMetadata = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentNullException.ThrowIfNull(tokenName);
        ArgumentNullException.ThrowIfNull(tokenTicker);
        ValidateTokenParameters(tokenName, tokenTicker, decimals, extraMetadata);

        var createInput = new TokenCreateInput
        {
            IssuerPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
            TokenName = tokenName,
            TokenTicker = tokenTicker,
            Decimals = decimals,
            MaxSupply = EncodeUInt128(maxSupply),
            IsFreezable = isFreezable,
        };
        if (extraMetadata is not null)
        {
            createInput.ExtraMetadata = ByteString.CopyFrom(extraMetadata);
        }

        var (hashHex, tokenId) = await SendTokenTransactionAsync(
            wallet, Draft(wallet, DraftInputs.Create(createInput), []), [], idempotencyKey: null, ct).ConfigureAwait(false);

        string? bech32 = null;
        if (tokenId is { Length: 32 })
        {
            bech32 = TokenIdentifier.Encode(tokenId, wallet.Options.Network);
        }
        return new TokenCreationResult(hashHex, bech32);
    }

    /// <summary>
    /// The operators' rules for a new token (<c>TokenMetadata.ValidatePartial</c>), also the
    /// reference SDK's: the name 3–20 and the ticker 3–6 UTF-8 bytes, both in Unicode
    /// normalization form C; decimals up to 255; extra metadata up to 1024 bytes. The operators
    /// refuse a token that breaks them with INTERNAL, which reaches the wallet as "Something went
    /// wrong.", so each rule is checked here to say which one.
    /// </summary>
    /// <exception cref="SparkConfigurationException">A rule is broken.</exception>
    internal static void ValidateTokenParameters(string tokenName, string tokenTicker, uint decimals, byte[]? extraMetadata)
    {
        const string operation = "token.create";
        if (!string.Equals(tokenName, tokenName.Normalize(NormalizationForm.FormC), StringComparison.Ordinal))
        {
            throw new SparkConfigurationException(operation, "Token name must be NFC-normalized UTF-8.");
        }
        if (!string.Equals(tokenTicker, tokenTicker.Normalize(NormalizationForm.FormC), StringComparison.Ordinal))
        {
            throw new SparkConfigurationException(operation, "Token ticker must be NFC-normalized UTF-8.");
        }

        var nameBytes = Encoding.UTF8.GetByteCount(tokenName);
        if (nameBytes is < 3 or > 20)
        {
            throw new SparkConfigurationException(operation, $"Token name must be 3-20 UTF-8 bytes, not {nameBytes}.");
        }

        var tickerBytes = Encoding.UTF8.GetByteCount(tokenTicker);
        if (tickerBytes is < 3 or > 6)
        {
            throw new SparkConfigurationException(operation, $"Token ticker must be 3-6 UTF-8 bytes, not {tickerBytes}.");
        }

        if (decimals > 255)
        {
            throw new SparkConfigurationException(operation, "Decimals must be <= 255.");
        }

        if (extraMetadata is { Length: > 1024 })
        {
            throw new SparkConfigurationException(operation, "Extra metadata must be <= 1024 bytes.");
        }
    }

    /// <summary>
    /// Mint additional units of a token already issued by this wallet's identity key.
    /// </summary>
    public static async Task<TokenTransferResult> MintTokensAsync(
        this SparkWallet wallet,
        string bech32mTokenIdentifier,
        UInt128 amount,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentException.ThrowIfNullOrEmpty(bech32mTokenIdentifier);
        if (amount == UInt128.Zero)
        {
            throw new SparkConfigurationException(
                "token.mint",
                "Mint amount must be greater than 0.");
        }

        var (rawTokenId, _) = TokenIdentifier.Decode(bech32mTokenIdentifier, wallet.Options.Network);
        var mintInput = new TokenMintInput
        {
            IssuerPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
            TokenIdentifier = ByteString.CopyFrom(rawTokenId),
        };
        var output = new TokenOutputSpec(wallet.IdentityPublicKey, rawTokenId, amount);

        var (hashHex, _) = await SendTokenTransactionAsync(
            wallet, Draft(wallet, DraftInputs.Mint(mintInput), [output]), [], idempotencyKey: null, ct).ConfigureAwait(false);
        return new TokenTransferResult(hashHex);
    }

    /// <summary>
    /// Burn tokens by transferring them to the canonical dead address
    /// (33 bytes of <c>0x02</c>).
    /// </summary>
    public static async Task<TokenTransferResult> BurnTokensAsync(
        this SparkWallet wallet,
        string bech32mTokenIdentifier,
        UInt128 amount,
        TokenSelectionStrategy strategy = TokenSelectionStrategy.SmallFirst,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentException.ThrowIfNullOrEmpty(bech32mTokenIdentifier);

        var burnPubKey = new byte[33];
        Array.Fill(burnPubKey, (byte)0x02);

        var (rawTokenId, _) = TokenIdentifier.Decode(bech32mTokenIdentifier, wallet.Options.Network);
        var outputs = await FetchTokenOutputsAsync(wallet, [ByteString.CopyFrom(rawTokenId)], ct).ConfigureAwait(false);
        if (outputs.Count == 0)
        {
            throw new SparkConfigurationException(
                "token.burn",
                $"Insufficient token balance for {bech32mTokenIdentifier}: need {amount}, have 0.");
        }

        var selected = wallet.TokenOutputLocks.Acquire(outputs, spendable => SelectTokenOutputs(spendable, amount, strategy));
        var burn = new TokenOutputSpec(burnPubKey, rawTokenId, amount);
        var draft = TransferDraft(wallet, selected, TransferOutputs(selected, [burn], wallet.IdentityPublicKey));

        var (hashHex, _) = await SendTokenTransactionAsync(wallet, draft, selected, idempotencyKey: null, ct).ConfigureAwait(false);
        return new TokenTransferResult(hashHex);
    }

    // ───────────────────────────────── Output selection ─────────────────────────────────

    /// <summary>
    /// Pick a set of token outputs whose values sum to at least <paramref name="amount"/>,
    /// following the requested <paramref name="strategy"/>. Used by transfer / burn.
    /// </summary>
    internal static List<OutputWithPreviousTransactionData> SelectTokenOutputs(
        IReadOnlyList<OutputWithPreviousTransactionData> outputs,
        UInt128 amount,
        TokenSelectionStrategy strategy)
    {
        if (amount == UInt128.Zero)
        {
            throw new SparkConfigurationException("token.select", "Token amount must be greater than 0.");
        }

        UInt128 totalAvailable = UInt128.Zero;
        foreach (var o in outputs)
        {
            totalAvailable += DecodeUInt128(o.Output.TokenAmount);
        }
        if (totalAvailable < amount)
        {
            throw new SparkConfigurationException(
                "token.select",
                $"Insufficient token balance: need {amount}, have {totalAvailable}.");
        }

        // Exact-match short-circuit (any strategy).
        var exact = outputs.FirstOrDefault(o => DecodeUInt128(o.Output.TokenAmount) == amount);
        if (exact is not null)
        {
            return [exact];
        }

        if (strategy == TokenSelectionStrategy.SmallFirst)
        {
            var sorted = outputs.OrderBy(o => DecodeUInt128(o.Output.TokenAmount)).ToList();
            UInt128 sum = UInt128.Zero;
            int count = 0;
            foreach (var o in sorted)
            {
                sum += DecodeUInt128(o.Output.TokenAmount);
                count++;
                if (sum >= amount)
                {
                    return sorted.Take(count).ToList();
                }
                if (count >= MaxTokenOutputsPerTx)
                {
                    break;
                }
            }

            // Cap reached but still short — try swapping smallest for largest remaining.
            var taken = sorted.Take(Math.Min(count, MaxTokenOutputsPerTx)).ToList();
            var remaining = sorted.Skip(Math.Min(count, MaxTokenOutputsPerTx)).Reverse().ToList();
            UInt128 smallSum = UInt128.Zero;
            foreach (var o in taken)
            {
                smallSum += DecodeUInt128(o.Output.TokenAmount);
            }

            foreach (var large in remaining)
            {
                if (smallSum >= amount)
                {
                    break;
                }
                if (taken.Count == 0)
                {
                    break;
                }

                var smallest = taken[0];
                taken.RemoveAt(0);
                smallSum = smallSum - DecodeUInt128(smallest.Output.TokenAmount) + DecodeUInt128(large.Output.TokenAmount);
                taken.Add(large);
            }

            if (smallSum < amount)
            {
                throw new SparkConfigurationException(
                    "token.select",
                    $"Insufficient token balance after selection cap: need {amount}, have {smallSum}.");
            }
            return taken;
        }
        else
        {
            // LargeFirst: greedy largest-to-smallest.
            var sorted = outputs.OrderByDescending(o => DecodeUInt128(o.Output.TokenAmount)).ToList();
            var selected = new List<OutputWithPreviousTransactionData>();
            UInt128 remaining = amount;
            foreach (var o in sorted)
            {
                if (remaining == UInt128.Zero)
                {
                    break;
                }
                if (selected.Count >= MaxTokenOutputsPerTx)
                {
                    break;
                }
                selected.Add(o);
                var v = DecodeUInt128(o.Output.TokenAmount);
                remaining = v >= remaining ? UInt128.Zero : (remaining - v);
            }
            if (remaining != UInt128.Zero)
            {
                throw new SparkConfigurationException(
                    "token.select",
                    $"Insufficient token balance: need {amount}, have {amount - remaining}.");
            }
            return selected;
        }
    }

    // ───────────────────────────────── Building ─────────────────────────────────

    /// <summary>An output a token transaction creates.</summary>
    internal sealed record TokenOutputSpec(byte[] Owner, byte[] TokenIdentifier, UInt128 Amount);

    /// <summary>
    /// The outputs of a transfer spending <paramref name="spent"/> to <paramref name="receivers"/>:
    /// the receivers' outputs, then change to <paramref name="changeOwner"/> for each token spent
    /// beyond what they are paid.
    /// </summary>
    internal static List<TokenOutputSpec> TransferOutputs(
        IReadOnlyList<OutputWithPreviousTransactionData> spent,
        IReadOnlyList<TokenOutputSpec> receivers,
        byte[] changeOwner)
    {
        var change = new Dictionary<ByteString, UInt128>();
        var order = new List<ByteString>();
        foreach (var output in spent)
        {
            var token = output.Output.TokenIdentifier;
            if (!change.ContainsKey(token))
            {
                order.Add(token);
                change[token] = UInt128.Zero;
            }
            change[token] += DecodeUInt128(output.Output.TokenAmount);
        }

        foreach (var receiver in receivers)
        {
            var token = ByteString.CopyFrom(receiver.TokenIdentifier);
            if (change.TryGetValue(token, out var available))
            {
                change[token] = available - UInt128.Min(available, receiver.Amount);
            }
        }

        var result = new List<TokenOutputSpec>(receivers);
        foreach (var token in order)
        {
            if (change[token] > UInt128.Zero)
            {
                result.Add(new TokenOutputSpec(changeOwner, token.ToByteArray(), change[token]));
            }
        }

        return result;
    }

    /// <summary>A transfer spending <paramref name="spent"/>, in vout order, to <paramref name="outputs"/>.</summary>
    internal static TokenTransactionDraft TransferDraft(
        SparkWallet wallet,
        IReadOnlyList<OutputWithPreviousTransactionData> spent,
        IReadOnlyList<TokenOutputSpec> outputs)
    {
        var input = new TokenTransferInput();
        foreach (var output in spent.OrderBy(o => o.PreviousTransactionVout))
        {
            input.OutputsToSpend.Add(new TokenOutputToSpend
            {
                PrevTokenTransactionHash = output.PreviousTransactionHash,
                PrevTokenTransactionVout = output.PreviousTransactionVout,
            });
        }

        return Draft(wallet, DraftInputs.Transfer(input), outputs);
    }

    /// <summary>The inputs of a draft: exactly one of the three.</summary>
    private sealed record DraftInputs(TokenTransferInput? TransferInput, TokenMintInput? MintInput, TokenCreateInput? CreateInput)
    {
        public static DraftInputs Transfer(TokenTransferInput input) => new(input, null, null);

        public static DraftInputs Mint(TokenMintInput input) => new(null, input, null);

        public static DraftInputs Create(TokenCreateInput input) => new(null, null, input);
    }

    private static TokenTransactionDraft Draft(SparkWallet wallet, DraftInputs inputs, IReadOnlyList<TokenOutputSpec> outputs)
    {
        var options = wallet.Options;
        var operatorKeys = CollectOperatorIdentityPublicKeys(wallet);
        switch (options.TokenTransactionVersion)
        {
            case TokenTransactionVersion.V2:
            {
                var tx = new TokenTransaction
                {
                    Version = 2,
                    Network = options.ProtoNetwork(),
                    ClientCreatedTimestamp = CurrentTimestamp(wallet),
                };
                if (inputs.TransferInput is not null)
                {
                    tx.TransferInput = inputs.TransferInput;
                }
                else if (inputs.MintInput is not null)
                {
                    tx.MintInput = inputs.MintInput;
                }
                else
                {
                    tx.CreateInput = inputs.CreateInput;
                }

                // The coordinator adds the withdraw bond and locktime to V2 outputs.
                foreach (var spec in outputs)
                {
                    tx.TokenOutputs.Add(new TokenOutput
                    {
                        OwnerPublicKey = ByteString.CopyFrom(spec.Owner),
                        TokenIdentifier = ByteString.CopyFrom(spec.TokenIdentifier),
                        TokenAmount = EncodeUInt128(spec.Amount),
                    });
                }
                tx.SparkOperatorIdentityPublicKeys.AddRange(operatorKeys.Select(ByteString.CopyFrom));
                return new TokenTransactionDraft.V2(tx);
            }
            default:
            {
                var metadata = new TokenTransactionMetadata
                {
                    Network = options.ProtoNetwork(),
                    ClientCreatedTimestamp = CurrentTimestamp(wallet),
                    ValidityDurationSeconds = TokenValidityDurationSeconds,
                };
                // Strictly ascending, as the operators require of V3 transactions.
                metadata.SparkOperatorIdentityPublicKeys.AddRange(operatorKeys.Select(ByteString.CopyFrom));

                var partial = new PartialTokenTransaction
                {
                    Version = 3,
                    TokenTransactionMetadata = metadata,
                };
                if (inputs.TransferInput is not null)
                {
                    partial.TransferInput = inputs.TransferInput;
                }
                else if (inputs.MintInput is not null)
                {
                    partial.MintInput = inputs.MintInput;
                }
                else
                {
                    partial.CreateInput = inputs.CreateInput;
                }

                // V3 outputs carry the withdraw bond and locktime, which must equal the network's.
                foreach (var spec in outputs)
                {
                    partial.PartialTokenOutputs.Add(new PartialTokenOutput
                    {
                        OwnerPublicKey = ByteString.CopyFrom(spec.Owner),
                        WithdrawBondSats = options.ExpectedWithdrawBondSats,
                        WithdrawRelativeBlockLocktime = options.ExpectedWithdrawRelativeBlockLocktime,
                        TokenIdentifier = ByteString.CopyFrom(spec.TokenIdentifier),
                        TokenAmount = EncodeUInt128(spec.Amount),
                    });
                }

                return new TokenTransactionDraft.V3(partial);
            }
        }
    }

    // ───────────────────────────────── Sending ─────────────────────────────────

    /// <summary>
    /// Sends <paramref name="draft"/>, signing for the <paramref name="spentOutputs"/> of a
    /// transfer (or as the issuer of a mint or create), and returns the final transaction's hash
    /// and, for a create, the token's identifier.
    /// </summary>
    private static async Task<(string TransactionHashHex, byte[]? TokenIdentifier)> SendTokenTransactionAsync(
        SparkWallet wallet,
        TokenTransactionDraft draft,
        IReadOnlyList<OutputWithPreviousTransactionData> spentOutputs,
        string? idempotencyKey,
        CancellationToken ct)
    {
        var owners = spentOutputs
            .OrderBy(o => o.PreviousTransactionVout)
            .Select(o => o.Output.OwnerPublicKey.ToByteArray())
            .ToList();
        return draft switch
        {
            TokenTransactionDraft.V2 v2 => await BroadcastTokenTransactionV2Async(
                wallet,
                v2.Transaction,
                v2.Transaction.TokenInputsCase == TokenTransaction.TokenInputsOneofCase.TransferInput ? owners : null,
                idempotencyKey,
                ct).ConfigureAwait(false),
            TokenTransactionDraft.V3 v3 => await BroadcastTokenTransactionV3Async(wallet, v3.Partial, owners, idempotencyKey, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unknown token transaction draft."),
        };
    }

    /// <summary>
    /// V3: one <c>broadcast_transaction</c>, signed over the protohash of
    /// <paramref name="partial"/>, which binds its inputs, outputs and amounts; the operators then
    /// build, sign and commit the final transaction, which is checked to be
    /// <paramref name="partial"/> before its hash is returned.
    /// </summary>
    private static async Task<(string TransactionHashHex, byte[]? TokenIdentifier)> BroadcastTokenTransactionV3Async(
        SparkWallet wallet,
        PartialTokenTransaction partial,
        IReadOnlyList<byte[]> spentOutputOwners,
        string? idempotencyKey,
        CancellationToken ct)
    {
        var client = wallet.GetTokenClient(wallet.CoordinatorAddress);
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        if (idempotencyKey is { Length: > 0 })
        {
            headers.Add(RenewalService.IdempotencyKeyHeader, idempotencyKey);
        }

        var request = new BroadcastTransactionRequest
        {
            IdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
            PartialTokenTransaction = partial,
        };
        request.TokenTransactionOwnerSignatures.AddRange(
            await OwnerSignaturesV3Async(wallet, partial, ProtoHash.Hash(partial), spentOutputOwners, ct).ConfigureAwait(false));

        var response = await client.broadcast_transactionAsync(request, headers, cancellationToken: ct).ConfigureAwait(false);
        var final = response.FinalTokenTransaction
            ?? throw new SparkUntrustedResponseException("token.broadcast", "Missing final token transaction in the broadcast response.");
        TokenTransactionValidator.ValidateV3(final, partial);

        var hash = ProtoHash.Hash(final);
        byte[]? tokenId = response.HasTokenIdentifier ? response.TokenIdentifier.ToByteArray() : null;
        return (Convert.ToHexString(hash).ToLowerInvariant(), tokenId);
    }

    /// <summary>
    /// One signature per input of a transfer, by the owner of the output it spends, or one by the
    /// issuer for a mint or create; in <c>single_signature</c>, as the reference SDK sends them.
    /// </summary>
    private static async Task<List<SignatureWithIndex>> OwnerSignaturesV3Async(
        SparkWallet wallet,
        PartialTokenTransaction partial,
        byte[] hash,
        IReadOnlyList<byte[]> spentOutputOwners,
        CancellationToken ct)
    {
        IReadOnlyList<byte[]> keys = partial.TokenInputsCase switch
        {
            PartialTokenTransaction.TokenInputsOneofCase.TransferInput
                when spentOutputOwners.Count == partial.TransferInput.OutputsToSpend.Count => spentOutputOwners,
            PartialTokenTransaction.TokenInputsOneofCase.TransferInput =>
                throw new SparkConfigurationException("token.sign", "Missing signing keys for the outputs to spend."),
            PartialTokenTransaction.TokenInputsOneofCase.MintInput
                or PartialTokenTransaction.TokenInputsOneofCase.CreateInput => [wallet.IdentityPublicKey],
            _ => throw new SparkConfigurationException("token.sign", "Token transaction has no inputs."),
        };

        var signatures = new List<SignatureWithIndex>(keys.Count);
        for (var index = 0; index < keys.Count; index++)
        {
            var key = keys[index];
            if (!key.AsSpan().SequenceEqual(wallet.IdentityPublicKey))
            {
                throw new SparkConfigurationException(
                    "token.sign", $"Cannot sign with unknown key: {Convert.ToHexString(key).ToLowerInvariant()}.");
            }

            var signature = await wallet.Signer.SignWithIdentityKeyAsync(hash, ct).ConfigureAwait(false);
            signatures.Add(new SignatureWithIndex
            {
                InputIndex = (uint)index,
                SingleSignature = new KeyedSignature
                {
                    PublicKey = ByteString.CopyFrom(key),
                    Signature = ByteString.CopyFrom(signature),
                },
            });
        }

        return signatures;
    }

    /// <summary>V2: <c>start_transaction</c>, checked, then <c>commit_transaction</c>.</summary>
    private static async Task<(string TransactionHashHex, byte[]? TokenIdentifier)>
        BroadcastTokenTransactionV2Async(
            SparkWallet wallet,
            TokenTransaction tx,
            IReadOnlyList<byte[]>? signingPublicKeys,
            string? idempotencyKey,
            CancellationToken ct)
    {
        var client = wallet.GetTokenClient(wallet.CoordinatorAddress);
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        var startHeaders = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        if (idempotencyKey is { Length: > 0 })
        {
            startHeaders.Add(RenewalService.IdempotencyKeyHeader, idempotencyKey);
        }

        // Phase 1: sign the partial hash, send start_transaction.
        var partialHash = TokenHashing.HashTokenTransactionV2(tx, partialHash: true);
        var ownerSignatures = await BuildOwnerSignaturesAsync(wallet, tx, partialHash, signingPublicKeys, ct).ConfigureAwait(false);

        var startRequest = new StartTransactionRequest
        {
            IdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
            PartialTokenTransaction = tx,
            ValidityDurationSeconds = 60,
        };
        startRequest.PartialTokenTransactionOwnerSignatures.AddRange(ownerSignatures);

        var startResponse = await client.start_transactionAsync(
            startRequest, startHeaders, cancellationToken: ct).ConfigureAwait(false);

        var finalTx = startResponse.FinalTokenTransaction
            ?? throw new SparkUntrustedResponseException("token.broadcast", "Missing final token transaction in the start response.");

        // The coordinator may only add server-set fields; anything else is refused before the
        // wallet signs the final hash for each operator (reference SDK: validateTokenTransaction).
        var options = wallet.Options;
        TokenTransactionValidator.Validate(
            finalTx,
            tx,
            startResponse.KeyshareInfo,
            new TokenTransactionValidator.Expectations(
                CollectOperatorIdentityPublicKeys(wallet),
                options.SigningOperators.Select(o => o.Identifier).ToHashSet(StringComparer.Ordinal),
                options.EffectiveSigningThreshold,
                options.ExpectedWithdrawBondSats,
                options.ExpectedWithdrawRelativeBlockLocktime));

        // Phase 2: hash the final tx, build per-operator signatures, commit.
        var finalHash = TokenHashing.HashTokenTransactionV2(finalTx, partialHash: false);
        var operatorSignatures = await BuildOperatorSignaturesAsync(wallet, finalTx, finalHash, ct).ConfigureAwait(false);

        var commitRequest = new CommitTransactionRequest
        {
            FinalTokenTransaction = finalTx,
            FinalTokenTransactionHash = ByteString.CopyFrom(finalHash),
            OwnerIdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
        };
        commitRequest.InputTtxoSignaturesPerOperator.AddRange(operatorSignatures);

        var commitResponse = await client.commit_transactionAsync(
            commitRequest, headers, cancellationToken: ct).ConfigureAwait(false);

        byte[]? tokenId = commitResponse.HasTokenIdentifier ? commitResponse.TokenIdentifier.ToByteArray() : null;
        return (Convert.ToHexString(finalHash).ToLowerInvariant(), tokenId);
    }

    // ───────────────────────────────── Owner / operator signatures (V2) ─────────────────────────────────

    private static async Task<List<SignatureWithIndex>> BuildOwnerSignaturesAsync(
        SparkWallet wallet,
        TokenTransaction tx,
        byte[] hash,
        IReadOnlyList<byte[]>? signingPublicKeys,
        CancellationToken ct)
    {
        var signatures = new List<SignatureWithIndex>();

        switch (tx.TokenInputsCase)
        {
            case TokenTransaction.TokenInputsOneofCase.MintInput:
            case TokenTransaction.TokenInputsOneofCase.CreateInput:
                {
                    var sig = await wallet.Signer.SignWithIdentityKeyAsync(hash, ct).ConfigureAwait(false);
                    signatures.Add(new SignatureWithIndex
                    {
                        Signature = ByteString.CopyFrom(sig),
                        InputIndex = 0,
                    });
                    break;
                }
            case TokenTransaction.TokenInputsOneofCase.TransferInput:
                {
                    if (signingPublicKeys is null)
                    {
                        throw new SparkConfigurationException(
                            "token.sign",
                            "Missing signing public keys for transfer transaction.");
                    }
                    var identityKey = wallet.IdentityPublicKey;
                    for (int i = 0; i < signingPublicKeys.Count; i++)
                    {
                        if (!signingPublicKeys[i].AsSpan().SequenceEqual(identityKey))
                        {
                            throw new SparkConfigurationException(
                                "token.sign",
                                $"Cannot sign token input with unknown key (hex: {Convert.ToHexString(signingPublicKeys[i]).ToLowerInvariant()}).");
                        }
                        var sig = await wallet.Signer.SignWithIdentityKeyAsync(hash, ct).ConfigureAwait(false);
                        signatures.Add(new SignatureWithIndex
                        {
                            Signature = ByteString.CopyFrom(sig),
                            InputIndex = (uint)i,
                        });
                    }
                    break;
                }
            default:
                throw new SparkConfigurationException("token.sign", "Unknown token input type.");
        }

        return signatures;
    }

    private static async Task<List<InputTtxoSignaturesPerOperator>> BuildOperatorSignaturesAsync(
        SparkWallet wallet,
        TokenTransaction tx,
        byte[] finalHash,
        CancellationToken ct)
    {
        var result = new List<InputTtxoSignaturesPerOperator>();
        foreach (var operatorPubKey in CollectOperatorIdentityPublicKeys(wallet))
        {
            var payloadHash = TokenHashing.HashOperatorSpecificPayload(finalHash, operatorPubKey);
            var ttxoSignatures = new List<SignatureWithIndex>();

            switch (tx.TokenInputsCase)
            {
                case TokenTransaction.TokenInputsOneofCase.MintInput:
                case TokenTransaction.TokenInputsOneofCase.CreateInput:
                    {
                        var sig = await wallet.Signer.SignWithIdentityKeyAsync(payloadHash, ct).ConfigureAwait(false);
                        ttxoSignatures.Add(new SignatureWithIndex
                        {
                            Signature = ByteString.CopyFrom(sig),
                            InputIndex = 0,
                        });
                        break;
                    }
                case TokenTransaction.TokenInputsOneofCase.TransferInput:
                    {
                        var inputs = tx.TransferInput.OutputsToSpend;
                        for (int i = 0; i < inputs.Count; i++)
                        {
                            var sig = await wallet.Signer.SignWithIdentityKeyAsync(payloadHash, ct).ConfigureAwait(false);
                            ttxoSignatures.Add(new SignatureWithIndex
                            {
                                Signature = ByteString.CopyFrom(sig),
                                InputIndex = (uint)i,
                            });
                        }
                        break;
                    }
                default:
                    throw new SparkConfigurationException("token.sign", "Unknown token input type.");
            }

            var perOp = new InputTtxoSignaturesPerOperator
            {
                OperatorIdentityPublicKey = ByteString.CopyFrom(operatorPubKey),
            };
            perOp.TtxoSignatures.AddRange(ttxoSignatures);
            result.Add(perOp);
        }

        return result;
    }

    // ───────────────────────────────── Operator keys and time ─────────────────────────────────

    /// <summary>
    /// Operator identity public keys for the configured SOs, lexicographically
    /// sorted. Empty hex / unparseable entries are skipped.
    /// </summary>
    internal static IReadOnlyList<byte[]> CollectOperatorIdentityPublicKeys(SparkWallet wallet)
    {
        var keys = new List<byte[]>();
        foreach (var op in wallet.Options.SigningOperators)
        {
            if (string.IsNullOrEmpty(op.IdentityPublicKeyHex))
            {
                continue;
            }
            try
            {
                var bytes = Convert.FromHexString(op.IdentityPublicKeyHex);
                if (bytes.Length > 0)
                {
                    keys.Add(bytes);
                }
            }
            catch (FormatException)
            {
                // Skip unparseable hex.
            }
        }
        keys.Sort(static (a, b) => a.AsSpan().SequenceCompareTo(b));
        return keys;
    }

    /// <summary>
    /// Now on the operators' clock, to the microsecond: they refuse a client timestamp outside the
    /// transaction's validity window measured on theirs (the reference SDK stamps server time too).
    /// </summary>
    private static Google.Protobuf.WellKnownTypes.Timestamp CurrentTimestamp(SparkWallet wallet)
    {
        var timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(wallet.Clock.ServerNow);
        timestamp.Nanos = timestamp.Nanos / 1000 * 1000;
        return timestamp;
    }

    // ───────────────────────────────── Internal helpers ─────────────────────────────────

    private static async Task<List<OutputWithPreviousTransactionData>> FetchTokenOutputsAsync(
        SparkWallet wallet,
        IReadOnlyList<ByteString>? tokenIdentifiers,
        CancellationToken ct)
    {
        var client = wallet.GetTokenClient(wallet.CoordinatorAddress);
        var network = wallet.Options.ProtoNetwork();

        var all = new List<OutputWithPreviousTransactionData>();
        string? cursor = null;
        do
        {
            var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
            var request = new QueryTokenOutputsRequest
            {
                Network = network,
                PageRequest = new PageRequest
                {
                    PageSize = QueryTokenOutputsPageSize,
                    Direction = Direction.Next,
                    Cursor = cursor ?? string.Empty,
                },
            };
            request.OwnerPublicKeys.Add(ByteString.CopyFrom(wallet.IdentityPublicKey));
            if (tokenIdentifiers is not null)
            {
                foreach (var id in tokenIdentifiers)
                {
                    request.TokenIdentifiers.Add(id);
                }
            }

            var response = await client.query_token_outputsAsync(
                request, headers, cancellationToken: ct).ConfigureAwait(false);

            all.AddRange(response.OutputsWithPreviousTransactionData);

            cursor = response.PageResponse is { NextCursor: { Length: > 0 } next }
                ? next
                : null;
        }
        while (cursor is not null);

        return all;
    }

    /// <summary>
    /// Metadata of <paramref name="rawTokenIdentifiers"/>, asked for at most
    /// <see cref="TokenMetadataBatchSize"/> at a time: the operators refuse larger filters, and
    /// anyone can send a wallet tokens of as many kinds as they like.
    /// </summary>
    private static async Task<Dictionary<ByteString, TokenMetadata>> FetchTokenMetadataMapAsync(
        SparkWallet wallet,
        IReadOnlyList<ByteString> rawTokenIdentifiers,
        CancellationToken ct)
    {
        var result = new Dictionary<ByteString, TokenMetadata>(rawTokenIdentifiers.Count);
        if (rawTokenIdentifiers.Count == 0)
        {
            return result;
        }

        var client = wallet.GetTokenClient(wallet.CoordinatorAddress);
        foreach (var batch in rawTokenIdentifiers.Chunk(TokenMetadataBatchSize))
        {
            var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
            var request = new QueryTokenMetadataRequest();
            request.TokenIdentifiers.AddRange(batch);
            var response = await client.query_token_metadataAsync(
                request, headers, cancellationToken: ct).ConfigureAwait(false);
            foreach (var meta in response.TokenMetadata)
            {
                result[meta.TokenIdentifier] = ToModel(meta, wallet.Options.Network);
            }
        }

        return result;
    }

    private static TokenMetadata ToModel(ProtoTokenMetadata proto, SparkNetwork network)
    {
        var bech32m = TokenIdentifier.Encode(proto.TokenIdentifier.ToByteArray(), network);
        return new TokenMetadata(
            TokenIdentifier: bech32m,
            RawTokenIdentifier: proto.TokenIdentifier.ToByteArray(),
            IssuerPublicKey: proto.IssuerPublicKey.ToByteArray(),
            TokenName: proto.TokenName,
            TokenTicker: proto.TokenTicker,
            Decimals: proto.Decimals,
            MaxSupply: proto.MaxSupply.ToByteArray(),
            IsFreezable: proto.IsFreezable,
            ExtraMetadata: proto.HasExtraMetadata ? proto.ExtraMetadata.ToByteArray() : null);
    }

    private static TokenOutputInfo MapOutput(OutputWithPreviousTransactionData entry)
    {
        var o = entry.Output;
        return new TokenOutputInfo(
            Id: o.HasId && !string.IsNullOrEmpty(o.Id) ? o.Id : null,
            OwnerPublicKey: o.OwnerPublicKey.ToByteArray(),
            TokenIdentifier: o.TokenIdentifier.ToByteArray(),
            TokenAmount: DecodeUInt128(o.TokenAmount),
            PreviousTransactionHash: entry.PreviousTransactionHash.ToByteArray(),
            PreviousTransactionVout: entry.PreviousTransactionVout,
            Status: o.HasStatus ? StatusName(o.Status) : "AVAILABLE");
    }

    /// <summary>The operators' name of an output status, as <see cref="TokenOutputInfo.Status"/> documents it.</summary>
    internal static string StatusName(TokenOutputStatus status) => status switch
    {
        TokenOutputStatus.Available => "AVAILABLE",
        TokenOutputStatus.PendingOutbound => "PENDING_OUTBOUND",
        TokenOutputStatus.Unspecified => "UNSPECIFIED",
        _ => ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Decode a 16-byte big-endian buffer to <see cref="UInt128"/>.
    /// </summary>
    internal static UInt128 DecodeUInt128(ByteString data)
    {
        if (data.Length != 16)
        {
            return UInt128.Zero;
        }
        Span<byte> tmp = stackalloc byte[16];
        data.Span.CopyTo(tmp);
        return BinaryPrimitives.ReadUInt128BigEndian(tmp);
    }

    /// <summary>
    /// Encode a <see cref="UInt128"/> to a 16-byte big-endian buffer.
    /// </summary>
    internal static ByteString EncodeUInt128(UInt128 value)
    {
        Span<byte> buf = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(buf, value);
        return ByteString.CopyFrom(buf);
    }
}
