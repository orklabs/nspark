# On-chain withdrawals

Withdraw Spark sats back to a Bitcoin L1 address via a **cooperative
exit** brokered by the SSP. The wallet:

1. Validates the destination (P2PKH, P2SH, P2WPKH, P2WSH, or P2TR on the
   wallet's network) before anything moves.
2. Selects spendable leaves that sum to exactly the amount (swapping with
   the SSP first if no exact combination exists).
3. Quotes the SSP's fee for those leaves and bounds it.
4. Asks the SSP to build the exit and connector transactions, and
   **verifies them** before signing anything.
5. FROST-signs the connector refund transactions with the Signing
   Operators and hands the leaves over in one `cooperative_exit_v2` call.
6. Tells the SSP to complete the exit; the SSP broadcasts on-chain.

The returned identifier is the on-chain transaction id.

## Amount and fee semantics

The SSP's fee is deducted from `amountSats`: the destination receives
`amountSats` minus the fee. Leaves are swapped to denominations that sum to
exactly `amountSats` first, so no more than the requested amount ever
leaves the wallet.

`maxFeeSats` bounds what the SSP may take. When omitted, the SSP's own fee
quote for the selected leaves is the bound. A quote above the cap, or a cap
that would consume the whole amount, throws `FeeExceedsLimitException`
before any leaf is moved.

## Quote the fee first

```csharp
using NSpark;
using NSpark.Services;

var leaves = await wallet.SelectLeavesWithSwapAsync(10_000);
var quote = await wallet.GetFeeQuoteAsync(
    leaves.Select(l => l.Id).ToArray(),
    "bc1q...");
Console.WriteLine($"Fee: {quote.FeeSats} sats");
```

`WithdrawAsync` fetches the same quote internally and uses it as the fee
bound unless you pass `maxFeeSats`. Show it to the user before confirming.

## Withdraw

```csharp
var txid = await wallet.WithdrawAsync(
    onChainAddress: "bc1q...",     // user-supplied destination
    amountSats: 10_000,
    maxFeeSats: 500,               // optional; default: the SSP's quote
    ct: cancellationToken);

Console.WriteLine($"Withdrawal tx: {txid}");
```

After this returns, the SSP has the signed package and broadcasts the
spending transaction; the leaves that funded it are out of `AVAILABLE`
state and will not show up on subsequent `GetBalanceAsync` calls.

> **Destructive.** Withdrawal spends balance. There's no second
> claim — the destination receives sats on L1 directly.

## What is verified before signing

The SSP's `request_coop_exit` response carries the raw exit transaction,
the raw connector transaction, and the exit txid. Before any refund is
signed or any key tweak is prepared, NSpark checks that:

- the raw exit transaction hashes to the reported txid (either byte order
  is accepted, as the operators do);
- it pays the requested address at least `amountSats - feeCap`;
- the connector transaction's first input spends that exit transaction;
- the connector transaction carries one output per leaf plus the SSP's own.

A response that fails any check throws `SparkUntrustedResponseException`
and nothing is handed over. Without these checks the wallet would hand its
leaves to the SSP on the SSP's word: the operators release the transfer once
the exit txid confirms, but only the client knows what that transaction was
supposed to pay. This mirrors the reference SDK's
`validateCoopExitPayoutTransaction` and
`validateConnectorTxBindsToCoopExitTxid`.

## What happens behind the scenes

1. The wallet picks spendable leaves that sum to the amount, swapping with
   the SSP first if no exact-value combination exists.
2. The SSP returns the exit transaction (paying the destination and the
   SSP's fee) and a connector transaction that spends it, with one
   connector output per leaf.
3. For each leaf the wallet builds the next refund transaction trio (CPFP,
   direct, direct-from-CPFP) with the matching connector output appended as
   a second input, and FROST-signs each one against the Signing Operators'
   nonce commitments.
4. The signed refunds and the ECIES-encrypted key-tweak package that hands
   the leaves to the SSP go to the coordinator in a single
   `cooperative_exit_v2` call, with a 7-day expiry on mainnet.
5. The wallet calls the SSP's `complete_coop_exit`; the SSP broadcasts the
   exit transaction on-chain.

The refunds spend a connector output, so they can only confirm after the
exit transaction does: the operators release the transfer to the SSP once
the exit txid confirms, and the pre-signed refunds let the wallet recover
the leaves on-chain if it never does. `GetRecoverySnapshotAsync()` captures
everything besides the seed that a unilateral exit needs — see
[docs/recovery.md](recovery.md).

The coordinator only accepts this single-call form today. The earlier
two-step form (unsigned refund jobs, then
`finalize_transfer_with_transfer_package`) is rejected by mainnet with
"transfer_package is required for cooperative exit".

## Selecting which leaves to spend

`WithdrawAsync` delegates to `SelectLeavesWithSwapAsync(amount)`, which:

1. Takes the spendable leaves (`GetSpendableLeavesAsync()`): leaves in the
   coordinator's renewable range are renewed first, and leaves at the
   timelock floor are left out so one stuck leaf cannot fail the exit.
2. Looks for an exact-amount leaf match, then a multi-leaf combination that
   sums to the amount.
3. If neither exists, requests a leaf swap from the SSP (the SSP exchanges
   your leaves for freshly-sized ones via two intra-Spark transfers) and
   retries.

You can pre-swap explicitly if you want to control when the swap happens:

```csharp
var leaves = await wallet.SelectLeavesWithSwapAsync(10_000);
// leaves now sum to exactly 10_000 (or throws)
var txid = await wallet.WithdrawAsync("bc1q...", 10_000);
```

Use `balance.SatsBalance.Available` (which already excludes frozen leaves)
as the basis for a "withdraw everything" amount.

## Errors

| Exception                         | Cause                                                                 |
| --------------------------------- | --------------------------------------------------------------------- |
| `SparkConfigurationException`     | Destination address malformed or for another network.                 |
| `FeeExceedsLimitException`        | SSP quote above `maxFeeSats`, or the fee would consume the amount.    |
| `SparkUntrustedResponseException` | The SSP's exit or connector transaction failed verification.          |
| `SparkWithdrawalException`        | The coordinator or SSP refused the exit.                              |
| `SparkConnectionException`        | gRPC / HTTP transport problem. Often retryable.                       |
| `SparkAuthenticationException`    | Wallet identity rejected by SO or SSP.                                |
| `SparkSignerException`            | FROST signing failed locally.                                         |

See [`error-handling.md`](error-handling.md) for the full hierarchy.

## Idempotency

The `transferId` NSpark generates for each `WithdrawAsync` call is a
fresh UUID — replays produce a new on-chain transaction. If your
application retries on transient errors, you **must** persist the
returned txid and check it against the SSP's status endpoint before
re-issuing the withdrawal.

The simplest pattern: wrap your withdrawal in an outer try/catch that
catches `SparkConnectionException`, polls the SSP for the most recent
exit request status before retrying.

## See also

- [`deposits.md`](deposits.md) — the inbound side of L1 ↔ Spark
  movement.
- [`error-handling.md`](error-handling.md) — what each exception means.
- [`trust-model.md`](trust-model.md) — what the SSP can and cannot do
  during a withdrawal.
