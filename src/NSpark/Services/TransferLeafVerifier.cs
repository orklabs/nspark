using System.Security.Cryptography;
using System.Text;
using NBitcoin;
using NBitcoin.Crypto;
using NSpark.Exceptions;
using NSpark.Proto;
using ProtoSignature = NSpark.Proto.Common.Signature;
using ProtoSignatureScheme = NSpark.Proto.Common.SignatureScheme;

namespace NSpark.Services;

/// <summary>
/// Authenticates the leaves of a pending inbound transfer before the wallet decrypts the leaf
/// secrets, derives tweaks, or signs refund transactions. Mirrors the reference SDK's
/// <c>verifyPendingTransfer</c> and <c>verifyTypedSignature</c>: each leaf carries the sender's
/// signature over <c>sha256(leafId || transferId || secretCipher)</c>, made with the sender's
/// identity key, either as legacy bare bytes (compact or DER ECDSA) or as a scheme-tagged
/// signature (strict-DER ECDSA or BIP-340 Schnorr).
/// </summary>
internal static class TransferLeafVerifier
{
    private const string Operation = "transfer.claim.verify";

    internal static byte[] PayloadHash(string leafId, string transferId, ReadOnlySpan<byte> secretCipher)
    {
        var leafBytes = Encoding.UTF8.GetBytes(leafId);
        var transferBytes = Encoding.UTF8.GetBytes(transferId);
        var payload = new byte[leafBytes.Length + transferBytes.Length + secretCipher.Length];
        leafBytes.CopyTo(payload, 0);
        transferBytes.CopyTo(payload, leafBytes.Length);
        secretCipher.CopyTo(payload.AsSpan(leafBytes.Length + transferBytes.Length));
        return SHA256.HashData(payload);
    }

    /// <summary>
    /// Verify whichever signature form the leaf carries. Malformed input is an invalid
    /// signature, never a crash.
    /// </summary>
    internal static bool VerifyLeafSignature(TransferLeaf leaf, byte[] digest, byte[] senderIdentityPublicKey)
    {
        return leaf.SigCase switch
        {
            TransferLeaf.SigOneofCase.Signature => VerifyLegacySignature(leaf.Signature.Span, digest, senderIdentityPublicKey),
            TransferLeaf.SigOneofCase.TypedSignature => VerifyTypedSignature(leaf.TypedSignature, digest, senderIdentityPublicKey),
            _ => false,
        };
    }

    /// <summary>
    /// ECDSA over a 32-byte digest with a 33-byte compressed key. The legacy field predates the
    /// strict-DER contract and carries both encodings on the wire (JS senders emit compact, Go
    /// senders DER), so it is parsed leniently.
    /// </summary>
    internal static bool VerifyLegacySignature(ReadOnlySpan<byte> signature, byte[] digest, byte[] compressedPublicKey)
    {
        if (signature.IsEmpty || !TryParsePublicKey(compressedPublicKey, digest, out var pubKey))
        {
            return false;
        }

        ECDSASignature? parsed = null;
        if (signature.Length == 64 && !ECDSASignature.TryParseFromCompact(signature.ToArray(), out parsed))
        {
            parsed = null;
        }
        if (parsed is null && !TryParseDer(signature, strict: false, out parsed))
        {
            return false;
        }

        return pubKey.Verify(new uint256(digest), parsed);
    }

    /// <summary>
    /// A scheme-tagged signature: strict-DER ECDSA or BIP-340 Schnorr against the x-only form of
    /// the compressed key. Unknown schemes fail closed.
    /// </summary>
    internal static bool VerifyTypedSignature(ProtoSignature? typed, byte[] digest, byte[] compressedPublicKey)
    {
        if (typed is null || typed.Signature_.IsEmpty)
        {
            return false;
        }

        switch (typed.Scheme)
        {
            case ProtoSignatureScheme.Ecdsa:
            {
                if (!TryParsePublicKey(compressedPublicKey, digest, out var pubKey)
                    || !TryParseDer(typed.Signature_.Span, strict: true, out var parsed))
                {
                    return false;
                }
                return pubKey.Verify(new uint256(digest), parsed);
            }
            case ProtoSignatureScheme.Schnorr:
                return VerifySchnorr(typed.Signature_.Span, digest, compressedPublicKey);
            default:
                return false;
        }
    }

