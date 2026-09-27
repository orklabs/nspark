using Google.Protobuf;
using NSpark.Proto;
using NSpark.Signer;
using ProtoSigningCommitment = NSpark.Proto.Common.SigningCommitment;
using SignerSigningCommitment = NSpark.Signer.SigningCommitment;

namespace NSpark.Services;

/// <summary>
/// Helpers around <see cref="ISparkSigner"/>'s FROST surface: build <see cref="UserSignedTxSigningJob"/>
/// instances for the SO-side protocol, convert between proto and signer commitment types,
/// and combine self+SO partial signatures via <see cref="FrostAggregator"/>.
/// </summary>
/// <remarks>
/// This helper does NOT touch private key material and does NOT import <c>uniffi.spark_frost</c>.
/// Every cryptographic operation flows through <see cref="ISparkSigner"/> (for private-material
/// ops) or <see cref="FrostAggregator"/> (for public aggregation).
/// </remarks>
internal static class FrostSigningHelper
{
    /// <summary>
    /// Build a <see cref="UserSignedTxSigningJob"/> by running a complete one-shot FROST
    /// signing round on the supplied leaf: generate the user's nonce + sign against the
    /// provided SO commitments + the transaction sighash. The signing key never leaves
    /// <paramref name="signer"/>.
    /// </summary>
    internal static async Task<UserSignedTxSigningJob> BuildSigningJobAsync(
        ISparkSigner signer,
        string leafId,
        byte[] verifyingKey,
        byte[] rawTx,
        byte[] sighash,
        IReadOnlyDictionary<string, ProtoSigningCommitment> soCommitments,
        CancellationToken ct)
    {
        var commitments = ConvertSoCommitments(soCommitments);
        var result = await signer.SignLeafFrostAsync(
            leafId, sighash, verifyingKey, commitments, adaptorPublicKey: null, ct)
            .ConfigureAwait(false);

        return BuildJob(leafId, rawTx, soCommitments, result);
    }

    /// <summary>
    /// One-shot FROST signing with an adaptor public key (used in atomic-swap flows).
    /// Returns the signing job plus the self-commitment and sighash so a later
    /// <see cref="AggregateFrostSignature"/> call can finalise the aggregated signature.
    /// </summary>
    internal static async Task<(UserSignedTxSigningJob Job, SignerSigningCommitment SelfCommitment, byte[] Sighash)>
        BuildSigningJobWithAdaptorAsync(
            ISparkSigner signer,
            string leafId,
            byte[] verifyingKey,
            byte[] rawTx,
            byte[] sighash,
            IReadOnlyDictionary<string, ProtoSigningCommitment> soCommitments,
            byte[] adaptorPublicKey,
            CancellationToken ct)
    {
        var commitments = ConvertSoCommitments(soCommitments);
        var result = await signer.SignLeafFrostAsync(
            leafId, sighash, verifyingKey, commitments, adaptorPublicKey, ct)
            .ConfigureAwait(false);
        var job = BuildJob(leafId, rawTx, soCommitments, result);
        return (job, result.Commitment, sighash);
    }

    /// <summary>
    /// Aggregate self + SO partial FROST signatures into a final aggregated signature, given a
    /// proto-shaped <see cref="SigningResult"/> from the SO response. Thin adapter over
    /// <see cref="FrostAggregator.Aggregate"/> that extracts the proto fields into the public
    /// aggregator surface.
    /// </summary>
    internal static byte[] AggregateFrostSignature(
        byte[] sighash,
        SignerSigningCommitment selfCommitment,
        byte[] selfSignature,
        byte[] selfPublicKey,
        byte[] verifyingKey,
        SigningResult signingResult,
        byte[]? adaptorPublicKey = null)
    {
        var soCommitments = new Dictionary<string, SignerSigningCommitment>(signingResult.SigningNonceCommitments.Count);
        foreach (var (soId, c) in signingResult.SigningNonceCommitments)
        {
            soCommitments[soId] = new SignerSigningCommitment(c.Hiding.ToByteArray(), c.Binding.ToByteArray());
        }

        var soSignatures = new Dictionary<string, byte[]>(signingResult.SignatureShares.Count);
        foreach (var (soId, sig) in signingResult.SignatureShares)
        {
            soSignatures[soId] = sig.ToByteArray();
        }

        var soPublicKeys = new Dictionary<string, byte[]>(signingResult.PublicKeys.Count);
        foreach (var (soId, pk) in signingResult.PublicKeys)
        {
            soPublicKeys[soId] = pk.ToByteArray();
        }

        return FrostAggregator.Aggregate(
            message: sighash,
            statechainCommitments: soCommitments,
            selfCommitment: selfCommitment,
            statechainSignatures: soSignatures,
            selfSignature: selfSignature,
            statechainPublicKeys: soPublicKeys,
            selfPublicKey: selfPublicKey,
            verifyingKey: verifyingKey,
            adaptorPublicKey: adaptorPublicKey);
    }

