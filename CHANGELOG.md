# Changelog

All notable changes to **NSpark** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The public API contract is enforced by `PublicAPI.Shipped.txt` files in the
source tree; any change to that file is, by definition, a breaking change and
will be reflected here.

## [Unreleased]

## [0.3.0-alpha.1] - 2026-09-27

Brings NSpark to the level of the Swift SDK 0.3.0 (`spark-swift-sdk`, 2026-09-27): parity
with the reference TypeScript SDK (`buildonspark/spark` at `0b3a32a`) for V3 token
transactions, verified deposit addresses, safer claims and Lightning sends, a self-healing
event stream, working static-deposit refunds, and the reference transport. Also carries the
Swift 0.2.0 items NSpark had not received: operator-list reconciliation, BIP-39 validation,
bounds-checked transaction parsing, network-checked Spark addresses, and `WithdrawAllAsync`.
Every flow was re-run on mainnet with the integration wallets: Spark transfers and claims,
swaps, Lightning receives and sends (the resume path, and a payment to an external Lightning
address), the event stream, V3 and V2 tokens, renewal and consolidation, and an on-chain round
trip — a cooperative exit to a static deposit address, the deposit's refund, and its claim back
into Spark. 485 unit tests run against an in-process operator stand-in on net8.0, net9.0 and
net10.0, on Linux, macOS and Windows.

### Security
- **Deposit addresses are verified before they are returned**, as the reference SDK does.
  `GetDepositAddressAsync` and `GetStaticDepositAddressAsync` returned whatever address and
  verifying key the coordinator sent, so a coordinator — or anyone impersonating it — could
  substitute an address it alone controls, and a static address is reused for every deposit.
  They now check the operators' proof of possession (BIP-340, BIP-86-tweaked operator key),
  every operator's signature over the address against the configured keys (the coordinator's
  too for static addresses), and that the address pays the verifying key, and throw
  `SparkUntrustedResponseException` otherwise.
- **A custom SSP no longer receives transfers addressed to Lightspark's SSP key.** The SSP
  identity key defaulted per network whatever `SspUrl` said, so Lightning sends, swaps and
  cooperative exits named Lightspark's key while another SSP was asked to act on them.
  `SparkOptions.SspIdentityPublicKeyHex` is now `string?` (default `null`): the default key
  applies only to the default SSP (`SparkOptions.DefaultSspUrl`), and without a key those
  operations throw `SparkConfigurationException` (`ssp.identity`) before any leaf moves.
- **Operators are reached over TLS only on mainnet.** An `http://` operator address got a
  plaintext channel carrying session tokens and signing material; it now fails when the
  `SparkConnection` is built. Regtest still accepts `http://` for local operators.
- **The coordinator's operator list is reconciled with the configuration** before every
  signing round: it must name exactly the configured operators, each index once, or the
  operation fails with `SparkUntrustedResponseException`. Secret shares are only encrypted to
  configured identity keys.
- `CreateLightningInvoiceAsync` refuses an SSP invoice that carries a Spark fallback — a Spark
  identity in the sentinel route hint (`f42400f424000001`) or a Spark invoice in a version-31
  fallback field — which the wallet never asks for; payers that prefer Spark would pay whoever
  it names. The BOLT-11 decoder reads both forms as the reference SDK does.
- V2 token commits check the coordinator's final transaction as the reference SDK does: the
  client timestamp must be unchanged to the millisecond (which the hash covers), and keyshare
  info naming the configured operators is required — the keyshare checks were skipped when the
  coordinator left it out.
