using NBitcoin;
using NSpark.Exceptions;

namespace NSpark.Services;

/// <summary>
/// Client-side checks on the SSP's cooperative-exit response, run before any refund transaction
/// is signed or any key tweak is prepared. Mirrors the reference SDK's
/// <c>validateCoopExitPayoutTransaction</c> and <c>validateConnectorTxBindsToCoopExitTxid</c>.
/// </summary>
/// <remarks>
/// Without these checks the wallet hands its leaves to the SSP on the SSP's word: the operators
/// release the transfer once the exit txid confirms, but only the client knows what that
/// transaction was supposed to pay.
/// </remarks>
internal static class CoopExitValidator
{
    private const string Operation = "withdrawal.verify";

    /// <summary>The parts of a verified SSP response the exit flow needs.</summary>
    /// <param name="ExitTxidInternal">Exit txid in internal (little-endian) byte order, as the coordinator expects it.</param>
    /// <param name="ExitTxidHex">Exit txid in display (big-endian hex) order.</param>
    /// <param name="ConnectorTx">The parsed connector transaction.</param>
    /// <param name="ConnectorTxidInternal">Connector txid in internal byte order, used as the refund inputs' prevout.</param>
    /// <param name="PayoutVout">Index of the exit output that satisfied the payout check.</param>
    /// <param name="PayoutSats">Value of that output.</param>
    internal sealed record ValidatedExit(
        byte[] ExitTxidInternal,
        string ExitTxidHex,
        Transaction ConnectorTx,
        byte[] ConnectorTxidInternal,
        int PayoutVout,
        ulong PayoutSats);

