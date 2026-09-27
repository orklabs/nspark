using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NSpark.Exceptions;
using NSpark.Proto.Token;
using ProtoTransaction = NSpark.Proto.Token.TokenTransaction;
using ProtoTransactionType = NSpark.Proto.Token.TokenTransactionType;

namespace NSpark.Services;

/// <summary>
/// V2 token transaction hashing — ports
/// <c>Sources/SparkSDK/TokenHashing.swift</c> byte-for-byte so .NET-built and
/// Swift-built clients produce identical transaction hashes for the same
/// transaction. Any divergence here will cause signature rejection by the SOs.
/// </summary>
/// <remarks>
/// The hash is computed as <c>SHA256(SHA256(field_1) ‖ SHA256(field_2) ‖ …)</c>
/// over an ordered set of fields. When <c>partialHash</c> is true, fields that
/// the server sets (output id, revocation commitment, withdraw bond + locktime,
/// expiry, creation-entity-pubkey) are omitted, producing the client-side
/// signing hash for the <c>start_transaction</c> request.
/// </remarks>
internal static class TokenHashing
{
    /// <summary>
    /// Hash a V2 <see cref="ProtoTransaction"/>. Pass <c>partialHash=true</c>
    /// for the owner-side signing hash submitted with
    /// <c>start_transaction</c>; pass <c>false</c> for the final hash used in
    /// per-operator signatures during <c>commit_transaction</c>.
    /// </summary>
    public static byte[] HashTokenTransactionV2(ProtoTransaction tx, bool partialHash)
    {
        ArgumentNullException.ThrowIfNull(tx);

        var allHashes = new List<byte[]>();

        // Version (uint32 BE).
        allHashes.Add(SHA256.HashData(UInt32BigEndian(tx.Version)));

        // Transaction type — derived from which oneof input is set.
        uint txType = tx.TokenInputsCase switch
        {
            ProtoTransaction.TokenInputsOneofCase.MintInput => (uint)ProtoTransactionType.Mint,
            ProtoTransaction.TokenInputsOneofCase.TransferInput => (uint)ProtoTransactionType.Transfer,
            ProtoTransaction.TokenInputsOneofCase.CreateInput => (uint)ProtoTransactionType.Create,
            _ => throw new SparkConfigurationException(
                "token.hash",
                "Token transaction must have exactly one input type set."),
        };
        allHashes.Add(SHA256.HashData(UInt32BigEndian(txType)));

        switch (tx.TokenInputsCase)
        {
            case ProtoTransaction.TokenInputsOneofCase.TransferInput:
                HashTransferInput(tx.TransferInput, allHashes);
                break;
            case ProtoTransaction.TokenInputsOneofCase.MintInput:
                HashMintInput(tx.MintInput, allHashes);
                break;
            case ProtoTransaction.TokenInputsOneofCase.CreateInput:
                HashCreateInput(tx.CreateInput, allHashes, partialHash);
                break;
            default:
                throw new SparkConfigurationException("token.hash", "Unknown token input type.");
        }

        // Output count + per-output digests.
        allHashes.Add(SHA256.HashData(UInt32BigEndian((uint)tx.TokenOutputs.Count)));
        foreach (var output in tx.TokenOutputs)
        {
            allHashes.Add(SHA256.HashData(HashTokenOutputPayload(output, partialHash)));
        }

        // Sorted operator identity public keys.
        var sortedOps = tx.SparkOperatorIdentityPublicKeys
            .Select(b => b.ToByteArray())
            .OrderBy(b => b, ByteArrayLexicographicComparer.Instance)
            .ToList();
        allHashes.Add(SHA256.HashData(UInt32BigEndian((uint)sortedOps.Count)));
        foreach (var op in sortedOps)
        {
            allHashes.Add(SHA256.HashData(op));
        }

        // Network.
        allHashes.Add(SHA256.HashData(UInt32BigEndian((uint)tx.Network)));

        // Client-created timestamp in milliseconds, uint64 BE.
        ulong clientMs = 0;
        if (tx.ClientCreatedTimestamp is not null)
        {
            clientMs = ((ulong)tx.ClientCreatedTimestamp.Seconds * 1000UL)
                + ((ulong)tx.ClientCreatedTimestamp.Nanos / 1_000_000UL);
        }
        allHashes.Add(SHA256.HashData(UInt64BigEndian(clientMs)));

        // Expiry seconds (full hash only).
        if (!partialHash)
        {
            ulong expirySecs = tx.ExpiryTime is { Seconds: var s } ? (ulong)s : 0UL;
            allHashes.Add(SHA256.HashData(UInt64BigEndian(expirySecs)));
        }

        // Invoice attachments (V2): each raw invoice string, ordered by the invoice's id (its 16
        // UUID bytes) as the operators and the reference SDK order them.
        var attachments = tx.InvoiceAttachments;
        allHashes.Add(SHA256.HashData(UInt32BigEndian((uint)attachments.Count)));
        var keyed = attachments
            .Select((attachment, index) => (Id: SparkInvoiceId(attachment.SparkInvoice, index), Raw: attachment.SparkInvoice))
            .OrderBy(a => a.Id, ByteArrayLexicographicComparer.Instance)
            .ToList();
        foreach (var attachment in keyed)
        {
            allHashes.Add(SHA256.HashData(Encoding.UTF8.GetBytes(attachment.Raw)));
        }

        // Final SHA-256 over the concatenation.
        return SHA256.HashData(Concat(allHashes));
    }

