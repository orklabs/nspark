using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using NBitcoin;
using NSpark.Exceptions;
using NSpark.Proto;
using NSpark.Services;
using NSpark.Signer;
using NSpark.UnitTests.TestSupport;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Deposit addresses are checked before the wallet hands them out, as the reference SDK's
/// <c>validateDepositAddress</c> does.
/// </summary>
[TestFixture]
public class DepositAddressVerificationTests
{
    /// <summary>
    /// A deposit address as the operators produce it (<c>deposit_handler.go</c>), with keys the test
    /// controls: the verifying key is the wallet's signing key plus the operators' share, the
    /// address pays it, the proof of possession is the operators' share (BIP-86 tweaked) signing
    /// the tagged hash of identity key, operator key and address, and every operator signs
    /// sha256(address).
    /// </summary>
    private sealed class SyntheticDeposit
    {
        public byte[] UserSigningPublicKey { get; } = new Key().PubKey.ToBytes();

        public byte[] IdentityPublicKey { get; } = new Key().PubKey.ToBytes();

        public Key OperatorShare { get; } = new();

        public (SigningOperatorConfig Config, Key Key)[] Operators { get; } = Enumerable.Range(1, 3).Select(index =>
        {
            var key = new Key();
            return (new SigningOperatorConfig(
                $"https://{index}.example",
                new string('0', 63) + index,
                Convert.ToHexString(key.PubKey.ToBytes())), key);
        }).ToArray();

        public string CoordinatorIdentifier => Operators[0].Config.Identifier;

        public byte[] VerifyingKey => Secp256k1Points.Add(UserSigningPublicKey, OperatorShare.PubKey.ToBytes())!;

        /// <summary>BIP-340 signature by the BIP-86 tweak of the operators' share.</summary>
        public byte[] ProofOfPossession(byte[] identity, string address)
        {
            var message = SparkTaggedHash.Create("spark", "deposit", "proof_of_possession")
                .AddBytes(identity)
                .AddBytes(OperatorShare.PubKey.ToBytes())
                .AddBytes(Encoding.UTF8.GetBytes(address))
                .Hash();
            return OperatorShare.SignTaprootKeySpend(new uint256(message), null, TaprootSigHash.Default).SchnorrSignature.ToBytes();
        }

        public static byte[] AddressSignature(Key key, string address) =>
            key.Sign(new uint256(SHA256.HashData(Encoding.UTF8.GetBytes(address)))).ToDER();

        public Address Address(SparkNetwork network = SparkNetwork.Mainnet, bool withCoordinatorSignature = true)
        {
            var addressString = RenewalService.LeafNodeAddress(VerifyingKey, network);
            var address = new Address
            {
                Address_ = addressString,
                VerifyingKey = ByteString.CopyFrom(VerifyingKey),
                DepositAddressProof = new DepositAddressProof
                {
                    ProofOfPossessionSignature = ByteString.CopyFrom(ProofOfPossession(IdentityPublicKey, addressString)),
                },
            };
            foreach (var (config, key) in Operators)
            {
                if (withCoordinatorSignature || config.Identifier != CoordinatorIdentifier)
                {
                    address.DepositAddressProof.AddressSignatures[config.Identifier] = ByteString.CopyFrom(AddressSignature(key, addressString));
                }
            }

            return address;
        }

        public void Verify(Address address, bool isStatic, SparkNetwork network = SparkNetwork.Mainnet) =>
            DepositAddressVerifier.Verify(
                address,
                UserSigningPublicKey,
                IdentityPublicKey,
                isStatic,
                new SparkOptions { Network = network, SigningOperators = Operators.Select(o => o.Config).ToArray() });
    }

    [Test]
    public void An_address_with_a_valid_proof_of_possession_and_operator_signatures_is_accepted()
    {
        var deposit = new SyntheticDeposit();

        deposit.Verify(deposit.Address(), isStatic: false);
        deposit.Verify(deposit.Address(), isStatic: true);
        deposit.Verify(deposit.Address(SparkNetwork.Regtest), isStatic: false, SparkNetwork.Regtest);
    }

    [Test]
    public void The_coordinators_own_signature_is_required_for_static_addresses_only()
    {
        var deposit = new SyntheticDeposit();
        var withoutCoordinator = deposit.Address(withCoordinatorSignature: false);

        deposit.Verify(withoutCoordinator, isStatic: false);
        var asStatic = () => deposit.Verify(withoutCoordinator, isStatic: true);
        asStatic.Should().Throw<SparkUntrustedResponseException>().WithMessage("*signature from operator*");
    }

