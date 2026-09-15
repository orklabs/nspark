# NSpark

[![NuGet](https://img.shields.io/nuget/v/NSpark.svg?style=flat-square)](https://www.nuget.org/packages/NSpark/)
[![Downloads](https://img.shields.io/nuget/dt/NSpark.svg?style=flat-square)](https://www.nuget.org/packages/NSpark/)
[![Build](https://img.shields.io/github/actions/workflow/status/p-i-g-g-y/nspark/ci.yml?branch=main&style=flat-square)](https://github.com/p-i-g-g-y/nspark/actions/workflows/ci.yml)
[![License](https://img.shields.io/github/license/p-i-g-g-y/nspark.svg?style=flat-square)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512BD4?style=flat-square)](https://dotnet.microsoft.com/)

**NSpark** is a first-class .NET SDK for the [Spark](https://www.spark.money) Lightning Network protocol. Send and receive Bitcoin over Lightning — BOLT11 invoices, FROST threshold signing, deposits, withdrawals, Spark-to-Spark transfers — from any modern .NET application with a clean, DI-friendly API.

```bash
dotnet add package NSpark
```

## Why NSpark

- **Real Lightning, not a wrapper.** Talks directly to Spark Signing Operators over gRPC and to your SSP over GraphQL — no daemon to babysit, no node to operate.
- **Modern .NET.** `net8.0`/`net9.0`/`net10.0`, nullable reference types, async-first, `IHttpClientFactory`, `IOptions<T>`, `ILogger<T>`, `OpenTelemetry`. Plays nicely with ASP.NET Core, the generic host, and minimal APIs.
- **Cryptographically sound.** BIP-39/BIP-32 derivation via NBitcoin, FROST threshold signing via the audited `spark_frost` Rust library through UniFFI bindings, ECIES share encryption, tagged-hash domain separation, BOLT11 description-hash support for NIP-57 zaps.
- **Observability by default.** Structured logging with documented `EventId`s, an `ActivitySource` named `"NSpark"`, and a `Meter` with counters/histograms — all of it inactive unless you subscribe.
- **Resilient.** Pluggable Polly v8 retry/backoff/breaker on transient gRPC failures, bounded auth token cache with TTL eviction.
- **Open source.** MIT-licensed, single-package distribution with reproducible builds and SBOM in every release.

## Quickstart

```csharp
using NSpark;

var options = Microsoft.Extensions.Options.Options.Create(new SparkOptions
{
    Network = SparkNetwork.Mainnet,
});

using var http = new HttpClient();
await using var spark = new SparkConnection(options, http);

// Use a real mnemonic in production — derive once and store securely.
const string mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
var wallet = await spark.CreateWalletAsync(mnemonic);

// Receive
var invoice = await wallet.CreateLightningInvoiceAsync(amountSats: 21_000, memo: "hello world");
Console.WriteLine(invoice.PaymentRequest);

// Send (maxFeeSats caps the routing fee; the SSP's estimate is checked first)
await wallet.PayLightningInvoiceAsync("lnbc...", maxFeeSats: 100);

// Balance
var balance = await wallet.GetBalanceAsync();
Console.WriteLine($"Available: {balance.SatsBalance.Available} sats  (spendable right now)");
Console.WriteLine($"Owned:     {balance.SatsBalance.Owned} sats  (incl. locked in-flight and frozen)");
Console.WriteLine($"Incoming:  {balance.SatsBalance.Incoming} sats (pending claim)");
```

### ASP.NET Core / generic host

```csharp
// Program.cs
builder.Services.AddSpark(options =>
{
    options.Network = SparkNetwork.Mainnet;
});

// Optional: background lifecycle service for token refresh hooks
builder.Services.AddHostedService<NSpark.Hosting.SparkHostedService>();
```

```csharp
// In any controller / service
public class InvoicesController(SparkConnection spark) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(long sats, string memo, CancellationToken ct)
    {
        var wallet = await spark.CreateWalletAsync(Environment.GetEnvironmentVariable("WALLET_MNEMONIC")!, ct: ct);
        var invoice = await wallet.CreateLightningInvoiceAsync(sats, memo, ct: ct);
        return Ok(invoice);
    }
}
```

## Supported platforms

NSpark ships a native FROST signing library (`libspark_frost`) for the following runtime identifiers:

| RID | Status |
|---|---|
| `osx-arm64`  | ✅ supported |
| `osx-x64`    | ✅ supported |
| `linux-x64`  | ✅ supported |
| `linux-arm64`| ✅ supported |
| `win-x64`    | ✅ supported |
| `win-arm64`  | ✅ supported |

SHA-256 hashes for every shipped binary are published in each [GitHub release](https://github.com/p-i-g-g-y/nspark/releases) and packed at `runtimes/SHA256SUMS.txt` inside the NuGet.

## Documentation

| Topic | Link |
|---|---|
| Getting started | [docs/getting-started.md](docs/getting-started.md) |
| Architecture & threading | [docs/architecture.md](docs/architecture.md) |
| Receiving Lightning payments | [docs/lightning/receiving-invoices.md](docs/lightning/receiving-invoices.md) |
| Sending Lightning payments | [docs/lightning/paying-invoices.md](docs/lightning/paying-invoices.md) |
| NIP-57 zaps (description hash) | [docs/lightning/description-hash.md](docs/lightning/description-hash.md) |
| Custom signers / HSM | [docs/signer.md](docs/signer.md) |
| Deposits & withdrawals | [docs/deposits.md](docs/deposits.md), [docs/withdrawals.md](docs/withdrawals.md) |
| Recovery snapshots & leaf maintenance | [docs/recovery.md](docs/recovery.md) |
| Configuration reference | [docs/configuration.md](docs/configuration.md) |
| Logging events | [docs/logging.md](docs/logging.md) |
| OpenTelemetry / observability | [docs/observability.md](docs/observability.md) |
| Error handling & retry | [docs/error-handling.md](docs/error-handling.md) |
| **Trust model** | [docs/trust-model.md](docs/trust-model.md) |
| Native library build | [docs/native-build.md](docs/native-build.md) |
| FAQ & glossary | [docs/faq.md](docs/faq.md), [docs/glossary.md](docs/glossary.md) |

Working examples live under [`samples/`](samples/):

- **`samples/QuickStart`** — console app: mnemonic → wallet → invoice → balance
- **`samples/AspNetCore`** — minimal-API receiver wired with OpenTelemetry *(see roadmap)*
- **`samples/CustomSigner`** — implementing `ISparkSigner` against an HSM *(see roadmap)*

## Status & roadmap

NSpark targets a **1.0.0** release on NuGet. Until then, versions are published as pre-releases under the same package id. See [CHANGELOG.md](CHANGELOG.md).

| Area | v1.0 |
|---|---|
| BOLT11 send / receive | ✅ |
| Description-hash (NIP-57 zaps) | ✅ |
| On-chain deposits / withdrawals | ✅ |
| Spark-to-Spark transfers | ✅ |
| Unilateral-exit recovery snapshots | ✅ |
| Leaf renewal & consolidation | ✅ |
| FROST signing via Rust UniFFI | ✅ |
| OpenTelemetry tracing + metrics | ✅ |
| Polly v8 resilience | ✅ |
| Multi-target net8/9/10 | ✅ |
| BOLT12 offers | ❌ not yet — open a discussion if you need it |
| Native AOT | 🚧 v1.1 |

## Security

This SDK handles Bitcoin keys. Read [SECURITY.md](SECURITY.md) before deploying in production, and review [docs/trust-model.md](docs/trust-model.md) for the threat model — including the default Signing Operators and SSP that NSpark trusts. To report a vulnerability privately, see the disclosure process in `SECURITY.md`.

## Contributing

Issues, discussions, and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for build setup, coding conventions, and the PR checklist. Please read [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) before participating.

## License

NSpark is licensed under the [MIT License](LICENSE). Third-party dependencies and their licenses are enumerated in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). © OrkLabs S.A.S. and NSpark contributors.
