using System.Security.Cryptography;
using System.Text;
using NBitcoin;
using NBitcoin.Crypto;
using NSpark.Exceptions;
using NSpark.Proto;

namespace NSpark.Services;

/// <summary>
/// Checks a deposit address the coordinator generated before the wallet hands it to anyone, as
/// the reference SDK does (<c>DepositService.validateDepositAddress</c>). Without these checks a
/// coordinator — or anyone impersonating it — could hand out an address it alone controls; a
/// static address is reused for every deposit.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><description>The proof of possession: a BIP-340 signature by the operators' share of the
///   key (verifying key minus the wallet's signing key, BIP-86 tweaked) over the tagged hash
///   <c>["spark", "deposit", "proof_of_possession"]</c> of the wallet's identity key, that operator
///   key and the address (<c>ProofOfPossessionMessageHashForDepositAddress</c>, hash variant V2).</description></item>
///   <item><description>Every operator's ECDSA signature over sha256(address), by its configured
///   identity key. The coordinator's own signature is required for static addresses only.</description></item>
///   <item><description>The address pays P2TR of the verifying key
///   (<c>P2TRAddressFromPublicKey</c>), so the proof covers the key the funds actually go to.</description></item>
/// </list>
/// </remarks>
internal static class DepositAddressVerifier
{
    private const string Operation = "deposit.address.verify";

    /// <summary>
    /// Throws <see cref="SparkUntrustedResponseException"/> unless <paramref name="address"/> passes
    /// every check. <paramref name="options"/> supplies the operators (their identifiers and identity
    /// keys, the first one being the coordinator) and the network.
    /// </summary>
    internal static void Verify(
        Address address,
        byte[] userSigningPublicKey,
        byte[] identityPublicKey,
        bool isStatic,
        SparkOptions options)
    {
        ArgumentNullException.ThrowIfNull(address);
        var proof = address.DepositAddressProof;
        if (proof is null || proof.ProofOfPossessionSignature.IsEmpty || proof.AddressSignatures.Count == 0)
        {
            throw new SparkUntrustedResponseException(
                Operation, $"Deposit address {address.Address_} comes without a proof of possession and operator signatures.");
        }

        var verifyingKey = address.VerifyingKey.ToByteArray();
        string? expectedAddress;
        try
        {
            expectedAddress = RenewalService.LeafNodeAddress(verifyingKey, options.Network);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            expectedAddress = null;
        }

        if (!string.Equals(expectedAddress, address.Address_, StringComparison.Ordinal))
        {
            throw new SparkUntrustedResponseException(
                Operation, $"Deposit address {address.Address_} does not pay the reported verifying key.");
        }

        var operatorPublicKey = SubtractPublicKeys(verifyingKey, userSigningPublicKey);
        var message = SparkTaggedHash.Create("spark", "deposit", "proof_of_possession")
            .AddBytes(identityPublicKey)
            .AddBytes(operatorPublicKey)
            .AddBytes(Encoding.UTF8.GetBytes(address.Address_))
            .Hash();
        if (!VerifySchnorr(proof.ProofOfPossessionSignature.ToByteArray(), message, operatorPublicKey))
        {
            throw new SparkUntrustedResponseException(
                Operation, $"Deposit address {address.Address_} has an invalid proof of possession.");
        }

        var addressHash = SHA256.HashData(Encoding.UTF8.GetBytes(address.Address_));
        var coordinatorIdentifier = options.SigningOperators.FirstOrDefault()?.Identifier;
        foreach (var signingOperator in options.SigningOperators)
        {
            if (!isStatic && string.Equals(signingOperator.Identifier, coordinatorIdentifier, StringComparison.Ordinal))
            {
                continue;
            }

            byte[]? operatorKey = null;
            try
            {
                operatorKey = Convert.FromHexString(signingOperator.IdentityPublicKeyHex ?? string.Empty);
            }
            catch (FormatException)
            {
                operatorKey = null;
            }

            if (!proof.AddressSignatures.TryGetValue(signingOperator.Identifier, out var signature)
                || operatorKey is null
                || !TransferLeafVerifier.VerifyLegacySignature(signature.Span, addressHash, operatorKey))
            {
                throw new SparkUntrustedResponseException(
                    Operation,
                    $"Deposit address {address.Address_} lacks a valid signature from operator {signingOperator.Identifier}.");
            }
        }
    }

    /// <summary><c>a - b</c> for two compressed secp256k1 public keys.</summary>
    internal static byte[] SubtractPublicKeys(byte[] a, byte[] b)
    {
        return Signer.Secp256k1Points.Subtract(a, b)
            ?? throw new SparkUntrustedResponseException(Operation, "Invalid deposit address key material.");
    }

    /// <summary>
    /// BIP-340 verification of <paramref name="signature"/> over the 32-byte
    /// <paramref name="message"/> under the BIP-86 output key of <paramref name="taprootInternalKey"/>.
    /// </summary>
    internal static bool VerifySchnorr(byte[] signature, byte[] message, byte[] taprootInternalKey)
    {
        if (signature.Length != 64 || message.Length != 32)
        {
            return false;
        }

        try
        {
            var tweaked = uniffi.spark_frost.SparkFrostMethods.GetTaprootPubkey(taprootInternalKey);
            return tweaked.Length == 33
                && TaprootPubKey.TryCreate(tweaked.AsSpan(1), out var key)
                && SchnorrSignature.TryParse(signature, out var schnorr)
                && key.VerifySignature(new uint256(message), schnorr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
