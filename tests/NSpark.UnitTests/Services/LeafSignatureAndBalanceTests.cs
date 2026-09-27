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
/// The reference SDK's <c>verifyTypedSignature</c> tests (<c>signature.test.ts</c>), with its keys
/// and digest. Its two cases where both a legacy and a typed signature are present cannot arise
/// here: the generated oneof holds one or the other.
/// </summary>
[TestFixture]
public class LeafSignatureTests
{
    private const string SignerHex = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string OtherHex = "0000000000000000000000000000000000000000000000000000000000000007";
    private static readonly byte[] s_digest = SHA256.HashData(Encoding.UTF8.GetBytes("leaf-id:transfer-id:cipher"));

    private static Key PrivateKey(string hex) => new(Convert.FromHexString(hex));

    private static byte[] PublicKey(string hex = SignerHex) => PrivateKey(hex).PubKey.ToBytes();

    private static NBitcoin.Crypto.ECDSASignature Ecdsa(byte[]? digest = null, string key = SignerHex) =>
        PrivateKey(key).Sign(new uint256(digest ?? s_digest));

    private static byte[] Schnorr(byte[]? digest = null, string key = SignerHex) =>
        PrivateKey(key).SignTaprootScriptSpend(new uint256(digest ?? s_digest), TaprootSigHash.Default).SchnorrSignature.ToBytes();

    private static TransferLeaf Legacy(byte[] signature) => new() { Signature = ByteString.CopyFrom(signature) };

    private static TransferLeaf Typed(ProtoSignatureScheme scheme, byte[] signature) =>
        new() { TypedSignature = new ProtoSignature { Scheme = scheme, Signature_ = ByteString.CopyFrom(signature) } };

    private static bool Verify(TransferLeaf leaf, byte[]? digest = null, byte[]? key = null) =>
        TransferLeafVerifier.VerifyLeafSignature(leaf, digest ?? s_digest, key ?? PublicKey());

    [Test]
    public void Legacy_ECDSA_signatures_verify_in_compact_and_DER_encoding_and_only_for_the_signers_key()
    {
        Verify(Legacy(Ecdsa().ToCompact())).Should().BeTrue();
        Verify(Legacy(Ecdsa().ToDER())).Should().BeTrue();
        Verify(Legacy(Ecdsa(key: OtherHex).ToCompact())).Should().BeFalse();
    }

    [Test]
    public void A_typed_ECDSA_signature_must_be_strict_DER()
    {
        Verify(Typed(ProtoSignatureScheme.Ecdsa, Ecdsa().ToDER())).Should().BeTrue();
        Verify(Typed(ProtoSignatureScheme.Ecdsa, Ecdsa().ToCompact())).Should().BeFalse();
    }

    [Test]
    public void A_typed_Schnorr_signature_verifies_against_the_compressed_keys_x_only_form()
    {
        Verify(Typed(ProtoSignatureScheme.Schnorr, Schnorr())).Should().BeTrue();
        Verify(Typed(ProtoSignatureScheme.Schnorr, Schnorr()), key: PublicKey(OtherHex)).Should().BeFalse();
    }

    [Test]
    public void A_signature_labelled_with_the_other_scheme_is_refused()
    {
        Verify(Typed(ProtoSignatureScheme.Schnorr, Ecdsa().ToCompact())).Should().BeFalse();
        Verify(Typed(ProtoSignatureScheme.Ecdsa, Schnorr())).Should().BeFalse();
    }

    [Test]
    public void An_unspecified_or_unrecognised_scheme_or_no_signature_at_all_is_refused()
    {
        Verify(Typed(ProtoSignatureScheme.Unspecified, Ecdsa().ToDER())).Should().BeFalse();
        Verify(Typed((ProtoSignatureScheme)9, Ecdsa().ToDER())).Should().BeFalse();
        Verify(new TransferLeaf()).Should().BeFalse();
        Verify(Typed(ProtoSignatureScheme.Ecdsa, [])).Should().BeFalse();
        Verify(Legacy([])).Should().BeFalse();
    }

