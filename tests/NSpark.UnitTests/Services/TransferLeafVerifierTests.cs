using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using NBitcoin;
using NSpark.Exceptions;
using NSpark.Proto;
using NSpark.Services;
using ProtoSignature = NSpark.Proto.Common.Signature;
using ProtoSignatureScheme = NSpark.Proto.Common.SignatureScheme;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for the sender-signature check every inbound claim runs before decrypting a leaf
/// secret. Signatures are produced with NBitcoin the way the in-process signer produces them.
/// </summary>
[TestFixture]
public sealed class TransferLeafVerifierTests
{
    private static readonly Key Sender = new();
    private static readonly byte[] SenderPubKey = Sender.PubKey.ToBytes();
    private static readonly byte[] Receiver = new Key().PubKey.ToBytes();
    private static readonly byte[] Cipher = [1, 2, 3, 4, 5];

    private const string TransferId = "0192a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b";
    private const string LeafId = "leaf-1";

    [Test]
    public void PayloadHash_is_sha256_of_leafId_transferId_and_cipher()
    {
        var expected = SHA256.HashData([.. Encoding.UTF8.GetBytes(LeafId), .. Encoding.UTF8.GetBytes(TransferId), .. Cipher]);

        TransferLeafVerifier.PayloadHash(LeafId, TransferId, Cipher).Should().Equal(expected);
    }

    [Test]
    public void Accepts_a_legacy_compact_signature()
    {
        var transfer = MakeTransfer(Leaf(LegacySignature(compact: true)));

        var act = () => TransferLeafVerifier.Verify(transfer, Receiver);

        act.Should().NotThrow();
    }

    [Test]
    public void Accepts_a_legacy_der_signature()
    {
        var transfer = MakeTransfer(Leaf(LegacySignature(compact: false)));

        var act = () => TransferLeafVerifier.Verify(transfer, Receiver);

        act.Should().NotThrow();
    }

    [Test]
    public void Accepts_a_typed_strict_der_ecdsa_signature()
    {
        var leaf = Leaf(null);
        leaf.TypedSignature = new ProtoSignature
        {
            Scheme = ProtoSignatureScheme.Ecdsa,
            Signature_ = ByteString.CopyFrom(Sender.Sign(new uint256(Digest())).ToDER()),
        };

        var act = () => TransferLeafVerifier.Verify(MakeTransfer(leaf), Receiver);

        act.Should().NotThrow();
    }

    [Test]
    public void Rejects_a_typed_ecdsa_signature_that_is_not_strict_der()
    {
        var leaf = Leaf(null);
        leaf.TypedSignature = new ProtoSignature
        {
            Scheme = ProtoSignatureScheme.Ecdsa,
            Signature_ = ByteString.CopyFrom(Sender.Sign(new uint256(Digest())).ToCompact()),
        };

        var act = () => TransferLeafVerifier.Verify(MakeTransfer(leaf), Receiver);

        act.Should().Throw<SparkUntrustedResponseException>().WithMessage("*missing or invalid*");
    }

    [Test]
    public void Accepts_a_typed_schnorr_signature_over_the_x_only_key()
    {
        var schnorr = Sender.SignTaprootScriptSpend(new uint256(Digest()), TaprootSigHash.Default).SchnorrSignature.ToBytes();
        var leaf = Leaf(null);
        leaf.TypedSignature = new ProtoSignature
        {
            Scheme = ProtoSignatureScheme.Schnorr,
            Signature_ = ByteString.CopyFrom(schnorr),
        };

        var act = () => TransferLeafVerifier.Verify(MakeTransfer(leaf), Receiver);

        act.Should().NotThrow();
    }

    [Test]
    public void Rejects_an_unspecified_signature_scheme()
    {
        var leaf = Leaf(null);
        leaf.TypedSignature = new ProtoSignature
        {
            Scheme = ProtoSignatureScheme.Unspecified,
            Signature_ = ByteString.CopyFrom(Sender.Sign(new uint256(Digest())).ToDER()),
        };

        var act = () => TransferLeafVerifier.Verify(MakeTransfer(leaf), Receiver);

        act.Should().Throw<SparkUntrustedResponseException>();
    }

