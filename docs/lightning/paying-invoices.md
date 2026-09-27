# Sending Lightning payments

NSpark wallets pay BOLT11 invoices and Lightning addresses (LNURL-pay /
LUD-16). Both flows are extension methods on `SparkWallet`.

## Pay a BOLT11 invoice

```csharp
using NSpark;
using NSpark.Services;

string paymentRequest = "lnbc1u1p4qx..."; // user-supplied

// Show the user the SSP's fee, then cap the payment at what they accepted.
long feeSats = await wallet.GetLightningSendFeeEstimateAsync(paymentRequest);

var paymentId = await wallet.PayLightningInvoiceAsync(
    paymentRequest,
    maxFeeSats: feeSats);

Console.WriteLine($"Payment id: {paymentId}");
```

`PayLightningInvoiceAsync` returns the SSP-side payment id (e.g.
`SparkLightningSendRequest:019e1c4...`), which is the canonical identifier
you persist for reconciliation. The call resolves once the SSP has accepted
the send; poll `GetLightningSendStatusAsync` for the outcome.

> **`maxFeeSats` is required.** Lightning routing fees are not bounded by
> the protocol — only by what your wallet is willing to pay. NSpark fetches
> the SSP's fee estimate first and throws `FeeExceedsLimitException` if it
> is above the cap, before any leaf is touched. The estimate is offered to
> the SSP as is (an estimate of 0 is paid as 0), so `maxFeeSats: estimate`
> always goes through.

The invoice is accepted trimmed and in either case (a string pasted in upper
case with surrounding whitespace is fine); the trimmed, lower-case form is
what goes to the SSP. It must carry a payment secret, as BOLT-11 readers
require, and be for the wallet's network.

### Amountless invoices

An invoice without an amount needs `amountSats`; an invoice that carries
one refuses it unless it matches:

```csharp
var paymentId = await wallet.PayLightningInvoiceAsync(
    "lnbc1...",              // zero-amount invoice
    maxFeeSats: 50,
    amountSats: 2_100);
```

## Pay a Lightning address (LNURL-pay / LUD-16)

```csharp
var paymentId = await wallet.PayLightningAddressAsync(
    "user@example.com",
    amountSats: 100,
    maxFeeSats: 10);
```

Under the hood `PayLightningAddressAsync`:

1. Fetches `https://example.com/.well-known/lnurlp/user` via HTTPS.
2. Reads the LNURL-pay metadata (callback URL, `minSendable`, `maxSendable`).
3. Validates `amountSats * 1000` is within the sendable msat range.
4. Calls the callback to get a freshly issued BOLT11.
5. Delegates to `PayLightningInvoiceAsync`.

Failures surface as `SparkLightningException` (LNURL response invalid,
amount out of range, callback errored) or `PaymentFailedException` (the
underlying BOLT11 payment failed).

## What "payment" means in NSpark

A Lightning send is conceptually three steps:

1. **Decode + select**. NSpark decodes the BOLT11 with a parser that
   verifies the bech32 checksum and the network, resolves the amount
   (rounding sub-sat amounts up), fetches the SSP fee estimate and checks it
   against `maxFeeSats`, then selects spendable leaves for amount + fee. If
   no exact-value leaf exists, the wallet swaps leaves with the SSP first.
2. **HTLC construction**. The wallet builds three HTLC refund
   transactions per input (CPFP, direct, directFromCpfp) and FROST-signs
   each branch with the Signing Operators.
3. **Preimage swap**. The wallet hands the transfer package to the
   coordinator (`initiate_preimage_swap_v3`), which locks the leaves, then
   asks the SSP to pay (`request_lightning_send`). The SSP pays the BOLT11,
   retrieves the preimage, and atomically claims the HTLC leaves — at which
   point the wallet's leaves move out of `AVAILABLE` and the payment is final.

Because the preimage swap is atomic, **either the payment succeeds and
the leaves are spent, or it fails and the leaves stay yours**. The partial
states are between steps: if the preimage swap fails in a way that leaves
its outcome unknown (a connection lost after the request went out, a
deadline, a cancellation, an internal error), or the SSP cannot be asked to
pay after the coordinator locked the leaves, the call throws
`SparkLightningSendIncompleteException` carrying the coordinator transfer
id. A swap the operators refused before committing (an invalid argument,
an unavailable leaf, a lock conflict) throws its own error instead.