    [Test]
    public void A_valid_signature_over_another_digest_or_malformed_bytes_is_refused_without_throwing()
    {
        var other = SHA256.HashData(Encoding.UTF8.GetBytes("something else"));

        Verify(Legacy(Ecdsa(other).ToCompact())).Should().BeFalse();
        Verify(Typed(ProtoSignatureScheme.Schnorr, Schnorr(other))).Should().BeFalse();
        Verify(Legacy([1, 2, 3])).Should().BeFalse();
        Verify(Typed(ProtoSignatureScheme.Ecdsa, [0x30, 0x02, 0x01])).Should().BeFalse();
        Verify(Typed(ProtoSignatureScheme.Schnorr, Enumerable.Repeat((byte)0xFF, 64).ToArray())).Should().BeFalse();
    }

    [Test]
    public void Only_a_33_byte_compressed_key_and_a_32_byte_digest_are_accepted_on_every_path()
    {
        var uncompressed = PrivateKey(SignerHex).PubKey.Decompress().ToBytes();
        uncompressed.Should().HaveCount(65);

        Verify(Typed(ProtoSignatureScheme.Ecdsa, Ecdsa().ToDER()), key: uncompressed).Should().BeFalse();
        Verify(Legacy(Ecdsa().ToCompact()), key: uncompressed).Should().BeFalse();
        Verify(Typed(ProtoSignatureScheme.Schnorr, Schnorr()), key: PublicKey()[1..]).Should().BeFalse();
        var badPrefix = PublicKey();
        badPrefix[0] = 0x05;
        Verify(Typed(ProtoSignatureScheme.Schnorr, Schnorr()), key: badPrefix).Should().BeFalse();
        // A 33-byte digest whose first 32 bytes the signature covers is still refused.
        Verify(Legacy(Ecdsa().ToCompact()), digest: [.. s_digest, 0]).Should().BeFalse();
    }

    [Test]
    public void A_transfer_whose_leaves_carry_typed_signatures_is_verified_before_it_is_claimed()
    {
        var receiver = new Key().PubKey.ToBytes();
        var transfer = new Transfer
        {
            Id = "0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b",
            SenderIdentityPublicKey = ByteString.CopyFrom(PublicKey()),
            ReceiverIdentityPublicKey = ByteString.CopyFrom(receiver),
        };
        var cipher = Enumerable.Repeat((byte)7, 40).ToArray();
        var digest = TransferLeafVerifier.PayloadHash("leaf-a", transfer.Id, cipher);
        var leaf = Typed(ProtoSignatureScheme.Schnorr, Schnorr(digest));
        leaf.Leaf = new TreeNode { Id = "leaf-a" };
        leaf.SecretCipher = ByteString.CopyFrom(cipher);
        transfer.Leaves.Add(leaf);

        ((Action)(() => TransferLeafVerifier.Verify(transfer, receiver))).Should().NotThrow();

        leaf.TypedSignature.Signature_ = ByteString.CopyFrom(Schnorr(digest, OtherHex));
        ((Action)(() => TransferLeafVerifier.Verify(transfer, receiver))).Should().Throw<SparkUntrustedResponseException>();
    }
}

/// <summary>The balance figures, modelled on the reference SDK's leaf manager.</summary>
[TestFixture]
public class BalanceSummaryTests
{
    private static byte[] Tx(uint sequence) => TestTransactions.Tx((1u << 30) | sequence, new NSpark.Bitcoin.RawTransaction.Output(1_000, TestTransactions.P2trScript(0x01)));

    private static TreeNode TreeNode(string id, string status, ulong value, uint refundTimelock) => new()
    {
        Id = id,
        Status = status,
        Value = value,
        NodeTx = ByteString.CopyFrom(Tx(0)),
        RefundTx = ByteString.CopyFrom(Tx(refundTimelock)),
    };

