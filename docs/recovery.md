# Recovery snapshots & leaf maintenance

Spark leaves live off-chain: if the Signing Operators ever disappear, the only
way out is a **unilateral exit** — broadcasting each leaf's pre-signed
transaction chain on-chain and sweeping after the timelocks expire. Three
wallet operations keep that path healthy:

| Operation | What it does |
|---|---|
| `GetRecoverySnapshotAsync()` | Captures everything besides the seed a unilateral exit needs, while operators are still online |
| `RenewExhaustedLeavesAsync()` | Resets refund timelocks that have run low so frozen leaves can move again |
| `ConsolidateLeavesAsync()` | Swaps fragmented leaves toward the fewest denominations so exits stay cheap |

All three are extension methods on `SparkWallet` and require operators online —
they are maintenance, not the emergency path itself.

## Recovery snapshots

```csharp
using NSpark.Services;

var snapshot = await wallet.GetRecoverySnapshotAsync();
Console.WriteLine($"{snapshot.Leaves.Count} leaves, {snapshot.Nodes.Count} ancestors, " +
                  $"{snapshot.TotalLeafSats} sats covered");
await File.WriteAllTextAsync("exit-bundle.json", JsonSerializer.Serialize(snapshot));
```

`SparkRecoverySnapshot` contains:

- `Leaves` — every node the wallet currently owns (statuses `AVAILABLE`,
  `TRANSFER_LOCKED`, `SPLIT_LOCKED`, `AGGREGATE_LOCK`, `RENEW_LOCKED` — the
  same set `GetBalanceAsync` counts as owned, broader than
  `GetLeavesAsync`'s AVAILABLE-only view).
- `Nodes` — the ancestors on each leaf's parent chain up to its tree root,
  **pruned** to exactly that union. The owner query also returns historical
  nodes (old splits, spent intermediates, disconnected old trees) that no
  exit package uses; keeping them would bloat the snapshot severalfold.
- `TreeNodeHex` on each entry — the hex-encoded protobuf `TreeNode` carrying
  the raw node tx, the pre-signed refund txs, the verifying key, and the
  parent id. **No private keys**: leaf signing keys re-derive from the seed.

Both lists are sorted by id so identical wallet state yields identical bytes —
callers can fingerprint the snapshot to skip redundant writes.

Rules that matter:

- **Capture while operators are online, refresh whenever the leaf set
  changes.** Leaves cannot be re-discovered from the seed once operators are
  down. Snapshot after deposits, claims, sends, and consolidation.
- **A failed call must never replace a previous good snapshot.** The call
  throws (`InvalidOperationException`) when a needed ancestor chain cannot be
  completed rather than returning a bundle that silently cannot exit a leaf.
  Keep the last good file when that happens.
- Snapshot queries run on dedicated gRPC channels with 128 MiB message caps:
  the include-parents query returns every leaf's full chain in one message,
  and long-lived wallets exceed the 4 MiB transport default. Other wallet
  calls keep the default cap.

## Leaf renewal

Spark leaves age: each transfer decrements the refund timelock by 100 blocks.
Below **200** a leaf needs renewal; at or below **100** the coordinator
refuses to move it at all. Inspect a leaf's remaining margin with
`SparkLeaf.RefundTimelockBlocks`, or the two derived flags:

- `SparkLeaf.IsSpendable` — above the floor; every spend path selects only
  from these.
- `SparkLeaf.IsRenewable` — refund timelock in `[100, 200)`, the range the
  coordinator will renew.

Spend paths renew automatically: `GetSpendableLeavesAsync()` renews the
renewable leaves first, then excludes whatever is still at the floor, so one
stuck leaf never fails a send other leaves could cover. Those sats show up
as `SatsBalance.Frozen` rather than `Available`. Leaves below 100 stay
frozen: the coordinator will not renew them either, and only a unilateral
exit recovers them.

```csharp
var result = await wallet.RenewExhaustedLeavesAsync();
Console.WriteLine($"checked {result.Checked}, renewed {result.Renewed}");
foreach (var failure in result.Failures) Console.WriteLine($"  {failure}");
```

The sweep renews every leaf whose refund timelock is below 200 via the
coordinator's `renew_leaf` RPC, choosing the protocol variant per leaf:

- node timelock `== 0` → `renew_node_zero_timelock` (L1-deposit roots)
- node timelock `< 200` → `renew_node_timelock` (splices in a zero-timelock
  "split node"; node and refund timelocks reset to 2000)
- otherwise → `renew_refund_timelock` (node timelock −100, refund reset to 2000)

Renewal only re-signs transactions with the leaf's existing signing key —
there is no key tweak and no ownership change. It is per-leaf and
best-effort: one failing leaf lands in `Failures` and never aborts the sweep.
A leaf whose refund timelock already reached 0 is rejected by operators and
stays exit-only.

## Consolidation

Many small leaves make unilateral exits uneconomical (each leaf costs its own
on-chain transaction chain) and recovery snapshots huge. Consolidation swaps
the wallet's leaves toward the **binary decomposition** of the total — the
fewest power-of-two denominations that sum to it:

```csharp
var result = await wallet.ConsolidateLeavesAsync();
Console.WriteLine($"{result.LeavesBefore} -> {result.LeavesAfter} leaves " +
                  $"in {result.Rounds} rounds, fee {result.FeeSats} sats, " +
                  $"skipped {result.SkippedLeaves}");
```

- Off-chain and instant: each round is an atomic SSP leaf swap — the same
  mechanism sends already use for denominations — requested with fee_sats 0.
  `FeeSats` is **measured** (total before minus after) rather than assumed,
  so it surfaces if the SSP ever starts charging.
- Runs `RenewExhaustedLeavesAsync` first (best-effort) so frozen leaves can
  join the swap; leaves that stay at the timelock floor are consolidated
  *around* and reported as `SkippedLeaves` — they remain exitable via the
  recovery snapshot.
- Bounded: at most 12 rounds, smallest leaves first, at most
  `maxLeavesPerRound` (default 100) per swap; stops as soon as the leaf count
  reaches the decomposition minimum or a round makes no progress.

Consolidate, then refresh the recovery snapshot — the whole point is a
smaller bundle.

## Executing a unilateral exit

NSpark deliberately captures the exit *inputs* rather than broadcasting exit
packages itself (CPFP fee-bumping, package relay, and timelock sweeps are an
operational pipeline of their own). A snapshot plus the seed phrase is a
complete exit kit for external tooling such as
[blinkbitcoin/spark-unilateral-exit](https://github.com/blinkbitcoin/spark-unilateral-exit):
plan → package (with a fee-funding UTXO) → broadcast → wait out the CSV
timelocks → sweep. Budget roughly one transaction chain per leaf — which is
why consolidation runs first.
