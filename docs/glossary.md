# Glossary

Terminology you'll meet in NSpark and the Spark protocol. Listed
alphabetically.

---

**AVAILABLE** — Leaf status meaning the value is immediately spendable.
The only status that counts toward `SatsBalance.Available`.

**Bech32m** — BIP-350 string encoding used for Spark addresses, token
identifiers, and Bitcoin segwit-v1 addresses. NSpark exposes
`Bech32mHelper.Encode` / `Decode` for raw operations.

**BIP-32** — Hierarchical deterministic key derivation. NSpark derives
five keys per wallet at `m/8797555' / account' / {0..4}'`:

| Index | Purpose                          |
| ----- | -------------------------------- |
| 0     | Identity (auth + ECIES)          |
| 1     | Signing (per-leaf signing keys)  |
| 2     | Deposit (single-use addresses)   |
| 3     | Static deposit                    |
| 4     | HTLC preimage (HMAC key)         |

**BIP-39** — Mnemonic phrase → seed standard. The default
`SparkSigner.FromMnemonic` consumes a 12- to 24-word BIP-39 phrase and
checks its wordlist and checksum (`Bip39.Validate`) unless told not to.

**BOLT11** — The original Lightning payment-request format. Encoded as
a Bech32 string starting with `lnbc...` (mainnet), `lntb...` (testnet),
or `lnbcrt...` (regtest). NSpark's decoder verifies the bech32 checksum,
requires a payment secret, and extracts the payment hash, amount, expiry,
description and any Spark fallback.

**BOLT12** — Newer Lightning offer format. **Not yet supported** by
NSpark; the package metadata and docs deliberately don't claim BOLT12
support. Open an issue if you need it.

**CPFP** — Child-pays-for-parent transaction. One of three HTLC refund
branches NSpark builds during Lightning send / receive.

