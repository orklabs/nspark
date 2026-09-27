using System.Buffers.Binary;
using System.Security.Cryptography;
using NSpark.Exceptions;

namespace NSpark.Bitcoin;

/// <summary>
/// A parsed Bitcoin transaction. Parsing is fully bounds-checked and never throws anything but
/// <see cref="SparkUntrustedResponseException"/> on malformed input, so transaction bytes from an
/// operator, the SSP or a block explorer cannot crash the host with an index or overflow error.
/// </summary>
/// <remarks>
/// Replaces the ad-hoc offset arithmetic the SDK used on transaction bytes. Unlike NBitcoin's
/// parser it accepts a witness-serialised transaction whose witness stacks are all empty, which
/// unsigned Spark transactions routinely are.
/// </remarks>
internal sealed class RawTransaction
{
    /// <summary>
    /// Sanity cap so a hostile response cannot make the parser allocate absurd amounts before the
    /// byte-level bounds checks fire.
    /// </summary>
    private const ulong MaxItems = 100_000;

    /// <summary>One transaction input.</summary>
    internal sealed class Input
    {
        public Input(byte[] previousTxid, uint previousIndex, byte[]? scriptSig = null, uint sequence = 0xFFFFFFFF, List<byte[]>? witness = null)
        {
            PreviousTxid = previousTxid;
            PreviousIndex = previousIndex;
            ScriptSig = scriptSig ?? [];
            Sequence = sequence;
            Witness = witness ?? [];
        }

        /// <summary>Previous output's txid in internal (little-endian) byte order.</summary>
        public byte[] PreviousTxid { get; set; }

        public uint PreviousIndex { get; set; }

        public byte[] ScriptSig { get; set; }

        public uint Sequence { get; set; }

        public List<byte[]> Witness { get; set; }
    }

    /// <summary>One transaction output.</summary>
    internal sealed record Output(ulong Value, byte[] ScriptPubKey);

    public RawTransaction(uint version, List<Input> inputs, List<Output> outputs, uint locktime, bool hasWitnessSerialization)
    {
        Version = version;
        Inputs = inputs;
        Outputs = outputs;
        Locktime = locktime;
        HasWitnessSerialization = hasWitnessSerialization;
    }

    public uint Version { get; set; }

    public List<Input> Inputs { get; }

    public List<Output> Outputs { get; }

    public uint Locktime { get; set; }

    /// <summary>
    /// <c>true</c> when the bytes carried the segwit marker and flag. Preserved so that
    /// re-serialisation reproduces the input format.
    /// </summary>
    public bool HasWitnessSerialization { get; set; }

    /// <summary>Transaction id in internal (little-endian) byte order — the form used in input prevouts.</summary>
    public byte[] Txid => SHA256.HashData(SHA256.HashData(Serialize(includeWitness: false)));

    /// <summary>Transaction id as the conventional display hex (big-endian).</summary>
    public string TxidHex
    {
        get
        {
            var txid = Txid;
            Array.Reverse(txid);
            return Convert.ToHexString(txid).ToLowerInvariant();
        }
    }

    /// <summary>nSequence of the first input. Spark encodes leaf timelocks here.</summary>
    public uint FirstInputSequence => Inputs.Count > 0
        ? Inputs[0].Sequence
        : throw Malformed("transaction has no inputs");

