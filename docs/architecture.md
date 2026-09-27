# Architecture

This document explains how NSpark is layered, which types are public vs.
internal, what's shared across wallets vs. per-wallet, and how the SDK talks
to Spark Signing Operators (SOs) and the Spark Service Provider (SSP).

## High-level shape

```
┌─────────────────────────────────────────────────────────────────────┐
│                       Your application (.NET)                       │
└─────────────────────────────────────────────────────────────────────┘
                                  │
                                  ▼
┌─────────────────────────────────────────────────────────────────────┐
│  SparkConnection (singleton)        ── shared transport + lifetime  │
│   ├─ HttpClient (from IHttpClientFactory)                           │
│   ├─ GrpcConnectionPool  (one channel per Signing Operator)         │
│   ├─ SparkAuthenticator  (token cache, TTL + LRU eviction)          │
│   ├─ ServerTimeSync                                                 │
│   └─ ILoggerFactory, ActivitySource, Meter, Polly pipeline          │
└─────────────────────────────────────────────────────────────────────┘
                                  │
             await CreateWalletAsync(mnemonic | ISparkSigner)
                                  ▼
┌─────────────────────────────────────────────────────────────────────┐
│  SparkWallet (per-wallet, lightweight)                              │
│   ├─ ISparkSigner   (async: identity, FROST, ECIES, tweak batches)  │
│   ├─ Cached IdentityPublicKey + DepositPublicKey (sync accessors)   │
│   └─ SspGraphQLClient   (per-wallet auth token, shared HttpClient)  │
└─────────────────────────────────────────────────────────────────────┘
                                  │
              ┌───────────────────┼───────────────────┐
              ▼                   ▼                   ▼
       ┌──────────────┐    ┌──────────────┐    ┌──────────────┐
       │ Signing Ops  │    │     SSP      │    │   Bitcoin    │
       │ (gRPC × N)   │    │  (GraphQL)   │    │  L1 chain    │
       └──────────────┘    └──────────────┘    └──────────────┘
```

## Lifetimes

| Object              | Lifetime                        | Why                                                              |
| ------------------- | ------------------------------- | ---------------------------------------------------------------- |
| `SparkConnection`   | Singleton per app               | Owns gRPC channels + auth-token cache; expensive to spin up.     |
| `SparkWallet`       | Cheap, create one per identity  | Construction performs one round-trip to the signer to cache identity + deposit pubkeys, then no I/O until you call an operation. |
| `ISparkSigner`      | Lifetime of the wallet          | Holds the key material; replace via DI for HSM-backed wallets.   |
| gRPC channel        | Lifetime of `SparkConnection`   | HTTP/2 multiplexing — one channel per SO is enough.              |
| Auth tokens         | TTL-evicted in `SparkAuthenticator` | Bounded LRU cache (default 1024 entries).                    |

Register once, create many wallets:

```csharp
builder.Services.AddSpark(opts => opts.Network = SparkNetwork.Mainnet);
// builder.Services.AddHostedService<NSpark.Hosting.SparkHostedService>();  // optional
```

Then in any controller / handler:

```csharp
public sealed class Wallets(SparkConnection spark)
{
    public Task<SparkWallet> ForUserAsync(string mnemonic, CancellationToken ct = default)
        => spark.CreateWalletAsync(mnemonic, ct: ct);
}
```

`SparkConnection` is `IAsyncDisposable`. The DI container disposes it on
shutdown; outside DI, wrap in `await using`.

## Public vs. internal surface

Everything you write code against lives in three namespaces:

- `NSpark` — `SparkConnection`, `SparkWallet`, `SparkOptions`, `SparkNetwork`,
  `SigningOperatorConfig`, `ServiceCollectionExtensions`.
- `NSpark.Models` — value records (`SparkLeaf`, `SatsBalance`, `WalletBalance`,
  `LightningInvoice`, `SparkTransfer`, `DepositAddress`, `TokenMetadata`,
  `TokenBalance`, `TokenOutputInfo`, `TokenCreationResult`,
  `TokenTransferResult`, `TokenSelectionStrategy`, `SparkEvent` and its
  cases, `WalletSettings`, `FeeQuote`, `DepositFeeEstimate`, `DepositUtxo`,
  `TransferPage`, `UnusedDepositAddress`).
- `NSpark.Services` — extension methods on `SparkWallet` for every domain
  operation (Lightning, Transfer, Deposit, Withdrawal, Claim, Swap, Token,
  Privacy, Event subscription, Bech32m / TokenIdentifier / SparkAddress
  helpers).