- Mnemonics are validated against the BIP-39 English wordlist and checksum before any key is
  derived (`CreateWalletAsync(mnemonic)`, `SparkSigner.FromMnemonic`,
  `KeyDerivation.FromMnemonic`): a mistyped phrase silently derived a different, empty wallet.
  `validateMnemonic: false` keeps the old behaviour for phrases known to be non-standard, now
  including words outside the wordlist (BIP-39's PBKDF2 seed, as the Swift SDK derives it).
  Account indexes outside `0 … 2^31 − 1` are refused.
- Transactions from operators, the SSP and the block explorer are parsed by a bounds-checked
  parser (`RawTransaction`) that throws `SparkUntrustedResponseException` on malformed input,
  and a deposit transaction must hash to its txid. Reported amounts are capped at the bitcoin
  supply, so a hostile value of 2^63 sats or more can no longer wrap negative.
- Spark addresses are decoded whole, with the identity key checked to be a curve point and the
  network enforced (`SparkAddress.Decode(address, network)`); a Spark invoice is refused where
  an address is expected (`SendAsync`, `TransferTokensAsync`): paying it as an address ignored
  its amount, expiry and sender restriction, and the payee never saw it paid.

### Added
- `SparkOptions.TokenTransactionVersion` and `TokenTransactionVersion` (`V3` default, `V2`).
- `SparkOptions.DefaultSspUrl`, `EffectiveSspIdentityPublicKeyHex`.
- `ClaimStaticDepositAsync(transactionId, DepositFeeEstimate quote, uint? outputIndex)`: claims
  a static deposit for exactly the credit of a quote from `GetDepositFeeEstimateAsync`.
  `QueryStaticDepositAddressesAsync`, `RefundAndBroadcastStaticDepositAsync`.
- `QuoteWithdrawAllAsync` / `WithdrawAllAsync` with `WithdrawAllQuote` / `WithdrawAllResult`:
  send every spendable sat in one cooperative exit, reporting what stays behind (`FrozenSats`,
  `UnrenewedSats`, `LockedSats`, `UnclaimedSats`).
- `PendingTransferClaim` (returned by `ClaimPendingTransfersAsync`): the claimed transfers and
  `ClaimedTransferIds`, plus `Failures` — transfers that could not be claimed, with their error.
  It is an `IReadOnlyList<SparkTransfer>` of the claimed transfers, so existing callers compile.
- `SendAsync(string receiverSparkAddress, long amountSats)`.
- `GetTransfersAsync(..., TransferDirection direction)` (`Both`, `Sent`, `Received`).
- `SparkLeaf.IsFrozen`; `SparkTransfer.SparkInvoice`.
- Events: `ReconnectingEvent(Attempt, RetryIn, Reason)`, `TransferSentEvent`.
- `SparkSspException` (`HttpStatusCode`, `GraphQLErrors`; retryable on 502/503/504).
- `Bip39.Validate` / `Bip39.IsValid`; `validateMnemonic` on `SparkSigner.FromMnemonic` and
  `KeyDerivation.FromMnemonic`.
- `SparkAddress.Encode(identityPublicKey, network)` / `Decode(sparkAddress, network)`
  (`DecodeIdentityPublicKey` is obsolete).
- Integration tests: `HardeningIntegrationTests` (the Swift hardening suite: transfers, claim
  passes, swaps, Lightning incl. resume, fee caps, events, frozen accounting; on-chain drains,
  static claims and refunds behind `NSPARK_TEST_*` opt-ins), `TransportIntegrationTests`, and
  token tests for concurrent sends, idempotent retries and V2, each checked against the
  operators' own record of the transaction.

### Changed
- **Token transactions use the operators' V3 format by default**, as the reference SDK has
  since 0.5.1: one `broadcast_transaction`, signed over the protohash of the partial
  transaction, outputs carrying the network's withdraw bond and locktime, valid for 180 s. The
  final transaction the operators answer with must be the one signed (protohash-compared), and
  its protohash is returned — on mainnet the operators hold each transaction under that hash.
  `TokenTransactionVersion.V2` keeps the two-step flow. Token timestamps use the operators'
  clock.
- **Breaking:** `SubscribeEventsAsync` streams until the caller stops, cancels, or the
  `SparkConnection` is disposed; it throws only when the connection is already disposed. It
  reconnects by itself (1 s doubling to 15 s, a `ReconnectingEvent` before each wait), claims
  pending transfers on every connection and each payment as it arrives before reporting it as
  `TransferReceivedEvent`, and drops a subscription silent for 15 s once it has seen heartbeats.
  Counter-transfers of the wallet's own swaps and self-transfers are no longer reported as
  received; `TransferSentEvent` reports outgoing transfers; a deposit is reported once its leaf
  is AVAILABLE. Migration: switches over `SparkEvent` should handle the new types, and code that
  claimed on each `TransferReceivedEvent` can stop.
- **Breaking:** `ClaimPendingTransfersAsync` returns `PendingTransferClaim` (see Added) and
  follows the reference SDK's claim pass: pages of 25 until drained, claimable statuses only,
  each transfer tried once per pass, failures recorded instead of stopping the pass, claims
  serialised per wallet, and a transfer already recorded as claimed by this wallet
  (ALREADY_EXISTS) counts as claimed. Claimed leaves in the renewal range are renewed.
- **Breaking:** static-deposit calls take `uint? outputIndex = null`: without one,
  `GetDepositFeeEstimateAsync`, `ClaimStaticDepositAsync`, `ClaimStaticDepositWithMaxFeeAsync`,
  `RefundStaticDepositAsync` and `RefundAndBroadcastStaticDepositAsync` use the output that pays
  the wallet's static deposit address instead of output 0. `ClaimDepositAsync` takes
  `uint? vout = null` and finds the output that pays one of the wallet's unused deposit
  addresses (an explicit `vout` must pay one). `ClaimStaticDepositAsync(transactionId,
  outputIndex)` is obsolete: it signs whatever credit the SSP quotes.
- **Breaking:** `GetTransfersAsync` lists only user transfers — Spark transfers, Lightning
  payments, cooperative exits and static-deposit claims — as the reference SDK does, not
  leaf-swap legs; on mainnet the unfiltered query took 17 s to over a minute for a long history.
  `GetTransferAsync` uses the operators' by-id query (`query_transfers_by_id`) and takes the id
  in any case.
- **Breaking:** `CreateTokenAsync` checks the name (3–20 UTF-8 bytes) and ticker (3–6) in
  Unicode normalization form C, as the operators and the reference SDK do; the operators refused
  other tokens with only INTERNAL "Something went wrong."
- **Breaking:** BOLT-11 invoices without a payment secret are refused, as BOLT-11 readers must
  and the reference SDK does.
- `SatsBalance.Owned` / `Locked` follow the reference SDK: available + frozen + leaves an
  in-flight operation still holds for the wallet (outgoing transfers, Lightning payments and
  cooperative exits before the sender key tweak, swaps and their counter-transfers until
  claimed). Sent sats leave `Owned` as soon as the transfer is committed. `Incoming` sums this
  wallet's leaves of every page of pending transfers, except counter-transfers of its own swaps.
  Node queries are paged at 100 and only AVAILABLE nodes are read.
- `SatsBalance.Frozen` counts only leaves below 100 blocks (the coordinator renews from 100);
  leaves at 100–199 count as available because every spend path renews them first.
- `SparkLeaf.IsSpendable` means a rounded refund timelock above 100 (at least 200); see Fixed.
  `RefundTimelockBlocks` reads the refund transaction only.
- The regtest preset (`GetDefaultOperators(SparkNetwork.Regtest)`) is the hosted operators under
  their keys, as the reference SDK's REGTEST preset: it named localhost operators with empty
  keys that nothing could talk to.
- Protos re-vendored from `buildonspark/spark` at `0b3a32a` (event heartbeats, transfer
  receivers, `query_transfers_by_id`, V3 token messages, …). The C# generator's clash between
  `UpdateWalletSettingRequest`'s oneof and its field is avoided by renaming the oneof (oneof
  names are not on the wire).
