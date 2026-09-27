# Configuration reference

Everything NSpark exposes for configuration lives on `SparkOptions`.
This page documents every field, what the default does, and when you'd
override it.

## The full surface

```csharp
public sealed class SparkOptions
{
    public SparkNetwork Network { get; set; } = SparkNetwork.Mainnet;

    public SigningOperatorConfig[] SigningOperators { get; set; }
        = GetDefaultOperators(SparkNetwork.Mainnet);

    public string[] SigningOperatorAddresses => /* derived */;

    public const string DefaultSspUrl = "https://api.lightspark.com/graphql/spark/2025-03-19";
    public string SspUrl { get; set; } = DefaultSspUrl;

    // null: the default SSP's key for the network, but only while SspUrl is the default SSP.
    public string? SspIdentityPublicKeyHex { get; set; }
    public string? EffectiveSspIdentityPublicKeyHex { get; }

    public TokenTransactionVersion TokenTransactionVersion { get; set; } = TokenTransactionVersion.V3;

    public uint? SigningThreshold { get; set; }
    public uint EffectiveSigningThreshold { get; }

    public ulong ExpectedWithdrawBondSats { get; set; } = 10_000;
    public ulong ExpectedWithdrawRelativeBlockLocktime { get; set; } = 1_000;

    public static string GetSspIdentityPublicKey(SparkNetwork network);
    public static SigningOperatorConfig[] GetDefaultOperators(SparkNetwork network);
    public static uint DefaultSigningThreshold(int operatorCount);
}

public sealed record SigningOperatorConfig(
    string Address,
    string Identifier,
    string IdentityPublicKeyHex);
```

## Wiring it up

### Direct construction

```csharp
using Microsoft.Extensions.Options;
using NSpark;

var options = Options.Create(new SparkOptions
{
    Network = SparkNetwork.Mainnet,
});

using var http = new HttpClient();
await using var spark = new SparkConnection(options, http);
```

### Via DI (`Microsoft.Extensions.DependencyInjection`)

```csharp
builder.Services.AddSpark(options =>
{
    options.Network = SparkNetwork.Mainnet;
});
```

### Bound from `IConfiguration`

```jsonc
// appsettings.json
{
  "Spark": {
    "Network": "Mainnet",
    "SspUrl": "https://api.lightspark.com/graphql/spark/2025-03-19"
  }
}
```

```csharp
builder.Services
    .AddOptions<SparkOptions>()
    .Bind(builder.Configuration.GetSection("Spark"))
    .ValidateOnStart();

builder.Services.AddSpark();   // uses the bound options
```

`SigningOperators` is a complex object — bind it explicitly or set it in
the `AddSpark` configuration lambda.

## Field-by-field

### `Network`

- Type: `SparkNetwork` (`Mainnet` | `Regtest`)
- Default: `Mainnet`
- Purpose: chooses which Bitcoin network the wallet operates on.

Determines:

- The HRP of Spark addresses (`spark1...` mainnet, `sparkrt1...` regtest)
  and token identifiers (`btkn1...` / `btknrt1...`). An address or
  invoice for the other network is refused.
- The BIP-32 derivation account number used by
  `SparkConnection.CreateWalletAsync(mnemonic)` if the caller doesn't pass
  `account` explicitly (regtest → account 0, mainnet → account 1).
- The proto `Network` enum on every SO request.
- Transport security: on mainnet every operator address must be `https://`
  (a `http://` address throws `SparkConfigurationException` when the
  `SparkConnection` is built), since operator traffic carries session tokens
  and signing material. Regtest also accepts `http://`, for local operators.

**Setting `Network` alone does not auto-switch `SigningOperators`.** Each
property defaults independently. The regtest preset is the hosted
operators under their mainnet keys, as in the reference SDK's `REGTEST`
preset:

```csharp
var options = Options.Create(new SparkOptions
{
    Network = SparkNetwork.Regtest,
    SigningOperators = SparkOptions.GetDefaultOperators(SparkNetwork.Regtest),
});
```

With the default `SspUrl`, the regtest SSP key applies automatically (see
`SspIdentityPublicKeyHex`).

### `SigningOperators`