    /// <summary>
    /// The id of the Spark invoice in invoice attachment <paramref name="index"/>: the 16 UUID
    /// bytes of its <c>SparkInvoiceFields.id</c>. On any network: the hash does not check it, as
    /// the operators' and the reference SDK's do not.
    /// </summary>
    /// <exception cref="SparkConfigurationException">The attachment is not a Spark invoice with a 16-byte id.</exception>
    internal static byte[] SparkInvoiceId(string invoice, int index)
    {
        try
        {
            var (_, payload) = Bech32mHelper.Decode(invoice, limit: 1024);
            var address = NSpark.Proto.SparkAddress.Parser.ParseFrom(payload);
            if (address.SparkInvoiceFields is { Id.Length: 16 } fields)
            {
                return fields.Id.ToByteArray();
            }
        }
        catch (Exception ex) when (ex is SparkConfigurationException or Google.Protobuf.InvalidProtocolBufferException or ArgumentException)
        {
            // Refused below.
        }

        throw new SparkConfigurationException(
            "token.hash", $"Invoice attachment {index} is not a Spark invoice with a 16-byte id.");
    }

    /// <summary>
    /// Hash an operator-specific signable payload used during
    /// <c>commit_transaction</c>: <c>SHA256(SHA256(finalHash) ‖ SHA256(opKey))</c>.
    /// </summary>
    public static byte[] HashOperatorSpecificPayload(byte[] finalTokenTransactionHash, byte[] operatorIdentityPublicKey)
    {
        ArgumentNullException.ThrowIfNull(finalTokenTransactionHash);
        ArgumentNullException.ThrowIfNull(operatorIdentityPublicKey);
        if (finalTokenTransactionHash.Length != 32)
        {
            throw new SparkConfigurationException(
                "token.hash.operator",
                $"Final token transaction hash must be 32 bytes, got {finalTokenTransactionHash.Length}.");
        }
        if (operatorIdentityPublicKey.Length == 0)
        {
            throw new SparkConfigurationException(
                "token.hash.operator",
                "Operator identity public key cannot be empty.");
        }

        var inner1 = SHA256.HashData(finalTokenTransactionHash);
        var inner2 = SHA256.HashData(operatorIdentityPublicKey);
        return SHA256.HashData(Concat(new List<byte[]> { inner1, inner2 }));
    }

    // ───────────────────────────────── input hashing ─────────────────────────────────

    private static void HashTransferInput(TokenTransferInput input, List<byte[]> allHashes)
    {
        if (input.OutputsToSpend.Count == 0)
        {
            throw new SparkConfigurationException("token.hash", "Outputs to spend cannot be empty.");
        }

        allHashes.Add(SHA256.HashData(UInt32BigEndian((uint)input.OutputsToSpend.Count)));
        foreach (var spend in input.OutputsToSpend)
        {
            using var ms = new MemoryStream();
            var prev = spend.PrevTokenTransactionHash;
            if (prev.Length > 0)
            {
                if (prev.Length != 32)
                {
                    throw new SparkConfigurationException(
                        "token.hash",
                        $"Invalid previous transaction hash length: {prev.Length}.");
                }
                ms.Write(prev.Span);
            }
            ms.Write(UInt32BigEndian(spend.PrevTokenTransactionVout));
            allHashes.Add(SHA256.HashData(ms.ToArray()));
        }
    }

    private static void HashMintInput(TokenMintInput input, List<byte[]> allHashes)
    {
        var issuer = input.IssuerPublicKey;
        if (issuer.Length == 0)
        {
            throw new SparkConfigurationException("token.hash", "Issuer public key cannot be empty.");
        }
        allHashes.Add(SHA256.HashData(issuer.ToByteArray()));

        if (input.HasTokenIdentifier)
        {
            allHashes.Add(SHA256.HashData(input.TokenIdentifier.ToByteArray()));
        }
        else
        {
            allHashes.Add(SHA256.HashData(new byte[32]));
        }
    }

