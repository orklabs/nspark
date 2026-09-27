using NSpark.Proto;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// The claim pass ported from the reference SDK's <c>claimTransfers</c>
/// (<c>spark-wallet-claim-transfers.test.ts</c>), in its fallback mode: pages of 25, restart from
/// the head after progress, advance otherwise, at most 100 pages per pass.
/// </summary>
[TestFixture]
public class ClaimDrainTests
{
    private static Transfer Transfer(string id, TransferStatus status = TransferStatus.SenderKeyTweaked) =>
        new() { Id = id, Status = status, Type = TransferType.Transfer };

    private static List<Transfer> Transfers(string prefix, int count, TransferStatus status = TransferStatus.SenderKeyTweaked) =>
        Enumerable.Range(1, count).Select(i => Transfer($"{prefix}-{i}", status)).ToList();

    private static IEnumerable<string> Ids(string prefix, int count) => Enumerable.Range(1, count).Select(i => $"{prefix}-{i}");

    /// <summary>Pending transfers that leave the set once claimed, as they do on the operators.</summary>
    private sealed class PendingServer(IEnumerable<Transfer> pending, params string[] failing)
    {
        private readonly HashSet<string> _failing = [.. failing];

        public List<Transfer> Pending { get; } = [.. pending];

        public List<(int Limit, int Offset)> Queries { get; } = [];

        public List<string> ClaimAttempts { get; } = [];

        public Task<IReadOnlyList<Transfer>> Page(int limit, int offset, CancellationToken ct)
        {
            Queries.Add((limit, offset));
            IReadOnlyList<Transfer> page = offset < Pending.Count ? Pending.Skip(offset).Take(limit).ToList() : [];
            return Task.FromResult(page);
        }

        public Task Claim(Transfer transfer, CancellationToken ct)
        {
            ClaimAttempts.Add(transfer.Id);
            if (_failing.Contains(transfer.Id))
            {
                throw new NSpark.Exceptions.SparkUntrustedResponseException("transfer.claim", $"sender signature on {transfer.Id} does not verify");
            }

            Pending.RemoveAll(t => t.Id == transfer.Id);
            return Task.CompletedTask;
        }

        public Task<PendingTransferDrain.Result> RunAsync() => PendingTransferDrain.RunAsync(Page, Claim, CancellationToken.None);
    }

    /// <summary>Answers queries from a script (the last answer repeats) and never removes anything.</summary>
    private sealed class ScriptedServer(List<List<Transfer>> script, Func<string, bool>? succeeding = null)
    {
        private readonly Func<string, bool> _succeeding = succeeding ?? (_ => true);

        public List<(int Limit, int Offset)> Queries { get; } = [];

        public List<string> ClaimAttempts { get; } = [];

        public Task<IReadOnlyList<Transfer>> Page(int limit, int offset, CancellationToken ct)
        {
            Queries.Add((limit, offset));
            IReadOnlyList<Transfer> page = script[0];
            if (script.Count > 1)
            {
                script.RemoveAt(0);
            }

            return Task.FromResult(page);
        }

        public Task Claim(Transfer transfer, CancellationToken ct)
        {
            ClaimAttempts.Add(transfer.Id);
            return _succeeding(transfer.Id)
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException($"failed to claim {transfer.Id}"));
        }