- `NSpark.Exceptions` — `SparkException` and its subclasses.
- `NSpark.Diagnostics` — `SparkActivitySource`, `SparkMeter`, `LogEvents`,
  `Sensitive<T>`.
- `NSpark.Connection` — `SparkResiliencePolicies` (Polly v8 pipeline) only.
  Everything else under that namespace is `internal`.
- `NSpark.Hosting` — `SparkHostedService`.

Generated protobuf types under `NSpark.Proto*` are deliberately `internal`.
The public API contract is tracked in `src/NSpark/PublicAPI.Shipped.txt` +
`PublicAPI.Unshipped.txt` and enforced by
`Microsoft.CodeAnalysis.PublicApiAnalyzers` at build time.

## Transport layers

### gRPC to Signing Operators

`GrpcConnectionPool` (internal) holds one `GrpcChannel` per configured SO.
HTTP/2 multiplexes — one channel handles every concurrent request for that
operator. Channels live as long as the `SparkConnection`. On mainnet every
operator address must be `https://`.

Auth uses challenge-response: the SO emits a nonce, the wallet signs it with
its identity key, the SO returns a session token. Concurrent callers share
one authentication per operator and identity, and up to 8 challenge
exchanges are made (a fresh challenge at once when one expired or was
already used, after 250 ms when the connection failed). Tokens are cached in
`SparkAuthenticator` (LRU + TTL — default 1024 entries, refreshed a minute
before they expire), with expiry compared against the operators' clock
(`ServerTimeSync`, estimated from the `date` and `x-processing-time-ms`
headers of their answers) rather than the host's. Each wallet's calls go
through `AuthRetryInterceptor`: every attempt carries the operator's current
token, and a call answered UNAUTHENTICATED drops that token and is re-issued
with a fresh one, up to 3 attempts.

### GraphQL to the SSP

`SspGraphQLClient` (internal) uses the shared `HttpClient` from
`IHttpClientFactory`. The SSP runs a separate auth flow (also
challenge-response, signed with the same identity key). A separate token
cache lives in `SspAuthenticator` (internal); a session the SSP rejects is
dropped and the request retried once with a fresh one.

The SSP brokers off-Spark interactions:
- Lightning routing (in and out)
- Leaf swaps (exact change for sends)
- Cooperative on-chain exits (withdrawals)
- Static-deposit claim quotes

## Resilience

The operator transport follows the reference SDK's connection manager:

1. **Deadline** — 60 s on every unary call that has none. The event
   subscription is a long-lived stream without one.
2. **Retry** — the channel's service config retries UNAVAILABLE and
   CANCELLED, up to 3 attempts with 1 s → 10 s backoff; this is how a pooled
   connection the operator closed heals. The authentication service is not
   retried by the transport (a retried `verify_challenge` would re-send a
   consumed challenge), nor is the event subscription, which reconnects by
   itself.
3. **Idempotency** — operations the transport may retry after the operators
   applied them carry an `x-idempotency-key`: leaf renewals (the txid of the
   refund being replaced), Lightning preimage swaps (the transfer id) and
   keyed token transfers.
4. **Message size** — 20 MB, as in the reference SDK; node queries are paged
   at the operators' 100 per page.

SSP requests are retried up to 5 more times, 1 s doubling to 10 s, on HTTP
502/503/504 and on a failed connection.

`SparkResiliencePolicies.Build(timeout, maxRetries)` assembles a Polly v8
pipeline for your own calls around NSpark (it is not wired into the
operator transport). See [`error-handling.md`](error-handling.md) for the
retryability matrix and how `SparkConnectionException.IsRetryable` maps to
gRPC status codes.

## Observability

NSpark ships **two** OpenTelemetry sources, both named `"NSpark"`:

- `ActivitySource` — every public wallet operation emits a span (e.g.
  `nspark.lightning.invoice.create`, `nspark.transfer.send`, `nspark.frost.sign`).
- `Meter` — counters (`nspark.payments.completed`, `nspark.payments.failed`,
  `nspark.so_grpc.errors`, …) and histograms (`nspark.payment.duration`,
  `nspark.frost.sign.duration`, …).

No exporter is wired up by default; subscribe in your OTel builder. See
[`observability.md`](observability.md).

Logging uses `Microsoft.Extensions.Logging`. Every log entry carries a
documented `EventId` from `NSpark.Diagnostics.LogEvents`; see
[`logging.md`](logging.md) for the full table.

## FROST signing & the signer boundary