- `TokenOutputInfo.Status` is reported in the documented operator spelling (`"AVAILABLE"`,
  `"PENDING_OUTBOUND"`); it read `"Available"` when the operators set it.
- Spark transfers and Lightning sends carry a 16-day transfer expiry (transfers had 10 minutes), as
  the Swift SDK sets it.
- Dependencies bumped to the latest stable releases: Microsoft.Extensions.* 10.0.12,
  Google.Protobuf 3.36.1, Grpc.Net.Client / Grpc.Net.ClientFactory 2.83.0, Grpc.Tools
  2.84.0, NBitcoin 10.0.10, Polly 8.8.0, MinVer 8.0.0,
  Microsoft.CodeAnalysis.PublicApiAnalyzers 5.6.0, and the test stack
  (Microsoft.NET.Test.Sdk 18.10.1, NUnit 4.6.1, NUnit3TestAdapter 6.3.0,
  NUnit.Analyzers 4.15.0, coverlet.collector 10.0.1, FluentAssertions 7.2.2,
  BenchmarkDotNet 0.15.8). FluentAssertions stays on the 7.x line because 8.x moved to
  a paid license for commercial use. Consumers on .NET 8 receive the
  Microsoft.Extensions 10.0.x packages transitively; those still ship net8.0 assets.