    /// <summary>
    /// Build an unsigned <see cref="SigningJob"/> that only carries the nonce commitment
    /// (used by deposit tree creation where the user signs only the root + refund).
    /// </summary>
    internal static SigningJob BuildUnsignedJob(
        byte[] signingPublicKey,
        byte[] rawTx,
        byte[] hidingNonce,
        byte[] bindingNonce)
    {
        return new SigningJob
        {
            SigningPublicKey = ByteString.CopyFrom(signingPublicKey),
            RawTx = ByteString.CopyFrom(rawTx),
            SigningNonceCommitment = new ProtoSigningCommitment
            {
                Hiding = ByteString.CopyFrom(hidingNonce),
                Binding = ByteString.CopyFrom(bindingNonce),
            },
        };
    }

    /// <summary>
    /// Convert a proto <see cref="ProtoSigningCommitment"/> dictionary into the signer's
    /// <see cref="SignerSigningCommitment"/> shape.
    /// </summary>
    internal static IReadOnlyDictionary<string, SignerSigningCommitment> ConvertSoCommitments(
        IReadOnlyDictionary<string, ProtoSigningCommitment> protoCommitments)
    {
        var result = new Dictionary<string, SignerSigningCommitment>(protoCommitments.Count);
        foreach (var (id, c) in protoCommitments)
        {
            result[id] = new SignerSigningCommitment(c.Hiding.ToByteArray(), c.Binding.ToByteArray());
        }
        return result;
    }

    /// <summary>
    /// Network string ("mainnet" or "regtest") for native lib calls.
    /// </summary>
    internal static string GetNetworkString(SparkNetwork network)
        => network == SparkNetwork.Mainnet ? "mainnet" : "regtest";

    /// <summary>
    /// Build the list of <see cref="SoTarget"/>s the signer needs to address its encrypted share
    /// bundles, by reconciling the coordinator's operator list with the wallet configuration.
    /// Every listed operator must be configured, the two lists must be the same size, and the
    /// indices must be a permutation of <c>0..n-1</c>. Secret shares are only ever encrypted to the
    /// configured operators' identity keys — never to keys the coordinator reports.
    /// </summary>
    /// <exception cref="Exceptions.SparkUntrustedResponseException">The coordinator's list does not match the configuration.</exception>
    /// <exception cref="Exceptions.SparkConfigurationException">A configured operator has no valid identity key.</exception>
    internal static IReadOnlyList<SoTarget> BuildSoTargets(
        Google.Protobuf.Collections.MapField<string, NSpark.Proto.SigningOperatorInfo> soOperators,
        NSpark.SigningOperatorConfig[] soConfigs)
    {
        const string operation = "operators.reconcile";
        if (soOperators.Count == 0)
        {
            throw new Exceptions.SparkUntrustedResponseException(
                operation, "The coordinator returned an empty signing operator list.");
        }
        if (soOperators.Count != soConfigs.Length)
        {
            throw new Exceptions.SparkUntrustedResponseException(
                operation,
                $"The coordinator lists {soOperators.Count} signing operators; the wallet is configured for {soConfigs.Length}.");
        }

        var targets = new List<SoTarget>(soOperators.Count);
        var seenIndices = new HashSet<ulong>();
        foreach (var (soId, soInfo) in soOperators)
        {
            var soConfig = soConfigs.FirstOrDefault(c => string.Equals(c.Identifier, soId, StringComparison.Ordinal))
                ?? throw new Exceptions.SparkUntrustedResponseException(
                    operation, $"The coordinator listed operator {soId}, which is not in the wallet configuration.");
            if (soInfo.Index >= (ulong)soConfigs.Length || !seenIndices.Add(soInfo.Index))
            {
                throw new Exceptions.SparkUntrustedResponseException(
                    operation, $"The coordinator reported an invalid or duplicate index {soInfo.Index} for operator {soId}.");
            }

            targets.Add(new SoTarget(soId, (uint)soInfo.Index + 1, ConfiguredIdentityKey(soConfig)));
        }

        return targets.OrderBy(t => t.ShareIndex).ToList();
    }