    [Test]
    public void Rejects_a_signature_from_another_key()
    {
        var other = new Key();
        var leaf = Leaf(ByteString.CopyFrom(other.Sign(new uint256(Digest())).ToCompact()));

        var act = () => TransferLeafVerifier.Verify(MakeTransfer(leaf), Receiver);

        act.Should().Throw<SparkUntrustedResponseException>().WithMessage("*missing or invalid*");
    }

    [Test]
    public void Rejects_a_tampered_secret_cipher()
    {
        var leaf = Leaf(LegacySignature(compact: true));
        leaf.SecretCipher = ByteString.CopyFrom([9, 9, 9]);

        var act = () => TransferLeafVerifier.Verify(MakeTransfer(leaf), Receiver);

        act.Should().Throw<SparkUntrustedResponseException>().WithMessage("*missing or invalid*");
    }

    [Test]
    public void Rejects_a_missing_signature_and_malformed_bytes()
    {
        var unsigned = () => TransferLeafVerifier.Verify(MakeTransfer(Leaf(null)), Receiver);
        var garbage = () => TransferLeafVerifier.Verify(MakeTransfer(Leaf(ByteString.CopyFrom([1, 2, 3]))), Receiver);

        unsigned.Should().Throw<SparkUntrustedResponseException>();
        garbage.Should().Throw<SparkUntrustedResponseException>();
    }

    [Test]
    public void Rejects_a_transfer_that_does_not_name_this_wallet_as_its_receiver_or_one_of_its_receivers()
    {
        var transfer = MakeTransfer(Leaf(LegacySignature(compact: true)));
        var elsewhere = () => TransferLeafVerifier.Verify(transfer, new Key().PubKey.ToBytes());
        elsewhere.Should().Throw<SparkUntrustedResponseException>().WithMessage("*not addressed*");

        transfer.ReceiverIdentityPublicKey = ByteString.Empty;
        var unset = () => TransferLeafVerifier.Verify(transfer, Receiver);
        unset.Should().Throw<SparkUntrustedResponseException>().WithMessage("*not addressed*");

        // The operators record only the lowest receiver key of a multi-receiver transfer.
        transfer.ReceiverIdentityPublicKey = ByteString.CopyFrom(new Key().PubKey.ToBytes());
        transfer.Receivers.Add(new TransferReceiver { Id = "edge-me", IdentityPublicKey = ByteString.CopyFrom(Receiver) });
        var listed = () => TransferLeafVerifier.Verify(transfer, Receiver);
        listed.Should().NotThrow();
    }

    [Test]
    public void A_multi_receiver_transfer_is_claimed_for_this_wallets_own_leaves_whichever_receiver_the_operators_recorded()
    {
        byte[] other = [0x03, .. Enumerable.Repeat((byte)0x44, 32)];
        static TransferReceiver Edge(string id, byte[] key, TransferReceiverStatus status = TransferReceiverStatus.KeyTweaked) =>
            new() { Id = id, IdentityPublicKey = ByteString.CopyFrom(key), Status = status };
        var mine = Leaf(LegacySignature(compact: true));
        mine.TransferReceiverId = "edge-me";
        var theirs = new TransferLeaf { Leaf = new TreeNode { Id = "leaf-2" }, SecretCipher = ByteString.CopyFrom(Cipher), TransferReceiverId = "edge-other" };
        var split = MakeTransfer(mine);
        split.Leaves.Add(theirs);
        split.ReceiverIdentityPublicKey = ByteString.CopyFrom(other);
        split.Receivers.Add(Edge("edge-other", other));
        split.Receivers.Add(Edge("edge-me", Receiver));

        var scoped = TransferLeafVerifier.Scoped(split, Receiver);
        scoped.Leaves.Select(l => l.Leaf.Id).Should().Equal(LeafId);
        var verify = () => TransferLeafVerifier.Verify(scoped, Receiver);
        verify.Should().NotThrow("the other receiver's leaf is not this wallet's to verify");

        // Not among the receivers, or no leaves on this wallet's edge.
        var stranger = () => TransferLeafVerifier.Scoped(split, SenderPubKey);
        stranger.Should().Throw<SparkUntrustedResponseException>();
        var unassigned = split.Clone();
        unassigned.Leaves.Remove(unassigned.Leaves.First(l => l.TransferReceiverId == "edge-me"));
        var nothing = () => TransferLeafVerifier.Scoped(unassigned, Receiver);
        nothing.Should().Throw<SparkUntrustedResponseException>();
        // A single-receiver transfer is not narrowed.
        var single = MakeTransfer(Leaf(LegacySignature(compact: true)));
        TransferLeafVerifier.Scoped(single, Receiver).Should().BeSameAs(single);

        // This wallet's leg completes with its own edge, before the whole transfer does.
        TransferLeafVerifier.IsReceiverLegComplete(split, Receiver).Should().BeFalse();
        var legDone = split.Clone();
        legDone.Receivers[1].Status = TransferReceiverStatus.Completed;
        TransferLeafVerifier.IsReceiverLegComplete(legDone, Receiver).Should().BeTrue();
        TransferLeafVerifier.IsReceiverLegComplete(legDone, other).Should().BeFalse();
        var whole = single.Clone();
        whole.Status = TransferStatus.Completed;
        TransferLeafVerifier.IsReceiverLegComplete(whole, Receiver).Should().BeTrue();
        TransferLeafVerifier.IsReceiverLegComplete(single, Receiver).Should().BeFalse();
    }

