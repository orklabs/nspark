using System.Security.Cryptography;
using Google.Protobuf;
using NSpark.Exceptions;
using NSpark.GraphQL;
using NSpark.Models;
using NSpark.Proto;
using NSpark.Signer;

namespace NSpark.Services;

/// <summary>
/// Extension methods on <see cref="SparkWallet"/> for withdrawing to Bitcoin L1 through a
/// cooperative exit brokered by the SSP.
/// </summary>
public static class WithdrawalService
{
    private const string Operation = "withdrawal.withdraw";

    /// <summary>
    /// Get a fee estimate for an on-chain withdrawal (cooperative exit).
    /// </summary>
    public static async Task<FeeQuote> GetFeeQuoteAsync(
        this SparkWallet wallet,
        string[] leafIds,
        string onChainAddress,
        CancellationToken ct = default)
    {
        var variables = new Dictionary<string, object>
        {
            ["leaf_external_ids"] = leafIds,
            ["withdrawal_address"] = onChainAddress,
        };

        var response = await wallet.SspClient.ExecuteAsync<CoopExitFeeEstimateResponse>(
            Mutations.CoopExitFeeEstimate, variables, ct).ConfigureAwait(false);

        // The SSP returns each fee as a CurrencyAmount with an `original_unit` discriminator.
        // Convert via the shared helper so all three SSP fee call sites (this one, lightning
        // send fee estimate, lightning send status) use identical dispatch.
        var fast = response.CoopExitFeeEstimates.SpeedFast;
        var totalFeeSats =
            CurrencyAmountExtensions.ToSats(fast.UserFee.OriginalValue, fast.UserFee.OriginalUnit) +
            CurrencyAmountExtensions.ToSats(fast.L1BroadcastFee.OriginalValue, fast.L1BroadcastFee.OriginalUnit);
        return new FeeQuote(FeeSats: totalFeeSats, FeeRateSatsPerVbyte: 0);
    }