        public Task<PendingTransferDrain.Result> RunAsync() => PendingTransferDrain.RunAsync(Page, Claim, CancellationToken.None);
    }

    [Test]
    public async Task Drains_pending_transfers_from_the_head_in_25_transfer_batches()
    {
        var server = new ScriptedServer([Transfers("transfer", 25), [Transfer("transfer-26")]]);

        var result = await server.RunAsync();

        result.Claimed.Select(t => t.Id).Should().Equal(Ids("transfer", 26));
        result.Failures.Should().BeEmpty();
        server.Queries.Should().Equal((25, 0), (25, 0));
    }

    [Test]
    public async Task Drains_a_shrinking_server_side_pending_set_across_several_batches()
    {
        var server = new PendingServer(Transfers("server-transfer", 76));

        var result = await server.RunAsync();

        result.Claimed.Select(t => t.Id).Should().Equal(Ids("server-transfer", 76));
        server.Pending.Should().BeEmpty();
        server.Queries.Should().Equal((25, 0), (25, 0), (25, 0), (25, 0));
        server.ClaimAttempts.Should().HaveCount(76);
    }

    [Test]
    public async Task Restarts_from_the_head_after_partially_claiming_a_full_batch_skipping_non_claimable_statuses()
    {
        var skipped = Transfer("server-transfer-skipped", TransferStatus.Expired);
        var server = new PendingServer([skipped, .. Transfers("server-transfer", 50)]);

        var result = await server.RunAsync();

        result.Claimed.Select(t => t.Id).Should().Equal(Ids("server-transfer", 50));
        server.Pending.Select(t => t.Id).Should().Equal(skipped.Id);
        server.Queries.Select(q => q.Offset).Should().Equal(0, 0, 0);
        server.ClaimAttempts.Should().HaveCount(50);
    }

    [Test]
    public async Task Skips_a_non_claimable_head_batch_to_reach_later_claimable_transfers()
    {
        var server = new ScriptedServer([Transfers("expired", 25, TransferStatus.Expired), [Transfer("claimable-later")]]);

        var result = await server.RunAsync();

        result.Claimed.Select(t => t.Id).Should().Equal("claimable-later");
        server.Queries.Select(q => q.Offset).Should().Equal(0, 25);
    }

    [Test]
    public async Task Skips_a_fully_failing_head_batch_to_reach_later_claimable_transfers()
    {
        var server = new ScriptedServer([Transfers("transfer", 25), [Transfer("transfer-26")]], id => id == "transfer-26");

        var result = await server.RunAsync();

        result.Claimed.Select(t => t.Id).Should().Equal("transfer-26");
        result.Failures.Select(f => f.TransferId).Should().Equal(Ids("transfer", 25));
        server.Queries.Select(q => q.Offset).Should().Equal(0, 25);
        server.ClaimAttempts.Should().HaveCount(26);
    }

    [Test]
    public async Task A_transfer_that_cannot_be_claimed_never_blocks_the_ones_behind_it()
    {
        // Anyone can create a pending transfer the SDK refuses: the operators store the per-leaf
        // sender signature without verifying it.
        var server = new PendingServer([Transfer("refused"), .. Transfers("good", 30)], "refused");

        var result = await server.RunAsync();

        result.Claimed.Select(t => t.Id).Should().Equal(Ids("good", 30));
        result.Failures.Select(f => f.TransferId).Should().Equal("refused");
        result.Failures[0].Error.Should().BeOfType<NSpark.Exceptions.SparkUntrustedResponseException>();
        server.Pending.Select(t => t.Id).Should().Equal("refused");
        server.ClaimAttempts.Count(id => id == "refused").Should().Be(1, "it is tried once per pass, not once per page");
    }

    [Test]
    public async Task The_pass_reports_the_leaves_of_the_transfers_it_claimed_for_the_renewal_that_follows()
    {
        static Transfer WithLeaves(string id, params string[] leafIds)
        {
            var transfer = Transfer(id);
            transfer.Leaves.AddRange(leafIds.Select(leafId => new TransferLeaf { Leaf = new TreeNode { Id = leafId } }));
            return transfer;
        }

        var server = new PendingServer([WithLeaves("t1", "a", "b"), WithLeaves("refused", "x"), WithLeaves("t2", "c")], "refused");

        var result = await server.RunAsync();

        result.Claimed.Select(t => t.Id).Should().Equal("t1", "t2");
        result.ClaimedLeafIds.Should().Equal("a", "b", "c");
    }

    [Test]
    public async Task Scans_through_unclaimable_pages_for_at_most_100_batches()
    {
        var server = new ScriptedServer([Transfers("expired-loop", 25, TransferStatus.Expired)]);

        var result = await server.RunAsync();

        result.Claimed.Should().BeEmpty();
        server.Queries.Should().HaveCount(PendingTransferDrain.MaxBatches);
    }

    [Test]
    public async Task A_server_that_never_shrinks_is_drained_for_at_most_100_batches_claiming_each_transfer_once()
    {
        // The reference SDK re-claims the same 25 every batch here; one attempt per pass suffices.
        var batch = Transfers("loop", 25);
        var server = new ScriptedServer([batch]);

        var result = await server.RunAsync();

        result.Claimed.Select(t => t.Id).Should().Equal(batch.Select(t => t.Id));
        server.Queries.Should().HaveCount(PendingTransferDrain.MaxBatches);
    }

    [Test]
    public void Only_the_reference_SDKs_claimable_statuses_are_claimed()
    {
        PendingTransferDrain.ClaimableStatuses.Should().BeEquivalentTo(new[]
        {
            TransferStatus.SenderKeyTweaked, TransferStatus.ReceiverKeyTweaked, TransferStatus.ReceiverRefundSigned,
            TransferStatus.ReceiverKeyTweakApplied, TransferStatus.ReceiverKeyTweakLocked,
        });
        foreach (var status in new[]
                 {
                     TransferStatus.SenderInitiated, TransferStatus.SenderKeyTweakPending, TransferStatus.Completed,
                     TransferStatus.Expired, TransferStatus.Returned, TransferStatus.SenderInitiatedCoordinator,
                     TransferStatus.ApplyingSenderKeyTweak,
                 })
        {
            PendingTransferDrain.ClaimableStatuses.Should().NotContain(status);
        }
    }

    [Test]
    public async Task A_cancelled_pass_stops_instead_of_recording_the_cancellation_as_a_failure()
    {
        using var cts = new CancellationTokenSource();
        var claims = 0;

        var act = () => PendingTransferDrain.RunAsync(
            (_, _, _) => Task.FromResult<IReadOnlyList<Transfer>>(Transfers("t", 25)),
            (_, ct) =>
            {
                if (++claims == 3)
                {
                    cts.Cancel();
                }

                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        claims.Should().Be(3);
    }
}
