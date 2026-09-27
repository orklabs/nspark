using Google.Protobuf;
using NSpark.Exceptions;
using NSpark.Proto;
using NSpark.Proto.Token;

namespace NSpark.Services;

/// <summary>
/// Checks that the "final" token transaction the coordinator returns from
/// <c>start_transaction</c> is the transaction the wallet submitted, plus only the server-set
/// fields it is allowed to add (output ids, revocation commitments, withdraw bond and locktime,
/// expiry). Mirrors the reference SDK's <c>validateTokenTransaction</c>. Runs before the wallet
/// signs the final hash for every operator, so a coordinator cannot redirect or resize token
/// outputs.
/// </summary>
internal static class TokenTransactionValidator
{
    private const string Operation = "token.verify";

    /// <summary>What the wallet expects the coordinator to have preserved or set.</summary>
    /// <param name="OperatorIdentityPublicKeys">Operator identity keys the wallet placed in the partial transaction.</param>
    /// <param name="OperatorIdentifiers">Operator identifiers from the wallet configuration.</param>
    /// <param name="Threshold">Configured FROST signing threshold.</param>
    /// <param name="WithdrawBondSats">Withdraw bond every output must carry.</param>
    /// <param name="WithdrawRelativeBlockLocktime">Relative locktime every output must carry.</param>
    internal sealed record Expectations(
        IReadOnlyList<byte[]> OperatorIdentityPublicKeys,
        IReadOnlySet<string> OperatorIdentifiers,
        uint Threshold,
        ulong WithdrawBondSats,
        ulong WithdrawRelativeBlockLocktime);

    internal static void Validate(
        TokenTransaction final,
        TokenTransaction partial,
        SigningKeyshare? keyshareInfo,
        Expectations expectations)
    {
        ArgumentNullException.ThrowIfNull(final);
        ArgumentNullException.ThrowIfNull(partial);
        ArgumentNullException.ThrowIfNull(expectations);

        if (final.Version != partial.Version)
        {
            throw Fail("version changed");
        }
        if (final.Network != partial.Network)
        {
            throw Fail("network changed");
        }
        if (!final.InvoiceAttachments.Equals(partial.InvoiceAttachments))
        {
            throw Fail("invoice attachments changed");
        }
        // To the millisecond, the precision the transaction hash covers.
        if (final.ClientCreatedTimestamp is null
            || partial.ClientCreatedTimestamp is null
            || Milliseconds(final.ClientCreatedTimestamp) != Milliseconds(partial.ClientCreatedTimestamp))
        {
            throw Fail("client created timestamp changed");
        }

        var expectedKeys = new HashSet<ByteString>(expectations.OperatorIdentityPublicKeys.Select(ByteString.CopyFrom));
        if (final.SparkOperatorIdentityPublicKeys.Count != expectations.OperatorIdentityPublicKeys.Count
            || !expectedKeys.SetEquals(final.SparkOperatorIdentityPublicKeys)
            || !expectedKeys.SetEquals(partial.SparkOperatorIdentityPublicKeys))
        {
            throw Fail("operator identity public keys changed");
        }

        ValidateInputs(final, partial);
        ValidateOutputs(final, partial, expectations);

        // The reference SDK refuses a start response without keyshare info: the revocation
        // keyshare is what makes the outputs recoverable, so it must name the configured operators.
        if (keyshareInfo is null)
        {
            throw Fail("keyshare info missing from the start response");
        }
        ValidateKeyshare(keyshareInfo, expectations);
    }