    /// <summary>Parse transaction bytes; throws <see cref="SparkUntrustedResponseException"/> when they are not one.</summary>
    public static RawTransaction Parse(ReadOnlySpan<byte> data, string context = "transaction")
    {
        var reader = new ByteReader(data, context);
        var version = reader.ReadUInt32LE();

        var witnessSerialization = false;
        var inputCount = reader.ReadVarInt();
        if (inputCount == 0 && reader.Remaining > 0)
        {
            var flag = reader.ReadByte();
            if (flag != 0x01)
            {
                throw Malformed($"{context}: unknown segwit flag {flag}");
            }

            witnessSerialization = true;
            inputCount = reader.ReadVarInt();
        }

        if (inputCount > MaxItems)
        {
            throw Malformed($"{context}: implausible input count {inputCount}");
        }

        var inputs = new List<Input>((int)inputCount);
        for (ulong i = 0; i < inputCount; i++)
        {
            var txid = reader.ReadBytes(32);
            var index = reader.ReadUInt32LE();
            var script = reader.ReadVarBytes();
            var sequence = reader.ReadUInt32LE();
            inputs.Add(new Input(txid, index, script, sequence));
        }

        var outputCount = reader.ReadVarInt();
        if (outputCount > MaxItems)
        {
            throw Malformed($"{context}: implausible output count {outputCount}");
        }

        var outputs = new List<Output>((int)outputCount);
        for (ulong i = 0; i < outputCount; i++)
        {
            var value = reader.ReadUInt64LE();
            var script = reader.ReadVarBytes();
            outputs.Add(new Output(value, script));
        }

        if (witnessSerialization)
        {
            foreach (var input in inputs)
            {
                var itemCount = reader.ReadVarInt();
                if (itemCount > MaxItems)
                {
                    throw Malformed($"{context}: implausible witness item count {itemCount}");
                }

                var items = new List<byte[]>((int)itemCount);
                for (ulong j = 0; j < itemCount; j++)
                {
                    items.Add(reader.ReadVarBytes());
                }

                input.Witness = items;
            }
        }

        var locktime = reader.ReadUInt32LE();
        reader.ExpectEnd();
        return new RawTransaction(version, inputs, outputs, locktime, witnessSerialization);
    }

