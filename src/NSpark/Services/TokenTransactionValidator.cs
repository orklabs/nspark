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
        if (!final.ClientCreatedTimestamp.Equals(partial.ClientCreatedTimestamp))
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

    private static SparkUntrustedResponseException Fail(string what) =>
        new(Operation, $"The coordinator's final token transaction was rejected: {what}.");
}