    /// <summary>
    /// Withdraw from Spark to an on-chain Bitcoin address via a cooperative exit brokered by
    /// the SSP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SSP's fee is deducted from <paramref name="amountSats"/>: the destination receives
    /// <paramref name="amountSats"/> minus the fee. Leaves are swapped to denominations that sum
    /// to exactly <paramref name="amountSats"/> first, so no more than the requested amount ever
    /// leaves the wallet.
    /// </para>
    /// <para>
    /// Before anything is signed the SSP's response is verified: the exit transaction must hash
    /// to the txid it reports, pay <paramref name="onChainAddress"/> at least
    /// <paramref name="amountSats"/> minus the fee cap, and the connector transaction must spend
    /// it. A response that fails these checks throws <see cref="SparkUntrustedResponseException"/>
    /// and no leaves are handed over.
    /// </para>
    /// <para>
    /// The exit speaks the protocol the coordinator requires today: the connector-input refund
    /// transactions are FROST-signed by the user and sent together with the encrypted key-tweak
    /// package in a single <c>cooperative_exit_v2</c> call, as the reference SDK's
    /// <c>CoopExitService</c> does. The older two-step form (unsigned jobs, then
    /// <c>finalize_transfer_with_transfer_package</c>) is rejected by mainnet.
    /// </para>
    /// </remarks>
    /// <param name="wallet">The Spark wallet.</param>
    /// <param name="onChainAddress">Destination Bitcoin address on the wallet's network (P2PKH, P2SH, P2WPKH, P2WSH, or P2TR).</param>
    /// <param name="amountSats">Amount in sats to withdraw, fee included.</param>
    /// <param name="maxFeeSats">
    /// Highest fee the caller accepts. When <c>null</c> the SSP's fee quote for the selected
    /// leaves is used as the bound. Throws <see cref="FeeExceedsLimitException"/> if the quote is
    /// above the cap, or if the cap would consume the whole amount.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The L1 transaction id of the cooperative exit (display order, big-endian hex).</returns>
    public static async Task<string> WithdrawAsync(
        this SparkWallet wallet,
        string onChainAddress,
        long amountSats,
        long? maxFeeSats = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentException.ThrowIfNullOrWhiteSpace(onChainAddress);
        if (amountSats <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amountSats), amountSats, "Withdrawal amount must be positive.");
        }

        var network = wallet.Client.Options.Network;

        // Fail fast on a malformed or wrong-network destination, before any leaf is moved.
        _ = CoopExitValidator.ScriptPubKeyFor(onChainAddress, network);

        // Select leaves that sum to exactly the requested amount (swapping via the SSP if
        // needed): the SSP exits the full value of the leaves it is given.
        var selectedLeaves = await wallet.SelectLeavesWithSwapAsync(amountSats, ct).ConfigureAwait(false);
        var selectedTotal = selectedLeaves.Sum(l => l.ValueSats);
        if (selectedTotal != amountSats)
        {
            throw new SparkWithdrawalException(
                Operation, $"Selected leaves sum to {selectedTotal} sats, expected exactly {amountSats}.");
        }

        // Bound the fee before asking the SSP to build the exit.
        var quote = await wallet.GetFeeQuoteAsync(
            selectedLeaves.Select(l => l.Id).ToArray(), onChainAddress, ct).ConfigureAwait(false);
        var feeCap = CoopExitValidator.ResolveFeeCap(quote.FeeSats, maxFeeSats, amountSats);

        return await PerformCooperativeExitAsync(
            wallet, selectedLeaves, amountSats, feeCap, onChainAddress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The cooperative exit proper: request the exit from the SSP, verify what it built, sign
    /// the connector refunds, hand the leaves over in one transfer package, complete via the
    /// SSP. <paramref name="selectedLeaves"/> must sum to <paramref name="amountSats"/>; the
    /// payout must be at least <c>amountSats - feeCap</c>.
    /// </summary>
    private static async Task<string> PerformCooperativeExitAsync(
        SparkWallet wallet,
        IReadOnlyList<SparkLeaf> selectedLeaves,
        long amountSats,
        long feeCap,
        string onChainAddress,
        CancellationToken ct)
    {
        var options = wallet.Client.Options;
        var coordinatorAddress = options.SigningOperatorAddresses[0];
        var coordinatorClient = wallet.Pool.GetSparkClient(coordinatorAddress);
        var headers = await wallet.GetAuthMetadataAsync(coordinatorAddress, ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(options.Network);
        var leafIds = selectedLeaves.Select(l => l.Id).ToArray();
        var minimumPayoutSats = amountSats - feeCap;
        var receiverPubKey = Convert.FromHexString(options.SspIdentityPublicKeyHex);

        // Step 1: ask the SSP to build the exit and connector transactions.
        var transferId = Guid.NewGuid().ToString("D");
        var sspResponse = await wallet.SspClient.ExecuteAsync<RequestCoopExitResponse>(
            Mutations.RequestCoopExit,
            new Dictionary<string, object>
            {
                ["leaf_external_ids"] = leafIds,
                ["withdrawal_address"] = onChainAddress,
                ["exit_speed"] = "FAST",
                ["withdraw_all"] = true,
                ["user_outbound_transfer_external_id"] = transferId,
            },
            ct).ConfigureAwait(false);
        var request = sspResponse.RequestCoopExit.Request;

        // Step 2: verify what the SSP built before signing anything.
        var validated = CoopExitValidator.Validate(
            request.RawCoopExitTransaction,
            request.RawConnectorTransaction,
            request.CoopExitTxid,
            onChainAddress,
            minimumPayoutSats,
            selectedLeaves.Count,
            options.Network);
        var connectorTxBytes = Convert.FromHexString(request.RawConnectorTransaction.Trim());

        // Step 3: operator nonce commitments, three per leaf (cpfp, direct, directFromCpfp),
        // laid out leaf-major like the transfer flow.
        var commitmentsRequest = new GetSigningCommitmentsRequest { Count = 3 };
        commitmentsRequest.NodeIds.AddRange(leafIds);
        var commitmentsResponse = await coordinatorClient.get_signing_commitmentsAsync(
            commitmentsRequest, headers, cancellationToken: ct).ConfigureAwait(false);
        var commitments = commitmentsResponse.SigningCommitments;
        if (commitments.Count < 3 * selectedLeaves.Count)
        {
            throw new SparkWithdrawalException(
                Operation, $"Got {commitments.Count} signing commitments, need {3 * selectedLeaves.Count}.");
        }

        // Step 4: refund transactions that also spend a connector output, FROST-signed by the user.
        var cpfpJobs = new List<UserSignedTxSigningJob>(selectedLeaves.Count);
        var directJobs = new List<UserSignedTxSigningJob>(selectedLeaves.Count);
        var directFromCpfpJobs = new List<UserSignedTxSigningJob>(selectedLeaves.Count);
        for (int i = 0; i < selectedLeaves.Count; i++)
        {
            var leaf = selectedLeaves[i];
            var verifyingKey = leaf.Node.VerifyingPublicKey.ToByteArray();
            var connectorOutput = validated.ConnectorTx.Outputs[i];
            var refunds = BuildConnectorRefunds(
                leaf.Node,
                receiverPubKey,
                validated.ConnectorTxidInternal,
                connectorOutput.ScriptPubKey.ToBytes(),
                (ulong)connectorOutput.Value.Satoshi,
                (uint)i,
                networkStr);

            cpfpJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                wallet.Signer, leaf.Id, verifyingKey,
                refunds.Cpfp.Tx, refunds.Cpfp.Sighash,
                commitments[i].SigningNonceCommitments, ct).ConfigureAwait(false));

            if (refunds.Direct is { } direct)
            {
                directJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                    wallet.Signer, leaf.Id, verifyingKey,
                    direct.Tx, direct.Sighash,
                    commitments[i + selectedLeaves.Count].SigningNonceCommitments, ct).ConfigureAwait(false));
            }

            directFromCpfpJobs.Add(await FrostSigningHelper.BuildSigningJobAsync(
                wallet.Signer, leaf.Id, verifyingKey,
                refunds.DirectFromCpfp.Tx, refunds.DirectFromCpfp.Sighash,
                commitments[i + (2 * selectedLeaves.Count)].SigningNonceCommitments, ct).ConfigureAwait(false));
        }

        // Step 5: key tweaks handing the leaves to the SSP, encrypted per operator and signed.
        var soListResponse = await coordinatorClient.get_signing_operator_listAsync(
            new Google.Protobuf.WellKnownTypes.Empty(), headers, cancellationToken: ct).ConfigureAwait(false);
        var soTargets = FrostSigningHelper.BuildSoTargets(soListResponse.SigningOperators, options.SigningOperators);
        var leafDescriptors = selectedLeaves
            .Select(l => new SendTweakLeafDescriptor(l.Id, receiverPubKey))
            .ToList();
        var encryptedBatch = await wallet.Signer.BuildEncryptedSendTweaksAsync(
            leafDescriptors, soTargets, transferId, options.EffectiveSigningThreshold, ct).ConfigureAwait(false);

        var keyTweakPackage = new Dictionary<string, ByteString>(encryptedBatch.EncryptedPackageBySoId.Count);
        foreach (var (soId, blob) in encryptedBatch.EncryptedPackageBySoId)
        {
            keyTweakPackage[soId] = ByteString.CopyFrom(blob);
        }

        var transferPackage = new TransferPackage { HashVariant = HashVariant.V2 };
        transferPackage.LeavesToSend.AddRange(cpfpJobs);
        transferPackage.DirectLeavesToSend.AddRange(directJobs);
        transferPackage.DirectFromCpfpLeavesToSend.AddRange(directFromCpfpJobs);
        foreach (var (soId, cipher) in keyTweakPackage)
        {
            transferPackage.KeyTweakPackage.Add(soId, cipher);
        }

        var transferIdBytes = Convert.FromHexString(transferId.Replace("-", "", StringComparison.Ordinal));
        var packageHash = SparkTaggedHash.Create("spark", "transfer", "signing payload")
            .AddBytes(transferIdBytes)
            .AddMapStringToBytes(keyTweakPackage)
            .Hash();
        transferPackage.UserSignature = ByteString.CopyFrom(
            await wallet.Signer.SignWithIdentityKeyAsync(packageHash, ct).ConfigureAwait(false));

        // Step 6: cooperative_exit_v2 with the transfer package. The expiry mirrors the
        // reference SDK: seven days (plus slack) on mainnet, 35 minutes elsewhere.
        var expiry = options.Network == SparkNetwork.Mainnet
            ? DateTimeOffset.UtcNow.AddDays(7).AddMinutes(5)
            : DateTimeOffset.UtcNow.AddMinutes(35);
        var transferRequest = new StartTransferRequest
        {
            TransferId = transferId,
            OwnerIdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
            ReceiverIdentityPublicKey = ByteString.CopyFrom(receiverPubKey),
            ExpiryTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(expiry),
            TransferPackage = transferPackage,
        };

        var exitResponse = await coordinatorClient.cooperative_exit_v2Async(
            new CooperativeExitRequest
            {
                Transfer = transferRequest,
                ExitId = Guid.NewGuid().ToString("D"),
                ExitTxid = ByteString.CopyFrom(validated.ExitTxidInternal),
                ConnectorTx = ByteString.CopyFrom(connectorTxBytes),
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);
        if (exitResponse.Transfer is null)
        {
            throw new SparkWithdrawalException(Operation, "cooperative_exit_v2 returned no transfer.");
        }

        // Step 7: complete the exit via the SSP, which broadcasts once the transfer is in place.
        await wallet.SspClient.ExecuteAsync<CompleteCoopExitResponse>(
            Mutations.CompleteCoopExit,
            new Dictionary<string, object>
            {
                ["user_outbound_transfer_external_id"] = exitResponse.Transfer.Id,
            },
            ct).ConfigureAwait(false);

        return validated.ExitTxidHex;
    }

    // ── Connector refunds ──

    /// <summary>A refund transaction with the connector output appended, and its two-input sighash.</summary>
    internal sealed record ConnectorRefund(byte[] Tx, byte[] Sighash);

    /// <summary>
    /// The three connector refunds of one leaf. <see cref="Direct"/> is absent for zero-timelock
    /// nodes and leaves without a direct node transaction.
    /// </summary>
    internal sealed record ConnectorRefunds(ConnectorRefund Cpfp, ConnectorRefund? Direct, ConnectorRefund DirectFromCpfp);

    /// <summary>
    /// The leaf's next refund transactions with the connector output appended as a second
    /// input, and their two-input sighashes (BIP-341, prevouts = node output + connector
    /// output). This is what the user signs for a cooperative exit; mirrors the reference SDK's
    /// <c>createConnectorRefundTxs</c> + <c>signRefundsForCoopExit</c>.
    /// </summary>
    internal static ConnectorRefunds BuildConnectorRefunds(
        TreeNode node,
        byte[] receiverPubKey,
        byte[] connectorTxidInternal,
        byte[] connectorOutputScript,
        ulong connectorOutputValue,
        uint connectorVout,
        string networkStr)
    {
        var refundTxBytes = node.RefundTx.Length > 0
            ? node.RefundTx.ToByteArray()
            : node.NodeTx.ToByteArray();
        var (cpfpSequence, directSequence) = TimelockHelper.ComputeNextSequences(
            refundTxBytes, Operation, node.Id);

        var cpfpNodeTx = node.NodeTx.ToByteArray();
        var isZeroNode = IsZeroTimelockNode(cpfpNodeTx);
        var directNodeTx = node.DirectTx.Length == 0 || isZeroNode ? null : node.DirectTx.ToByteArray();

        var trio = SparkTxBuilder.BuildRefundTxTrio(
            cpfpNodeTx: cpfpNodeTx,
            directNodeTx: directNodeTx,
            vout: 0,
            receivingPublicKey: receiverPubKey,
            network: networkStr,
            sequence: cpfpSequence,
            directSequence: directSequence,
            // The SSP validates all three refund outputs on coop-exit and rejects with
            // "expected value X on output 0" if the standard fee isn't deducted.
            feeSats: SparkConstants.DefaultRefundFeeSats);

        var connectorInput = MakeConnectorInputBytes(connectorTxidInternal, connectorVout);
        var nodeOutput = ParseTxOutput(cpfpNodeTx, 0);

        ConnectorRefund WithConnector(byte[] refundTx, (byte[] Script, ulong Value) spending)
        {
            var tx = AddInputToRawTx(refundTx, connectorInput);
            var sighash = SparkTxBuilder.ComputeMultiInputSighash(
                tx: tx,
                inputIndex: 0,
                prevOutScripts: [spending.Script, connectorOutputScript],
                prevOutValues: [spending.Value, connectorOutputValue]);
            return new ConnectorRefund(tx, sighash);
        }

        var cpfp = WithConnector(trio.CpfpRefund.Tx, nodeOutput);
        ConnectorRefund? direct = null;
        if (trio.DirectRefund is { } directRefund && directNodeTx is not null)
        {
            direct = WithConnector(directRefund.Tx, ParseTxOutput(directNodeTx, 0));
        }
        var directFromCpfp = WithConnector(trio.DirectFromCpfpRefund.Tx, nodeOutput);
        return new ConnectorRefunds(cpfp, direct, directFromCpfp);
    }

    // ── Raw tx helpers ──

    /// <summary>
    /// Compute txid from raw transaction bytes (double SHA-256 of witness-stripped serialization).
    /// Returns bytes in internal byte order (used as prevout hash in inputs).
    /// </summary>
    internal static byte[] ComputeTxId(byte[] rawTx)
    {
        var strippedTx = StripWitness(rawTx);
        var hash1 = SHA256.HashData(strippedTx);
        return SHA256.HashData(hash1);
    }

    /// <summary>
    /// Strip witness data from a segwit transaction to get legacy serialization for txid.
    /// </summary>
    internal static byte[] StripWitness(byte[] rawTx)
    {
        int offset = 4; // skip version
        bool hasWitness = rawTx.Length > 5 && rawTx[offset] == 0x00 && rawTx[offset + 1] == 0x01;
        if (!hasWitness)
        {
            return rawTx;
        }

        using var result = new MemoryStream();
        result.Write(rawTx, 0, 4); // version

        offset += 2; // skip marker + flag

        // Parse inputs
        var (inputCount, inputCountLen) = ReadVarInt(rawTx, offset);
        int inputCountStart = offset;
        offset += inputCountLen;

        for (ulong j = 0; j < inputCount; j++)
        {
            offset += 36; // txid + vout
            var (scriptLen, scriptLenLen) = ReadVarInt(rawTx, offset);
            offset += scriptLenLen + (int)scriptLen + 4; // script + sequence
        }

        int afterInputs = offset;

        // Parse outputs
        var (outputCount, outputCountLen) = ReadVarInt(rawTx, offset);
        offset += outputCountLen;
        for (ulong j = 0; j < outputCount; j++)
        {
            offset += 8; // value
            var (scriptLen, scriptLenLen) = ReadVarInt(rawTx, offset);
            offset += scriptLenLen + (int)scriptLen;
        }

        int afterOutputs = offset;

        // result = version + inputs + outputs + locktime
        result.Write(rawTx, inputCountStart, afterOutputs - inputCountStart);
        result.Write(rawTx, rawTx.Length - 4, 4); // locktime

        return result.ToArray();
    }

    /// <summary>
    /// Parse a tx output (script + value) at a given vout index.
    /// </summary>
    internal static (byte[] Script, ulong Value) ParseTxOutput(byte[] rawTx, uint vout)
    {
        int offset = 4; // skip version
        if (rawTx.Length > 5 && rawTx[offset] == 0x00 && rawTx[offset + 1] == 0x01)
        {
            offset += 2; // skip segwit marker + flag
        }

        // Skip inputs
        var (inputCount, inputCountLen) = ReadVarInt(rawTx, offset);
        offset += inputCountLen;
        for (ulong j = 0; j < inputCount; j++)
        {
            offset += 36;
            var (scriptLen, scriptLenLen) = ReadVarInt(rawTx, offset);
            offset += scriptLenLen + (int)scriptLen + 4;
        }

        // Parse outputs
        var (outputCount, outputCountLen) = ReadVarInt(rawTx, offset);
        offset += outputCountLen;
        if (vout >= outputCount)
        {
            throw new InvalidOperationException($"vout {vout} not found in transaction with {outputCount} outputs");
        }

        for (uint i = 0; i <= vout; i++)
        {
            ulong value = BitConverter.ToUInt64(rawTx, offset);
            offset += 8;
            var (scriptLen, scriptLenLen) = ReadVarInt(rawTx, offset);
            offset += scriptLenLen;
            var script = new byte[scriptLen];
            Array.Copy(rawTx, offset, script, 0, (int)scriptLen);
            offset += (int)scriptLen;

            if (i == vout)
            {
                return (script, value);
            }
        }

        throw new InvalidOperationException($"vout {vout} not found in transaction");
    }

    /// <summary>
    /// Check if a node tx has zero timelock (sequence lower 16 bits == 0).
    /// </summary>
    internal static bool IsZeroTimelockNode(byte[] nodeTx)
    {
        var seq = ClaimService.ParseInputSequence(nodeTx);
        return (seq & 0xFFFF) == 0;
    }

    /// <summary>
    /// Create raw bytes for a connector input: txid(32) + vout(4) + empty scriptSig(1) + sequence(4).
    /// </summary>
    internal static byte[] MakeConnectorInputBytes(byte[] txId, uint vout)
    {
        var input = new byte[32 + 4 + 1 + 4]; // 41 bytes
        // txid already in internal byte order
        Array.Copy(txId, 0, input, 0, 32);
        // vout LE
        BitConverter.TryWriteBytes(input.AsSpan(32), vout);
        // scriptSig length = 0 (already zero)
        // sequence = 0xFFFFFFFF
        BitConverter.TryWriteBytes(input.AsSpan(37), 0xFFFFFFFFu);
        return input;
    }

    /// <summary>
    /// Add an input to a raw Bitcoin transaction, bumping the input count varint and
    /// handling witness data if present.
    /// </summary>
    internal static byte[] AddInputToRawTx(byte[] rawTx, byte[] input)
    {
        int offset = 4; // skip version
        bool hasWitness = rawTx.Length > 5 && rawTx[offset] == 0x00 && rawTx[offset + 1] == 0x01;
        if (hasWitness)
        {
            offset += 2;
        }

        // Read input count
        var (inputCount, inputCountLen) = ReadVarInt(rawTx, offset);
        int inputCountOffset = offset;
        offset += inputCountLen;

        // Find end of all inputs
        for (ulong j = 0; j < inputCount; j++)
        {
            offset += 36; // txid + vout
            var (scriptLen, scriptLenLen) = ReadVarInt(rawTx, offset);
            offset += scriptLenLen + (int)scriptLen + 4; // script + sequence
        }
        int afterInputs = offset;

        using var result = new MemoryStream();

        if (hasWitness)
        {
            result.Write(rawTx, 0, 4); // version
            result.Write([0x00, 0x01]); // marker + flag
        }
        else
        {
            result.Write(rawTx, 0, 4); // version
        }

        // New input count
        var newCountBytes = EncodeVarInt(inputCount + 1);
        result.Write(newCountBytes);
        // Existing inputs (skip old input count bytes)
        result.Write(rawTx, inputCountOffset + inputCountLen, afterInputs - (inputCountOffset + inputCountLen));
        // New connector input
        result.Write(input);

        if (hasWitness)
        {
            // Find end of outputs
            int outOffset = afterInputs;
            var (outputCount, outputCountLen) = ReadVarInt(rawTx, outOffset);
            outOffset += outputCountLen;
            for (ulong j = 0; j < outputCount; j++)
            {
                outOffset += 8;
                var (scriptLen, scriptLenLen) = ReadVarInt(rawTx, outOffset);
                outOffset += scriptLenLen + (int)scriptLen;
            }
            int afterOutputs = outOffset;

            // Outputs
            result.Write(rawTx, afterInputs, afterOutputs - afterInputs);

            // Existing witness data
            for (ulong j = 0; j < inputCount; j++)
            {
                var (witnessCount, witnessCountLen) = ReadVarInt(rawTx, outOffset);
                int witnessStart = outOffset;
                outOffset += witnessCountLen;
                for (ulong k = 0; k < witnessCount; k++)
                {
                    var (itemLen, itemLenLen) = ReadVarInt(rawTx, outOffset);
                    outOffset += itemLenLen + (int)itemLen;
                }
                result.Write(rawTx, witnessStart, outOffset - witnessStart);
            }
            // Empty witness for new connector input
            result.WriteByte(0x00);

            // Locktime
            result.Write(rawTx, rawTx.Length - 4, 4);
        }
        else
        {
            // Rest of tx (outputs + locktime)
            result.Write(rawTx, afterInputs, rawTx.Length - afterInputs);
        }

        return result.ToArray();
    }

    internal static (ulong value, int bytesRead) ReadVarInt(byte[] data, int offset)
    {
        var first = data[offset];
        return first switch
        {
            < 0xFD => (first, 1),
            0xFD => (BitConverter.ToUInt16(data, offset + 1), 3),
            0xFE => (BitConverter.ToUInt32(data, offset + 1), 5),
            _ => (BitConverter.ToUInt64(data, offset + 1), 9),
        };
    }

    internal static byte[] EncodeVarInt(ulong value)
    {
        if (value < 0xFD)
        {
            return [(byte)value];
        }

        if (value <= 0xFFFF)
        {
            var buf = new byte[3];
            buf[0] = 0xFD;
            BitConverter.TryWriteBytes(buf.AsSpan(1), (ushort)value);
            return buf;
        }
        if (value <= 0xFFFFFFFF)
        {
            var buf = new byte[5];
            buf[0] = 0xFE;
            BitConverter.TryWriteBytes(buf.AsSpan(1), (uint)value);
            return buf;
        }
        {
            var buf = new byte[9];
            buf[0] = 0xFF;
            BitConverter.TryWriteBytes(buf.AsSpan(1), value);
            return buf;
        }
    }
}