    /// <summary>Parse lower- or upper-case transaction hex.</summary>
    public static RawTransaction ParseHex(string? hex, string context = "transaction")
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            throw Malformed($"{context}: empty transaction");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex.Trim());
        }
        catch (FormatException ex)
        {
            throw new SparkUntrustedResponseException("transaction.parse", $"{context}: not valid hex.", ex);
        }

        return Parse(bytes, context);
    }

    /// <summary>
    /// Serialise. <paramref name="includeWitness"/> is only honoured when the transaction uses
    /// witness serialisation; the non-witness form is what the txid commits to.
    /// </summary>
    public byte[] Serialize(bool includeWitness)
    {
        using var ms = new MemoryStream();
        WriteUInt32(ms, Version);
        var withWitness = includeWitness && HasWitnessSerialization;
        if (withWitness)
        {
            ms.WriteByte(0x00);
            ms.WriteByte(0x01);
        }

        WriteVarInt(ms, (ulong)Inputs.Count);
        foreach (var input in Inputs)
        {
            ms.Write(input.PreviousTxid);
            WriteUInt32(ms, input.PreviousIndex);
            WriteVarInt(ms, (ulong)input.ScriptSig.Length);
            ms.Write(input.ScriptSig);
            WriteUInt32(ms, input.Sequence);
        }

        WriteVarInt(ms, (ulong)Outputs.Count);
        Span<byte> value = stackalloc byte[8];
        foreach (var output in Outputs)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(value, output.Value);
            ms.Write(value);
            WriteVarInt(ms, (ulong)output.ScriptPubKey.Length);
            ms.Write(output.ScriptPubKey);
        }

        if (withWitness)
        {
            foreach (var input in Inputs)
            {
                WriteVarInt(ms, (ulong)input.Witness.Count);
                foreach (var item in input.Witness)
                {
                    WriteVarInt(ms, (ulong)item.Length);
                    ms.Write(item);
                }
            }
        }

        WriteUInt32(ms, Locktime);
        return ms.ToArray();
    }

    /// <summary>The output at <paramref name="vout"/>.</summary>
    public Output OutputAt(uint vout) => vout < (uint)Outputs.Count
        ? Outputs[(int)vout]
        : throw Malformed($"vout {vout} out of range: transaction has {Outputs.Count} output(s)");

    /// <summary>
    /// Whether two txids refer to the same transaction, accepting either byte order. Mirrors the
    /// operators' accept-both tolerance for ids that arrive as display hex.
    /// </summary>
    public static bool TxidMatches(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != 32 || b.Length != 32)
        {
            return false;
        }

        if (a.SequenceEqual(b))
        {
            return true;
        }

        Span<byte> reversed = stackalloc byte[32];
        b.CopyTo(reversed);
        reversed.Reverse();
        return a.SequenceEqual(reversed);
    }

    /// <summary>Bitcoin CompactSize encoding.</summary>
    public static byte[] EncodeVarInt(ulong value)
    {
        using var ms = new MemoryStream(9);
        WriteVarInt(ms, value);
        return ms.ToArray();
    }

    internal static SparkUntrustedResponseException Malformed(string message) =>
        new("transaction.parse", $"Malformed transaction: {message}.");

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteVarInt(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[9];
        switch (value)
        {
            case < 0xFD:
                stream.WriteByte((byte)value);
                break;
            case <= 0xFFFF:
                buffer[0] = 0xFD;
                BinaryPrimitives.WriteUInt16LittleEndian(buffer[1..], (ushort)value);
                stream.Write(buffer[..3]);
                break;
            case <= 0xFFFFFFFF:
                buffer[0] = 0xFE;
                BinaryPrimitives.WriteUInt32LittleEndian(buffer[1..], (uint)value);
                stream.Write(buffer[..5]);
                break;
            default:
                buffer[0] = 0xFF;
                BinaryPrimitives.WriteUInt64LittleEndian(buffer[1..], value);
                stream.Write(buffer);
                break;
        }
    }

    /// <summary>Bounds-checked reader over raw bytes.</summary>
    private ref struct ByteReader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private readonly string _context;
        private int _offset;

        public ByteReader(ReadOnlySpan<byte> bytes, string context)
        {
            _bytes = bytes;
            _context = context;
            _offset = 0;
        }

        public readonly int Remaining => _bytes.Length - _offset;

        public byte ReadByte()
        {
            if (Remaining < 1)
            {
                throw Truncated(1);
            }

            return _bytes[_offset++];
        }

        public byte[] ReadBytes(int count)
        {
            if (count < 0 || count > Remaining)
            {
                throw Truncated(count);
            }

            var slice = _bytes.Slice(_offset, count).ToArray();
            _offset += count;
            return slice;
        }

        public ushort ReadUInt16LE() => BinaryPrimitives.ReadUInt16LittleEndian(ReadSpan(2));

        public uint ReadUInt32LE() => BinaryPrimitives.ReadUInt32LittleEndian(ReadSpan(4));

        public ulong ReadUInt64LE() => BinaryPrimitives.ReadUInt64LittleEndian(ReadSpan(8));

        /// <summary>Bitcoin CompactSize integer.</summary>
        public ulong ReadVarInt()
        {
            var first = ReadByte();
            return first switch
            {
                < 0xFD => first,
                0xFD => ReadUInt16LE(),
                0xFE => ReadUInt32LE(),
                _ => ReadUInt64LE(),
            };
        }

        /// <summary>
        /// CompactSize length prefix followed by that many bytes. The length is checked against
        /// the bytes actually remaining before anything is allocated.
        /// </summary>
        public byte[] ReadVarBytes()
        {
            var length = ReadVarInt();
            if (length > (ulong)Remaining)
            {
                throw Truncated(length > int.MaxValue ? int.MaxValue : (int)length);
            }

            return ReadBytes((int)length);
        }

        public readonly void ExpectEnd()
        {
            if (Remaining != 0)
            {
                throw Malformed($"{_context}: {Remaining} trailing byte(s) after the end of the transaction");
            }
        }

        private ReadOnlySpan<byte> ReadSpan(int count)
        {
            if (count > Remaining)
            {
                throw Truncated(count);
            }

            var span = _bytes.Slice(_offset, count);
            _offset += count;
            return span;
        }

        private readonly SparkUntrustedResponseException Truncated(int needed) =>
            Malformed($"{_context}: truncated at byte {_offset}, needed {needed} more byte(s) but only {Remaining} remain");
    }
}
