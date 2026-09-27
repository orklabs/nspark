using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using NBitcoin;
using NSpark.Proto;
using uniffi.spark_frost;
using FrostSigningCommitment = uniffi.spark_frost.SigningCommitment;
using ProtoSecretShare = NSpark.Proto.SecretShare;

namespace NSpark.Signer;

/// <summary>
/// In-process <see cref="ISparkSigner"/> backed by a BIP-39 mnemonic. Derives keys via
/// <see cref="KeyDerivation"/> and delegates FROST / ECIES / VSS to the native
/// <c>spark_frost</c> library through the auto-generated UniFFI bindings.
/// </summary>
/// <remarks>
/// All methods are synchronous under the hood — the async signatures exist to match
/// <see cref="ISparkSigner"/>, which is shaped for out-of-process / remote signers. The
/// process is the trust boundary (see <c>docs/trust-model.md</c>); for stricter custody
/// implement <see cref="ISparkSigner"/> directly against an HSM or KMS.
/// </remarks>
public sealed class SparkSigner : ISparkSigner
{
    private static readonly BigInteger Secp256k1Order = BigInteger.Parse(
        "0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141",
        System.Globalization.NumberStyles.HexNumber,
        System.Globalization.CultureInfo.InvariantCulture);

    private readonly KeyDerivation _keys;

    /// <inheritdoc/>
    public SparkSigner(KeyDerivation keys)
    {
        _keys = keys;
    }