- Type: `SigningOperatorConfig[]`
- Default (mainnet and regtest, 3 operators):

| Identifier | Address                                    | Identity public key                                                  |
| ---------- | ------------------------------------------ | -------------------------------------------------------------------- |
| `…001`     | `https://0.spark.lightspark.com`           | `03dfbdff4b6332c220f8fa2ba8ed496c698ceada563fa01b67d9983bfc5c95e763` |
| `…002`     | `https://spark-operator.breez.technology`  | `03e625e9768651c9be268e287245cc33f96a68ce9141b0b4769205db027ee8ed77` |
| `…003`     | `https://2.spark.flashnet.xyz`             | `022eda13465a59205413086130a65dc0ed1b8f8e51937043161f8be0c369b1a410` |

The trust model documents these as **trusted by configuration** — they
are the production Spark operators. See
[`trust-model.md`](trust-model.md) for the threat-model implications of
keeping the default vs. overriding.

Each `SigningOperatorConfig` has three fields:

- `Address` — the operator's endpoint (gRPC over HTTP/2); `https://` on
  mainnet.
- `Identifier` — 32-byte hex identifier used as the SO map key in
  multi-operator coordination messages. It encodes the operator's
  secret-share index (identifier = index + 1), which is how each operator
  gets its own preimage share whatever the order of the array. Must be
  unique within the array.
- `IdentityPublicKeyHex` — operator's secp256k1 identity public key,
  used for ECIES encryption of FROST shares destined for that operator
  and for verifying operator-signed messages (deposit-address signatures,
  token keyshares).

The first operator is the coordinator. The operator list the coordinator
reports is reconciled against this array before each signing round: it
must name exactly the configured operators, with each index used once, or
the operation fails with `SparkUntrustedResponseException`. Secret shares
are only ever encrypted to the configured identity keys, never to keys the
coordinator reports.

### `SigningOperatorAddresses`

- Type: `string[]` (read-only, derived from `SigningOperators`)

Convenience accessor used internally by `GrpcConnectionPool`. You don't
set this — set `SigningOperators` instead.

### `SigningThreshold` / `EffectiveSigningThreshold`

- Type: `uint?` / `uint`
- Default: `null` → `DefaultSigningThreshold(SigningOperators.Length)`
  (2 of 3, 3 of 5)

The FROST threshold the operators enforce. Set it only for a custom
operator set.

### `SspUrl`

- Type: `string`
- Default: `SparkOptions.DefaultSspUrl`
  (`https://api.lightspark.com/graphql/spark/2025-03-19`)
- Purpose: GraphQL endpoint of the Spark Service Provider.

The SSP brokers Lightning routing (both directions), leaf swaps,
cooperative exits (withdrawals), and static-deposit claims. Replace this if
you run your own SSP or target a different Spark deployment.

The path component `/2025-03-19` is the schema version pin. NSpark's
GraphQL queries were written against that schema; updating the URL to a
newer version is a breaking-change moment — be sure your NSpark version
is compatible.

SSP requests are retried as the reference SDK retries them: up to 5 more
attempts, 1 s doubling to 10 s, on HTTP 502/503/504 and on a failed
connection (not on a timeout or cancellation). A session the SSP no longer
honours is dropped and the request retried once with a fresh one.

### `SspIdentityPublicKeyHex` / `EffectiveSspIdentityPublicKeyHex`

- Type: `string?` (hex) / `string?`
- Default: `null`
- Purpose: identity public key of the SSP — the receiver of every transfer
  to the SSP: Lightning sends (the HTLC hashlock destination), leaf swaps
  and cooperative exits.

The key comes with the SSP, as in the reference SDK. With the default
`SspUrl` and no key set, the default SSP's key for the network applies:

- mainnet: `023e33e2920326f64ea31058d44777442d97d7d5cbfcf54e3060bc1695e5261c93`
- regtest: `022bf283544b16c0622daecb79422007d167eca6ce9f0c98c0c49833b1f7170bfe`

