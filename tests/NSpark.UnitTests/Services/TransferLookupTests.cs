using NSpark.Proto;
using NSpark.Services;
using NSpark.UnitTests.TestSupport;

namespace NSpark.UnitTests.Services;

/// <summary>Transfer lookups and history against the operator stand-in.</summary>
[TestFixture]
public class TransferLookupTests
{
    [Test]
    [CancelAfter(60_000)]
    public async Task A_transfer_is_looked_up_by_id_with_the_operators_by_id_query(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();
        var known = new Transfer { Id = "0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b", TotalValue = 42, Status = TransferStatus.Completed };
        state.Know(known);

        await FakeOperator.RunAsync(state, async wallet =>
        {
            var transfer = await wallet.GetTransferAsync(known.Id.ToUpperInvariant(), ct);
            transfer.Should().NotBeNull();
            transfer!.Id.Should().Be(known.Id);
            transfer.TotalValueSats.Should().Be(42);
            transfer.Status.Should().Be(nameof(TransferStatus.Completed));

            (await wallet.GetTransferAsync("0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c", ct)).Should().BeNull();
            var missing = () => wallet.QueryTransferByIdAsync("0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c", ct);
            await missing.Should().ThrowAsync<NSpark.Exceptions.SparkUntrustedResponseException>();
        });

        state.Methods.Should().Equal("query_transfers_by_id", "query_transfers_by_id", "query_transfers_by_id");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task History_lists_the_reference_SDKs_transfer_types_in_the_direction_asked_for(CancellationToken ct)
    {
        var state = FakeOperatorState.Accepting();

        var identity = await FakeOperator.RunAsync(state, async wallet =>
        {
            await wallet.GetTransfersAsync(limit: 10, ct: ct);
            await wallet.GetTransfersAsync(limit: 10, direction: TransferDirection.Sent, ct: ct);
            await wallet.GetTransfersAsync(limit: 10, direction: TransferDirection.Received, ct: ct);
            return wallet.IdentityPublicKey;
        });

        var filters = state.TransferFilters;
        filters.Should().HaveCount(3);
        foreach (var filter in filters)
        {
            filter.Types_.Should().Equal(TransferType.CooperativeExit, TransferType.PreimageSwap, TransferType.UtxoSwap, TransferType.Transfer);
            filter.TransferIds.Should().BeEmpty();
            filter.Limit.Should().Be(10);
            filter.Network.Should().Be(Network.Regtest);
        }

        filters[0].SenderOrReceiverIdentityPublicKey.ToByteArray().Should().Equal(identity);
        filters[1].SenderIdentityPublicKey.ToByteArray().Should().Equal(identity);
        filters[2].ReceiverIdentityPublicKey.ToByteArray().Should().Equal(identity);
    }
}