    /// <summary>
    /// The fee the withdrawal is allowed to pay: the SSP's quote, bounded by the caller's cap.
    /// The payout is later required to be at least <c>amountSats - cap</c>.
    /// </summary>
    internal static long ResolveFeeCap(long quotedFeeSats, long? maxFeeSats, long amountSats)
    {
        if (amountSats <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amountSats), amountSats, "Withdrawal amount must be positive.");
        }
        if (quotedFeeSats < 0)
        {
            throw new SparkUntrustedResponseException(Operation, $"The SSP quoted a negative withdrawal fee ({quotedFeeSats} sats).");
        }
        if (maxFeeSats is { } max)
        {
            if (max < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxFeeSats), max, "maxFeeSats must not be negative.");
            }
            if (quotedFeeSats > max)
            {
                throw new FeeExceedsLimitException("withdrawal.fee", quotedFeeSats, max);
            }
        }

        var cap = maxFeeSats ?? quotedFeeSats;
        if (cap >= amountSats)
        {
            throw new FeeExceedsLimitException("withdrawal.fee", cap, amountSats - 1);
        }
        return cap;
    }

    internal static Network ToBitcoinNetwork(SparkNetwork network) => network switch
    {
        SparkNetwork.Mainnet => Network.Main,
        SparkNetwork.Regtest => Network.RegTest,
        _ => throw new ArgumentOutOfRangeException(nameof(network)),
    };

    /// <summary>
    /// The scriptPubKey <paramref name="address"/> pays to, enforcing the wallet's network
    /// (P2PKH, P2SH, P2WPKH, P2WSH, P2TR).
    /// </summary>
    /// <exception cref="SparkConfigurationException">The address is malformed or for another network.</exception>
    internal static Script ScriptPubKeyFor(string address, SparkNetwork network)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        try
        {
            return BitcoinAddress.Create(address.Trim(), ToBitcoinNetwork(network)).ScriptPubKey;
        }
        catch (FormatException ex)
        {
            throw new SparkConfigurationException(
                "withdrawal.address", $"'{address}' is not a valid Bitcoin address for {network}: {ex.Message}", ex);
        }
    }

    /// <summary>Transaction id in internal (little-endian) byte order, the form used in input prevouts.</summary>
    internal static byte[] TxidInternal(Transaction tx) => tx.GetHash().ToBytes();

    /// <summary>Whether <paramref name="claimed"/> names <paramref name="internalTxid"/> in either byte order, like the operators accept.</summary>
    internal static bool TxidMatches(ReadOnlySpan<byte> internalTxid, ReadOnlySpan<byte> claimed)
    {
        if (internalTxid.Length != 32 || claimed.Length != 32)
        {
            return false;
        }
        if (internalTxid.SequenceEqual(claimed))
        {
            return true;
        }

        Span<byte> reversed = stackalloc byte[32];
        claimed.CopyTo(reversed);
        reversed.Reverse();
        return internalTxid.SequenceEqual(reversed);
    }

    /// <summary>
    /// Validate the SSP's exit and connector transactions.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description>The raw exit transaction must hash to <paramref name="coopExitTxidHex"/> (either byte order is accepted, like the operators do).</description></item>
    ///   <item><description>It must contain an output paying <paramref name="payoutAddress"/> at least <paramref name="minimumPayoutSats"/>.</description></item>
    ///   <item><description>The connector transaction's first input must spend that exit transaction, and it must carry one connector output per leaf plus the SSP's own output.</description></item>
    /// </list>
    /// </remarks>
    internal static ValidatedExit Validate(
        string? rawCoopExitTransactionHex,
        string? rawConnectorTransactionHex,
        string? coopExitTxidHex,
        string payoutAddress,
        long minimumPayoutSats,
        int leafCount,
        SparkNetwork network)
    {
        if (minimumPayoutSats <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumPayoutSats), minimumPayoutSats, "Minimum payout must be positive.");
        }
        if (leafCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(leafCount), leafCount, "At least one leaf is required.");
        }

        var expectedScript = ScriptPubKeyFor(payoutAddress, network);

        var exitTx = ParseTransaction(rawCoopExitTransactionHex, "raw_coop_exit_transaction", network);
        var exitTxid = TxidInternal(exitTx);

        var claimedTxid = TryDecodeHex(coopExitTxidHex);
        if (claimedTxid is null || !TxidMatches(exitTxid, claimedTxid))
        {
            throw new SparkUntrustedResponseException(
                Operation,
                $"The SSP coop exit response is inconsistent: coop_exit_txid '{coopExitTxidHex}' does not match raw_coop_exit_transaction ({exitTx.GetHash()}).");
        }

        var payoutVout = -1;
        ulong payoutSats = 0;
        for (int i = 0; i < exitTx.Outputs.Count; i++)
        {
            var output = exitTx.Outputs[i];
            if (output.ScriptPubKey == expectedScript && output.Value.Satoshi >= minimumPayoutSats)
            {
                payoutVout = i;
                payoutSats = (ulong)output.Value.Satoshi;
                break;
            }
        }
        if (payoutVout < 0)
        {
            throw new SparkUntrustedResponseException(
                Operation,
                $"The SSP cooperative exit transaction does not pay {payoutAddress} at least {minimumPayoutSats} sats.");
        }

        var connectorTx = ParseTransaction(rawConnectorTransactionHex, "raw_connector_transaction", network);
        if (connectorTx.Inputs.Count == 0)
        {
            throw new SparkUntrustedResponseException(Operation, "The SSP coop exit response is malformed: the connector transaction has no inputs.");
        }
        if (!TxidMatches(exitTxid, connectorTx.Inputs[0].PrevOut.Hash.ToBytes()))
        {
            throw new SparkUntrustedResponseException(
                Operation, "The SSP coop exit response is inconsistent: the connector transaction does not spend the coop exit transaction.");
        }
        if (connectorTx.Outputs.Count != leafCount + 1)
        {
            throw new SparkUntrustedResponseException(
                Operation,
                $"The SSP coop exit response is malformed: the connector transaction has {connectorTx.Outputs.Count} outputs for {leafCount} leaves.");
        }

        return new ValidatedExit(
            exitTxid,
            exitTx.GetHash().ToString(),
            connectorTx,
            TxidInternal(connectorTx),
            payoutVout,
            payoutSats);
    }

    private static byte[]? TryDecodeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }
        try
        {
            return Convert.FromHexString(hex.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Transaction ParseTransaction(string? hex, string field, SparkNetwork network)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            throw new SparkUntrustedResponseException(Operation, $"The SSP coop exit response is missing {field}.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex.Trim());
        }
        catch (FormatException ex)
        {
            throw new SparkUntrustedResponseException(Operation, $"The SSP coop exit response: {field} is not valid hex.", ex);
        }

        try
        {
            // Nothing verified here depends on witness data (txid, inputs, outputs), and NBitcoin
            // refuses a witness-serialised transaction whose witness stacks are all empty, which
            // an unsigned connector transaction may well be. Parse the witness-stripped form.
            var stripped = WithdrawalService.StripWitness(bytes);
            return Transaction.Load(stripped, ToBitcoinNetwork(network));
        }
        catch (Exception ex) when (ex is FormatException or EndOfStreamException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new SparkUntrustedResponseException(Operation, $"The SSP coop exit response: {field} is not a parseable transaction.", ex);
        }
    }
}