With a custom `SspUrl`, set `SspIdentityPublicKeyHex` to that SSP's key.
Without it, `EffectiveSspIdentityPublicKeyHex` is `null` and Lightning
sends, swaps and withdrawals throw `SparkConfigurationException`
(`ssp.identity`) before any leaf moves — rather than address Lightspark's
key while another SSP is asked to act on the transfer. A key that is not a
compressed secp256k1 key (33 bytes, `02`/`03` prefix) is refused the same
way.

### `TokenTransactionVersion`

- Type: `TokenTransactionVersion` (`V3` | `V2`)
- Default: `V3`

How token transactions (`TransferTokensAsync`, `MintTokensAsync`,
`BurnTokensAsync`, `CreateTokenAsync`) are sent. `V3`, the reference SDK's
default, is one `broadcast_transaction` call signed over the protohash of
the partial transaction; the final transaction the operators answer with is
checked to be the one signed, and its protohash is returned. `V2` keeps the
older `start_transaction` / `commit_transaction` flow while the operators
accept it.

### `ExpectedWithdrawBondSats` / `ExpectedWithdrawRelativeBlockLocktime`

- Type: `ulong`
- Defaults: `10_000` / `1_000` (the reference SDK's)

Every token output carries these; a V3 transaction sets them itself, and a
V2 final transaction with other values is refused before the wallet signs
it.

## Static helpers

### `SparkOptions.GetDefaultOperators(network)`

Returns the canonical operator array for the named network. Useful when
overriding `Network` after construction:

```csharp
var opts = new SparkOptions { Network = SparkNetwork.Regtest };
opts.SigningOperators = SparkOptions.GetDefaultOperators(opts.Network);
```

### `SparkOptions.GetSspIdentityPublicKey(network)`

Returns the default SSP's identity public key for the named network.

## Per-wallet vs. per-connection

`SparkOptions` is **per `SparkConnection`** — one configuration for the
whole singleton. There's no `SparkOptions` on `SparkWallet`. The
implication: if your application talks to two networks (e.g. mainnet
and regtest simultaneously), spin up two `SparkConnection` instances
with two separate `IOptions<SparkOptions>` registrations.

Two `SparkConnection`s in one DI container is unusual but supported:

```csharp
builder.Services.AddKeyedSingleton<SparkConnection>("mainnet", (sp, _) =>
{
    var options = Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet });
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("spark-mainnet");
    var lf = sp.GetRequiredService<ILoggerFactory>();
    return new SparkConnection(options, http, lf);
});
builder.Services.AddKeyedSingleton<SparkConnection>("regtest", /* … */);
```

## What you don't configure

The operator transport follows the reference SDK and is not configurable:

- **Deadline**: 60 s on every unary call that has none; the event
  subscription is a long-lived stream without one.
- **Retries**: up to 3 attempts, 1 s → 10 s backoff, on UNAVAILABLE and
  CANCELLED (a pooled connection the operator closed heals this way). The
  authentication service and the event subscription are not retried by the
  transport.
- **Authentication**: one challenge exchange per operator and identity at a
  time, shared by concurrent calls, up to 8 exchanges (a fresh challenge at
  once when one expired or was used, after 250 ms when the connection
  failed). A call the operator answers UNAUTHENTICATED drops the token and
  is re-issued with a fresh one, up to 3 attempts.
- **Clock**: token expiry and token-transaction timestamps use the
  operators' clock, estimated from the `date` and `x-processing-time-ms`
  headers of their answers.
- **Message size**: 20 MB (128 MiB on the channels recovery snapshots use).

Also not on `SparkOptions`:

- **Token cache size / TTL**. `SparkAuthenticator` accepts these via
  constructor, but `SparkConnection` always uses the defaults
  (1024 entries / 1-minute refresh buffer).
- **Polly pipeline parameters**. `SparkResiliencePolicies.Build()`
  constructs a pipeline for your own calls; the operator transport uses
  the retry policy above.
- **HttpClient settings** (the SSP and the block explorer). Drive these via
  `IHttpClientFactory` named clients in your DI container.

## See also

- [`getting-started.md`](getting-started.md) — minimum setup.
- [`trust-model.md`](trust-model.md) — what the defaults imply for
  trust.
- [`logging.md`](logging.md) — how to attach an `ILoggerProvider`.
- [`observability.md`](observability.md) — how to plug in OTel.