    /// <summary>
    /// Throws <see cref="SparkUntrustedResponseException"/> unless every leaf is present and
    /// carries a valid sender signature. When the transfer names a receiver it must be this
    /// wallet.
    /// </summary>
    internal static void Verify(Transfer transfer, byte[] receiverIdentityPublicKey)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        ArgumentNullException.ThrowIfNull(receiverIdentityPublicKey);

        if (!transfer.ReceiverIdentityPublicKey.IsEmpty
            && !transfer.ReceiverIdentityPublicKey.Span.SequenceEqual(receiverIdentityPublicKey))
        {
            throw new SparkUntrustedResponseException(Operation, $"Transfer {transfer.Id} is not addressed to this wallet.");
        }
        if (transfer.Leaves.Count == 0)
        {
            throw new SparkUntrustedResponseException(Operation, $"Transfer {transfer.Id} has no leaves.");
        }

        var sender = transfer.SenderIdentityPublicKey.ToByteArray();
        foreach (var transferLeaf in transfer.Leaves)
        {
            if (transferLeaf.Leaf is null || string.IsNullOrEmpty(transferLeaf.Leaf.Id))
            {
                throw new SparkUntrustedResponseException(Operation, $"Transfer {transfer.Id} contains a leaf without node data.");
            }
            if (transferLeaf.SecretCipher.IsEmpty)
            {
                throw new SparkUntrustedResponseException(
                    Operation, $"Leaf {transferLeaf.Leaf.Id} in transfer {transfer.Id} has no secret cipher.");
            }

            var digest = PayloadHash(transferLeaf.Leaf.Id, transfer.Id, transferLeaf.SecretCipher.Span);
            if (!VerifyLeafSignature(transferLeaf, digest, sender))
            {
                throw new SparkUntrustedResponseException(
                    Operation,
                    $"The sender's signature on leaf {transferLeaf.Leaf.Id} in transfer {transfer.Id} is missing or invalid.");
            }
        }
    }

    private static bool TryParsePublicKey(byte[] compressedPublicKey, byte[] digest, out PubKey pubKey)
    {
        pubKey = null!;
        if (digest is not { Length: 32 }
            || compressedPublicKey is not { Length: 33 }
            || (compressedPublicKey[0] != 0x02 && compressedPublicKey[0] != 0x03))
        {
            return false;
        }

        try
        {
            pubKey = new PubKey(compressedPublicKey);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryParseDer(ReadOnlySpan<byte> signature, bool strict, out ECDSASignature parsed)
    {
        parsed = null!;
        var bytes = signature.ToArray();
        if (strict && !ECDSASignature.IsValidDER(bytes))
        {
            return false;
        }

        try
        {
            parsed = ECDSASignature.FromDER(bytes);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static bool VerifySchnorr(ReadOnlySpan<byte> signature, byte[] digest, byte[] compressedPublicKey)
    {
        if (signature.Length != 64 || digest is not { Length: 32 } || compressedPublicKey is not { Length: 33 })
        {
            return false;
        }
        // The parity byte is stripped rather than parsed, so the prefix must be checked here.
        if (compressedPublicKey[0] != 0x02 && compressedPublicKey[0] != 0x03)
        {
            return false;
        }

        try
        {
            if (!SchnorrSignature.TryParse(signature.ToArray(), out var schnorr))
            {
                return false;
            }
            var xOnly = new TaprootPubKey(compressedPublicKey.AsSpan(1).ToArray());
            return xOnly.VerifySignature(new uint256(digest), schnorr);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }
}