The cryptographic heart of Spark is a 2-of-N FROST threshold signature
across the Signing Operators. NSpark routes **every** private-key
operation through `ISparkSigner` (see [`signer.md`](signer.md)):

- Per-leaf FROST signing (`SignLeafFrostAsync` one-shot, or
  `GenerateLeafFrostNonceAsync` + `SignLeafFrostWithNonceAsync` two-phase).
- Per-leaf tweak shares (`BuildEncryptedSendTweaksAsync`,
  `BuildEncryptedClaimTweaksAsync`) — the signer generates the VSS shares
  *and* ECIES-encrypts them per-SO inside its trust boundary; the wallet
  receives only opaque encrypted blobs.
- Lightning preimage shares (`BuildEncryptedPreimageSharesAsync`) — same
  pattern, encrypted per-SO inside the signer.
- ECDSA over the identity key (`SignWithIdentityKeyAsync`,
  `SignCompactWithIdentityKeyAsync`) and ECIES decryption
  (`DecryptEciesWithIdentityKeyAsync`).

The default `SparkSigner` implements all of this in-process via NBitcoin
(BIP-39/32 key derivation) and the native `spark_frost` Rust library
(same one the JS/Swift/Kotlin SDKs use) through UniFFI-generated C#
bindings. The native binaries ship inside the NuGet at
`runtimes/<rid>/native/` for six RIDs — see [`native-build.md`](native-build.md)
if you want to rebuild from source.

Two architectural invariants enforced by file layout:

- `using uniffi.spark_frost;` appears in exactly three files —
  `Signer/SparkSigner.cs` (the default in-process signer),
  `Signer/FrostAggregator.cs` (public-only FROST aggregation wrapper),
  and `Services/SparkTxBuilder.cs` (public-only Bitcoin tx
  construction wrapper). Service code is uniffi-free.
- No plaintext share material, intermediate signing key, tweak signature
  payload, or random preimage ever crosses the wallet's address space
  in the encrypted-batch flows. Custom HSM/KMS-backed `ISparkSigner`
  implementations can run with zero plaintext private material in the
  wallet process.

## The two-claim model

This is the most important runtime contract to internalize, and it's
where most "why are my sats not showing up" questions come from.

**Receiving in Spark is a two-step process.** The sender produces a
transfer record on the Signing Operators; the receiver must **claim** it
before the value lands as a spendable `AVAILABLE` leaf.

The exception is on-chain deposits, which are claimed once their funding
transaction has enough confirmations. The pattern is:

```csharp
// After someone sends you sats:
//   1. their SendAsync / PayLightningInvoiceAsync returns
//   2. the SOs hold the transfer in a pending state for you
//   3. you claim it to materialize it
var claim = await wallet.ClaimPendingTransfersAsync(ct);
// claim.ClaimedTransfers: the transfers claimed by this pass.
// claim.Failures: transfers that could not be claimed (e.g. a sender signature
// that does not verify), with the error — they never block the others.
// Now GetBalanceAsync() reflects the new sats.
```

A claim pass follows the reference SDK: pages of 25 until the pending set is
drained, only claimable statuses, each transfer tried once per pass, claims
serialised per wallet (a claim pass, a swap's counter-transfer claim and a
withdraw-all never race). Claimed leaves whose refund timelock is in the
renewal range are renewed right away.

Long-running services that receive payments typically subscribe to
`SubscribeEventsAsync`: the stream claims every pending transfer when it
connects (payments that arrived while it was down included) and each payment
as it arrives, then reports it as a `TransferReceivedEvent` — the event means
the sats are already claimed. It reconnects by itself (1 s doubling to 15 s,
with a `ReconnectingEvent` before each wait) and drops a subscription whose
heartbeats stop for 15 s. Polling `ClaimPendingTransfersAsync` on an interval
works too.

The `Incoming` field of `SatsBalance` shows pending receivable sats that
haven't been claimed yet. See [`lightning/receiving-invoices.md`](lightning/receiving-invoices.md)
for the full receive flow with examples.

## Trust assumptions

NSpark trusts the host process, a majority of the configured Signing
Operators (currently 2 of 3 on mainnet), and the configured SSP. The full
threat model lives in [`trust-model.md`](trust-model.md) — required reading
before production deployments.

## Where to next

- [`getting-started.md`](getting-started.md) — install + wallet + first invoice
- [`configuration.md`](configuration.md) — every `SparkOptions` field
- [`signer.md`](signer.md) — custom `ISparkSigner` (HSM / KMS)
- [`error-handling.md`](error-handling.md) — what each exception means
- [`trust-model.md`](trust-model.md) — what NSpark defends against