    private static void ValidateInputs(TokenTransaction final, TokenTransaction partial)
    {
        if (final.TokenInputsCase != partial.TokenInputsCase)
        {
            throw Fail($"transaction type changed ({final.TokenInputsCase} vs {partial.TokenInputsCase})");
        }

        switch (partial.TokenInputsCase)
        {
            case TokenTransaction.TokenInputsOneofCase.MintInput:
            {
                var f = final.MintInput;
                var p = partial.MintInput;
                if (f.IssuerPublicKey.IsEmpty || !f.IssuerPublicKey.Equals(p.IssuerPublicKey))
                {
                    throw Fail("mint issuer changed");
                }
                if (!f.HasTokenIdentifier || !p.HasTokenIdentifier || !f.TokenIdentifier.Equals(p.TokenIdentifier))
                {
                    throw Fail("mint token identifier changed");
                }
                break;
            }
            case TokenTransaction.TokenInputsOneofCase.CreateInput:
            {
                var f = final.CreateInput;
                var p = partial.CreateInput;
                if (f.IssuerPublicKey.IsEmpty || !f.IssuerPublicKey.Equals(p.IssuerPublicKey))
                {
                    throw Fail("create issuer changed");
                }
                if (f.TokenName != p.TokenName || f.TokenTicker != p.TokenTicker || f.Decimals != p.Decimals
                    || !f.MaxSupply.Equals(p.MaxSupply) || f.IsFreezable != p.IsFreezable)
                {
                    throw Fail("token creation parameters changed");
                }
                if (f.HasExtraMetadata != p.HasExtraMetadata
                    || (f.HasExtraMetadata && !f.ExtraMetadata.Equals(p.ExtraMetadata)))
                {
                    throw Fail("token extra metadata changed");
                }
                break;
            }
            case TokenTransaction.TokenInputsOneofCase.TransferInput:
            {
                var f = final.TransferInput.OutputsToSpend;
                var p = partial.TransferInput.OutputsToSpend;
                if (p.Count == 0 || f.Count != p.Count)
                {
                    throw Fail($"outputs to spend count changed ({f.Count} vs {p.Count})");
                }
                for (int i = 0; i < p.Count; i++)
                {
                    if (!f[i].PrevTokenTransactionHash.Equals(p[i].PrevTokenTransactionHash)
                        || f[i].PrevTokenTransactionVout != p[i].PrevTokenTransactionVout)
                    {
                        throw Fail($"input {i} changed");
                    }
                }
                break;
            }
            default:
                throw Fail("transaction type missing");
        }
    }

    private static void ValidateOutputs(TokenTransaction final, TokenTransaction partial, Expectations expectations)
    {
        if (final.TokenOutputs.Count != partial.TokenOutputs.Count)
        {
            throw Fail($"output count changed ({final.TokenOutputs.Count} vs {partial.TokenOutputs.Count})");
        }

        for (int i = 0; i < partial.TokenOutputs.Count; i++)
        {
            var f = final.TokenOutputs[i];
            var p = partial.TokenOutputs[i];

            if (!f.OwnerPublicKey.Equals(p.OwnerPublicKey))
            {
                throw Fail($"output {i} owner changed");
            }
            if (!f.TokenAmount.Equals(p.TokenAmount))
            {
                throw Fail($"output {i} amount changed");
            }
            if (p.HasTokenIdentifier && (!f.HasTokenIdentifier || !f.TokenIdentifier.Equals(p.TokenIdentifier)))
            {
                throw Fail($"output {i} token identifier changed");
            }
            if (f.HasTokenPublicKey && p.HasTokenPublicKey && !f.TokenPublicKey.Equals(p.TokenPublicKey))
            {
                throw Fail($"output {i} token public key changed");
            }
            if (f.HasWithdrawBondSats && f.WithdrawBondSats != expectations.WithdrawBondSats)
            {
                throw Fail($"output {i} withdraw bond {f.WithdrawBondSats} differs from the expected {expectations.WithdrawBondSats}");
            }
            if (f.HasWithdrawRelativeBlockLocktime
                && f.WithdrawRelativeBlockLocktime != expectations.WithdrawRelativeBlockLocktime)
            {
                throw Fail($"output {i} withdraw locktime {f.WithdrawRelativeBlockLocktime} differs from the expected {expectations.WithdrawRelativeBlockLocktime}");
            }
        }
    }

