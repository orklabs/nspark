# Receiving Lightning payments

NSpark wallets receive Lightning payments via BOLT11 invoices. The
high-level shape is:

1. Generate a BOLT11 invoice (`CreateLightningInvoiceAsync`).
2. Hand the `PaymentRequest` string to the payer.
3. The payer pays it.
4. **Claim** the resulting incoming transfer
   (`ClaimPendingTransfersAsync`) — this is the step that turns the
   invoice payment into a spendable `AVAILABLE` leaf.

> **Read [`../architecture.md#the-two-claim-model`](../architecture.md#the-two-claim-model) once.**
> The claim step is not optional, and most "I paid the invoice but my
> balance is still zero" questions are missing claims.

## Minimum example

```csharp
using NSpark;
using NSpark.Services;

// One-time per process.
await using var spark = new SparkConnection(options, http);
var wallet = await spark.CreateWalletAsync(mnemonic);

// 1. Create a BOLT11 invoice.
var invoice = await wallet.CreateLightningInvoiceAsync(
    amountSats: 1_000,
    memo: "Coffee");

Console.WriteLine(invoice.PaymentRequest);   // give this to the payer
Console.WriteLine(invoice.PaymentHash);      // 32-byte hex
Console.WriteLine(invoice.ExpiresAt);        // DateTimeOffset

// 2. ... payer pays the invoice ...

// 3. Claim incoming. Typically polled on a timer or driven by events.
var claimed = await wallet.ClaimPendingTransfersAsync();
foreach (var transfer in claimed)
{
    Console.WriteLine($"Claimed {transfer.TotalValueSats} sats from transfer {transfer.Id}");
}

// 4. Now the balance reflects the receive.
var balance = await wallet.GetBalanceAsync();
Console.WriteLine($"Available: {balance.SatsBalance.Available} sats");
```

## What the receive does behind the scenes

`CreateLightningInvoiceAsync` performs a FROST-signed VSS share
distribution to the Signing Operators so the SOs can later release the
preimage to the payer (which is what turns "Lightning payment" into "the
SOs credit you with a Spark leaf"). The wallet:

1. Generates a 32-byte HTLC preimage deterministically via
   `HMAC-SHA256(htlcKey, transferId)`.
2. Splits each SO's preimage share with FROST VSS, encrypts each share
   to that SO's identity public key (ECIES).
3. Asks the SSP to issue the BOLT11 invoice with the right payment hash
   and routing hints.
4. Returns a `LightningInvoice` you hand to the payer.

When the payer pays:

1. The Lightning HTLC reaches the SSP's node.
2. The SSP gathers preimage shares from the SOs, reveals the preimage to
   complete the HTLC.
3. The SOs mark the wallet as the recipient of a Spark transfer worth
   (amount − routing fee) sats. The transfer is in a **pending** state —
   value held on your behalf but not yet credited to a leaf you own.

`ClaimPendingTransfersAsync` is what flips that pending transfer into a
spendable `AVAILABLE` leaf. Until you call it, the value sits in
`SatsBalance.Incoming`, not `SatsBalance.Available`.


### The SSP's invoice is verified before shares are stored

The invoice string the SSP returns is decoded and must carry the wallet's
own payment hash, the requested amount (or none for an amountless
request), the wallet's network and a payment secret, and must not carry a
Spark fallback — a Spark identity in the sentinel route hint or a Spark
invoice in a version-31 fallback field, which the wallet never asks for and
which payers that prefer Spark would pay instead of this wallet. Only then
are the preimage shares stored with the operators, each at the operator's
own share index. A mismatch throws `SparkUntrustedResponseException` and no
share leaves the wallet, so an SSP cannot hand out an invoice whose preimage
it controls under your wallet's name. This mirrors the reference SDK's
`validateAndCreateLightningInvoice`.

## Continuous polling for incoming payments

Server-side wallets typically poll on an interval:

```csharp
var poll = TimeSpan.FromSeconds(5);
while (!ct.IsCancellationRequested)
{
    var claim = await wallet.ClaimPendingTransfersAsync(ct);
    foreach (var t in claim.ClaimedTransfers)
    {
        // Persist t.Id, credit user's account in your own ledger, etc.
        logger.LogInformation(
            "Claimed transfer {Id}: {Sats} sats from {Sender}",
            t.Id, t.TotalValueSats, t.SenderIdentityPublicKey);
    }
    foreach (var failure in claim.Failures)
    {
        // A transfer that cannot be claimed (e.g. its sender signature does not
        // verify) is skipped and never blocks the others.
        logger.LogWarning(failure.Error, "Could not claim transfer {Id}", failure.TransferId);
    }
    await Task.Delay(poll, ct);
}
```

Idempotency: claiming the same transfer twice is a no-op (the SOs only
return un-claimed transfers each call, and a transfer this wallet already
claimed counts as claimed). Persist the transfer id in your ledger and treat
it as the deduplication key.

## Event-driven receive (no polling)

If your runtime can hold a long-lived stream, subscribe to events instead.
The stream claims for you: every pending transfer when it connects
(payments that arrived while it was down included) and each payment as it
arrives, and reports it afterwards. It reconnects by itself — 1 s doubling
to 15 s between attempts — and drops a subscription whose heartbeats stop
for 15 s. It ends when you stop iterating, cancel, or dispose the
`SparkConnection`.

```csharp
await foreach (var evt in wallet.SubscribeEventsAsync(ct))
{
    switch (evt)
    {
        case ConnectedEvent:
            // Stream is open; pending receives are being claimed.
            break;

        case TransferReceivedEvent received:
            // Already claimed: the sats are in the wallet.
            logger.LogInformation("Received transfer {Id}: {Sats} sats",
                received.Transfer.Id, received.Transfer.TotalValueSats);
            break;

        case TransferSentEvent sent:
            // A status change of one of this wallet's outgoing transfers.
            break;

        case ReconnectingEvent reconnecting:
            logger.LogInformation("Event stream down ({Reason}); retrying in {Delay}",
                reconnecting.Reason, reconnecting.RetryIn);
            break;

        case DepositConfirmedEvent deposit:
            // On-chain deposit became spendable; claim it.
            // See docs/deposits.md
            break;
    }
}
```

The counter-transfer of the wallet's own leaf swap and a transfer to itself
are not reported as received: they are not payments, and the operation that
made them claims them.

## Inspecting an in-flight receive

While the payer's wallet is mid-route, you can query the SSP for the
status of a specific receive request:

```csharp
var status = await wallet.GetLightningReceiveRequestStatusAsync(
    invoice.RequestId!);  // RequestId comes from the LightningInvoice
Console.WriteLine(status ?? "(no status yet)");
```

Status strings include `INITIATED`, `PREIMAGE_SHARES_SECURED`,
`INVOICE_PAID`, `PREIMAGE_RECOVERED`, `TRANSFER_CREATED`. The wallet only
needs to call `ClaimPendingTransfersAsync` once the transfer exists; the
intermediate states are informational.

## Description hash (NIP-57 zaps)

To issue an invoice locked to a specific description hash (for Nostr zaps
or BOLT11's `h` field), pass `descriptionHash`. See
[`description-hash.md`](description-hash.md) for the full zap-receipt
flow.

```csharp
var sha256 = System.Security.Cryptography.SHA256.HashData(
    System.Text.Encoding.UTF8.GetBytes(zapRequestEvent.ToJson()));

var invoice = await wallet.CreateLightningInvoiceAsync(
    amountSats: 21_000,
    descriptionHash: sha256);
```

## Receiving on behalf of a third party (delegated invoices)

A wallet can issue an invoice payable into *another* wallet — handy for
custodial Lightning addresses, marketplaces, or Nostr zap receivers
serving multiple recipients. Pass the receiver's identity public key:

```csharp
var receiverPubKey = Convert.FromHexString(otherWallet.IdentityPublicKeyHex);
var invoice = await wallet.CreateLightningInvoiceAsync(
    amountSats: 1_000,
    receiverIdentityPublicKey: receiverPubKey);

// The payment lands as a pending transfer for `otherWallet`.
// That other wallet calls ClaimPendingTransfersAsync to materialize it.
```

This is what `LightningAddressService` does under the hood for
`PayLightningAddressAsync`.

## Errors

| Exception                          | Cause                                                                |
| ---------------------------------- | -------------------------------------------------------------------- |
| `SparkConnectionException`         | gRPC or SSP transport problem. Often retryable; see `IsRetryable`.   |
| `SparkAuthenticationException`     | SO / SSP rejected the wallet identity (e.g. signature mismatch).     |
| `InvalidBolt11Exception`           | Returned BOLT11 is malformed (SSP bug; report as a security issue).  |
| `SparkLightningException`          | Generic Lightning-domain failure with a specific reason string.      |

See [`../error-handling.md`](../error-handling.md) for the full hierarchy
and how to recover.