    [Test]
    public void Rejects_transfers_without_leaves_node_data_or_cipher()
    {
        var empty = new Transfer
        {
            Id = TransferId,
            SenderIdentityPublicKey = ByteString.CopyFrom(SenderPubKey),
            ReceiverIdentityPublicKey = ByteString.CopyFrom(Receiver),
        };
        var noNode = MakeTransfer(new TransferLeaf { SecretCipher = ByteString.CopyFrom(Cipher) });
        var noCipherLeaf = Leaf(LegacySignature(compact: true));
        noCipherLeaf.SecretCipher = ByteString.Empty;
        var noCipher = MakeTransfer(noCipherLeaf);

        ((Action)(() => TransferLeafVerifier.Verify(empty, Receiver))).Should().Throw<SparkUntrustedResponseException>().WithMessage("*no leaves*");
        ((Action)(() => TransferLeafVerifier.Verify(noNode, Receiver))).Should().Throw<SparkUntrustedResponseException>().WithMessage("*without node data*");
        ((Action)(() => TransferLeafVerifier.Verify(noCipher, Receiver))).Should().Throw<SparkUntrustedResponseException>().WithMessage("*no secret cipher*");
    }

    [Test]
    public void VerifyLegacySignature_never_throws_on_bad_keys_or_digests()
    {
        var sig = Sender.Sign(new uint256(Digest())).ToCompact();

        TransferLeafVerifier.VerifyLegacySignature(sig, new byte[31], SenderPubKey).Should().BeFalse();
        TransferLeafVerifier.VerifyLegacySignature(sig, Digest(), new byte[33]).Should().BeFalse();
        TransferLeafVerifier.VerifyLegacySignature(sig, Digest(), new byte[65]).Should().BeFalse();
        TransferLeafVerifier.VerifyLegacySignature([], Digest(), SenderPubKey).Should().BeFalse();
    }

    private static byte[] Digest() => TransferLeafVerifier.PayloadHash(LeafId, TransferId, Cipher);

    private static ByteString LegacySignature(bool compact)
    {
        var sig = Sender.Sign(new uint256(Digest()));
        return ByteString.CopyFrom(compact ? sig.ToCompact() : sig.ToDER());
    }

    private static TransferLeaf Leaf(ByteString? legacySignature)
    {
        var leaf = new TransferLeaf
        {
            Leaf = new TreeNode { Id = LeafId },
            SecretCipher = ByteString.CopyFrom(Cipher),
        };
        if (legacySignature is not null)
        {
            leaf.Signature = legacySignature;
        }
        return leaf;
    }

    private static Transfer MakeTransfer(TransferLeaf leaf)
    {
        var transfer = new Transfer
        {
            Id = TransferId,
            SenderIdentityPublicKey = ByteString.CopyFrom(SenderPubKey),
            ReceiverIdentityPublicKey = ByteString.CopyFrom(Receiver),
        };
        transfer.Leaves.Add(leaf);
        return transfer;
    }
}
