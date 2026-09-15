# Error handling

Every exception thrown by NSpark inherits from `NSpark.Exceptions.SparkException`.
Catch the base type to reach any SDK-originated failure; catch subclasses to
respond to specific scenarios.

## Hierarchy

```text
SparkException (abstract)
├── SparkConfigurationException        not retryable — misuse / bad inputs
├── SparkAuthenticationException       not retryable — credential rejected
├── SparkConnectionException           usually retryable — wraps RpcException
├── SparkSignerException               not retryable — local signing failure
├── SparkLightningException            usually not retryable
│   ├── InvalidBolt11Exception
│   ├── InsufficientFundsException
│   ├── PaymentFailedException
│   ├── InvoiceExpiredException
│   └── SparkLightningSendIncompleteException  resume with the carried TransferId
├── FeeExceedsLimitException           not retryable — raise the cap or wait
├── SparkUntrustedResponseException    not retryable — a remote response failed verification
├── SparkLeafTimelockExhaustedException  not retryable — renew the leaf first
├── SparkTransferException
├── SparkDepositException
└── SparkWithdrawalException
```

`SparkUntrustedResponseException` means NSpark refused to sign or hand
anything over because a remote party's response did not verify: an SSP
cooperative-exit transaction that does not pay the requested address, an
SSP invoice without the wallet's payment hash, a coordinator token
transaction that differs from the submitted one, or an inbound transfer
whose leaves lack a valid sender signature.

## What's retryable?

The base `SparkException` exposes `IsRetryable`. The default is `false`;
`SparkConnectionException` overrides it to return `true` for transient
gRPC status codes:

| gRPC status | Retryable | Notes |
|---|---|---|
| `Unavailable` | ✅ | Operator restart, transient network |
| `DeadlineExceeded` | ✅ | Slow operator; backoff helps |
| `ResourceExhausted` | ✅ | Rate limited; backoff helps |
| `Aborted` | ✅ | Optimistic concurrency conflict |
| `Cancelled` | ❌ | Caller cancelled |
| `Unauthenticated` | ❌ | Re-authenticate first |
| `PermissionDenied` | ❌ | Identity rejected |
| `InvalidArgument` | ❌ | Bad input |
| `FailedPrecondition` | ❌ | Server-side state mismatch |

Polly v8 (see `NSpark.Connection.SparkResiliencePolicies`) ships a default
pipeline that handles the retryable codes with exponential backoff + jitter.

## Pattern for callers

```csharp
try
{
    await wallet.PayLightningInvoiceAsync(bolt11, maxFeeSats: 100, ct: ct);
}
catch (InvalidBolt11Exception ex)
{
    // User error — show the invoice format hint, do not retry.
    logger.LogWarning(ex, "Invoice could not be decoded.");
}
catch (InsufficientFundsException ex)
{
    logger.LogWarning(ex, "Need {Needed} sats, have {Have}.", ex.RequestedSats, ex.AvailableSats);
}
catch (PaymentFailedException ex)
{
    // The SSP refused or routed-but-failed. Not safe to silently retry;
    // surface to the user.
    logger.LogError(ex, "Payment failed: {Reason}", ex.Reason);
}
catch (SparkConnectionException ex) when (ex.IsRetryable)
{
    // Polly already retried within the pipeline budget. If we got here,
    // the budget is exhausted — schedule a follow-up rather than spinning.
    logger.LogError(ex, "Transient connection failure to {Endpoint}.", ex.Endpoint);
}
catch (SparkException ex)
{
    logger.LogError(ex, "Spark operation {Operation} failed.", ex.Operation);
    throw;
}
```

## Mapping from `Grpc.Core.RpcException`

The internal call sites translate `RpcException` into
`SparkConnectionException` via
`SparkConnectionException.FromRpc(operation, endpoint, rpcException)`. This:

- Preserves the inner exception (you can inspect `ex.InnerException` to read
  the original `RpcException.Status`).
- Sets `Endpoint` so logs and traces have a target.
- Sets `GrpcStatusCode` for retry classification.
- Sets the human-readable message in the form `"gRPC call to <endpoint>
  failed (<status>): <detail>"`.

If you implement a custom `ISparkSigner` or otherwise call into gRPC
directly, you should follow the same mapping for consistent telemetry.

## Operation tags and correlation IDs

Every exception carries an `Operation` string in dotted form
(`lightning.pay`, `transfer.send`, `so.authenticate`). Use it as a label in
metrics or filtering criteria in log queries.

`SparkException.CorrelationId` is optional and populated when the
originating activity scope produced one. Where you control the call site,
set it to a value that makes sense to your distributed-tracing tooling
(e.g. an upstream request id).
