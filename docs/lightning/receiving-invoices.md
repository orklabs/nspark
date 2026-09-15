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
request), and the wallet's network. Only then are the preimage shares
stored with the operators. A mismatch throws
`SparkUntrustedResponseException` and no share leaves the wallet, so an SSP
cannot hand out an invoice whose preimage it controls under your wallet's
name. This mirrors the reference SDK's `validateAndCreateLightningInvoice`.

## Continuous polling for incoming payments

Server-side wallets typically poll on an interval:

```csharp
var poll = TimeSpan.FromSeconds(5);
while (!ct.IsCancellationRequested)
{
    var claimed = await wallet.ClaimPendingTransfersAsync(ct);
    foreach (var t in claimed)
    {
        // Persist t.Id, credit user's account in your own ledger, etc.
        logger.LogInformation(
            "Claimed transfer {Id}: {Sats} sats from {Sender}",
            t.Id, t.TotalValueSats, t.SenderIdentityPublicKey);
    }
    await Task.Delay(poll, ct);
}
```

Idempotency: claiming the same transfer twice is a no-op (the SOs only
return un-claimed transfers each call). Persist the transfer id in your
ledger and treat it as the deduplication key.

## Event-driven receive (no polling)

If your runtime can hold a long-lived stream, subscribe to events instead:

```csharp
await foreach (var evt in wallet.SubscribeEventsAsync(ct))
{
    switch (evt)
    {
        case ConnectedEvent:
            // Stream is open. Catch up by claiming any pending receives.
            await wallet.ClaimPendingTransfersAsync(ct);
            break;

        case TransferReceivedEvent received:
            logger.LogInformation("Incoming transfer {Id}: {Sats} sats",
                received.Transfer.Id, received.Transfer.TotalValueSats);
            // The SDK does NOT auto-claim — you still call this.
            await wallet.ClaimPendingTransfersAsync(ct);
            break;

        case DepositConfirmedEvent deposit:
            // On-chain deposit became spendable; claim it.
            // See docs/deposits.md
            break;
    }
}
```

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