    private static void ValidateKeyshare(SigningKeyshare keyshareInfo, Expectations expectations)
    {
        if (keyshareInfo.Threshold != expectations.Threshold)
        {
            throw Fail($"keyshare threshold {keyshareInfo.Threshold} differs from the configured {expectations.Threshold}");
        }
        if (keyshareInfo.OwnerIdentifiers.Count != expectations.OperatorIdentifiers.Count)
        {
            throw Fail($"keyshare operator count {keyshareInfo.OwnerIdentifiers.Count} differs from the configured {expectations.OperatorIdentifiers.Count}");
        }
        if (keyshareInfo.OwnerIdentifiers.Distinct(StringComparer.Ordinal).Count() != keyshareInfo.OwnerIdentifiers.Count)
        {
            throw Fail("duplicate keyshare owner identifiers");
        }
        foreach (var identifier in keyshareInfo.OwnerIdentifiers)
        {
            if (!expectations.OperatorIdentifiers.Contains(identifier))
            {
                throw Fail($"keyshare owner {identifier} is not a configured operator");
            }
        }
    }

    /// <summary>
    /// Checks that the final transaction the coordinator answers <c>broadcast_transaction</c> with
    /// is the V3 partial transaction the wallet signed, plus only what the operators add: a
    /// revocation commitment per output, and a create's creation entity key. The wallet signs only
    /// the partial transaction, whose hash already binds the inputs, outputs and amounts; this
    /// makes sure the hash the SDK reports is of that transaction. Fields are compared by their
    /// protohash, which is what the transaction's hash covers.
    /// </summary>
    internal static void ValidateV3(FinalTokenTransaction final, PartialTokenTransaction partial)
    {
        ArgumentNullException.ThrowIfNull(final);
        ArgumentNullException.ThrowIfNull(partial);

        static bool Same(Google.Protobuf.IMessage lhs, Google.Protobuf.IMessage rhs) =>
            ProtoHash.Hash(lhs).AsSpan().SequenceEqual(ProtoHash.Hash(rhs));

        if (final.Version != partial.Version)
        {
            throw Fail("version changed");
        }
        if (final.TokenTransactionMetadata is null
            || partial.TokenTransactionMetadata is null
            || !Same(final.TokenTransactionMetadata, partial.TokenTransactionMetadata))
        {
            throw Fail("metadata changed");
        }
        if (!Equals(final.ExecuteBefore, partial.ExecuteBefore))
        {
            throw Fail("execute-before changed");
        }

        switch ((final.TokenInputsCase, partial.TokenInputsCase))
        {
            case (FinalTokenTransaction.TokenInputsOneofCase.TransferInput, PartialTokenTransaction.TokenInputsOneofCase.TransferInput):
                if (!Same(final.TransferInput, partial.TransferInput))
                {
                    throw Fail("inputs changed");
                }
                break;
            case (FinalTokenTransaction.TokenInputsOneofCase.MintInput, PartialTokenTransaction.TokenInputsOneofCase.MintInput):
                if (!Same(final.MintInput, partial.MintInput))
                {
                    throw Fail("mint input changed");
                }
                break;
            case (FinalTokenTransaction.TokenInputsOneofCase.CreateInput, PartialTokenTransaction.TokenInputsOneofCase.CreateInput):
                var answered = final.CreateInput.Clone();
                answered.ClearCreationEntityPublicKey(); // set by the operators
                if (!Same(answered, partial.CreateInput))
                {
                    throw Fail("create input changed");
                }
                break;
            default:
                throw Fail("transaction type changed or missing");
        }

        if (final.FinalTokenOutputs.Count != partial.PartialTokenOutputs.Count)
        {
            throw Fail($"output count changed ({final.FinalTokenOutputs.Count} vs {partial.PartialTokenOutputs.Count})");
        }

        for (var i = 0; i < partial.PartialTokenOutputs.Count; i++)
        {
            var answered = final.FinalTokenOutputs[i];
            if (answered.PartialTokenOutput is null || !Same(answered.PartialTokenOutput, partial.PartialTokenOutputs[i]))
            {
                throw Fail($"output {i} changed");
            }
            if (answered.RevocationCommitment.Length != 33)
            {
                throw Fail($"output {i} has no revocation commitment");
            }
        }
    }

    private static long Milliseconds(Google.Protobuf.WellKnownTypes.Timestamp timestamp) =>
        (timestamp.Seconds * 1_000) + (timestamp.Nanos / 1_000_000);

    private static SparkUntrustedResponseException Fail(string what) =>
        new(Operation, $"The coordinator's final token transaction was rejected: {what}.");
}
