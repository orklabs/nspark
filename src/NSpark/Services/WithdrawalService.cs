using Google.Protobuf;
using NSpark.Bitcoin;
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
    /// Get a fee estimate for an on-chain withdrawal (cooperative exit). Each fee is read in the
    /// unit the SSP reports it in; any other unit is refused.
    /// </summary>
    public static async Task<FeeQuote> GetFeeQuoteAsync(
        this SparkWallet wallet,
        string[] leafIds,
        string onChainAddress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var variables = new Dictionary<string, object>
        {
            ["leaf_external_ids"] = leafIds,
            ["withdrawal_address"] = onChainAddress,
        };

        var response = await wallet.SspClient.ExecuteAsync<CoopExitFeeEstimateResponse>(
            Mutations.CoopExitFeeEstimate, variables, ct).ConfigureAwait(false);

        var fast = response.CoopExitFeeEstimates?.SpeedFast
            ?? throw new SparkUntrustedResponseException("withdrawal.fee", "The SSP returned no fast exit estimate.");
        var userFee = CurrencyAmountExtensions.ToFeeSats(
            fast.UserFee?.OriginalValue ?? -1, fast.UserFee?.OriginalUnit, "cooperative exit user fee");
        var l1Fee = CurrencyAmountExtensions.ToFeeSats(
            fast.L1BroadcastFee?.OriginalValue ?? -1, fast.L1BroadcastFee?.OriginalUnit, "cooperative exit broadcast fee");
        long totalFeeSats;
        try
        {
            totalFeeSats = checked(userFee + l1Fee);
        }
        catch (OverflowException ex)
        {
            throw new SparkUntrustedResponseException("withdrawal.fee", "The SSP's cooperative exit fees overflow.", ex);
        }

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
    /// leaves the wallet. To send everything use <see cref="WithdrawAllAsync"/>.
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
    /// <c>CoopExitService</c> does.
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

        // Fail fast on a malformed or wrong-network destination, before any leaf is moved.
        _ = CoopExitValidator.ScriptPubKeyFor(onChainAddress, wallet.Options.Network);

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

        var exit = await PerformCooperativeExitAsync(
            wallet, selectedLeaves, amountSats, feeCap, onChainAddress, ct).ConfigureAwait(false);
        return exit.Txid;
    }

    /// <summary>
    /// Everything <see cref="WithdrawAllAsync"/> would do, without doing it: claims pending
    /// inbound transfers, renews renewable leaves, and quotes the SSP fee for every spendable
    /// leaf. Use it to show the user what will move, what it costs, and what stays behind
    /// (<see cref="WithdrawAllQuote.FrozenSats"/>, <see cref="WithdrawAllQuote.UnrenewedSats"/>).
    /// </summary>
    public static async Task<WithdrawAllQuote> QuoteWithdrawAllAsync(
        this SparkWallet wallet,
        string onChainAddress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var plan = await DrainPlanAsync(wallet, onChainAddress, ct).ConfigureAwait(false);
        return new WithdrawAllQuote(
            SpendableSats: plan.SpendableSats,
            QuotedFeeSats: plan.QuotedFeeSats,
            FrozenSats: plan.Balance.Frozen,
            UnrenewedSats: plan.UnrenewedSats,
            LockedSats: plan.Balance.Locked,
            IncomingSats: plan.Balance.Incoming,
            LeafCount: plan.Leaves.Count);
    }

    /// <summary>
    /// Send every spendable sat to <paramref name="onChainAddress"/> in one cooperative exit.
    /// </summary>
    /// <remarks>
    /// Pending inbound transfers are claimed first and renewable leaves renewed, then every
    /// spendable leaf is exited; the SSP's fee comes out of that amount. Frozen leaves (refund
    /// timelock below 100) cannot be included and are reported in the result as
    /// <see cref="WithdrawAllResult.FrozenSats"/>; leaves whose renewal failed as
    /// <see cref="WithdrawAllResult.UnrenewedSats"/>, sats locked by in-flight operations as
    /// <see cref="WithdrawAllResult.LockedSats"/> and inbound sats that could not be claimed as
    /// <see cref="WithdrawAllResult.UnclaimedSats"/>. The same response verification and fee bound
    /// as <see cref="WithdrawAsync"/> apply.
    /// </remarks>
    /// <param name="wallet">The Spark wallet.</param>
    /// <param name="onChainAddress">Destination Bitcoin address on the wallet's network.</param>
    /// <param name="maxFeeSats">Highest fee the caller accepts; <c>null</c> uses the SSP's own quote.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SparkWithdrawalException">Nothing is spendable.</exception>
    /// <exception cref="FeeExceedsLimitException">The fee would consume the whole balance or exceed the cap.</exception>
    public static async Task<WithdrawAllResult> WithdrawAllAsync(
        this SparkWallet wallet,
        string onChainAddress,
        long? maxFeeSats = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var plan = await DrainPlanAsync(wallet, onChainAddress, ct).ConfigureAwait(false);
        if (plan.SpendableSats <= 0)
        {
            throw new SparkWithdrawalException(Operation, "Nothing is spendable: need at least 1 sat, have 0.");
        }

        var feeCap = CoopExitValidator.ResolveFeeCap(plan.QuotedFeeSats, maxFeeSats, plan.SpendableSats);
        var exit = await PerformCooperativeExitAsync(
            wallet, plan.Leaves, plan.SpendableSats, feeCap, onChainAddress, ct).ConfigureAwait(false);
        return new WithdrawAllResult(
            Txid: exit.Txid,
            SentSats: plan.SpendableSats,
            PayoutSats: exit.PayoutSats,
            FrozenSats: plan.Balance.Frozen,
            UnrenewedSats: plan.UnrenewedSats,
            LockedSats: plan.Balance.Locked,
            UnclaimedSats: plan.Balance.Incoming);
    }

    /// <param name="Leaves">Every spendable leaf.</param>
    /// <param name="Balance">The balance after the claim and renewal pass.</param>
    /// <param name="SpendableSats">Sum of <paramref name="Leaves"/>.</param>
    /// <param name="QuotedFeeSats">The SSP's quote for exiting them.</param>
    private sealed record DrainPlan(IReadOnlyList<SparkLeaf> Leaves, SatsBalance Balance, long SpendableSats, long QuotedFeeSats)
    {
        /// <summary>
        /// Available sats that are not spendable after the renewal pass: leaves at 100…199 the
        /// operators did not renew.
        /// </summary>
        public long UnrenewedSats => Math.Max(0, Balance.Available - SpendableSats);
    }

    /// <summary>
    /// Shared prelude of <see cref="QuoteWithdrawAllAsync"/> and <see cref="WithdrawAllAsync"/>:
    /// validate the destination, claim what is pending, renew what the coordinator will renew,
    /// and quote the fee for the rest.
    /// </summary>
    private static async Task<DrainPlan> DrainPlanAsync(SparkWallet wallet, string onChainAddress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(onChainAddress);
        _ = CoopExitValidator.ScriptPubKeyFor(onChainAddress, wallet.Options.Network);
        try
        {
            _ = await wallet.ClaimPendingTransfersAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort: unclaimed sats are reported as such.
        }

        var leaves = await wallet.GetSpendableLeavesAsync(ct).ConfigureAwait(false);
        var balance = (await wallet.GetBalanceAsync(ct).ConfigureAwait(false)).SatsBalance;
        var spendable = leaves.Sum(l => l.ValueSats);
        long quotedFee = 0;
        if (spendable > 0)
        {
            quotedFee = (await wallet.GetFeeQuoteAsync(leaves.Select(l => l.Id).ToArray(), onChainAddress, ct).ConfigureAwait(false)).FeeSats;
        }

        return new DrainPlan(leaves, balance, spendable, quotedFee);
    }

    /// <summary>A completed cooperative exit.</summary>
    private sealed record CooperativeExit(string Txid, long PayoutSats);

    /// <summary>
    /// The cooperative exit proper: request the exit from the SSP, verify what it built, sign
    /// the connector refunds, hand the leaves over in one transfer package, complete via the
    /// SSP. <paramref name="selectedLeaves"/> must sum to <paramref name="amountSats"/>; the
    /// payout must be at least <c>amountSats - feeCap</c>.
    /// </summary>
    private static async Task<CooperativeExit> PerformCooperativeExitAsync(
        SparkWallet wallet,
        IReadOnlyList<SparkLeaf> selectedLeaves,
        long amountSats,
        long feeCap,
        string onChainAddress,
        CancellationToken ct)
    {
        var options = wallet.Options;
        var coordinatorClient = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(options.Network);
        var leafIds = selectedLeaves.Select(l => l.Id).ToArray();
        var minimumPayoutSats = amountSats - feeCap;
        var receiverPubKey = options.RequireSspIdentityPublicKey();

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
        var request = sspResponse.RequestCoopExit?.Request
            ?? throw new SparkUntrustedResponseException(Operation, "The SSP returned no cooperative exit request.");

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
        var connectorTx = RawTransaction.Parse(connectorTxBytes, "connector tx");

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
        for (var i = 0; i < selectedLeaves.Count; i++)
        {
            var leaf = selectedLeaves[i];
            var verifyingKey = leaf.Node.VerifyingPublicKey.ToByteArray();
            var refunds = BuildConnectorRefunds(
                leaf.Node,
                receiverPubKey,
                connectorTx,
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
        FrostSigningHelper.ValidateThreshold(options.EffectiveSigningThreshold, soTargets.Count);
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

        var packageHash = SparkTaggedHash.Create("spark", "transfer", "signing payload")
            .AddBytes(ClaimService.TransferIdBytes(transferId))
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

        return new CooperativeExit(validated.ExitTxidHex, TransferMapping.ReportedSats(validated.PayoutSats));
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
        RawTransaction connectorTx,
        uint connectorVout,
        string networkStr)
    {
        var (cpfpSequence, directSequence) = TimelockHelper.ComputeNextSequences(
            node.RefundTx.ToByteArray(), Operation, node.Id);
        var directNodeTx = TimelockHelper.DirectNodeTxForRefund(node);

        // The SSP validates all three refund outputs on coop-exit and rejects with
        // "expected value X on output 0" if the standard fee isn't deducted.
        var trio = TimelockHelper.LeafRefundTrio(node, receiverPubKey, networkStr, cpfpSequence, directSequence);

        var connectorOutput = connectorTx.OutputAt(connectorVout);
        var connectorTxid = connectorTx.Txid;
        var nodeOutput = RawTransaction.Parse(node.NodeTx.Span, "node tx").OutputAt(0);

        ConnectorRefund WithConnector(byte[] refundTx, RawTransaction.Output spending)
        {
            var tx = AddInputToRawTx(refundTx, new RawTransaction.Input(connectorTxid, connectorVout));
            var sighash = SparkTxBuilder.ComputeMultiInputSighash(
                tx: tx,
                inputIndex: 0,
                prevOutScripts: [spending.ScriptPubKey, connectorOutput.ScriptPubKey],
                prevOutValues: [spending.Value, connectorOutput.Value]);
            return new ConnectorRefund(tx, sighash);
        }

        var cpfp = WithConnector(trio.CpfpRefund.Tx, nodeOutput);
        ConnectorRefund? direct = null;
        if (trio.DirectRefund is { } directRefund && directNodeTx is not null)
        {
            direct = WithConnector(directRefund.Tx, RawTransaction.Parse(directNodeTx, "direct node tx").OutputAt(0));
        }
        var directFromCpfp = WithConnector(trio.DirectFromCpfpRefund.Tx, nodeOutput);
        return new ConnectorRefunds(cpfp, direct, directFromCpfp);
    }

    // ── Raw tx helpers (bounds-checked, see RawTransaction) ──

    /// <summary>Transaction id in internal byte order (the form used in input prevouts).</summary>
    internal static byte[] ComputeTxId(byte[] rawTx) => RawTransaction.Parse(rawTx).Txid;

    /// <summary>The transaction without its witness data: the serialisation its txid commits to.</summary>
    internal static byte[] StripWitness(byte[] rawTx) => RawTransaction.Parse(rawTx).Serialize(includeWitness: false);

    /// <summary>Parse a tx output (script + value) at a given vout.</summary>
    internal static (byte[] Script, ulong Value) ParseTxOutput(byte[] rawTx, uint vout)
    {
        var output = RawTransaction.Parse(rawTx).OutputAt(vout);
        return (output.ScriptPubKey, output.Value);
    }

    /// <summary>
    /// Append an input to a raw transaction, preserving its serialisation format. A witness
    /// transaction gets an empty witness stack for the new input.
    /// </summary>
    internal static byte[] AddInputToRawTx(byte[] rawTx, RawTransaction.Input input)
    {
        var tx = RawTransaction.Parse(rawTx, "refund tx");
        tx.Inputs.Add(input);
        return tx.Serialize(includeWitness: true);
    }
}