To resume, call `PayLightningInvoiceAsync` again with the same invoice and
that id as `transferId`. The wallet asks the coordinator for the send it
holds under that id (`query_htlc`) and selects no leaf again: the held
transfer must be this wallet's HTLC to the SSP for this invoice's payment
hash, neither returned nor expired, with at most `maxFeeSats` beyond the
amount — otherwise the call throws (`ArgumentException`,
`FeeExceedsLimitException`) without asking the SSP. The SSP then pays from
that transfer; it answers a repeated request for a transfer with the request
it already has, so a send that went through returns its request id instead
of paying twice.

## Estimating routing fees before paying

```csharp
var feeSats = await wallet.GetLightningSendFeeEstimateAsync(paymentRequest);
Console.WriteLine($"Fee estimate: {feeSats} sats");
```

The estimate comes from the SSP and is typically within a few sats of
the realized fee. Use it to inform the user before committing, or as a
sanity check on `maxFeeSats`.

## Failure modes

| Exception                       | Meaning                                                    | Retryable?                          |
| ------------------------------- | ---------------------------------------------------------- | ----------------------------------- |
| `InvalidBolt11Exception`        | The payment-request string couldn't be decoded, or is for another network. | ❌ caller bug, fix the input |
| `FeeExceedsLimitException`      | The SSP's fee estimate is above `maxFeeSats`.              | Raise the cap or wait                |
| `SparkLightningSendIncompleteException` | Leaves locked at the coordinator, SSP not reached. | ✅ resume with `transferId`         |
| `InsufficientFundsException`    | Available sats < amount + max fee.                         | ❌ until the wallet has more sats    |
| `PaymentFailedException`        | SSP routing failed (no path / unreachable destination).    | Sometimes — wait + try again        |
| `InvoiceExpiredException`       | The BOLT11 has expired between decode and pay.             | ❌ get a new invoice                 |
| `SparkConnectionException`      | gRPC / HTTP transport problem.                             | Check `IsRetryable`                 |
| `SparkAuthenticationException`  | Wallet identity rejected by SO or SSP.                     | ❌ until the wallet re-authenticates |

Everything else falls back to `SparkLightningException`. See
[`../error-handling.md`](../error-handling.md) for the full hierarchy.

## Idempotency

Lightning sends are **not idempotent at the protocol level** — paying the
same BOLT11 twice could pay twice if the first attempt was racing. Two
practical guards:

- BOLT11 payment hashes are unique per invoice. The SSP rejects
  duplicate active sends to the same hash.
- Pass your own `transferId` (a UUID). It is the preimage swap's
  idempotency key at the coordinator (every swap carries one — the transfer
  id when you pass none) and the SSP request's transfer reference, so a
  retry with the same id resumes the held send instead of locking a second
  set of leaves, and a retry after the send went through returns its
  request id.
- Persist the returned `paymentId` before showing "payment sent" in your
  UI; on retry/replay, check the SSP status via that id before
  re-issuing.

```csharp
try
{
    var paymentId = await wallet.PayLightningInvoiceAsync(req, maxFeeSats: 50, ct: ct);
    await ledger.RecordPayment(paymentId, req, /* status */ "submitted");
}
catch (Exception ex)
{
    // Was the payment actually submitted? Check by payment hash before retrying.
    await ledger.RecordPaymentError(req, ex);
    throw;
}
```

## Cancellation

Pass a `CancellationToken` to abort the local wait. Cancellation **does
not abort the in-flight Lightning payment** — once NSpark hands the
package to the SSP, the SSP runs the swap to completion. Cancellation
just stops your code from awaiting the result. Use the SSP's
status-query endpoint (the returned payment id) to learn the outcome.

## Sending to another NSpark wallet

You don't have to use Lightning for in-Spark transfers — `SendAsync`
moves leaves directly between wallets with no routing fee:

```csharp
var transfer = await wallet.SendAsync(otherWallet.GetSparkAddress(), amountSats: 1_000);
```

The receiver still has to claim it (`ClaimPendingTransfersAsync()`, or its
event stream). A Spark invoice is refused by `SendAsync`: it carries an
amount, expiry and sender restriction that a plain transfer would ignore.
Use Lightning when the destination is outside the Spark network; use Spark
transfers inside it.