    private static Transfer Transfer(string id, params (string LeafId, ulong Value)[] leaves)
    {
        var transfer = new Transfer { Id = id };
        transfer.Leaves.AddRange(leaves.Select(l => new TransferLeaf { Leaf = new TreeNode { Id = l.LeafId, Value = l.Value } }));
        return transfer;
    }

    [Test]
    public void Leaves_below_the_renewal_minimum_are_frozen_renewable_leaves_count_as_available_and_other_statuses_are_ignored()
    {
        var nodes = new Dictionary<string, TreeNode>
        {
            ["a"] = TreeNode("a", "AVAILABLE", 8192, 1600),
            ["b"] = TreeNode("b", "AVAILABLE", 32, 0),
            ["c"] = TreeNode("c", "AVAILABLE", 2, 100),
            ["d"] = TreeNode("d", "TRANSFER_LOCKED", 500, 2000),
            ["e"] = TreeNode("e", "CREATING", 700, 2000),
            // A renewal split node: permanently SPLIT_LOCKED, still carrying the owner key.
            ["f"] = TreeNode("f", "SPLIT_LOCKED", 9, 2000),
            ["g"] = TreeNode("g", "AVAILABLE", 64, 200),
            ["h"] = TreeNode("h", "AVAILABLE", 16, 150),
            ["i"] = TreeNode("i", "AVAILABLE", 4, 99),
        };

        var summary = BalanceService.SummarizeNodes(nodes);

        // 100 and 150 are renewable (the coordinator renews refund timelocks from 100), so they are
        // available; 0 and 99 are below the renewal minimum and frozen.
        summary.Available.Should().Be(8192 + 2 + 64 + 16);
        summary.Frozen.Should().Be(32 + 4);
        summary.Leaves.Select(l => l.Id).Should().BeEquivalentTo(["a", "b", "c", "g", "h", "i"]);
        var empty = BalanceService.SummarizeNodes([]);
        empty.Available.Should().Be(0);
        empty.Frozen.Should().Be(0);
        empty.Leaves.Should().BeEmpty();
    }

    [Test]
    public void In_flight_sats_count_each_leaf_once_and_never_a_leaf_that_is_already_available()
    {
        var transfers = new[]
        {
            Transfer("outgoing", ("l1", 500), ("l2", 20)),
            // A self-transfer, or a counter-swap leaf mid-claim, shows up in two queries.
            Transfer("counter", ("l2", 20), ("l3", 8)),
            Transfer("claimed", ("available-leaf", 64)),
        };

        BalanceService.LeafSats(transfers, new HashSet<string> { "available-leaf" }).Should().Be(500 + 20 + 8);
        BalanceService.LeafSats([], new HashSet<string>()).Should().Be(0);
        var withoutNode = new Transfer();
        withoutNode.Leaves.Add(new TransferLeaf());
        BalanceService.LeafSats([withoutNode], new HashSet<string>()).Should().Be(0);
    }

    [Test]
    public void Amounts_an_operator_reports_above_the_bitcoin_supply_are_capped_instead_of_overflowing()
    {
        const long maxSupply = TransferMapping.MaxSupplySats;
        TransferMapping.ReportedSats(0).Should().Be(0);
        TransferMapping.ReportedSats(12_345).Should().Be(12_345);
        TransferMapping.ReportedSats((ulong)maxSupply).Should().Be(maxSupply);
        TransferMapping.ReportedSats((ulong)long.MaxValue + 1).Should().Be(maxSupply);
        TransferMapping.ReportedSats(ulong.MaxValue).Should().Be(maxSupply);
        TransferMapping.ToModel(new Transfer { Id = "hostile", TotalValue = ulong.MaxValue }).TotalValueSats.Should().Be(maxSupply);

        // Two such leaves still add up without overflowing.
        var summary = BalanceService.SummarizeNodes(new Dictionary<string, TreeNode>
        {
            ["x"] = TreeNode("x", "AVAILABLE", ulong.MaxValue, 2000),
            ["y"] = TreeNode("y", "AVAILABLE", ulong.MaxValue, 50),
        });
        summary.Available.Should().Be(maxSupply);
        summary.Frozen.Should().Be(maxSupply);
        BalanceService.LeafSats([Transfer("t", ("l1", ulong.MaxValue), ("l2", ulong.MaxValue))], new HashSet<string>())
            .Should().Be(2 * maxSupply);
    }