    private static void HashCreateInput(TokenCreateInput input, List<byte[]> allHashes, bool partialHash)
    {
        if (input.IssuerPublicKey.Length == 0)
        {
            throw new SparkConfigurationException("token.hash", "Issuer public key cannot be empty.");
        }
        allHashes.Add(SHA256.HashData(input.IssuerPublicKey.ToByteArray()));

        var nameBytes = Encoding.UTF8.GetBytes(input.TokenName);
        if (nameBytes.Length == 0 || nameBytes.Length > 20)
        {
            throw new SparkConfigurationException("token.hash", "Token name must be 1-20 bytes.");
        }
        allHashes.Add(SHA256.HashData(nameBytes));

        var tickerBytes = Encoding.UTF8.GetBytes(input.TokenTicker);
        if (tickerBytes.Length == 0 || tickerBytes.Length > 6)
        {
            throw new SparkConfigurationException("token.hash", "Token ticker must be 1-6 bytes.");
        }
        allHashes.Add(SHA256.HashData(tickerBytes));

        allHashes.Add(SHA256.HashData(UInt32BigEndian(input.Decimals)));

        var maxSupply = input.MaxSupply.ToByteArray();
        if (maxSupply.Length != 16)
        {
            throw new SparkConfigurationException(
                "token.hash",
                $"Max supply must be exactly 16 bytes, got {maxSupply.Length}.");
        }
        allHashes.Add(SHA256.HashData(maxSupply));

        allHashes.Add(SHA256.HashData(new[] { input.IsFreezable ? (byte)1 : (byte)0 }));

        // Creation entity public key — only included for final hash.
        if (!partialHash && input.HasCreationEntityPublicKey)
        {
            allHashes.Add(SHA256.HashData(input.CreationEntityPublicKey.ToByteArray()));
        }
        else
        {
            allHashes.Add(SHA256.HashData(Array.Empty<byte>()));
        }
    }

    // ───────────────────────────────── output hashing ─────────────────────────────────

    private static byte[] HashTokenOutputPayload(TokenOutput output, bool partialHash)
    {
        using var ms = new MemoryStream();

        // ID — only for final hash.
        if (!partialHash && output.HasId && !string.IsNullOrEmpty(output.Id))
        {
            ms.Write(Encoding.UTF8.GetBytes(output.Id));
        }

        if (output.OwnerPublicKey.Length > 0)
        {
            ms.Write(output.OwnerPublicKey.Span);
        }

        if (!partialHash)
        {
            if (output.HasRevocationCommitment && output.RevocationCommitment.Length > 0)
            {
                ms.Write(output.RevocationCommitment.Span);
            }
            ms.Write(UInt64BigEndian(output.WithdrawBondSats));
            ms.Write(UInt64BigEndian(output.WithdrawRelativeBlockLocktime));
        }

        if (output.HasTokenPublicKey && output.TokenPublicKey.Length > 0)
        {
            ms.Write(output.TokenPublicKey.Span);
        }
        else
        {
            ms.Write(new byte[33]);
        }

        if (output.HasTokenIdentifier && output.TokenIdentifier.Length > 0)
        {
            ms.Write(output.TokenIdentifier.Span);
        }
        else
        {
            ms.Write(new byte[32]);
        }

        if (output.TokenAmount.Length > 0)
        {
            ms.Write(output.TokenAmount.Span);
        }

        return ms.ToArray();
    }

    // ───────────────────────────────── helpers ─────────────────────────────────

    private static byte[] UInt32BigEndian(uint value)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        return buf;
    }

    private static byte[] UInt64BigEndian(ulong value)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, value);
        return buf;
    }

    private static byte[] Concat(List<byte[]> chunks)
    {
        var total = chunks.Sum(c => c.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var c in chunks)
        {
            Buffer.BlockCopy(c, 0, result, offset, c.Length);
            offset += c.Length;
        }
        return result;
    }

    private sealed class ByteArrayLexicographicComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayLexicographicComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (x is null && y is null)
            {
                return 0;
            }
            if (x is null)
            {
                return -1;
            }
            if (y is null)
            {
                return 1;
            }

            var len = Math.Min(x.Length, y.Length);
            for (int i = 0; i < len; i++)
            {
                var c = x[i].CompareTo(y[i]);
                if (c != 0)
                {
                    return c;
                }
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}
