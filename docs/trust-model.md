# NSpark trust model

This document is the canonical statement of *what NSpark does and does not
defend against*. Read it before deploying NSpark in production. If your
threat model contradicts the assumptions below, NSpark may not be the right
SDK for your application.

## TL;DR

NSpark is a **non-custodial** Lightning SDK that trusts:

1. The **host process** to be honest (memory, files, network).
2. A **majority** of the configured Signing Operators (SOs) to be honest.
3. The **configured SSP** to be honest-but-curious (i.e. routes payments
   correctly but may attempt to learn the wallet's metadata). Every SSP
   answer the wallet signs or hands out is checked first (see below).

The host's clock is not trusted for protocol time: session-token expiry and
token-transaction timestamps use the operators' clock, estimated from the
`date` and `x-processing-time-ms` headers of their answers.

NSpark does **not** custody funds; keys are derived locally from a BIP-39
mnemonic (or a custom `ISparkSigner`) and only signatures and ECIES-
encrypted shares leave the process.

## Default endpoints

The default `SparkOptions` ship with hardcoded mainnet endpoints. These are
**trusted by configuration**. Override them via `SparkOptions.SigningOperators`
and `SparkOptions.SspUrl` (with `SparkOptions.SspIdentityPublicKeyHex`) if
your deployment requires different operators or another SSP. On mainnet the
operators are reached over TLS only.

### Default Signing Operators (mainnet)

| Identifier | Address | Identity public key |
|---|---|---|
| `…001` | `https://0.spark.lightspark.com` | `03dfbdff4b6332c220f8fa2ba8ed496c698ceada563fa01b67d9983bfc5c95e763` |
| `…002` | `https://spark-operator.breez.technology` | `03e625e9768651c9be268e287245cc33f96a68ce9141b0b4769205db027ee8ed77` |
| `…003` | `https://2.spark.flashnet.xyz` | `022eda13465a59205413086130a65dc0ed1b8f8e51937043161f8be0c369b1a410` |

The 2-of-3 threshold means **any single rogue operator cannot extract funds
or signatures**, but two colluding operators can. Take this into account
when deciding whether to override the defaults.

### Default Spark Service Provider (mainnet)

| Endpoint | Identity public key |
|---|---|
| `https://api.lightspark.com/graphql/spark/2025-03-19` | `023e33e2920326f64ea31058d44777442d97d7d5cbfcf54e3060bc1695e5261c93` |

The SSP routes outbound Lightning payments and brokers preimage swaps for
inbound payments. It **does not custody funds** — every payment is HTLC-
secured — but it can:

- Refuse to forward a payment (denial of service).
- Charge above-market fees (bounded by the required `maxFeeSats` on
  Lightning sends and by the fee quote, or `maxFeeSats`, on withdrawals).
- Observe payment metadata (amount, destination node, timing).

The SSP's identity key comes with its URL: the default key applies only to
the default SSP, so a custom SSP without its own key cannot be paid at
Lightspark's key by mistake — those operations throw before any leaf moves.

## What NSpark defends against

| Threat | Defense |
|---|---|
| Single rogue Signing Operator | FROST 2-of-3 threshold signing; no individual SO holds the full key. |
| Single rogue SSP | HTLC payment flow; preimage release is atomic with payment. |
| SSP handing out another invoice | An invoice the SSP creates must carry the wallet's payment hash, amount and network, and must not carry a Spark fallback (a Spark identity or invoice the payer could pay instead), before any preimage share is stored. |
| SSP cooperative exit paying someone else | The exit transaction must hash to the reported txid and pay the destination at least `amount − fee`, and the connector must spend it with one output per leaf, before anything is signed. |
| SSP fee misreporting | Fees are read in the unit the SSP reports (SATOSHI, MILLISATOSHI rounded up); any other unit is refused. |
| Coordinator substituting a deposit address | Before a deposit address is returned: the operators' proof of possession (BIP-340), every operator's signature over the address against the configured keys (the coordinator's too for static addresses), and that the address pays the verifying key. |
| Coordinator injecting an operator | The operator list it reports must match the configuration; secret shares are only encrypted to configured identity keys. |
| Coordinator altering a token transaction | V3: the final transaction must be the partial one signed (protohash-compared, with the server-set fields only). V2: inputs, outputs, owners, amounts, withdraw bond and locktime, operator keys, client timestamp and a keyshare naming the configured operators are checked before signing. |
| Sender planting a transfer the wallet refuses | The sender's signature on every leaf is verified before a claim; a transfer that fails is recorded and skipped, and never blocks the others. |
| Hostile amounts and transactions | Reported amounts are capped at the bitcoin supply; every transaction from an operator, the SSP or a block explorer is parsed with bounds checks, and a deposit transaction must hash to its txid. |
| Token replay against SO | Challenge-response auth with per-call signed challenge; tokens are TTL-bounded, and a token the operator rejects is dropped at once. |
| Plaintext operator traffic | Mainnet operator addresses must be `https://`. |
| Unbounded auth-token retention | Per-instance LRU cache with TTL + size cap. |
| Routing-fee griefing | `maxFeeSats` enforced before HTLC construction, and again when a held send is resumed. |
| Mistyped mnemonic | BIP-39 wordlist and checksum validation before any key is derived (`validateMnemonic: false` opts out). |
| Spark invoice pasted as an address | Refused: paying it as an address would ignore its amount, expiry and sender. |
| Truncated reads of native binaries | SHA-256 of every shipped `libspark_frost` published in `runtimes/SHA256SUMS.txt` and the GitHub release notes. |
| Tampered NuGet package | SLSA build provenance attestation (verify with `gh attestation verify`). NuGet author signing will land once OrkLabs acquires a code-signing certificate. |

## What NSpark does **not** defend against

| Threat | Recommendation |
|---|---|
| **Host process compromise** (memory dump, ptrace, etc.) | NSpark assumes the host is trusted. With the default `SparkSigner` the wallet process holds the BIP-39 master key state (via NBitcoin's `ExtKey`) for the lifetime of the signer. Sensitive intermediates (derived leaf keys, FROST nonces, VSS shares, tweak signature payloads, decrypted ECIES output) live only inside `SparkSigner` method scopes and are cleared via `CryptographicOperations.ZeroMemory` on the way out — none of them ever cross into the rest of the wallet process. For deployments that can't trust the wallet host, implement `ISparkSigner` against an HSM/KMS/remote service (see [`signer.md`](signer.md)): no plaintext key material, no shares, no preimages enter the wallet's address space at all. |
| **Mnemonic theft from disk / env** | NSpark accepts a `string mnemonic`. Loading it from a sealed secret store and avoiding string interning is the consumer's responsibility. For high-assurance setups, implement `ISparkSigner` directly against a hardware wallet or HSM and never let the mnemonic enter NSpark. |
| **Compromised user-supplied signer** | A faulty/malicious `ISparkSigner` implementation can produce invalid signatures or leak private keys. Audit any custom signer carefully. |
| **Colluding majority of SOs** | Out of scope. Choose operators you trust collectively. |
| **Quantum-capable adversary** | The protocol uses secp256k1; whole-network upgrade required. |
| **Side-channel attacks on the signing host** | Out of scope. Use HSMs for high-value workloads. |
| **Pre-image leakage via logs** | Mitigated by `Sensitive<T>` and the redaction conventions in `docs/logging.md`. Custom loggers that ignore those conventions can still leak — review your logger configuration. |
| **Bad-rng signer implementations** | NSpark inherits the entropy source from the underlying signer (NBitcoin's `RandomNumberGenerator` for the default `SparkSigner`). Ensure the platform's CSPRNG is healthy. |

## Operational recommendations

- **Pin dependencies**. Use `<PackageVersion>` (we already do via CPM) and a
  committed lock file. Run `dotnet list package --vulnerable --include-transitive`
  in CI.
- **Verify the NuGet** before installing in a production environment:
  ```bash
  dotnet nuget verify NSpark.<version>.nupkg
  gh attestation verify NSpark.<version>.nupkg --owner p-i-g-g-y
  ```
- **Limit log sinks**. Send structured logs to a sink that you control;
  redaction works only against `Sensitive<T>.ToString()`, not against
  consumers who log raw byte arrays.
- **Cap routing fees**. `PayLightningInvoiceAsync` requires `maxFeeSats`;
  pass the fee estimate you showed the user, not a generous constant.
- **Run multiple wallets out of one `SparkConnection`** for tenant
  isolation — it's cheap (the connection pool is shared) and limits
  blast radius from misconfiguration.

## Reporting trust-boundary concerns

If you believe NSpark's behavior contradicts this document, treat it as a
security bug and follow [`SECURITY.md`](../SECURITY.md). If you believe the
document itself is wrong or incomplete, open a regular issue or PR — the
trust model evolves with the code.
