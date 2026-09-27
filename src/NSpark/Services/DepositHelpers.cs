using System.Buffers.Binary;
using System.Text;
using Google.Protobuf;
using NSpark.Bitcoin;
using NSpark.Exceptions;
using NSpark.Proto;

namespace NSpark.Services;

/// <summary>An output paying a static deposit address, by display-order txid (lower-case hex) and vout.</summary>
internal sealed record DepositOutpoint
{
    /// <exception cref="SparkConfigurationException">The txid is not 64 hex characters.</exception>
    public DepositOutpoint(string txid, uint vout)
    {
        Txid = NormalizedTxid(txid);
        Vout = vout;
    }

    /// <summary>Lower-case display-order hex.</summary>
    public string Txid { get; }

    public uint Vout { get; }

    /// <summary>The txid bytes in display order: how the operators store and look up deposit UTXOs.</summary>
    public byte[] DisplayOrderTxid => Convert.FromHexString(Txid);

    /// <summary>The txid bytes in internal order: how a transaction input spends the output.</summary>
    public byte[] InternalOrderTxid
    {
        get
        {
            var bytes = DisplayOrderTxid;
            Array.Reverse(bytes);
            return bytes;
        }
    }

    /// <summary>
    /// A display-order txid in the lower-case form the operators print.
    /// </summary>
    /// <exception cref="SparkConfigurationException">The txid is not 64 hex characters.</exception>
    public static string NormalizedTxid(string? txid)
    {
        var normalized = (txid ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
        {
            throw new SparkConfigurationException(
                "deposit.txid", $"The transaction id must be 64 hex characters, got '{txid}'.");
        }

        return normalized;
    }

    public UTXO ToUtxo(Network network) => new()
    {
        Txid = ByteString.CopyFrom(DisplayOrderTxid),
        Vout = Vout,
        Network = network,
    };
}

/// <summary>What a static-deposit statement authorizes (the operators' <c>UtxoSwapRequestType</c>).</summary>
internal enum StaticDepositRequestType : byte
{
    Fixed = 0,
    MaxFee = 1,
    Refund = 2,
}

/// <summary>Static-deposit and deposit-matching helpers.</summary>
internal static class DepositHelpers
{
    /// <summary>
    /// The statement the wallet signs (SHA-256, identity key) to authorize a static-deposit claim
    /// or refund — the operators' <c>createUserStatementLegacy</c> and the reference SDK's
    /// <c>getStaticDepositSigningPayload</c>: "claim_static_deposit", the lower-case network, the
    /// display-order txid, the vout (little-endian u32), the request type (u8), the credit amount
    /// (little-endian u64), then <paramref name="authorization"/> as raw bytes — the SSP's quote
    /// signature for a claim, the refund transaction's 32-byte sighash for a refund.
    /// </summary>
    internal static byte[] StaticDepositStatement(
        DepositOutpoint outpoint,
        SparkNetwork network,
        StaticDepositRequestType requestType,
        ulong creditAmountSats,
        byte[] authorization)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.UTF8.GetBytes("claim_static_deposit"));
        ms.Write(Encoding.UTF8.GetBytes(network.LowerName()));
        ms.Write(Encoding.UTF8.GetBytes(outpoint.Txid));
        Span<byte> vout = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(vout, outpoint.Vout);
        ms.Write(vout);
        ms.WriteByte((byte)requestType);
        Span<byte> credit = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(credit, creditAmountSats);
        ms.Write(credit);
        ms.Write(authorization);
        return ms.ToArray();
    }

    /// <summary>
    /// The unsigned 1-input 1-output transaction spending a static deposit: version 3, final
    /// sequence, locktime 0, in the non-witness serialisation. The operators rebuild exactly that
    /// and compare it byte for byte (<c>validateStaticDepositSingleInputTx</c>), and the reference
    /// SDK sends <c>tx.toBytes()</c>; the signature is attached afterwards by <see cref="AddWitnessToTx"/>.
    /// </summary>
    internal static byte[] ConstructSpendTx(DepositOutpoint outpoint, byte[] destinationScriptPubKey, ulong amountSats)
    {
        var tx = new RawTransaction(
            version: 3,
            inputs: [new RawTransaction.Input(outpoint.InternalOrderTxid, outpoint.Vout)],
            outputs: [new RawTransaction.Output(amountSats, destinationScriptPubKey)],
            locktime: 0,
            hasWitnessSerialization: false);
        return tx.Serialize(includeWitness: false);
    }

    /// <summary>Attach a single-item witness (a schnorr signature) to the first input of a transaction.</summary>
    internal static byte[] AddWitnessToTx(byte[] rawTx, byte[] witness)
    {
        var tx = RawTransaction.Parse(rawTx, "spend tx");
        if (tx.Inputs.Count == 0)
        {
            throw RawTransaction.Malformed("spend tx has no inputs");
        }

        tx.HasWitnessSerialization = true;
        tx.Inputs[0].Witness = [witness];
        return tx.Serialize(includeWitness: true);
    }

    /// <summary>
    /// The output of <paramref name="tx"/> that pays one of <paramref name="candidateAddresses"/>:
    /// <paramref name="requestedVout"/> when given (it must pay one of them), else the first that
    /// does. So a one-time deposit claim is built for the leaf that actually received the funds.
    /// </summary>
    /// <exception cref="SparkConfigurationException">No output pays one of the addresses.</exception>
    internal static (uint Vout, string Address) MatchDepositOutput(
        RawTransaction tx,
        IEnumerable<string> candidateAddresses,
        uint? requestedVout,
        SparkNetwork network)
    {
        var scripts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var address in candidateAddresses)
        {
            try
            {
                scripts[Convert.ToHexString(CoopExitValidator.ScriptPubKeyFor(address, network).ToBytes())] = address;
            }
            catch (SparkConfigurationException)
            {
                // Not an address on this network: it cannot be paid by this transaction.
            }
        }

        if (scripts.Count == 0)
        {
            throw new SparkConfigurationException(
                "deposit.claim", "No unused deposit address found. Generate one first with GetDepositAddressAsync().");
        }

        if (requestedVout is { } vout)
        {
            var output = tx.OutputAt(vout);
            if (!scripts.TryGetValue(Convert.ToHexString(output.ScriptPubKey), out var paid))
            {
                throw new SparkConfigurationException(
                    "deposit.claim", $"Output {vout} of {tx.TxidHex} does not pay one of this wallet's deposit addresses.");
            }

            return (vout, paid);
        }

        for (var i = 0; i < tx.Outputs.Count; i++)
        {
            if (scripts.TryGetValue(Convert.ToHexString(tx.Outputs[i].ScriptPubKey), out var paid))
            {
                return ((uint)i, paid);
            }
        }

        throw new SparkConfigurationException(
            "deposit.claim", $"Transaction {tx.TxidHex} does not pay any of this wallet's unused deposit addresses.");
    }

    /// <summary>The first output of <paramref name="tx"/> paying one of <paramref name="addresses"/>.</summary>
    /// <exception cref="SparkConfigurationException">No output pays one of them.</exception>
    internal static uint StaticDepositVout(RawTransaction tx, IEnumerable<string> addresses, SparkNetwork network)
    {
        var scripts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var address in addresses)
        {
            try
            {
                scripts.Add(Convert.ToHexString(CoopExitValidator.ScriptPubKeyFor(address, network).ToBytes()));
            }
            catch (SparkConfigurationException)
            {
                // Not an address on this network.
            }
        }

        for (var i = 0; i < tx.Outputs.Count; i++)
        {
            if (scripts.Contains(Convert.ToHexString(tx.Outputs[i].ScriptPubKey)))
            {
                return (uint)i;
            }
        }

        throw new SparkConfigurationException(
            "deposit.static", $"Transaction {tx.TxidHex} does not pay this wallet's static deposit address.");
    }
}
