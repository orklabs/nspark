using System.Buffers.Binary;
using Google.Protobuf;
using NSpark.Exceptions;
using NSpark.Models;
using NSpark.Proto;
using NSpark.Proto.Token;
using ProtoTokenMetadata = NSpark.Proto.Token.TokenMetadata;
using TokenMetadata = NSpark.Models.TokenMetadata;

namespace NSpark.Services;

/// <summary>
/// Extension methods on <see cref="SparkWallet"/> for working with Spark
/// tokens (LRC-20-style fungible assets settled on Spark).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-side methods</b> (balances, outputs, metadata queries) are fully
/// implemented and match the Swift / Kotlin / TS Spark SDKs.
/// </para>
/// <para>
/// <b>Write-side methods</b> (<see cref="TransferTokensAsync"/>,
/// <see cref="CreateTokenAsync"/>, <see cref="MintTokensAsync"/>,
/// <see cref="BurnTokensAsync"/>) throw <see cref="NotImplementedException"/>
/// pending a focused port of the Swift token signing pipeline:
/// secp256k1 group operations for revocation commitments + FROST signing
/// rounds + two-phase broadcast against the SOs. Each method is shaped to
/// match the Swift API so consumers can write their code against the
/// final signature today and only the implementation drops in later.
/// </para>
/// <para>
/// Track the write-side port at
/// <see href="https://github.com/p-i-g-g-y/nspark/issues" />.
/// </para>
/// </remarks>
public static class TokenService
{
    private const uint QueryTokenOutputsPageSize = 100;

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
            if (IsAvailableStatus(output))
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
            var (raw, _) = TokenIdentifier.Decode(bech32mTokenIdentifier, wallet.Client.Options.Network);
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

        var soAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var client = wallet.Pool.GetTokenClient(soAddress);
        var headers = await wallet.GetAuthMetadataAsync(soAddress, ct).ConfigureAwait(false);

        var request = new QueryTokenMetadataRequest();
        if (bech32mTokenIdentifiers is not null)
        {
            foreach (var id in bech32mTokenIdentifiers)
            {
                var (raw, _) = TokenIdentifier.Decode(id, wallet.Client.Options.Network);
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

        return response.TokenMetadata.Select(m => ToModel(m, wallet.Client.Options.Network)).ToList();
    }

    // ───────────────────────────────── Write-side ─────────────────────────────────

    private const int MaxTokenOutputsPerTx = 500;

    /// <summary>
    /// Transfer tokens to a receiver identified by their Spark address. Returns
    /// the broadcast transaction hash (hex).
    /// </summary>
    /// <param name="wallet">The sending wallet.</param>
    /// <param name="bech32mTokenIdentifier">Bech32m token id (e.g. <c>"btkn1..."</c>).</param>
    /// <param name="amount">Token units to send.</param>
    /// <param name="receiverSparkAddress">Receiver's Spark address (e.g. <c>"spark1..."</c>).</param>
    /// <param name="strategy">Output-selection strategy.</param>
    /// <param name="idempotencyKey">Optional idempotency token forwarded as gRPC metadata.</param>
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

        var (rawTokenId, _) = TokenIdentifier.Decode(bech32mTokenIdentifier, wallet.Client.Options.Network);
        var outputs = await FetchTokenOutputsAsync(wallet, [ByteString.CopyFrom(rawTokenId)], ct).ConfigureAwait(false);
        if (outputs.Count == 0)
        {
            throw new SparkConfigurationException(
                "token.transfer",
                $"Insufficient token balance for {bech32mTokenIdentifier}: need {amount}, have 0.");
        }

        var selected = SelectTokenOutputs(outputs, amount, strategy);
        var receiverPubKey = SparkAddress.DecodeIdentityPublicKey(receiverSparkAddress);

        var tx = BuildTransferTokenTransaction(
            wallet,
            selected,
            [(receiverPubKey, rawTokenId, amount)],
            changeOwnerPubKey: wallet.IdentityPublicKey);

        var (hashHex, _) = await BroadcastTokenTransactionV2Async(
            wallet,
            tx,
            signingPublicKeys: selected.Select(o => o.Output.OwnerPublicKey.ToByteArray()).ToList(),
            idempotencyKey,
            ct).ConfigureAwait(false);

        return new TokenTransferResult(hashHex);
    }

    /// <summary>
    /// Create a new token on Spark — the caller's identity key becomes the issuer.
    /// </summary>
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
        ArgumentException.ThrowIfNullOrEmpty(tokenName);
        ArgumentException.ThrowIfNullOrEmpty(tokenTicker);

