# Changelog

All notable changes to **NSpark** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The public API contract is enforced by `PublicAPI.Shipped.txt` files in the
source tree; any change to that file is, by definition, a breaking change and
will be reflected here.

## [Unreleased]

## [0.2.0-alpha.4] - 2026-09-15

Ports the Swift SDK 0.2.0 / 0.2.1 hardening (September 2026), re-verifies each flow
against the reference TypeScript SDK (`buildonspark/spark`, August 2026), and was
exercised end to end on mainnet with the integration wallets: Lightning send and
receive, Spark transfers and claims, leaf renewal and consolidation, and a
cooperative exit that confirmed on-chain.

### Security
- `WithdrawAsync` verifies the SSP's cooperative-exit response before signing: the
  raw exit transaction must hash to the reported txid, pay the destination at least
  `amount - fee`, and the connector transaction must spend it and carry one output
  per leaf. A response that fails throws `SparkUntrustedResponseException` and no
  leaf is handed over. Fees are bounded by a new optional `maxFeeSats` (default: the
  SSP's own quote); a quote above the cap throws `FeeExceedsLimitException`. Mirrors
  the reference SDK's `validateCoopExitPayoutTransaction` and
  `validateConnectorTxBindsToCoopExitTxid`.
- `PayLightningInvoiceAsync` requires `maxFeeSats` and refuses a higher SSP
  estimate before any leaf is touched. Invoices are decoded by a BOLT-11 parser that
  verifies the bech32 checksum, enforces the wallet's network, rounds sub-sat
  amounts up, and rejects hostile amounts. A caller amount is only accepted for
  amountless invoices.
- `CreateLightningInvoiceAsync` verifies the SSP-returned invoice (payment hash,
  amount, network) before any preimage share is stored with the operators.
- Inbound claims verify the sender's signature on every leaf, over
  `sha256(leafId || transferId || secretCipher)` with the sender's identity key,
  before any secret is decrypted or refund signed (reference SDK:
  `verifyPendingTransfer`). Both the legacy bare-bytes form (compact or DER ECDSA)
  and the scheme-tagged `common.Signature` (strict-DER ECDSA, BIP-340 Schnorr) are
  accepted; the transfer proto now carries the typed field. A transfer that fails
  verification is skipped by `ClaimPendingTransfersAsync`.
- Token commits verify the coordinator's final transaction against the submitted
  partial transaction (inputs, outputs, owners, amounts, operator keys, version,
  timestamp, withdraw bond, relative locktime, keyshare threshold and owners)
  before signing it for each operator (reference SDK: `validateTokenTransaction`).
  New options: `SparkOptions.SigningThreshold`, `ExpectedWithdrawBondSats` (10 000),
  `ExpectedWithdrawRelativeBlockLocktime` (1 000).

### Fixed
- **Withdrawals were rejected by mainnet.** `WithdrawAsync` used the older two-step
  cooperative exit (unsigned refund jobs in `cooperative_exit_v2`, then
  `finalize_transfer_with_transfer_package`), which the coordinator now refuses with
  "transfer_package is required for cooperative exit". The connector-input refund
  transactions are FROST-signed by the user and sent together with the encrypted
  key-tweak package in a single `cooperative_exit_v2` call, with a 7-day expiry on
  mainnet, exactly as the reference SDK's `CoopExitService` does. Verified on mainnet
  with a real withdrawal to the wallet's static deposit address.
- One leaf at the timelock floor no longer fails a send, Lightning payment, swap, or
  withdrawal that other leaves could cover. Every spend path now selects from
  `GetSpendableLeavesAsync()`: leaves in the coordinator's renewable range
  `[100, 200)` are renewed first (best effort, like the reference leaf manager) and
  leaves at the floor are left out.
- `SatsBalance.Available` now means "can be sent right now": `AVAILABLE` leaves at
  the timelock floor are reported in the new `SatsBalance.Frozen` instead, so
  sending the full available balance always succeeds.
- A Lightning send whose SSP call fails after the coordinator locked the leaves now
  throws `SparkLightningSendIncompleteException` carrying the transfer id and
  payment hash instead of a bare error with no way to reconcile. Pass the id back
  as `transferId` to resume; it is also sent as the coordinator idempotency key
  (`x-idempotency-key`), as the reference SDK does.
- The FROST signing threshold is taken from the configuration
  (`SparkOptions.EffectiveSigningThreshold`) in every flow instead of being derived
  from whatever operator count the coordinator reports.
- `ClaimPendingTransfersAsync` no longer swallows cancellation.
- `SparkLeaf.RefundTimelockBlocks` reads as 0 for an unparseable refund
  transaction instead of throwing from a property getter.

### Added
- `GetSpendableLeavesAsync()`, `SparkLeaf.IsSpendable`, `SparkLeaf.IsRenewable`,
  `SatsBalance.Frozen`, `SatsBalance.Locked`.
- `PayLightningInvoiceAsync(..., amountSats:)` for amountless invoices and
  `transferId:` to resume an incomplete send.
- `WithdrawAsync(..., maxFeeSats:)`.
- `SparkOptions.SigningThreshold`, `EffectiveSigningThreshold`,
  `DefaultSigningThreshold`, `ExpectedWithdrawBondSats`,
  `ExpectedWithdrawRelativeBlockLocktime`.
- Exceptions: `FeeExceedsLimitException`, `SparkUntrustedResponseException`,
  `SparkLightningSendIncompleteException`.
- `common.Signature` / `SignatureScheme` and `TransferLeaf.typed_signature` in the
  protos, matching the current reference protos.
- Integration tests: `WithdrawalTests.CoopExitResponseFromTheSspValidatesWithoutSigning`
  requests a real cooperative exit from the SSP and runs the validator and connector-refund
  construction on it without signing (nothing moves); `ShouldWithdrawToOnChainAddress` is
  now opt-in (`NSPARK_TEST_ALLOW_WITHDRAW=1`) and pays the wallet's own static deposit
  address by default so the sats can be claimed back.

### Changed
- **Breaking:** `PayLightningInvoiceAsync` takes a required `maxFeeSats` (it was
  optional and unbounded by default) and gained `amountSats` and `transferId`
  before `ct`.
- **Breaking:** `WithdrawAsync` gained an optional `maxFeeSats` before `ct`; callers
  passing the cancellation token positionally must use `ct:`.
- `WithdrawAsync` returns the exit txid computed from the verified transaction, in
  display (big-endian hex) order.
- `PayLightningInvoiceAsync` no longer populates the legacy `transfer` field of
  `initiate_preimage_swap_v3` nor `leaves_to_send` on the transfer request: the
  reference SDK sends neither, and the coordinator reads everything from the
  transfer package. One signing-commitment round trip and one FROST round per leaf
  fewer.
- `PayLightningAddressAsync` requires `maxFeeSats` for the same reason.
- `RenewExhaustedLeavesAsync` still sweeps every leaf below 200; the automatic
  renewal before a spend only attempts the `[100, 200)` range.
- The internal BOLT-11 decoder (`LightningService.Bolt11Decoder`) is replaced by
  `Bolt11Invoice`; `Bech32mHelper` learned to decode plain bech32.
- `Microsoft.SourceLink.GitHub` bumped off 8.0.0, whose `Microsoft.Build.Tasks.Git`
  dependency carries advisory CVE-2026-62900 and failed every Release build as NU1902.

## [0.2.0-alpha.3] - 2026-07-15

### Added
- `GetRecoverySnapshotAsync()` — unilateral-exit recovery snapshots: the
  wallet's leaves plus the pruned ancestor transaction chains an exit package
  needs, with a best-effort repair pass for parents the bulk query omits.
  Snapshot queries run on dedicated 128 MiB gRPC channels because
  include-parents responses exceed the 4 MiB transport default on long-lived
  wallets. New models: `SparkRecoverySnapshot`, `SparkRecoveryLeaf`,
  `SparkRecoveryNode`. Ports `getRecoverySnapshot()` from the Swift SDK.
- `RenewExhaustedLeavesAsync()` — leaf timelock renewal via the coordinator's
  `renew_leaf` RPC with all three protocol variants (refund reset, split-node
  splice, zero-timelock node). Un-freezes leaves whose refund timelock ran
  below 200 blocks; per-leaf best-effort. New model: `SparkLeafRenewal`; new
  property: `SparkLeaf.RefundTimelockBlocks`.
- `ConsolidateLeavesAsync()` — off-chain leaf consolidation toward the binary
  decomposition of the balance via zero-fee SSP swaps, so recovery snapshots
  stay kilobytes instead of megabytes and unilateral exits cost a handful of
  transaction chains. Renews exhausted leaves first, consolidates around any
  that stay frozen. New model: `SparkLeafConsolidation` (with measured
  `FeeSats`).
- `SparkLeafTimelockExhaustedException` — typed error for leaves frozen at
  the refund-timelock floor; carries the `LeafId`.
- New docs page `docs/recovery.md` covering snapshots, renewal,
  consolidation, and unilateral-exit execution.

### Fixed
- Timelock-floor guard: the transfer, swap, withdrawal, and Lightning spend
  paths decremented the refund timelock without checking the floor, silently
  underflowing the `uint` sequence for a leaf at ≤ 100 blocks and producing
  garbage the coordinator rejected. All five call sites now route through a
  shared guard that throws `SparkLeafTimelockExhaustedException` instead, and
  consolidation skips such leaves until they are renewed.

## [0.2.0-alpha.2] - 2026-05-26

### Docs
- Full rewrite of `docs/signer.md` for the new async `ISparkSigner` surface:
  contract listing, per-member implementation guide, encrypted-batch design
  notes, async `RemoteSparkSigner` reference, pointers to the public
  `SparkTxBuilder` + `FrostAggregator` helpers.
- Strengthened `docs/trust-model.md` "host process compromise" row to
  reflect the encrypted-batch boundary — HSM-backed signers now have zero
  plaintext private material in the wallet process.
- `docs/architecture.md` diagram + FROST-signing section updated to
  `CreateWalletAsync` and the new signer-boundary invariants.
- `README.md`, `docs/getting-started.md`, `docs/faq.md`,
  `docs/lightning/receiving-invoices.md`, `docs/lightning/description-hash.md`:
  every quick-start example uses `await spark.CreateWalletAsync(...)`.
- No code changes — published to refresh the README bundled into the
  NuGet package.

## [0.2.0-alpha.1] - 2026-05-26

### Breaking — full remote-signing refactor

`ISparkSigner` is now fully async and proto-aware. Every cryptographic operation
that touches private key material — ECDSA over the identity key, ECIES
decryption, per-leaf FROST signing, tweak-share computation, deterministic
Lightning preimages, static-deposit key access, swap-adaptor key generation —
flows through this interface. Custom signer implementations targeting HSMs,
KMS-backed services, or hardware wallets now only need to fulfil one contract;
the rest of NSpark stays the same.

No plaintext share material, no intermediate-key plaintext, and no tweak
signature payload ever crosses the wallet's address space. The signer builds
and ECIES-encrypts the per-SO `SendLeafKeyTweaks` / `ClaimLeafKeyTweaks` /
`SecretShare` proto packages internally and returns only the encrypted blobs
the wallet plugs into the gRPC request.

### Added
- `NSpark.Signer.FrostAggregator` — public, pure-public-key FROST signature
  aggregation. Combines the user's partial signature with SO partial signatures
  into the final aggregated FROST signature. Safe to call from any custom
  signer implementation.
- `NSpark.Services.SparkTxBuilder` — public Spark-protocol Bitcoin transaction
  construction (`BuildRefundTxTrio`, `BuildHtlcTransaction`, `BuildNodeTxPair`,
  `ComputeMultiInputSighash`). No private key material crosses this boundary.
- `NSpark.Services.SparkBitcoinTx`, `SparkRefundTxTrio`, `SparkNodeTxPair` —
  public records returned by `SparkTxBuilder`.
- `NSpark.Signer` DTOs for the new signer surface: `SoTarget`,
  `SendTweakLeafDescriptor`, `ClaimTweakLeafDescriptor`,
  `EncryptedSendTweakBatch`, `EncryptedClaimTweakBatch`,
  `EncryptedPreimageShareBundle`, `LeafFrostSignature`,
  `LeafFrostNonceCommitment`, `SigningCommitment`, `AdaptorKeyHandle`.

### Changed
- **`SparkConnection.CreateWallet*` is now async** —
  `CreateWalletAsync(string mnemonic, ...)` and
  `CreateWalletAsync(ISparkSigner signer, ...)`. One round-trip to the signer
  at construction time caches the identity + deposit public keys so
  `wallet.IdentityPublicKey`, `IdentityPublicKeyHex`, `GetSparkAddress()`,
  `DepositPublicKey` remain synchronous accessors.
- **`ISparkSigner` is fully async** — every member returns `Task` /
  `Task<TResult>` and takes `CancellationToken`. New surface:
  `GetIdentityPublicKeyAsync`, `GetDepositPublicKeyAsync`,
  `GetLeafPublicKeyAsync`, `GetStaticDepositPublicKeyAsync`,
  `SignWithIdentityKeyAsync`, `SignCompactWithIdentityKeyAsync`,
  `DecryptEciesWithIdentityKeyAsync`, `SignLeafFrostAsync`,
  `GenerateLeafFrostNonceAsync` + `SignLeafFrostWithNonceAsync` (two-phase),
  `BuildEncryptedSendTweaksAsync`, `BuildEncryptedClaimTweaksAsync`,
  `BuildEncryptedPreimageSharesAsync`,
  `GenerateStaticDepositFrostNonceAsync` +
  `SignStaticDepositFrostWithNonceAsync` (two-phase),
  `ExportStaticDepositPrivateKeyAsync`, `GenerateAdaptorKeyAsync`.
- `SparkWallet` caches identity + deposit pubkeys; new `wallet.IdentityPublicKey`
  and `wallet.DepositPublicKey` byte-array accessors replace the synchronous
  `wallet.Signer.IdentityPublicKey` path.
- `SparkAuthenticator` and `SspAuthenticator` are fully async against the signer.
- Lightning invoice preimage generation is now deterministic via the signer
  (`HMAC-SHA256(htlcPreimageKey, transferId)`), matching the existing
  `docs/signer.md` contract. The preimage never crosses the wallet boundary
  — only the public payment hash + per-SO encrypted shares do.
- `TokenService` callers go through `SignWithIdentityKeyAsync`.

### Removed
- `ISparkSigner.IdentityPublicKey` (sync getter) — use
  `wallet.IdentityPublicKey` or `await signer.GetIdentityPublicKeyAsync()`.
- `ISparkSigner.IdentityPrivateKey` — replaced by
  `DecryptEciesWithIdentityKeyAsync` (the only legitimate consumer).
  Remote signers no longer need to expose the raw scalar.
- `ISparkSigner.DepositPublicKey` (sync getter) — use
  `wallet.DepositPublicKey` or `await signer.GetDepositPublicKeyAsync()`.
- `ISparkSigner.DeriveLeafSigningKey` / `DeriveStaticDepositKey` — replaced
  by the encrypted-batch builders and explicit `ExportStaticDepositPrivateKeyAsync`
  (used only in `ClaimStaticDepositAsync`, where the protocol requires
  revealing the key to the SSP).
- `ISparkSigner.FrostSign` / `GenerateFrostCommitments` / `GeneratePreimage`
  (old sync stubs) — superseded by the new async surface.
- `SparkConnection.CreateWallet` (sync) — replaced by `CreateWalletAsync`.

### Security
- The wallet's address space no longer contains any raw VSS share material,
  intermediate signing keys, ECIES-decrypted scalars, or random preimages.
  Every operation that produces sensitive material does so inside the signer's
  trust boundary and ECIES-encrypts before returning to the caller.
- `uniffi.spark_frost` is now imported only by three files —
  `Signer/SparkSigner.cs` (in-process default signer), `Signer/FrostAggregator.cs`
  (public-only aggregation wrapper), and `Services/SparkTxBuilder.cs`
  (public-only tx construction wrapper). Every service file is uniffi-free.
- Architecture invariants enforced by file layout — a single `grep -rl "using uniffi"`
  catches any regression in PR review.

### Tests
- 124 unit tests pass. 46 standard integration tests pass.
- All three new encrypted-batch APIs verified end-to-end on mainnet via the
  `[Explicit]` integration suite:
  `BuildEncryptedSendTweaksAsync` (Transfer, Lightning send, Swap, delegated
  Lightning, external Lightning address), `BuildEncryptedClaimTweaksAsync`
  (Swap return, transfer-to-third-wallet claim), and
  `BuildEncryptedPreimageSharesAsync` (Lightning invoice creation).

## [0.1.0-alpha.7] - 2026-05-18

### Fixed
- Unified `CurrencyAmount → sats` conversion across all three SSP fee
  call sites. `WithdrawalService.GetFeeQuoteAsync` already honoured the
  `original_unit` discriminator (alpha.6), but
  `LightningService.GetLightningSendFeeEstimateAsync` had a hard-coded
  `(millisats + 999) / 1000` (the GraphQL query didn't even request
  `original_unit`) and `GetLightningSendStatusAsync` only handled
  `MILLISATOSHI` explicitly, defaulting other units to sats-as-sats.
  Both would have under-quoted by 1000× if the SSP ever switched these
  fields to `SATOSHI` — exactly the same latent bug alpha.6 fixed on
  the withdrawal side. Both now route through
  `NSpark.GraphQL.CurrencyAmountExtensions.ToSats(value, unit)` with
  full unit support (SATOSHI, MILLISATOSHI, BITCOIN, MILLIBITCOIN,
  MICROBITCOIN, NANOBITCOIN).
- `Queries.LightningSendFeeEstimate` GraphQL now requests
  `original_unit` alongside `original_value` so the discriminator is
  observable.

### Changed
- `WithdrawalService.GetFeeQuoteAsync` no longer has its own inline
  `ToSats` switch — uses the shared helper.

### Tests
- `ShouldGetWithdrawalFeeEstimate` now asserts `fee > 100 sats` (was
  `> 0`). The alpha.5 silent regression returned 2 sats and passed the
  old assertion; the tightened threshold catches any future unit
  mishandling.

## [0.1.0-alpha.6] - 2026-05-18

### Added
- `LightningAddressService.ResolveLightningAddressAsync(wallet, lightningAddress, amountSats, ct)`
  — resolves a `user@domain` Lightning address to a concrete BOLT11
  invoice via LNURL-pay without paying it. Lets callers display a fee
  estimate (pair with `GetLightningSendFeeEstimateAsync`) or otherwise
  inspect the invoice before confirming. `PayLightningAddressAsync`
  now delegates to it.

### Fixed
- `WithdrawalService.GetFeeQuoteAsync` was hard-coded to assume SSP fees
  came back in millisats and divided by 1000 — that under-reported every
  fee returned in `SATOSHI` by a factor of 1000 (a 1606-sat fee was
  surfacing as 2 sats). The conversion now switches on the
  `CurrencyAmount.original_unit` discriminator and handles all
  Lightspark `CurrencyUnit` values (`SATOSHI`, `MILLISATOSHI`,
  `BITCOIN`, `MILLIBITCOIN`, `MICROBITCOIN`, `NANOBITCOIN`), rounding
  sub-sat units UP so the quote never under-quotes the SSP's required
  fee. Mirrors Lightspark's reference `amount_as_msats`.

## [0.1.0-alpha.5] - 2026-05-18

### Changed (breaking — unshipped API)
- `LightningService.GetLightningSendStatusAsync` now takes the SSP
  **request id** (the string returned from `PayLightningInvoiceAsync`)
  instead of the BOLT11 payment hash. The underlying GraphQL endpoint
  (`spark_lightning_payment`) was removed; status now flows through
  the polymorphic `user_request` query alongside Lightning receives.
  Only present on unshipped public API, so no SemVer-stable consumer
  is affected.
- The returned `LightningSendStatus.PaymentHash` is now an empty
  string — the new GraphQL projection on `LightningSendRequest` does
  not expose `payment_hash`. Callers that need the hash must keep
  their own `requestId ↔ paymentHash` mapping (it's available on the
  invoice you paid).
- Known `LightningSendRequestStatus` values are now documented on the
  method (`CREATED`, `REQUEST_VALIDATED`, `LIGHTNING_PAYMENT_INITIATED`,
  `LIGHTNING_PAYMENT_SUCCEEDED`/`FAILED`, `PREIMAGE_PROVIDED`/`PROVIDING_FAILED`,
  `TRANSFER_COMPLETED`/`FAILED`, `USER_TRANSFER_VALIDATION_FAILED`,
  `USER_SWAP_RETURNED`/`RETURN_FAILED`). Treat unknown values as still
  in flight — Spark reserves the right to add new ones.

### Fixed
- `GetLightningReceiveRequestStatusAsync` no longer returns a stray
  status string when the caller mistakenly passes a `LightningSendRequest`
  id (and vice-versa for `GetLightningSendStatusAsync`). Each method now
  verifies the polymorphic `__typename` and returns `null` for the wrong
  type rather than misinterpreting the payload.
- Fee millisatoshi→satoshi conversion: `LightningSendRequest.fee` is
  delivered in millisats; `GetLightningSendStatusAsync` now rounds up
  to whole sats, matching `GetLightningSendFeeEstimateAsync`.

### Added
- Integration test
  `LightningTests.GetLightningSendStatus_should_track_a_send_to_terminal`
  asserts:
  1. Unknown request ids return `null`.
  2. Receive-request ids return `null` (type-guard).
  3. A real A→B send transitions through known statuses to a terminal
     value within a 1-minute window.

## [0.1.0-alpha.4] - 2026-05-17

### Fixed
- SSP-rejected refund-tx construction in five hot paths. The SSP validates
  every refund tx and rejects with `"expected value X on output 0"` when
  the refund output value isn't decremented by the standard Bitcoin-network
  fee (191 vbytes × 5 sat/vbyte = 955 sats). The previous code was passing
  `feeSats: 0` in:
  - `ClaimService.ClaimSingleTransferAsync` (incoming-transfer claim refund)
  - `LightningService.PayLightningInvoiceAsync` (v3 preimage-swap refund)
  - `SwapService.ProcessSwapBatchAsync` (swap-output refund)
  - `TransferService.SendAsync` (sender-side refund)
  - `WithdrawalService.WithdrawAsync` (cooperative-exit refund)

  All now use the single `SparkConstants.DefaultRefundFeeSats` constant.
  Verified end-to-end against the live SSP: Lightning send debits exactly
  `invoiceAmount + SSP routing fee` (no 955-sat surcharge to the user
  balance — the 955 is the on-chain miner fee that would only apply in a
  unilateral exit broadcast).

### Changed
- `DepositService.DefaultFeeSats` (and the equivalent local constant in
  `LightningService`) now point at `SparkConstants.DefaultRefundFeeSats`
  so all flows reference one source of truth.

### Added
- `NSpark.Services.SparkConstants` (internal) — centralizes the
  `DefaultRefundFeeSats = 191 × 5 = 955` constant. Internal-only, no
  public-API surface change.

## [0.1.0-alpha.3] - 2026-05-17

### Added
- `LightningService.GetLightningSendStatusAsync(SparkWallet, string, CancellationToken)`
  extension method — query the SSP for the status of an outgoing Lightning
  payment by its BOLT11 payment hash. Returns `null` when the SSP has no
  record, an in-flight `LightningSendStatus` (no fee / no preimage) while the
  HTLC is pending, and the final status with `FeeSats` + `Preimage` populated
  once the payment flips to `SUCCEEDED`.
- `NSpark.Models.LightningSendStatus` record carrying `PaymentHash`, `Status`,
  `FeeSats`, and `Preimage`.

### Fixed
- CS1573 doc-comment warning on `GetLightningSendStatusAsync`: the lone
  `<param>` tag triggered the "missing param tag" rule for the other
  parameters. Folded the `paymentHash` description into the `<summary>` to
  match the convention used by sibling extension methods in
  `LightningService`.

## [0.1.0-alpha.2] - 2026-05-12

### Added
- Full reference documentation under `docs/`:
  - `architecture.md` — layering, lifetimes, transport, observability,
    the two-claim model, trust assumptions.
  - `lightning/receiving-invoices.md` — full receive flow with the
    `ClaimPendingTransfersAsync` step called out as required.
  - `lightning/paying-invoices.md` — BOLT11 + Lightning Address paths,
    fee estimation, failure modes, idempotency.
  - `lightning/description-hash.md` — NIP-57 zap-receiver pattern
    with the two-claim flow honored.
  - `signer.md` — `ISparkSigner` contract, when to write a custom one
    (HSM / KMS / hardware), per-member implementation guide.
  - `deposits.md` — single-use and static deposit flows, including the
    two-claim sequence for static deposits
    (`ClaimStaticDepositAsync` then `ClaimPendingTransfersAsync`).
  - `withdrawals.md` — cooperative-exit flow, fee quote, idempotency
    notes.
  - `configuration.md` — every `SparkOptions` field documented.
  - `faq.md`, `glossary.md`.
- Suppressed `CA1873` repository-wide (joins `CA1848` — neither rule
  fits NSpark's pattern of one-shot lifecycle logs with trivial
  property-access arguments). Fixes the CI build on the latest
  NetAnalyzers shipped on GitHub's Windows/macOS runners.

### Fixed
- Documentation URLs throughout the codebase now point at the public
  repository `github.com/p-i-g-g-y/nspark` (no remaining references to
  the pre-public path).

## [0.1.0-alpha.1] - 2026-05-12

### Added
- Multi-targeting: `net8.0;net9.0;net10.0`.
- Central package management (`Directory.Packages.props`) and shared build
  infrastructure (`Directory.Build.props`, `.editorconfig`, `global.json`).
- Custom exception hierarchy rooted at `SparkException` with retryability
  classification (`SparkConnectionException.FromRpc`, `InvalidBolt11Exception`,
  `InsufficientFundsException`, `PaymentFailedException`,
  `InvoiceExpiredException`, etc.).
- Structured logging via `ILogger<T>` with documented `EventId`s in
  `NSpark.Diagnostics.LogEvents`.
- Sensitive-data redaction via `NSpark.Diagnostics.Sensitive<T>`.
- OpenTelemetry instrumentation: `ActivitySource("NSpark")` and
  `Meter("NSpark")` (`NSpark.Diagnostics.SparkActivitySource`,
  `NSpark.Diagnostics.SparkMeter`).
- Polly v8 resilience pipeline scaffolding
  (`NSpark.Connection.SparkResiliencePolicies`).
- Hosted-service hook for ASP.NET Core / generic host integrations
  (`NSpark.Hosting.SparkHostedService`).
- `IAsyncDisposable` on `SparkConnection`.

### Changed
- Renamed package, assembly, and namespace from `Spark.Client` to **`NSpark`**.
- Renamed `SparkClient` → `SparkConnection`,
  `SparkClientOptions` → `SparkOptions`,
  `AddSparkClient(...)` → `AddSpark(...)`.
- Auth token cache is now per-instance, TTL-evicted with an LRU size cap
  (default 1024 entries) — previously process-global and unbounded.
- gRPC-generated protobuf types are emitted as `internal`; transport-layer
  classes (`GrpcConnectionPool`, `SparkAuthenticator`, `ServerTimeSync`,
  `SspGraphQLClient`) demoted to `internal`. Public surface is now limited
  to `SparkConnection`, `SparkWallet`, `SparkOptions`, `ISparkSigner`,
  models, exceptions, diagnostics, hosting, and resilience helpers.

### Security
- `KeyDerivation.ComputePreimage` zeroes the local HTLC private-key buffer
  with `CryptographicOperations.ZeroMemory` after computing the preimage.
- See [docs/trust-model.md](docs/trust-model.md) for the documented threat
  model and the default Signing Operator / SSP trust assumptions.

[Unreleased]: https://github.com/p-i-g-g-y/nspark/compare/v0.2.0-alpha.3...HEAD
[0.2.0-alpha.3]: https://github.com/p-i-g-g-y/nspark/releases/tag/v0.2.0-alpha.3
[0.1.0-alpha.2]: https://github.com/p-i-g-g-y/nspark/releases/tag/v0.1.0-alpha.2
[0.1.0-alpha.1]: https://github.com/p-i-g-g-y/nspark/releases/tag/v0.1.0-alpha.1