    [Test]
    public void Incoming_leaves_out_counter_transfers_of_the_wallets_own_swaps_and_leaves_counted_elsewhere()
    {
        var counterSwap = Transfer("counter", ("c1", 512));
        counterSwap.Type = TransferType.CounterSwapV3;
        var legacyCounterSwap = Transfer("legacy-counter", ("c2", 256));
        legacyCounterSwap.Type = TransferType.CounterSwap;
        var payment = Transfer("lightning", ("p1", 1_000), ("p2", 24));
        payment.Type = TransferType.PreimageSwap;
        var selfTransfer = Transfer("self", ("s1", 7));
        selfTransfer.Type = TransferType.Transfer;
        var pending = new[] { counterSwap, legacyCounterSwap, payment, selfTransfer };
        byte[] me = [0x02, .. Enumerable.Repeat((byte)0x33, 32)];

        // The self-transfer's leaf is already counted as outgoing.
        BalanceService.IncomingSats(pending, new HashSet<string> { "s1" }, me).Should().Be(1_000 + 24);
        BalanceService.IncomingSats(pending, new HashSet<string>(), me).Should().Be(1_000 + 24 + 7);

        // A multi-receiver payment counts only this wallet's leaves.
        var split = Transfer("split", ("m1", 300), ("m2", 200), ("m3", 100));
        split.Type = TransferType.Transfer;
        split.Receivers.Add(new TransferReceiver { Id = "edge-me", IdentityPublicKey = ByteString.CopyFrom(me) });
        split.Receivers.Add(new TransferReceiver { Id = "edge-other", IdentityPublicKey = ByteString.CopyFrom([0x03, .. Enumerable.Repeat((byte)0x44, 32)]) });
        split.Leaves[0].TransferReceiverId = "edge-me";
        split.Leaves[1].TransferReceiverId = "edge-other";
        split.Leaves[2].TransferReceiverId = "edge-me";
        BalanceService.IncomingSats([split], new HashSet<string>(), me).Should().Be(300 + 100);
    }

    [Test]
    public void In_flight_transfers_are_queried_with_the_reference_SDKs_types_and_statuses()
    {
        // transfer.ts SENDER_PENDING_STATUSES: before the sender key tweak is applied.
        BalanceService.SenderPendingStatuses.Should().Equal(
            TransferStatus.SenderInitiated, TransferStatus.SenderInitiatedCoordinator,
            TransferStatus.ApplyingSenderKeyTweak, TransferStatus.SenderKeyTweakPending);
        // ACTIVE_COUNTER_SWAP_STATUSES: the whole counter-transfer lifecycle until completion.
        BalanceService.ActiveCounterSwapStatuses.Should().Equal(
        [
            .. BalanceService.SenderPendingStatuses,
            TransferStatus.SenderKeyTweaked, TransferStatus.ReceiverKeyTweakLocked, TransferStatus.ReceiverKeyTweakApplied,
            TransferStatus.ReceiverKeyTweaked, TransferStatus.ReceiverRefundSigned,
        ]);
        BalanceService.ActiveCounterSwapStatuses.Should().NotContain(TransferStatus.Completed);
        BalanceService.OutgoingTransferTypes.Should().Equal(TransferType.CooperativeExit, TransferType.UtxoSwap, TransferType.PreimageSwap, TransferType.Transfer);
        BalanceService.PrimarySwapTypes.Should().Equal(TransferType.PrimarySwapV3, TransferType.Swap);
        BalanceService.CounterSwapTypes.Should().Equal(TransferType.CounterSwapV3, TransferType.CounterSwap);
    }
}