- GitHub Actions bumped to their current majors (checkout v7, setup-dotnet v6,
  upload-artifact v7, download-artifact v8, codecov-action v7, action-gh-release v3,
  attest-build-provenance v4, codeql-action v4), which clears the Node 20 deprecation
  warnings on every workflow run.
- Integration tests: the token lifecycle asserts the A→B→A round trip relative to B's
  starting balance instead of assuming B starts empty, and the pure leaf-selection
  tests build leaves with a fresh refund timelock now that selection skips leaves at
  the floor.

### Fixed
- **Operator transport, as the reference SDK's connection manager.** Unary calls had no
  deadline, so a connection that looked alive but never answered parked the caller until the
  process restarted; they now get 60 s. UNAVAILABLE and CANCELLED are retried (3 attempts,
  1 s → 10 s), which heals a pooled connection the operator closed. Messages up to 20 MB are
  accepted (gRPC's 4 MiB default failed ~5 MB answers). The authentication service and the event
  stream are not retried by the transport.
- **Authentication is shared and retried as in the reference SDK.** Concurrent calls each ran
  their own challenge — `GetBalanceAsync` alone started several — and a retried
  `verify_challenge` re-sent a consumed challenge ("challenge reused"). Callers now share one
  authentication per operator and identity, with up to 8 challenge exchanges (a fresh challenge
  at once when one expired or was used, after 250 ms when the connection failed). A call the
  operator answers UNAUTHENTICATED drops the token and is re-issued with a fresh one (3
  attempts); a rejected event subscription drops its token too.
- **The SDK keeps time by the operators' clock.** Session-token expiry (their time) was compared
  with the host clock, so a host clock running ahead re-authenticated on every call and one
  running behind kept using expired tokens; token transactions were stamped with the host clock,
  which the operators refuse outside the validity window. The clock is estimated from the `date`
  and `x-processing-time-ms` headers of their answers and advanced on the monotonic clock.
- **SSP requests are retried** as in the reference SDK: up to 5 more attempts, 1 s doubling to
  10 s, on HTTP 502/503/504 and failed connections; a session the SSP rejects is dropped and the
  request retried once. SSP fees are read in their reported unit (SATOSHI, MILLISATOSHI rounded
  up); any other unit is refused.
- **Leaves whose refund timelock is not a multiple of 100 can be spent again, and leaves at
  101–199 are no longer selected without a renewal.** The next refund timelock was the current
  one minus 100; the operators require the current one rounded down to the interval, minus 100
  (740 → 600, not 640), and refuse a leaf whose rounded timelock is 100 or less. Lightning HTLC
  refunds keep their unrounded offsets, as the operators rebuild them.
- **Leaves on a zero-timelock node can be sent and claimed again.** A direct refund was built
  whenever the node carried a direct transaction, which the operators reject for a zero node
  ("zero nodes must not have a direct refund tx") — the shape zero-timelock renewal leaves.
  Send, claim and cooperative exit now share one refund builder with the reference SDK's
  `isZeroNode` rule.
- **Leaves hanging off any output but the first of their parent can be renewed**, and legacy
  deposit roots with a final (timelock-disabled) node sequence are renewed like zero-timelock
  nodes. Renewals spent parent output 0 and paid that output's script, while the operators
  rebuild from the parent's output at the leaf's `vout`, paying P2TR of the leaf's verifying key.
  Renewals carry an idempotency key (the txid of the refund they replace), so a transport retry
  of an applied renewal no longer reports it failed. Consolidation renews between rounds.
- **One pending transfer the SDK cannot claim no longer blocks every other payment** (see the
  claim pass under Changed): anyone can plant a transfer the SDK rightly refuses, since the
  operators store per-leaf sender signatures without verifying them.
- **A multi-receiver transfer can be claimed by every receiver.** The operators record only the
  lowest receiver key in `receiver_identity_public_key`; the transfer is now narrowed to this
  wallet's receiver edge and its leaves before it is verified and claimed.
- **Static-deposit refunds work.** Four defects each stopped every refund: the unsigned spend
  was serialised with a segwit marker and empty witness, the deposit txid went to the operators
  in internal byte order (they look deposits up in display order), the refund statement ended
  with the sighash as hex instead of its 32 raw bytes, and the request set `hash_variant` V2,
  which has the operators check the signature against a tagged-hash statement, while the SDK
  signs the legacy one (the reference SDK leaves it unset). `ClaimStaticDepositWithMaxFeeAsync`
  claims the quote it checked (it fetched a second quote and signed that one unchecked), against
  a deposit value from a transaction that hashes to its txid. Txids are accepted in any case.
- **Lightning sends:** the SSP's fee estimate is offered as is (a 1-sat floor refused
  `maxFeeSats: estimate`); the trimmed, lower-case invoice is what goes to the SSP (a pasted
  invoice passed the checks but the raw string was refused); every preimage swap carries an
  idempotency key (the transfer id when none is given); a swap whose outcome is unknown (lost
  connection, deadline, cancellation, internal error) throws
  `SparkLightningSendIncompleteException` with the transfer id; and resuming with that id no
  longer selects leaves again — the held send is looked up (`query_htlc`), checked (this
  wallet's HTLC to the SSP for this invoice, not returned or expired, within `maxFeeSats`), and
  paid from; a send that went through returns its request id instead of paying twice.
- **Lightning receives:** each operator gets the preimage share at its own index (encoded in
  its identifier) whatever the configured order; the memo is limited to 639 bytes.
- **Token sends no longer collide:** only AVAILABLE outputs are picked (not PENDING_OUTBOUND),
  and picked outputs are locked for 30 s or until reported pending, so concurrent sends from one
  wallet spend different outputs. `TransferTokensAsync(idempotencyKey:)` resends the first
  transaction unchanged on retry (a rebuilt one was refused as "input 0 changed" after the first
  had gone through); a key used for another transfer is refused. Invoice attachments are hashed
  in invoice-id order, as the operators hash them.
- **Unwanted tokens no longer break balances:** metadata is asked for 500 tokens at a time (the
  operators refuse more), and `GetBalanceAsync` reports token balances best effort instead of
  failing the sats balance with them.

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

[Unreleased]: https://github.com/orklabs/nspark/compare/v0.3.0-alpha.1...HEAD
[0.3.0-alpha.1]: https://github.com/orklabs/nspark/releases/tag/v0.3.0-alpha.1
[0.2.0-alpha.4]: https://github.com/orklabs/nspark/releases/tag/v0.2.0-alpha.4
[0.2.0-alpha.3]: https://github.com/orklabs/nspark/releases/tag/v0.2.0-alpha.3
[0.2.0-alpha.2]: https://github.com/orklabs/nspark/releases/tag/v0.2.0-alpha.2
[0.2.0-alpha.1]: https://github.com/orklabs/nspark/releases/tag/v0.2.0-alpha.1
[0.1.0-alpha.7]: https://github.com/orklabs/nspark/releases/tag/v0.1.0-alpha.7
[0.1.0-alpha.6]: https://github.com/orklabs/nspark/releases/tag/v0.1.0-alpha.6
[0.1.0-alpha.5]: https://github.com/orklabs/nspark/releases/tag/v0.1.0-alpha.5
[0.1.0-alpha.4]: https://github.com/orklabs/nspark/releases/tag/v0.1.0-alpha.4
[0.1.0-alpha.3]: https://github.com/orklabs/nspark/releases/tag/v0.1.0-alpha.3
[0.1.0-alpha.2]: https://github.com/orklabs/nspark/releases/tag/v0.1.0-alpha.2
[0.1.0-alpha.1]: https://github.com/orklabs/nspark/releases/tag/v0.1.0-alpha.1