    /// <summary>
    /// Targets for a Lightning receive's preimage shares, from the configuration alone: each
    /// operator validates the share at its own index — its identifier, a 32-byte big-endian
    /// number equal to its index + 1 — whatever the order of the configuration (the reference
    /// SDK's <c>shares[operator.id]</c>).
    /// </summary>
    /// <exception cref="Exceptions.SparkConfigurationException">An identifier or identity key is not valid.</exception>
    internal static IReadOnlyList<SoTarget> BuildConfiguredSoTargets(NSpark.SigningOperatorConfig[] soConfigs)
    {
        var targets = new List<SoTarget>(soConfigs.Length);
        var seen = new HashSet<uint>();
        foreach (var soConfig in soConfigs)
        {
            var index = OperatorShareIndex(soConfig.Identifier)
                ?? throw new Exceptions.SparkConfigurationException(
                    "operators.config", $"Operator identifier {soConfig.Identifier} is not a 32-byte index.");
            if (index > (uint)soConfigs.Length || !seen.Add(index))
            {
                throw new Exceptions.SparkConfigurationException(
                    "operators.config", $"Operator identifier {soConfig.Identifier} is out of range or duplicated.");
            }

            targets.Add(new SoTarget(soConfig.Identifier, index, ConfiguredIdentityKey(soConfig)));
        }

        return targets;
    }

    /// <summary>
    /// The secret-share index an operator validates its share at: its identifier, a 32-byte
    /// big-endian number equal to its index + 1. <c>null</c> when it is not one.
    /// </summary>
    internal static uint? OperatorShareIndex(string identifier)
    {
        if (identifier is not { Length: 64 } || !identifier.All(Uri.IsHexDigit))
        {
            return null;
        }

        // Every byte above the last four must be zero.
        if (identifier[..56].Any(c => c != '0'))
        {
            return null;
        }

        var index = uint.Parse(identifier[56..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        return index > 0 ? index : null;
    }

    /// <summary>
    /// A FROST threshold must be at least 1 and at most the number of operators.
    /// </summary>
    /// <exception cref="Exceptions.SparkConfigurationException">The threshold is out of range.</exception>
    internal static void ValidateThreshold(uint threshold, int operatorCount)
    {
        if (threshold < 1 || threshold > (uint)operatorCount)
        {
            throw new Exceptions.SparkConfigurationException(
                "operators.threshold", $"Signing threshold {threshold} is not valid for {operatorCount} operators.");
        }
    }

    private static byte[] ConfiguredIdentityKey(NSpark.SigningOperatorConfig soConfig)
    {
        byte[]? key = null;
        try
        {
            key = Convert.FromHexString(soConfig.IdentityPublicKeyHex ?? string.Empty);
        }
        catch (FormatException)
        {
            key = null;
        }

        if (key is not { Length: 33 })
        {
            throw new Exceptions.SparkConfigurationException(
                "operators.config", $"Operator {soConfig.Identifier} has no valid identity public key configured.");
        }

        return key;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Internal builders
    // ─────────────────────────────────────────────────────────────────────────

    private static UserSignedTxSigningJob BuildJob(
        string leafId,
        byte[] rawTx,
        IReadOnlyDictionary<string, ProtoSigningCommitment> soCommitments,
        LeafFrostSignature signature)
    {
        var protoSigningCommitments = new SigningCommitments();
        foreach (var (soId, c) in soCommitments)
        {
            protoSigningCommitments.SigningCommitments_.Add(soId, c);
        }

        return new UserSignedTxSigningJob
        {
            LeafId = leafId,
            SigningPublicKey = ByteString.CopyFrom(signature.PublicKey),
            RawTx = ByteString.CopyFrom(rawTx),
            SigningNonceCommitment = new ProtoSigningCommitment
            {
                Hiding = ByteString.CopyFrom(signature.Commitment.Hiding),
                Binding = ByteString.CopyFrom(signature.Commitment.Binding),
            },
            UserSignature = ByteString.CopyFrom(signature.UserSignature),
            SigningCommitments = protoSigningCommitments,
        };
    }
}
