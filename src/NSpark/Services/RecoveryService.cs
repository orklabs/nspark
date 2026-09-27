using Google.Protobuf;
using NSpark.Models;
using NSpark.Proto;

namespace NSpark.Services;

/// <summary>
/// Extension methods on <see cref="SparkWallet"/> for capturing unilateral-exit
/// recovery snapshots: the wallet's leaves plus the ancestor transaction chains
/// an exit package needs to replay them on-chain without operator cooperation.
/// </summary>
public static class RecoveryService
{
    private const int MaxRepairAttempts = 10;

    /// <summary>
    /// Same set <c>GetBalanceAsync</c> counts as owned. Broader than
    /// <c>GetLeavesAsync</c>'s AVAILABLE-only view: a snapshot must also cover
    /// leaves momentarily locked by an in-flight transfer, split, or renewal.
    /// </summary>
    private static readonly HashSet<string> OwnedStatuses = new(StringComparer.Ordinal)
    {
        "AVAILABLE",
        "TRANSFER_LOCKED",
        "SPLIT_LOCKED",
        "AGGREGATE_LOCK",
        "RENEW_LOCKED",
    };

    /// <summary>
    /// Fetch the wallet's leaves plus the complete ancestor chain of every leaf.
    /// </summary>
    /// <remarks>
    /// The bulk include-parents query can omit nodes (notably legacy tree roots),
    /// so referenced-but-missing parents are re-fetched by node id until every
    /// chain terminates at a root. Throws if a chain still cannot be completed —
    /// callers must NOT persist a snapshot from a failed call over a previous
    /// good one, because an incomplete snapshot is useless for a unilateral exit.
    /// </remarks>
    public static async Task<SparkRecoverySnapshot> GetRecoverySnapshotAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);

        var client = wallet.GetLargeMessageSparkClient(wallet.CoordinatorAddress);

        // Paged at the operators' 100 nodes per page, on the large-message channel: with
        // include_parents every page carries the leaves' ancestor chains too.
        var request = new QueryNodesRequest
        {
            OwnerIdentityPubkey = ByteString.CopyFrom(wallet.IdentityPublicKey),
            IncludeParents = true,
            Network = wallet.Options.ProtoNetwork(),
        };
        var all = new Dictionary<string, TreeNode>(
            await wallet.QueryAllNodesAsync(request, client, ct).ConfigureAwait(false),
            StringComparer.Ordinal);

        // Repair pass: fetch any parent referenced by a node in the map but not
        // present in it. Bounded so a coordinator that keeps returning nothing
        // can't loop us forever; no-progress also exits. Best-effort here — the
        // build step is the arbiter of whether the chains that MATTER are whole.
        var missing = MissingParentIds(all);
        var attempts = 0;
        while (missing.Count > 0 && attempts < MaxRepairAttempts)
        {
            attempts++;

            var repairRequest = new QueryNodesRequest
            {
                NodeIds = new TreeNodeIds(),
                IncludeParents = true,
            };
            repairRequest.NodeIds.NodeIds.AddRange(missing.OrderBy(id => id, StringComparer.Ordinal));

            var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);
            var repairResponse = await client.query_nodesAsync(
                repairRequest, headers, cancellationToken: ct).ConfigureAwait(false);

            var countBefore = all.Count;
            foreach (var kv in repairResponse.Nodes)
            {
                all[kv.Key] = kv.Value;
            }

            if (all.Count == countBefore)
            {
                break;
            }

            missing = MissingParentIds(all);
        }

        return BuildRecoverySnapshot(all, wallet.IdentityPublicKey, wallet.Options.Network.GraphQLName());
    }

    /// <summary>Parent ids referenced by nodes in the map but absent from it.</summary>
    internal static HashSet<string> MissingParentIds(IReadOnlyDictionary<string, TreeNode> all)
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in all.Values)
        {
            if (node.HasParentNodeId && node.ParentNodeId.Length > 0 && !all.ContainsKey(node.ParentNodeId))
            {
                missing.Add(node.ParentNodeId);
            }
        }

        return missing;
    }

    /// <summary>
    /// Pure classification of a complete node map into snapshot leaves + ancestors.
    /// A leaf is a node we own, in a spendable/locked status, that no other node
    /// claims as parent. Ancestors are PRUNED to the union of the current leaves'
    /// parent chains — the owner query also returns historical nodes (old splits,
    /// spent intermediates) that no exit package will ever use, and keeping them
    /// bloats the bundle severalfold. Throws if a needed chain has a hole.
    /// </summary>
    internal static SparkRecoverySnapshot BuildRecoverySnapshot(
        IReadOnlyDictionary<string, TreeNode> all,
        byte[] identityPublicKey,
        string network)
    {
        var identity = ByteString.CopyFrom(identityPublicKey);

        var referencedAsParent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in all.Values)
        {
            if (node.HasParentNodeId && node.ParentNodeId.Length > 0)
            {
                referencedAsParent.Add(node.ParentNodeId);
            }
        }

        var leaves = new List<SparkRecoveryLeaf>();
        var leafIds = new List<string>();
        foreach (var (id, node) in all)
        {
            var isLeaf = node.OwnerIdentityPublicKey.Equals(identity)
                && OwnedStatuses.Contains(node.Status)
                && !referencedAsParent.Contains(id);
            if (!isLeaf)
            {
                continue;
            }

            leaves.Add(new SparkRecoveryLeaf(id, node.Status, TransferMapping.ReportedSats(node.Value), ToHex(node)));
            leafIds.Add(id);
        }

        // Walk each leaf's chain to its root, collecting exactly the ancestors
        // an exit package needs. A hole in a needed chain makes the snapshot
        // useless for that leaf — refuse to produce one (callers then keep
        // their previous good file).
        var neededIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leafId in leafIds)
        {
            var cursor = all[leafId];
            while (cursor.HasParentNodeId && cursor.ParentNodeId.Length > 0)
            {
                var parentId = cursor.ParentNodeId;
                if (!all.TryGetValue(parentId, out var parent))
                {
                    throw new InvalidOperationException(
                        $"Recovery snapshot incomplete: missing ancestor {parentId} above leaf {leafId}.");
                }

                if (!neededIds.Add(parentId))
                {
                    break; // chain already walked
                }

                cursor = parent;
            }
        }

        var nodes = neededIds
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => new SparkRecoveryNode(id, ToHex(all[id])))
            .ToList();

        // Deterministic ordering so identical wallet state yields identical bytes.
        leaves.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        return new SparkRecoverySnapshot(
            Network: network,
            IdentityPublicKeyHex: Convert.ToHexString(identityPublicKey).ToLowerInvariant(),
            Leaves: leaves,
            Nodes: nodes);
    }

    private static string ToHex(TreeNode node) => Convert.ToHexString(node.ToByteArray()).ToLowerInvariant();
}