    [Test]
    public void A_missing_or_forged_operator_signature_is_refused()
    {
        var deposit = new SyntheticDeposit();

        var missing = deposit.Address();
        missing.DepositAddressProof.AddressSignatures.Remove(deposit.Operators[1].Config.Identifier);
        ((Action)(() => deposit.Verify(missing, isStatic: false))).Should().Throw<SparkUntrustedResponseException>();

        var forged = deposit.Address();
        forged.DepositAddressProof.AddressSignatures[deposit.Operators[2].Config.Identifier] =
            ByteString.CopyFrom(SyntheticDeposit.AddressSignature(new Key(), forged.Address_));
        ((Action)(() => deposit.Verify(forged, isStatic: false))).Should().Throw<SparkUntrustedResponseException>();

        var garbage = deposit.Address();
        garbage.DepositAddressProof.AddressSignatures[deposit.Operators[2].Config.Identifier] = ByteString.CopyFrom(1, 2, 3);
        ((Action)(() => deposit.Verify(garbage, isStatic: false))).Should().Throw<SparkUntrustedResponseException>();
    }

    [Test]
    public void A_proof_of_possession_that_is_tampered_with_or_made_for_other_data_is_refused()
    {
        var deposit = new SyntheticDeposit();

        var tampered = deposit.Address();
        var proof = tampered.DepositAddressProof.ProofOfPossessionSignature.ToByteArray();
        proof[10] ^= 0x01;
        tampered.DepositAddressProof.ProofOfPossessionSignature = ByteString.CopyFrom(proof);
        ((Action)(() => deposit.Verify(tampered, isStatic: false))).Should().Throw<SparkUntrustedResponseException>().WithMessage("*proof of possession*");

        var otherIdentity = deposit.Address();
        otherIdentity.DepositAddressProof.ProofOfPossessionSignature =
            ByteString.CopyFrom(deposit.ProofOfPossession(new Key().PubKey.ToBytes(), otherIdentity.Address_));
        ((Action)(() => deposit.Verify(otherIdentity, isStatic: false))).Should().Throw<SparkUntrustedResponseException>();

        var missing = deposit.Address();
        missing.DepositAddressProof = null;
        ((Action)(() => deposit.Verify(missing, isStatic: false))).Should().Throw<SparkUntrustedResponseException>();
    }

    [Test]
    public void An_address_that_does_not_pay_the_verifying_key_is_refused_even_with_genuine_signatures_over_it()
    {
        var deposit = new SyntheticDeposit();

        // A coordinator's own address, signed by every operator key the test holds.
        var swapped = deposit.Address();
        var foreign = RenewalService.LeafNodeAddress(new Key().PubKey.ToBytes(), SparkNetwork.Mainnet);
        swapped.Address_ = foreign;
        swapped.DepositAddressProof.ProofOfPossessionSignature = ByteString.CopyFrom(deposit.ProofOfPossession(deposit.IdentityPublicKey, foreign));
        foreach (var (config, key) in deposit.Operators)
        {
            swapped.DepositAddressProof.AddressSignatures[config.Identifier] = ByteString.CopyFrom(SyntheticDeposit.AddressSignature(key, foreign));
        }

        ((Action)(() => deposit.Verify(swapped, isStatic: true))).Should().Throw<SparkUntrustedResponseException>().WithMessage("*does not pay*");
        // An address for another network is refused too.
        ((Action)(() => deposit.Verify(deposit.Address(SparkNetwork.Regtest), isStatic: false))).Should().Throw<SparkUntrustedResponseException>();
    }

    [Test]
    public void Degenerate_keys_are_refused_without_crashing()
    {
        var key = new Key().PubKey.ToBytes();

        ((Action)(() => DepositAddressVerifier.SubtractPublicKeys(key, key))).Should().Throw<SparkUntrustedResponseException>();
        ((Action)(() => DepositAddressVerifier.SubtractPublicKeys([0x02], key))).Should().Throw<SparkUntrustedResponseException>();
        DepositAddressVerifier.VerifySchnorr(new byte[64], new byte[32], key).Should().BeFalse();
        DepositAddressVerifier.VerifySchnorr(new byte[10], new byte[32], key).Should().BeFalse();
        DepositAddressVerifier.VerifySchnorr(new byte[64], new byte[32], [0x02]).Should().BeFalse();
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_coordinator_handing_out_an_address_without_a_proof_is_refused_before_the_wallet_shows_it(CancellationToken ct)
    {
        var unproven = new Address
        {
            Address_ = "bcrt1p5cyxnuxmeuwuvkwfem96lqzszd02n6xdcjrs20cac6yqjjwudpxqkedrcr",
            VerifyingKey = ByteString.CopyFrom(Convert.FromHexString("02cc8a4bc64d897bddc5fbc2f670f7a8ba0b386779106cf1223c6fc5d7cd6fc115")),
        };
        var state = new FakeOperatorState(_ => false, depositAddress: unproven);

        await FakeOperator.RunAsync(state, async wallet =>
        {
            await ((Func<Task>)(() => wallet.GetDepositAddressAsync(ct))).Should().ThrowAsync<SparkUntrustedResponseException>();
            await ((Func<Task>)(() => wallet.GetStaticDepositAddressAsync(ct))).Should().ThrowAsync<SparkUntrustedResponseException>();
        });

        state.Methods.Should().Equal("generate_deposit_address", "generate_static_deposit_address");
    }
}