        var nameBytes = System.Text.Encoding.UTF8.GetBytes(tokenName);
        if (nameBytes.Length == 0 || nameBytes.Length > 20)
        {
            throw new SparkConfigurationException(
                "token.create",
                "Token name must be 1-20 UTF-8 bytes.");
        }
        var tickerBytes = System.Text.Encoding.UTF8.GetBytes(tokenTicker);
        if (tickerBytes.Length == 0 || tickerBytes.Length > 6)
        {
            throw new SparkConfigurationException(
                "token.create",
                "Token ticker must be 1-6 UTF-8 bytes.");
        }
        if (decimals > 255)
        {
            throw new SparkConfigurationException(
                "token.create",
                "Decimals must be <= 255.");
        }
        if (extraMetadata is { Length: > 1024 })
        {
            throw new SparkConfigurationException(
                "token.create",
                "Extra metadata must be <= 1024 bytes.");
        }

        var issuerPubKey = wallet.IdentityPublicKey;
        var createInput = new TokenCreateInput
        {
            IssuerPublicKey = ByteString.CopyFrom(issuerPubKey),
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

        var tx = NewTokenTransaction(wallet);
        tx.CreateInput = createInput;

        var (hashHex, tokenId) = await BroadcastTokenTransactionV2Async(
            wallet, tx, signingPublicKeys: null, idempotencyKey: null, ct).ConfigureAwait(false);

        string? bech32 = null;
        if (tokenId is { Length: 32 })
        {
            bech32 = TokenIdentifier.Encode(tokenId, wallet.Client.Options.Network);
        }
        return new TokenCreationResult(hashHex, bech32);
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

        var (rawTokenId, _) = TokenIdentifier.Decode(bech32mTokenIdentifier, wallet.Client.Options.Network);
        var issuerPubKey = wallet.IdentityPublicKey;

        var mintInput = new TokenMintInput
        {
            IssuerPublicKey = ByteString.CopyFrom(issuerPubKey),
            TokenIdentifier = ByteString.CopyFrom(rawTokenId),
        };

        var mintOutput = new TokenOutput
        {
            OwnerPublicKey = ByteString.CopyFrom(issuerPubKey),
            TokenIdentifier = ByteString.CopyFrom(rawTokenId),
            TokenAmount = EncodeUInt128(amount),
        };

        var tx = NewTokenTransaction(wallet);
        tx.MintInput = mintInput;
        tx.TokenOutputs.Add(mintOutput);

        var (hashHex, _) = await BroadcastTokenTransactionV2Async(
            wallet, tx, signingPublicKeys: null, idempotencyKey: null, ct).ConfigureAwait(false);
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

        var (rawTokenId, _) = TokenIdentifier.Decode(bech32mTokenIdentifier, wallet.Client.Options.Network);
        var outputs = await FetchTokenOutputsAsync(wallet, [ByteString.CopyFrom(rawTokenId)], ct).ConfigureAwait(false);
        if (outputs.Count == 0)
        {
            throw new SparkConfigurationException(
                "token.burn",
                $"Insufficient token balance for {bech32mTokenIdentifier}: need {amount}, have 0.");
        }

        var selected = SelectTokenOutputs(outputs, amount, strategy);

        var tx = BuildTransferTokenTransaction(
            wallet,
            selected,
            [(burnPubKey, rawTokenId, amount)],
            changeOwnerPubKey: wallet.IdentityPublicKey);

        var (hashHex, _) = await BroadcastTokenTransactionV2Async(
            wallet,
            tx,
            signingPublicKeys: selected.Select(o => o.Output.OwnerPublicKey.ToByteArray()).ToList(),
            idempotencyKey: null,
            ct).ConfigureAwait(false);
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
            return new List<OutputWithPreviousTransactionData> { exact };
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

    // ───────────────────────────────── Build transfer tx ─────────────────────────────────

    private static Proto.Token.TokenTransaction BuildTransferTokenTransaction(
        SparkWallet wallet,
        IReadOnlyList<OutputWithPreviousTransactionData> selectedOutputs,
        IReadOnlyList<(byte[] ReceiverPubKey, byte[] RawTokenId, UInt128 Amount)> receiverOutputs,
        byte[] changeOwnerPubKey)
    {
        // Sort by previous-tx vout for deterministic ordering.
        var sorted = selectedOutputs.OrderBy(o => o.PreviousTransactionVout).ToList();

        // Sum per token-id available + requested for change calculation.
        var availableByToken = new Dictionary<ByteString, UInt128>();
        foreach (var output in sorted)
        {
            var id = output.Output.TokenIdentifier;
            availableByToken.TryGetValue(id, out var existing);
            availableByToken[id] = existing + DecodeUInt128(output.Output.TokenAmount);
        }
        var requestedByToken = new Dictionary<ByteString, UInt128>();
        foreach (var (_, rawTokenId, amount) in receiverOutputs)
        {
            var id = ByteString.CopyFrom(rawTokenId);
            requestedByToken.TryGetValue(id, out var existing);
            requestedByToken[id] = existing + amount;
        }

        var tokenOutputs = new List<TokenOutput>(receiverOutputs.Count + 1);
        foreach (var (receiverPubKey, rawTokenId, amount) in receiverOutputs)
        {
            tokenOutputs.Add(new TokenOutput
            {
                OwnerPublicKey = ByteString.CopyFrom(receiverPubKey),
                TokenIdentifier = ByteString.CopyFrom(rawTokenId),
                TokenAmount = EncodeUInt128(amount),
            });
        }

        // Change outputs per token.
        foreach (var (tokenId, availableAmount) in availableByToken)
        {
            requestedByToken.TryGetValue(tokenId, out var requested);
            if (availableAmount > requested)
            {
                tokenOutputs.Add(new TokenOutput
                {
                    OwnerPublicKey = ByteString.CopyFrom(changeOwnerPubKey),
                    TokenIdentifier = tokenId,
                    TokenAmount = EncodeUInt128(availableAmount - requested),
                });
            }
        }

        var transferInput = new TokenTransferInput();
        foreach (var output in sorted)
        {
            transferInput.OutputsToSpend.Add(new TokenOutputToSpend
            {
                PrevTokenTransactionHash = output.PreviousTransactionHash,
                PrevTokenTransactionVout = output.PreviousTransactionVout,
            });
        }

        var tx = NewTokenTransaction(wallet);
        tx.TransferInput = transferInput;
        tx.TokenOutputs.AddRange(tokenOutputs);
        return tx;
    }

    // ───────────────────────────────── Two-phase broadcast ─────────────────────────────────

    private static async Task<(string TransactionHashHex, byte[]? TokenIdentifier)>
        BroadcastTokenTransactionV2Async(
            SparkWallet wallet,
            Proto.Token.TokenTransaction tx,
            IReadOnlyList<byte[]>? signingPublicKeys,
            string? idempotencyKey,
            CancellationToken ct)
    {
        var soAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var client = wallet.Pool.GetTokenClient(soAddress);
        var headers = await wallet.GetAuthMetadataAsync(soAddress, ct).ConfigureAwait(false);

        if (idempotencyKey is { Length: > 0 })
        {
            headers = new Grpc.Core.Metadata();
            foreach (var entry in await wallet.GetAuthMetadataAsync(soAddress, ct).ConfigureAwait(false))
            {
                headers.Add(entry);
            }
            headers.Add("x-idempotency-key", idempotencyKey);
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
            startRequest, headers, cancellationToken: ct).ConfigureAwait(false);

        if (startResponse.FinalTokenTransaction is null)
        {
            throw new SparkConfigurationException(
                "token.broadcast",
                "Missing final token transaction in start response.");
        }
        var finalTx = startResponse.FinalTokenTransaction;

        // The coordinator may only add server-set fields; anything else is refused before the
        // wallet signs the final hash for each operator (reference SDK: validateTokenTransaction).
        var options = wallet.Client.Options;
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

    // ───────────────────────────────── Owner / operator signatures ─────────────────────────────────

    private static async Task<List<SignatureWithIndex>> BuildOwnerSignaturesAsync(
        SparkWallet wallet,
        Proto.Token.TokenTransaction tx,
        byte[] hash,
        IReadOnlyList<byte[]>? signingPublicKeys,
        CancellationToken ct)
    {
        var signatures = new List<SignatureWithIndex>();

        switch (tx.TokenInputsCase)
        {
            case Proto.Token.TokenTransaction.TokenInputsOneofCase.MintInput:
            case Proto.Token.TokenTransaction.TokenInputsOneofCase.CreateInput:
                {
                    var sig = await wallet.Signer.SignWithIdentityKeyAsync(hash, ct).ConfigureAwait(false);
                    signatures.Add(new SignatureWithIndex
                    {
                        Signature = ByteString.CopyFrom(sig),
                        InputIndex = 0,
                    });
                    break;
                }
            case Proto.Token.TokenTransaction.TokenInputsOneofCase.TransferInput:
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
        Proto.Token.TokenTransaction tx,
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
                case Proto.Token.TokenTransaction.TokenInputsOneofCase.MintInput:
                case Proto.Token.TokenTransaction.TokenInputsOneofCase.CreateInput:
                    {
                        var sig = await wallet.Signer.SignWithIdentityKeyAsync(payloadHash, ct).ConfigureAwait(false);
                        ttxoSignatures.Add(new SignatureWithIndex
                        {
                            Signature = ByteString.CopyFrom(sig),
                            InputIndex = 0,
                        });
                        break;
                    }
                case Proto.Token.TokenTransaction.TokenInputsOneofCase.TransferInput:
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

    // ───────────────────────────────── Transaction skeleton ─────────────────────────────────

    private static Proto.Token.TokenTransaction NewTokenTransaction(SparkWallet wallet)
    {
        var tx = new Proto.Token.TokenTransaction
        {
            Version = 2,
            Network = ToProtoNetwork(wallet.Client.Options.Network),
            ClientCreatedTimestamp = CurrentTimestamp(),
        };
        foreach (var op in CollectOperatorIdentityPublicKeys(wallet))
        {
            tx.SparkOperatorIdentityPublicKeys.Add(ByteString.CopyFrom(op));
        }
        return tx;
    }

    /// <summary>
    /// Operator identity public keys for the configured SOs, lexicographically
    /// sorted. Empty hex / unparseable entries are skipped to keep behaviour
    /// aligned with the Swift SDK on regtest where pubkeys may be unset.
    /// </summary>
    internal static IReadOnlyList<byte[]> CollectOperatorIdentityPublicKeys(SparkWallet wallet)
    {
        var keys = new List<byte[]>();
        foreach (var op in wallet.Client.Options.SigningOperators)
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
        keys.Sort(static (a, b) =>
        {
            var len = Math.Min(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                var c = a[i].CompareTo(b[i]);
                if (c != 0)
                {
                    return c;
                }
            }
            return a.Length.CompareTo(b.Length);
        });
        return keys;
    }

    private static Google.Protobuf.WellKnownTypes.Timestamp CurrentTimestamp()
    {
        // Match Swift behaviour: zero out sub-millisecond nanoseconds.
        var now = DateTimeOffset.UtcNow;
        var seconds = now.ToUnixTimeSeconds();
        var milliFraction = (int)(now.ToUnixTimeMilliseconds() % 1000);
        return new Google.Protobuf.WellKnownTypes.Timestamp
        {
            Seconds = seconds,
            Nanos = milliFraction * 1_000_000,
        };
    }

    // ───────────────────────────────── Internal helpers ─────────────────────────────────

    private static async Task<List<OutputWithPreviousTransactionData>> FetchTokenOutputsAsync(
        SparkWallet wallet,
        IReadOnlyList<ByteString>? tokenIdentifiers,
        CancellationToken ct)
    {
        var soAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var client = wallet.Pool.GetTokenClient(soAddress);
        var headers = await wallet.GetAuthMetadataAsync(soAddress, ct).ConfigureAwait(false);

        var network = ToProtoNetwork(wallet.Client.Options.Network);

        var all = new List<OutputWithPreviousTransactionData>();
        string? cursor = null;
        do
        {
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

    private static async Task<Dictionary<ByteString, TokenMetadata>> FetchTokenMetadataMapAsync(
        SparkWallet wallet,
        IReadOnlyList<ByteString> rawTokenIdentifiers,
        CancellationToken ct)
    {
        if (rawTokenIdentifiers.Count == 0)
        {
            return new Dictionary<ByteString, TokenMetadata>();
        }

        var soAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var client = wallet.Pool.GetTokenClient(soAddress);
        var headers = await wallet.GetAuthMetadataAsync(soAddress, ct).ConfigureAwait(false);

        var request = new QueryTokenMetadataRequest();
        foreach (var id in rawTokenIdentifiers)
        {
            request.TokenIdentifiers.Add(id);
        }

        var response = await client.query_token_metadataAsync(
            request, headers, cancellationToken: ct).ConfigureAwait(false);

        var result = new Dictionary<ByteString, TokenMetadata>(response.TokenMetadata.Count);
        foreach (var meta in response.TokenMetadata)
        {
            result[meta.TokenIdentifier] = ToModel(meta, wallet.Client.Options.Network);
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
            Status: o.HasStatus ? o.Status.ToString() : "AVAILABLE");
    }

    private static bool IsAvailableStatus(TokenOutput output) =>
        !output.HasStatus
        || output.Status == TokenOutputStatus.Unspecified
        || output.Status == TokenOutputStatus.Available;

    private static Network ToProtoNetwork(SparkNetwork network) => network switch
    {
        SparkNetwork.Mainnet => Network.Mainnet,
        SparkNetwork.Regtest => Network.Regtest,
        _ => throw new ArgumentOutOfRangeException(nameof(network)),
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
