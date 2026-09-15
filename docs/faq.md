# FAQ

## I paid an invoice / sent a transfer to the wallet but the balance is still zero.

Spark receives are a **two-step** process: the sender's call produces a
pending transfer, and the receiver has to **claim** it.

```csharp
await wallet.ClaimPendingTransfersAsync(ct);
var balance = await wallet.GetBalanceAsync(ct);
```

Long-running services typically poll this every 5–10 seconds, or
subscribe to `SubscribeEventsAsync` and call claim on each
`TransferReceivedEvent`. See
[`architecture.md`](architecture.md#the-two-claim-model) and
[`lightning/receiving-invoices.md`](lightning/receiving-invoices.md).

## What's the difference between `SatsBalance.Available` and `SatsBalance.Owned`?

- `Available` — sats backed by leaves with status `AVAILABLE` whose refund
  timelock is above the floor, ready to spend right now.
- `Frozen` — sats in `AVAILABLE` leaves at the timelock floor. The
  coordinator will neither move nor renew them; only a unilateral exit
  recovers them (see [`recovery.md`](recovery.md)).
- `Owned` — `Available` plus `Frozen` plus sats locked in in-flight
  outgoing transfers / swaps / renewals (statuses `TRANSFER_LOCKED`,
  `SPLIT_LOCKED`, `AGGREGATE_LOCK`, `RENEW_LOCKED`). `Locked` is that last
  part on its own.
- `Incoming` — pending inbound transfers + `CREATING` deposits not yet
  claimed.

Showing `Owned` in a UI matches what users intuit as "my balance";
showing `Available` matches what `SendAsync` / `PayLightningInvoiceAsync`
will actually be able to spend right now. The Lightning / Spark / Swift
/ Kotlin SDKs all surface the same three fields.

## Does NSpark custody my keys?

No. Keys are derived locally from a BIP-39 mnemonic (or whatever your
custom `ISparkSigner` is backed by) and never leave the process. NSpark
emits ECDSA signatures, ECIES-encrypted FROST shares, and gRPC
authentication tokens — never raw private keys. See
[`trust-model.md`](trust-model.md) for the full process-trust contract
and [`signer.md`](signer.md) for HSM/KMS integration patterns.

## How do I use it without DI?

```csharp
using Microsoft.Extensions.Options;
using NSpark;
using NSpark.Services;

var options = Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet });
using var http = new HttpClient();
await using var spark = new SparkConnection(options, http);
var wallet = await spark.CreateWalletAsync(mnemonic);

var balance = await wallet.GetBalanceAsync();
```

`SparkConnection` is `IAsyncDisposable`. The DI container disposes it on
shutdown; outside DI use `await using`.

## Why does the first SDK call after process start take a couple of seconds?

The first call into the native FROST library (`spark_frost`) triggers
JIT, dynamic library load, and a UniFFI contract-version handshake. The
overhead is one-time per process. Subsequent calls are fast (well under
100ms).

## What's the difference between Spark transfer (`SendAsync`) and Lightning send (`PayLightningInvoiceAsync`)?

- `SendAsync(receiverIdentityPublicKey, amountSats)` moves leaves
  directly between two NSpark wallets via the Signing Operators. No
  Lightning routing fee. Receiver still has to call
  `ClaimPendingTransfersAsync`.
- `PayLightningInvoiceAsync(bolt11)` pays a BOLT11 invoice through the
  SSP's Lightning node. Has a routing fee. Settles atomically (either
  the leaves are spent and the payment lands, or both fail).

Use Spark transfers between NSpark wallets; use Lightning when the
destination is outside Spark.

## Why are some of my CI builds warning about `CA1873` or other analyzer rules?

NSpark suppresses `CA1062`, `CA1303`, `CA1707`, `CA1716`, `CA1848`, and
`CA1873` repository-wide in `Directory.Build.props` — they're either
not applicable to a library exposing extension methods on opaque types
or actively counterproductive (`CA1848` insists on source-generated
logging for one-shot lifecycle events).

These are configuration choices, not bugs. If a new analyzer rule fires
unexpectedly after a NetAnalyzers bump, that's the dependabot PR's job
to fix — review case-by-case.

## How do I see what NSpark is doing under the hood?

1. **Logs**: hook up any `ILoggerProvider`. Every NSpark operation logs
   with a documented `EventId` from `NSpark.Diagnostics.LogEvents`. See
   [`logging.md`](logging.md).
2. **Traces + metrics**: subscribe to `ActivitySource("NSpark")` and
   `Meter("NSpark")` from your OpenTelemetry stack. See
   [`observability.md`](observability.md).
3. **gRPC wire**: set `Microsoft.Extensions.Logging.LogLevel.Trace` and
   look for `Grpc.Net.Client` log entries.

## Does NSpark work with native AOT / trimming?

Trimming: yes, with caveats. The `NSpark.csproj` sets `IsTrimmable=true`
but consumers should test their own AOT-published binaries — some
transitive dependencies (NBitcoin, Polly) may surface trim warnings the
NSpark project doesn't see.

AOT (`PublishAot`): not yet validated for v0.1.x. The native
`spark_frost` library uses standard P/Invoke, so the core wallet flow
should AOT-compile; report any issues you hit.

## Why does `dotnet pack` produce a 15+ MB package?

The package bundles the native `spark_frost` library for six runtime
identifiers (osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64,
win-arm64). Each binary is 2–4 MB. The .NET native library resolver
picks one at consumer runtime based on RID; the others are inert.

There's no way to ship a smaller package without dropping platforms. If
you only deploy on, say, Linux x64, you can build a custom NuGet from
source that only includes that RID's runtime — file an issue and we'll
document the procedure.

## Can I use NSpark with .NET 6 or .NET 7?

No — the lowest TFM is `net8.0`. The protocol-level cryptography
(`UInt128`, `BinaryPrimitives.ReadUInt128BigEndian`,
`CryptographicOperations.ZeroMemory`) depends on net8 BCL features.

## How do I report a security vulnerability?

See [`../SECURITY.md`](../SECURITY.md). The short version: use GitHub's
private vulnerability reporting or email `gm@orklabs.com` with `[NSpark
Security]` in the subject. Do not file public issues for security bugs.

## What's the relationship between NSpark and Lightspark / Breez / Flashnet?

NSpark is an independent .NET client for the Spark protocol. By
default, NSpark talks to the three production Signing Operators
(operated by Lightspark, Breez, and Flashnet) and Lightspark's SSP.
Those defaults are configurable — see
[`configuration.md`](configuration.md) and the trust assumptions in
[`trust-model.md`](trust-model.md).

NSpark is not affiliated with or endorsed by any of those operators.

## I have a question that isn't here.

- Reference docs: this directory.
- GitHub Discussions: https://github.com/p-i-g-g-y/nspark/discussions
- Bugs / feature requests: https://github.com/p-i-g-g-y/nspark/issues
- Security: see [`../SECURITY.md`](../SECURITY.md).
