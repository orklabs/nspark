# Getting started with NSpark

This guide walks through installing NSpark, creating a wallet from a
BIP-39 mnemonic, generating a Lightning invoice, paying one, and reading
your wallet balance.

## Install

```bash
dotnet add package NSpark
```

NSpark targets `net8.0`, `net9.0`, and `net10.0`. The package includes the
native FROST signing library for `osx-arm64`, `osx-x64`, `linux-x64`,
`linux-arm64`, `win-x64`, and `win-arm64`.

## Configure

The smallest possible setup — useful for prototypes and one-off scripts:

```csharp
using NSpark;
using Microsoft.Extensions.Options;

var options = Options.Create(new SparkOptions
{
    Network = SparkNetwork.Mainnet,
});

using var http = new HttpClient();
await using var spark = new SparkConnection(options, http);
```

For ASP.NET Core / generic host applications, use the DI extension:

```csharp
builder.Services.AddSpark(options =>
{
    options.Network = SparkNetwork.Mainnet;
});
```

`AddSpark` registers:

- `SparkConnection` as a singleton (shared gRPC channels, shared HTTP).
- A named `HttpClient` via `IHttpClientFactory`.
- `IOptions<SparkOptions>` from the supplied configuration delegate.
- A guarantee that `AddLogging` has been called so `ILogger<T>` resolves.

You can also opt in to the background lifecycle service:

```csharp
builder.Services.AddHostedService<NSpark.Hosting.SparkHostedService>();
```

## Create a wallet

A wallet is a thin handle around an `ISparkSigner`. The default signer is
built from a BIP-39 mnemonic via NBitcoin.

```csharp
const string mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
var wallet = await spark.CreateWalletAsync(mnemonic);

Console.WriteLine($"Wallet identity: {wallet.IdentityPublicKeyHex}");
Console.WriteLine($"Spark address: {wallet.GetSparkAddress()}");
```

`CreateWalletAsync` performs one round-trip to the signer to fetch and
cache the identity + deposit public keys, so synchronous accessors like
`wallet.IdentityPublicKeyHex` and `wallet.GetSparkAddress()` stay
non-async afterwards.

The phrase is checked against the BIP-39 English wordlist and checksum
first: a mistyped phrase throws `SparkConfigurationException` instead of
silently deriving a different, empty wallet. For a phrase known to be
non-standard, build the signer with
`SparkSigner.FromMnemonic(mnemonic, account, passphrase, validateMnemonic: false)`
and pass it to `CreateWalletAsync(signer)`.

> **Never** check a real mnemonic into source control or store it as a
> string literal in production. See [`docs/signer.md`](signer.md) for
> patterns that load the mnemonic from a secret store, a hardware wallet,
> or a remote signer.

## Receive Lightning

```csharp
var invoice = await wallet.CreateLightningInvoiceAsync(
    amountSats: 21_000,
    memo: "First NSpark invoice");

Console.WriteLine(invoice.PaymentRequest); // BOLT11 lnbc...
Console.WriteLine(invoice.PaymentHash);    // 64-hex
```

To attach a description hash (BOLT11 `h` field, required for NIP-57 zaps):

```csharp
var invoice = await wallet.CreateLightningInvoiceAsync(
    amountSats: 21_000,
    descriptionHash: Convert.FromHexString("a3b1...")); // 32-byte SHA-256
```

See [`docs/lightning/description-hash.md`](lightning/description-hash.md)
for the zap flow.

## Send Lightning

```csharp
var paymentRequest = "lnbc210u1p..."; // BOLT11 you want to pay
await wallet.PayLightningInvoiceAsync(paymentRequest, maxFeeSats: 100);
```

`maxFeeSats` caps the routing fee. If the SSP cannot deliver within that
cap, the call throws `PaymentFailedException` and no funds are spent.

## Read balance

```csharp
var balance = await wallet.GetBalanceAsync();
Console.WriteLine($"Available: {balance.SatsBalance.Available} sats");
Console.WriteLine($"Owned:     {balance.SatsBalance.Owned} sats (available + locked in-flight transfers)");
Console.WriteLine($"Incoming:  {balance.SatsBalance.Incoming} sats (pending inbound transfers + CREATING deposits)");
Console.WriteLine($"Leaves backing the available balance: {balance.Leaves.Count}");
```

## Where to next

- [`docs/lightning/paying-invoices.md`](lightning/paying-invoices.md) —
  fee estimation, retries, partial payments.
- [`docs/lightning/receiving-invoices.md`](lightning/receiving-invoices.md) —
  watching for incoming payments, claim semantics.
- [`docs/signer.md`](signer.md) — implementing `ISparkSigner` against
  an HSM, AWS KMS, or an external signer.
- [`docs/observability.md`](observability.md) — wiring NSpark spans and
  metrics to OpenTelemetry.
- [`docs/trust-model.md`](trust-model.md) — *required reading* before
  production deployments.