**ClaimPendingTransfersAsync** — The wallet-side step that materializes
pending Spark transfers (incoming Lightning payments, incoming Spark
sends, static-deposit credits) as spendable `AVAILABLE` leaves. See
[`architecture.md`](architecture.md#the-two-claim-model).

**Coordinator** — The Signing Operator the wallet sends RPCs to first.
By convention `SparkOptions.SigningOperators[0]`. The coordinator
forwards relevant messages to the other operators during multi-SO
rounds.

**Cooperative exit** — On-chain withdrawal path NSpark uses for
`WithdrawAsync`. The SSP brokers the exit by building a connector
transaction the wallet co-signs with FROST.

**ECIES** — Elliptic Curve Integrated Encryption Scheme. NSpark uses it
to encrypt FROST shares to each Signing Operator's identity public key
during transfer construction.

**Identity public key** — The wallet's 33-byte compressed secp256k1
public key derived at `m/8797555'/account'/0'`. Serves as the wallet's
permanent identifier in Spark — exposed as
`SparkWallet.IdentityPublicKeyHex` and used everywhere the SOs need to
know "which wallet is this".

**FROST** — Flexible Round-Optimized Schnorr Threshold signatures.
NSpark uses a 2-of-N FROST scheme across the Signing Operators to
produce leaf-spend signatures without any single party ever seeing the
full private key. The wallet-side crypto delegates to the
`spark_frost` Rust library.

**HRP** — Human-Readable Part of a Bech32(m) string. Examples:
`spark` (Spark address), `btkn` (mainnet token id), `btknrt` (regtest
token id), `lnbc` (mainnet BOLT11).

**HTLC** — Hash Time-Locked Contract. The conditional payment primitive
underlying Lightning. NSpark builds three HTLC refund-tx variants per
input during a payment (CPFP, direct, directFromCpfp).

**Leaf** — A unit of Spark-held value, equivalent to a Bitcoin UTXO. A
wallet's balance is a collection of leaves; each has an id, a value in
sats, and a status (`AVAILABLE`, `TRANSFER_LOCKED`, `CREATING`, …).

**LNURL-pay (LUD-16)** — Lightning Address standard. A user provides
`user@domain`; the wallet fetches `https://domain/.well-known/lnurlp/user`
to negotiate amount and request an invoice. NSpark implements this in
`PayLightningAddressAsync`.

**MinVer** — The tool NSpark uses to derive the package version from
git tags. `git tag v0.1.0-alpha.2` → package version `0.1.0-alpha.2`.
See [`release-process.md`](release-process.md).

**NIP-57** — Nostr standard for "zaps" (Lightning tips on Nostr).
Requires the BOLT11 invoice to commit to a `kind:9734` zap-request
event via the description-hash field. NSpark supports this via
`CreateLightningInvoiceAsync(descriptionHash: ...)`. See
[`lightning/description-hash.md`](lightning/description-hash.md).

**OpenTelemetry / OTel** — The cross-language observability stack
NSpark integrates with. Two sources: `ActivitySource("NSpark")` for
spans and `Meter("NSpark")` for counters/histograms. See
[`observability.md`](observability.md).

**P2TR / Taproot** — Pay-to-Taproot. The Bitcoin output script type
NSpark uses for on-chain deposit addresses (single-use and static).
Addresses encode in Bech32m and start with `bc1p...` on mainnet.

**Payment hash** — 32-byte SHA-256 of an HTLC preimage. The BOLT11
invoice commits to the payment hash; the payment resolves when the
payer learns the preimage.

**Pending transfer** — A `SparkTransfer` whose value the SOs are
holding for the receiving wallet but which the receiver hasn't yet
claimed. Surfaced as `SatsBalance.Incoming`; the event stream claims it
and then reports a `TransferReceivedEvent`.

**Preimage** — 32-byte secret whose SHA-256 is the payment hash.
NSpark derives preimages deterministically from the transfer id via
`HMAC-SHA256(htlcPreimageKey, transferId)` so they can be recovered
after a crash.

**Signing Operator (SO)** — One of the N entities that holds a share of
each leaf's FROST signing key. NSpark talks to three on mainnet by
default (Lightspark, Breez, Flashnet). See `SparkOptions.SigningOperators`.

**Spark address** — The wallet's user-facing identifier. Bech32m-encoded
with HRP `spark` (mainnet) or `sparkrt` (regtest). Internally wraps a
protobuf message containing the wallet's identity public key. NSpark
exposes `SparkWallet.GetSparkAddress()` and the round-trip helper
`NSpark.Services.SparkAddress`.

**SSP** — Spark Service Provider. The off-Spark service that brokers
Lightning routing (in and out), cooperative exits (withdrawals), and
static-deposit claim quotes. Configured via `SparkOptions.SspUrl`. On
mainnet, the default SSP is run by Lightspark.

**Static deposit** — A reusable on-chain deposit address. Many UTXOs
can land at the same address; each is claimed individually via the SSP
for a quoted credit amount. Contrast with single-use deposit. See
[`deposits.md`](deposits.md).

**Swap (leaf swap)** — SSP-brokered exchange of one set of leaves for
another. Used when no exact-value leaf combination exists to fund a
payment. NSpark calls this automatically inside `SelectLeavesWithSwapAsync`.

**TFM** — Target Framework Moniker. NSpark targets `net8.0`, `net9.0`,
and `net10.0`.

**TTXO** — Token Transaction Output. The token equivalent of a Bitcoin
UTXO. Each `TokenOutputInfo` returned from `GetTokenOutputsAsync`
describes one TTXO.

**Tree / TreeId** — The hierarchical structure the Signing Operators
use to represent a wallet's deposit. Each on-chain deposit creates one
tree containing one or more leaves. Exposed as `SparkLeaf.TreeId`.

**UniFFI** — Mozilla's foreign-function-interface tooling that
generates language bindings for Rust libraries. NSpark's
`SparkFrostBindings.cs` is auto-generated by `uniffi-bindgen-cs` from
the `spark_frost` UDL definition.

**VSS** — Verifiable Secret Sharing. The technique NSpark uses to split
the HTLC preimage into shares for each Signing Operator, where each
operator can verify its share without learning the secret.

**Zap** — A Lightning tip on the Nostr network, defined by NIP-57.
Implemented in NSpark via `CreateLightningInvoiceAsync(descriptionHash:
sha256(zapRequest))`. See
[`lightning/description-hash.md`](lightning/description-hash.md).

---

If a term you encountered isn't here, open a docs issue:
https://github.com/p-i-g-g-y/nspark/issues — the glossary should
cover everything a new user encounters in NSpark's public API or
documentation.