    /// <summary>
    /// Build a <see cref="SparkSigner"/> from a BIP-39 mnemonic at the given account index
    /// and optional passphrase.
    /// </summary>
    /// <param name="mnemonic">The BIP-39 phrase.</param>
    /// <param name="account">BIP-32 account index.</param>
    /// <param name="passphrase">Optional BIP-39 passphrase.</param>
    /// <param name="validateMnemonic">
    /// Refuse a phrase that fails the BIP-39 wordlist or checksum (the default). Pass <c>false</c>
    /// only for phrases known to be non-standard.
    /// </param>
    /// <exception cref="Exceptions.SparkConfigurationException">The mnemonic fails validation.</exception>
    public static SparkSigner FromMnemonic(
        string mnemonic,
        int account = 0,
        string? passphrase = null,
        bool validateMnemonic = true)
    {
        var keys = KeyDerivation.FromMnemonic(mnemonic, account, passphrase, validateMnemonic);
        return new SparkSigner(keys);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Identity
    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<byte[]> GetIdentityPublicKeyAsync(CancellationToken ct = default)
        => Task.FromResult(_keys.IdentityKey.GetPublicKey().ToBytes());

    /// <inheritdoc/>
    public Task<byte[]> SignWithIdentityKeyAsync(byte[] messageHash, CancellationToken ct = default)
    {
        var hash = new uint256(messageHash);
        var sig = _keys.IdentityKey.PrivateKey.Sign(hash);
        return Task.FromResult(sig.ToDER());
    }

    /// <inheritdoc/>
    public Task<byte[]> SignCompactWithIdentityKeyAsync(byte[] messageHash, CancellationToken ct = default)
    {
        var hash = new uint256(messageHash);
        var sig = _keys.IdentityKey.PrivateKey.Sign(hash);
        return Task.FromResult(sig.ToCompact());
    }

    /// <inheritdoc/>
    public Task<byte[]> DecryptEciesWithIdentityKeyAsync(byte[] ciphertext, CancellationToken ct = default)
    {
        var identityPriv = _keys.IdentityKey.PrivateKey.ToBytes();
        try
        {
            return Task.FromResult(SparkFrostMethods.DecryptEcies(ciphertext, identityPriv));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identityPriv);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Deposit
    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<byte[]> GetDepositPublicKeyAsync(CancellationToken ct = default)
        => Task.FromResult(_keys.DepositKey.GetPublicKey().ToBytes());

    // ─────────────────────────────────────────────────────────────────────────
    // Per-leaf
    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<byte[]> GetLeafPublicKeyAsync(string leafId, CancellationToken ct = default)
    {
        var leaf = _keys.DeriveLeafKey(leafId);
        return Task.FromResult(leaf.GetPublicKey().ToBytes());
    }

    /// <inheritdoc/>
    public Task<LeafFrostSignature> SignLeafFrostAsync(
        string leafId,
        byte[] message,
        byte[] verifyingKey,
        IReadOnlyDictionary<string, SigningCommitment> soCommitments,
        byte[]? adaptorPublicKey = null,
        CancellationToken ct = default)
    {
        var signingKey = _keys.DeriveLeafKey(leafId).PrivateKey.ToBytes();
        try
        {
            var publicKey = SparkFrostMethods.GetPublicKeyBytes(signingKey, compressed: true);
            var keyPackage = new KeyPackage(secretKey: signingKey, publicKey: publicKey, verifyingKey: verifyingKey);
            var nonce = SparkFrostMethods.FrostNonce(keyPackage);
            var signature = SparkFrostMethods.SignFrost(
                msg: message,
                keyPackage: keyPackage,
                nonce: nonce.@nonce,
                selfCommitment: nonce.@commitment,
                statechainCommitments: ToFrostCommitments(soCommitments),
                adaptorPublicKey: adaptorPublicKey);

            return Task.FromResult(new LeafFrostSignature(
                PublicKey: publicKey,
                Commitment: new SigningCommitment(nonce.@commitment.@hiding, nonce.@commitment.@binding),
                UserSignature: signature));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingKey);
        }
    }

    /// <inheritdoc/>
    public Task<LeafFrostNonceCommitment> GenerateLeafFrostNonceAsync(
        string leafId,
        CancellationToken ct = default)
    {
        var signingKey = _keys.DeriveLeafKey(leafId).PrivateKey.ToBytes();
        try
        {
            var publicKey = SparkFrostMethods.GetPublicKeyBytes(signingKey, compressed: true);
            // verifyingKey is only consulted by FrostNonce for randomness binding — passing
            // signingPubKey here is consistent with the prior NSpark behaviour for nonce-only
            // generation; the real group verifying key is bound at sign time.
            var keyPackage = new KeyPackage(secretKey: signingKey, publicKey: publicKey, verifyingKey: publicKey);
            var nonceResult = SparkFrostMethods.FrostNonce(keyPackage);
            return Task.FromResult(new LeafFrostNonceCommitment(
                PublicKey: publicKey,
                Commitment: new SigningCommitment(nonceResult.@commitment.@hiding, nonceResult.@commitment.@binding),
                NonceHandle: EncodeNonceHandle(nonceResult)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingKey);
        }
    }

    /// <inheritdoc/>
    public Task<byte[]> SignLeafFrostWithNonceAsync(
        string leafId,
        byte[] nonceHandle,
        byte[] message,
        byte[] verifyingKey,
        IReadOnlyDictionary<string, SigningCommitment> soCommitments,
        byte[]? adaptorPublicKey = null,
        CancellationToken ct = default)
    {
        var signingKey = _keys.DeriveLeafKey(leafId).PrivateKey.ToBytes();
        try
        {
            var publicKey = SparkFrostMethods.GetPublicKeyBytes(signingKey, compressed: true);
            var keyPackage = new KeyPackage(secretKey: signingKey, publicKey: publicKey, verifyingKey: verifyingKey);
            var (nonce, commitment) = DecodeNonceHandle(nonceHandle);
            var signature = SparkFrostMethods.SignFrost(
                msg: message,
                keyPackage: keyPackage,
                nonce: nonce,
                selfCommitment: commitment,
                statechainCommitments: ToFrostCommitments(soCommitments),
                adaptorPublicKey: adaptorPublicKey);
            return Task.FromResult(signature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingKey);
        }
    }

    /// <inheritdoc/>
    public Task<EncryptedSendTweakBatch> BuildEncryptedSendTweaksAsync(
        IReadOnlyList<SendTweakLeafDescriptor> leaves,
        IReadOnlyList<SoTarget> soTargets,
        string transferId,
        uint threshold,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(leaves);
        ArgumentNullException.ThrowIfNull(soTargets);
        ArgumentException.ThrowIfNullOrEmpty(transferId);

        // perLeafPerSo[leafIndex] holds a fully-built SendLeafKeyTweak for each SO, with that
        // SO's share already plugged in. We build one collection per leaf first, then transpose
        // into per-SO packages and ECIES-encrypt at the end.
        var perSoPackages = new Dictionary<string, SendLeafKeyTweaks>(soTargets.Count);
        foreach (var so in soTargets)
        {
            perSoPackages[so.SoId] = new SendLeafKeyTweaks();
        }

        var identityPriv = _keys.IdentityKey.PrivateKey.ToBytes();
        try
        {
            foreach (var leaf in leaves)
            {
                BuildOneSendLeaf(leaf, soTargets, transferId, threshold, identityPriv, perSoPackages);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identityPriv);
        }

        // ECIES-encrypt one package per SO to its identity public key.
        var encryptedBySo = new Dictionary<string, byte[]>(soTargets.Count);
        foreach (var so in soTargets)
        {
            var packageBytes = perSoPackages[so.SoId].ToByteArray();
            encryptedBySo[so.SoId] = SparkFrostMethods.EncryptEcies(packageBytes, so.IdentityPublicKey);
        }

        return Task.FromResult(new EncryptedSendTweakBatch(encryptedBySo));
    }

    private void BuildOneSendLeaf(
        SendTweakLeafDescriptor leaf,
        IReadOnlyList<SoTarget> soTargets,
        string transferId,
        uint threshold,
        byte[] identityPriv,
        Dictionary<string, SendLeafKeyTweaks> perSoPackages)
    {
        var oldKey = _keys.DeriveLeafKey(leaf.LeafId).PrivateKey.ToBytes();
        var newKey = SparkFrostMethods.RandomSecretKeyBytes();
        try
        {
            var tweak = SubtractScalarsModN(oldKey, newKey);
            try
            {
                var shares = SparkFrostMethods.SplitSecretWithProofsUniffi(
                    tweak, threshold, (uint)soTargets.Count);
                var secretCipher = SparkFrostMethods.EncryptEcies(newKey, leaf.ReceiverPublicKey);

                // Tweak signature: ECDSA compact over SHA256(leafId || transferId || secretCipher).
                // The wallet used to compute this; now it lives inside the signer's trust boundary.
                var sigPayload = new List<byte>();
                sigPayload.AddRange(Encoding.UTF8.GetBytes(leaf.LeafId + transferId));
                sigPayload.AddRange(secretCipher);
                var hashUint = new uint256(SHA256.HashData(sigPayload.ToArray()));
                var tweakSig = _keys.IdentityKey.PrivateKey.Sign(hashUint).ToCompact();

                // pubkey of each share (one per SO) for the pubkey_shares_tweak map.
                var pubkeySharesTweak = new Dictionary<string, ByteString>(soTargets.Count);
                foreach (var so in soTargets)
                {
                    var matchedShare = shares.First(s => s.@index == so.ShareIndex);
                    pubkeySharesTweak[so.SoId] = ByteString.CopyFrom(
                        SparkFrostMethods.GetPublicKeyBytes(matchedShare.@share, compressed: true));
                }

                // Plug each SO's share into its package.
                foreach (var so in soTargets)
                {
                    var share = shares.First(s => s.@index == so.ShareIndex);
                    var leafTweak = new SendLeafKeyTweak
                    {
                        LeafId = leaf.LeafId,
                        SecretShareTweak = new ProtoSecretShare
                        {
                            SecretShare_ = ByteString.CopyFrom(share.@share),
                        },
                        SecretCipher = ByteString.CopyFrom(secretCipher),
                        Signature = ByteString.CopyFrom(tweakSig),
                    };
                    foreach (var proof in share.@proofs)
                    {
                        leafTweak.SecretShareTweak.Proofs.Add(ByteString.CopyFrom(proof));
                    }

                    foreach (var (k, v) in pubkeySharesTweak)
                    {
                        leafTweak.PubkeySharesTweak.Add(k, v);
                    }

                    perSoPackages[so.SoId].LeavesToSend.Add(leafTweak);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(tweak);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldKey);
            CryptographicOperations.ZeroMemory(newKey);
        }
    }

    /// <inheritdoc/>
    public Task<EncryptedClaimTweakBatch> BuildEncryptedClaimTweaksAsync(
        IReadOnlyList<ClaimTweakLeafDescriptor> leaves,
        IReadOnlyList<SoTarget> soTargets,
        uint threshold,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(leaves);
        ArgumentNullException.ThrowIfNull(soTargets);

        var perSoPackages = new Dictionary<string, ClaimLeafKeyTweaks>(soTargets.Count);
        foreach (var so in soTargets)
        {
            perSoPackages[so.SoId] = new ClaimLeafKeyTweaks();
        }

        var newPubKeyByLeaf = new Dictionary<string, byte[]>(leaves.Count);
        var identityPriv = _keys.IdentityKey.PrivateKey.ToBytes();
        try
        {
            foreach (var leaf in leaves)
            {
                var newPubKey = BuildOneClaimLeaf(leaf, soTargets, threshold, identityPriv, perSoPackages);
                newPubKeyByLeaf[leaf.LeafId] = newPubKey;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identityPriv);
        }

        var encryptedBySo = new Dictionary<string, byte[]>(soTargets.Count);
        foreach (var so in soTargets)
        {
            var packageBytes = perSoPackages[so.SoId].ToByteArray();
            encryptedBySo[so.SoId] = SparkFrostMethods.EncryptEcies(packageBytes, so.IdentityPublicKey);
        }

        return Task.FromResult(new EncryptedClaimTweakBatch(newPubKeyByLeaf, encryptedBySo));
    }

    private byte[] BuildOneClaimLeaf(
        ClaimTweakLeafDescriptor leaf,
        IReadOnlyList<SoTarget> soTargets,
        uint threshold,
        byte[] identityPriv,
        Dictionary<string, ClaimLeafKeyTweaks> perSoPackages)
    {
        byte[]? oldKey = null;
        byte[]? newKey = null;
        try
        {
            oldKey = SparkFrostMethods.DecryptEcies(leaf.SenderSecretCipher, identityPriv);
            newKey = _keys.DeriveLeafKey(leaf.LeafId).PrivateKey.ToBytes();
            var newPubKey = SparkFrostMethods.GetPublicKeyBytes(newKey, compressed: true);
            var tweak = SubtractScalarsModN(oldKey, newKey);
            try
            {
                var shares = SparkFrostMethods.SplitSecretWithProofsUniffi(
                    tweak, threshold, (uint)soTargets.Count);

                var pubkeySharesTweak = new Dictionary<string, ByteString>(soTargets.Count);
                foreach (var so in soTargets)
                {
                    var matchedShare = shares.First(s => s.@index == so.ShareIndex);
                    pubkeySharesTweak[so.SoId] = ByteString.CopyFrom(
                        SparkFrostMethods.GetPublicKeyBytes(matchedShare.@share, compressed: true));
                }

                foreach (var so in soTargets)
                {
                    var share = shares.First(s => s.@index == so.ShareIndex);
                    var leafTweak = new ClaimLeafKeyTweak
                    {
                        LeafId = leaf.LeafId,
                        SecretShareTweak = new ProtoSecretShare
                        {
                            SecretShare_ = ByteString.CopyFrom(share.@share),
                        },
                    };
                    foreach (var proof in share.@proofs)
                    {
                        leafTweak.SecretShareTweak.Proofs.Add(ByteString.CopyFrom(proof));
                    }

                    foreach (var (k, v) in pubkeySharesTweak)
                    {
                        leafTweak.PubkeySharesTweak.Add(k, v);
                    }

                    perSoPackages[so.SoId].LeavesToReceive.Add(leafTweak);
                }

                return newPubKey;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(tweak);
            }
        }
        finally
        {
            if (oldKey is not null)
            {
                CryptographicOperations.ZeroMemory(oldKey);
            }

            if (newKey is not null)
            {
                CryptographicOperations.ZeroMemory(newKey);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Static deposit
    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<byte[]> GetStaticDepositPublicKeyAsync(int index = 0, CancellationToken ct = default)
        => Task.FromResult(_keys.DeriveStaticDepositChildKey(index).GetPublicKey().ToBytes());

    /// <inheritdoc/>
    public Task<byte[]> ExportStaticDepositPrivateKeyAsync(int index = 0, CancellationToken ct = default)
        => Task.FromResult(_keys.DeriveStaticDepositChildKey(index).PrivateKey.ToBytes());

    /// <inheritdoc/>
    public Task<LeafFrostNonceCommitment> GenerateStaticDepositFrostNonceAsync(
        int index,
        CancellationToken ct = default)
    {
        var signingKey = _keys.DeriveStaticDepositChildKey(index).PrivateKey.ToBytes();
        try
        {
            var publicKey = SparkFrostMethods.GetPublicKeyBytes(signingKey, compressed: true);
            var keyPackage = new KeyPackage(secretKey: signingKey, publicKey: publicKey, verifyingKey: publicKey);
            var nonceResult = SparkFrostMethods.FrostNonce(keyPackage);
            return Task.FromResult(new LeafFrostNonceCommitment(
                PublicKey: publicKey,
                Commitment: new SigningCommitment(nonceResult.@commitment.@hiding, nonceResult.@commitment.@binding),
                NonceHandle: EncodeNonceHandle(nonceResult)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingKey);
        }
    }

    /// <inheritdoc/>
    public Task<byte[]> SignStaticDepositFrostWithNonceAsync(
        int index,
        byte[] nonceHandle,
        byte[] message,
        byte[] verifyingKey,
        IReadOnlyDictionary<string, SigningCommitment> soCommitments,
        CancellationToken ct = default)
    {
        var signingKey = _keys.DeriveStaticDepositChildKey(index).PrivateKey.ToBytes();
        try
        {
            var publicKey = SparkFrostMethods.GetPublicKeyBytes(signingKey, compressed: true);
            var keyPackage = new KeyPackage(secretKey: signingKey, publicKey: publicKey, verifyingKey: verifyingKey);
            var (nonce, commitment) = DecodeNonceHandle(nonceHandle);
            var signature = SparkFrostMethods.SignFrost(
                msg: message,
                keyPackage: keyPackage,
                nonce: nonce,
                selfCommitment: commitment,
                statechainCommitments: ToFrostCommitments(soCommitments),
                adaptorPublicKey: null);
            return Task.FromResult(signature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingKey);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lightning preimage
    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<EncryptedPreimageShareBundle> BuildEncryptedPreimageSharesAsync(
        string transferId,
        IReadOnlyList<SoTarget> soTargets,
        uint threshold,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(transferId);
        ArgumentNullException.ThrowIfNull(soTargets);

        var preimage = _keys.ComputePreimage(transferId);
        try
        {
            var paymentHash = SHA256.HashData(preimage);
            var shares = SparkFrostMethods.SplitSecretWithProofsUniffi(
                preimage, threshold, (uint)soTargets.Count);

            var encryptedBySo = new Dictionary<string, byte[]>(soTargets.Count);
            foreach (var so in soTargets)
            {
                var share = shares.First(s => s.@index == so.ShareIndex);
                var proto = new ProtoSecretShare
                {
                    SecretShare_ = ByteString.CopyFrom(share.@share),
                };
                foreach (var proof in share.@proofs)
                {
                    proto.Proofs.Add(ByteString.CopyFrom(proof));
                }

                var protoBytes = proto.ToByteArray();
                encryptedBySo[so.SoId] = SparkFrostMethods.EncryptEcies(protoBytes, so.IdentityPublicKey);
            }

            return Task.FromResult(new EncryptedPreimageShareBundle(paymentHash, encryptedBySo));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(preimage);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Adaptor key
    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<AdaptorKeyHandle> GenerateAdaptorKeyAsync(CancellationToken ct = default)
    {
        var priv = SparkFrostMethods.RandomSecretKeyBytes();
        var pub = SparkFrostMethods.GetPublicKeyBytes(priv, compressed: true);
        // For the in-process trust boundary, the handle simply carries the raw private key
        // so a future RevealAdaptorPrivateKey call (when swap-completion lands) can consume
        // it. Remote signers can put any opaque token here.
        return Task.FromResult(new AdaptorKeyHandle(PublicKey: pub, Handle: priv));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Internal helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, FrostSigningCommitment> ToFrostCommitments(
        IReadOnlyDictionary<string, SigningCommitment> soCommitments)
    {
        var result = new Dictionary<string, FrostSigningCommitment>(soCommitments.Count);
        foreach (var (id, c) in soCommitments)
        {
            result[id] = new FrostSigningCommitment(hiding: c.Hiding, binding: c.Binding);
        }
        return result;
    }

    private static byte[] EncodeNonceHandle(NonceResult nonceResult)
    {
        // Layout: 32(secret nonce.hiding) || 32(secret nonce.binding)
        //       || 33(pub commitment.hiding) || 33(pub commitment.binding)
        // We round-trip the exact bytes from FrostNonce so phase-2 signing sees the
        // same commitment that was published to the SOs.
        var nh = nonceResult.@nonce.@hiding;
        var nb = nonceResult.@nonce.@binding;
        var ch = nonceResult.@commitment.@hiding;
        var cb = nonceResult.@commitment.@binding;

        var handle = new byte[nh.Length + nb.Length + ch.Length + cb.Length];
        var offset = 0;
        Buffer.BlockCopy(nh, 0, handle, offset, nh.Length); offset += nh.Length;
        Buffer.BlockCopy(nb, 0, handle, offset, nb.Length); offset += nb.Length;
        Buffer.BlockCopy(ch, 0, handle, offset, ch.Length); offset += ch.Length;
        Buffer.BlockCopy(cb, 0, handle, offset, cb.Length);
        return handle;
    }

    private static (SigningNonce Nonce, FrostSigningCommitment Commitment) DecodeNonceHandle(byte[] handle)
    {
        if (handle.Length != 32 + 32 + 33 + 33)
        {
            throw new ArgumentException(
                $"Invalid nonce handle length: expected {32 + 32 + 33 + 33}, got {handle.Length}",
                nameof(handle));
        }

        var nonceHiding = new byte[32];
        var nonceBinding = new byte[32];
        var commitHiding = new byte[33];
        var commitBinding = new byte[33];
        Buffer.BlockCopy(handle, 0, nonceHiding, 0, 32);
        Buffer.BlockCopy(handle, 32, nonceBinding, 0, 32);
        Buffer.BlockCopy(handle, 64, commitHiding, 0, 33);
        Buffer.BlockCopy(handle, 97, commitBinding, 0, 33);

        return (
            new SigningNonce(hiding: nonceHiding, binding: nonceBinding),
            new FrostSigningCommitment(hiding: commitHiding, binding: commitBinding));
    }

    /// <summary>
    /// Compute <c>(a - b) mod n</c> over the secp256k1 order, returning a 32-byte
    /// big-endian scalar. Used for FROST leaf key-tweak construction.
    /// </summary>
    private static byte[] SubtractScalarsModN(byte[] a, byte[] b)
    {
        var aInt = new BigInteger(a, isUnsigned: true, isBigEndian: true);
        var bInt = new BigInteger(b, isUnsigned: true, isBigEndian: true);
        var result = ((aInt - bInt) % Secp256k1Order + Secp256k1Order) % Secp256k1Order;

        var bytes = result.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == 32)
        {
            return bytes;
        }

        var padded = new byte[32];
        bytes.CopyTo(padded, 32 - bytes.Length);
        return padded;
    }
}
