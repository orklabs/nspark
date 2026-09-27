# Security Policy

NSpark is a Bitcoin / Lightning payments SDK. Bugs in NSpark may directly
move funds. We treat security reports as our highest-priority work.

## Reporting a vulnerability

**Please do not file public GitHub issues for security bugs.**

Use either:

- GitHub's [private vulnerability reporting](https://github.com/p-i-g-g-y/nspark/security/advisories/new)
  feature on this repository (preferred — gives us a private workspace,
  CVE issuance, and audit trail).
- Email **gm@orklabs.com** with `[NSpark Security]` in the subject.

We will:

1. Acknowledge receipt within **72 hours**.
2. Confirm or reject the report (with reasoning) within **7 days**.
3. Coordinate a fix and a disclosure timeline — typically **90 days**
   from confirmation, shorter if the bug is being exploited, longer if a
   dependency or downstream ecosystem coordination is required.
4. Credit you in the advisory unless you prefer anonymity.

## Severity rubric

| Severity | Examples |
|---|---|
| **Critical** | Remote theft of funds, key extraction, signature forgery, FROST round manipulation |
| **High**     | Bypass of payment authorization, denial of service on receiver, ECIES decryption oracle |
| **Medium**   | Insufficient validation that the user can defend against, memory leaks that survive Dispose, log lines that leak sensitive material |
| **Low**      | Hardening opportunities, missing checks where exploitation would also require a higher-severity bug |

## Scope

In scope:

- The `NSpark` NuGet package (assembly, dependencies, packaged native libs)
- This repository's CI/CD pipelines and release tooling
- The `spark_frost` native binary as shipped by this repository

Out of scope:

- The Spark protocol itself (report to <https://www.spark.money/security> or
  the relevant operator)
- The Signing Operators run by third parties
- Lightning Service Provider (SSP) implementations
- Vulnerabilities in upstream Rust crates that compose `spark_frost` —
  report to the affected crate's RustSec advisory first; we will mirror

## Trust boundary

NSpark assumes:

- The **host process** is trusted (i.e. NSpark does not defend against an
  attacker with code execution in the calling process — see
  [`docs/trust-model.md`](docs/trust-model.md)).
- The **configured Signing Operators** are honest-but-curious. NSpark
  protects against a minority of operators going rogue via FROST threshold
  signing; key extraction by a colluding majority is out of scope.
- The **configured SSP** is honest-but-curious. Its answers are verified
  before the wallet signs or hands anything out (invoices it creates,
  cooperative exits, fee units), and routing-fee griefing is bounded by the
  required `maxFeeSats`.
- The **coordinator** is checked where it could redirect funds: deposit
  addresses must carry the operators' proof of possession and signatures,
  its operator list must match the configuration, and token transactions
  must be the ones the wallet signed.

The host's clock is not trusted for protocol time: token expiry and token
timestamps follow the operators' clock (`ServerTimeSync`).

These assumptions are restated in detail in
[`docs/trust-model.md`](docs/trust-model.md). Any report that does not
respect this boundary will be acknowledged but is unlikely to result in a
code change.

## Supply chain

Every NSpark release ships:

- A SHA-256 manifest of all native binaries at
  `runtimes/SHA256SUMS.txt` inside the NuGet, also published on the GitHub
  Release page.
- An [SLSA build provenance attestation](https://slsa.dev/spec/v1.0/) via
  `actions/attest-build-provenance`. Verify with:

  ```bash
  gh attestation verify NSpark.<version>.nupkg --owner p-i-g-g-y
  ```

- A CycloneDX SBOM as a release asset.

Reports on supply-chain issues (compromised dependencies, dependency
confusion, malicious package suggestions) are explicitly in scope.
