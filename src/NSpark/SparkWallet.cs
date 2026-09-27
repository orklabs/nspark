using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Core.Interceptors;
using NSpark.Connection;
using NSpark.Proto;
using NSpark.Proto.Token;
using NSpark.Services;
using NSpark.Signer;

namespace NSpark;

/// <summary>
/// Per-wallet instance holding the signer and exposing Spark operations. Lightweight —
/// references shared connections from <see cref="SparkConnection"/>. The wallet caches
/// the identity and deposit public keys at construction so synchronous accessors
/// (<see cref="IdentityPublicKey"/>, <see cref="GetSparkAddress"/>) stay non-async even
/// when the signer is remote.
/// </summary>
/// <remarks>
/// Operator calls made through a wallet go through its auth interceptor
/// (<see cref="AuthRetryInterceptor"/>): each attempt carries the operator's current session
/// token, and one the operator rejects is dropped and the call re-issued with a fresh one.
/// </remarks>
public sealed class SparkWallet
{
    private readonly SparkConnection _client;
    private readonly ISparkSigner _signer;
    private readonly SspGraphQLClient _sspClient;
    private readonly byte[] _identityPublicKey;
    private readonly byte[] _depositPublicKey;
    private readonly ConcurrentDictionary<string, CallInvoker> _authenticatedInvokers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CallInvoker> _authenticatedLargeInvokers = new(StringComparer.Ordinal);

    internal SparkWallet(
        SparkConnection client,
        ISparkSigner signer,
        SspGraphQLClient sspClient,
        byte[] identityPublicKey,
        byte[] depositPublicKey)
    {
        _client = client;
        _signer = signer;
        _sspClient = sspClient;
        _identityPublicKey = identityPublicKey;
        _depositPublicKey = depositPublicKey;
    }

    internal SparkConnection Client => _client;
    internal ISparkSigner Signer => _signer;
    internal SspGraphQLClient SspClient => _sspClient;
    internal GrpcConnectionPool Pool => _client.Pool;
    internal SparkAuthenticator Authenticator => _client.Authenticator;
    internal SparkOptions Options => _client.Options;

    /// <summary>The operators' clock, as estimated from their answers.</summary>
    internal ServerTimeSync Clock => _client.TimeSync;

    /// <summary>
    /// Serialises this wallet's transfer claims, as the reference SDK's <c>claimTransferMutex</c>
    /// does: a claim pass, a swap's claim of its counter-transfer and a drain never race each other
    /// for the same transfer. Waiters are served in arrival order.
    /// </summary>
    internal SemaphoreSlim ClaimLock { get; } = new(1, 1);

    /// <summary>Token outputs picked by sends that may still be in flight (see <see cref="TokenOutputLocks"/>).</summary>
    internal TokenOutputLocks TokenOutputLocks { get; } = new();

    /// <summary>Token transfers sent with an idempotency key (see <see cref="TokenTransferAttempts"/>).</summary>
    internal TokenTransferAttempts TokenTransferAttempts { get; } = new();

    /// <summary>The coordinator: the first configured operator.</summary>
    internal string CoordinatorAddress => _client.Options.SigningOperators[0].Address;

    /// <summary>Identity public key (33-byte compressed secp256k1) cached at wallet creation.</summary>
    public byte[] IdentityPublicKey => _identityPublicKey;

    /// <summary>Deposit public key (33-byte compressed secp256k1) cached at wallet creation.</summary>
    public byte[] DepositPublicKey => _depositPublicKey;

    /// <summary>Identity public key hex string for this wallet.</summary>
    public string IdentityPublicKeyHex => Convert.ToHexString(_identityPublicKey).ToLowerInvariant();

    /// <summary>
    /// Get the Spark address for this wallet (Bech32m-encoded identity public key).
    /// </summary>
    public string GetSparkAddress() => Services.SparkAddress.Encode(_identityPublicKey, _client.Options.Network);

    /// <summary>Get auth metadata for gRPC calls to a specific SO.</summary>
    internal async Task<Metadata> GetAuthMetadataAsync(string soAddress, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(soAddress, ct).ConfigureAwait(false);
        return new Metadata { { AuthRetryInterceptor.AuthorizationHeader, $"Bearer {token}" } };
    }

    /// <summary>Auth metadata for the coordinator.</summary>
    internal Task<Metadata> GetCoordinatorAuthMetadataAsync(CancellationToken ct = default) =>
        GetAuthMetadataAsync(CoordinatorAddress, ct);

    /// <summary>The operator's current session token for this wallet.</summary>
    internal Task<string> GetTokenAsync(string soAddress, CancellationToken ct) =>
        Authenticator.GetTokenAsync(Pool, soAddress, _signer, _identityPublicKey, ct);

    /// <summary>A SparkService client for an operator, through this wallet's auth interceptor.</summary>
    internal SparkService.SparkServiceClient GetSparkClient(string soAddress) =>
        new(AuthenticatedInvoker(soAddress, _authenticatedInvokers, Pool.GetCallInvoker));

    /// <summary>A SparkService client for the coordinator, through this wallet's auth interceptor.</summary>
    internal SparkService.SparkServiceClient GetCoordinatorClient() => GetSparkClient(CoordinatorAddress);

    /// <summary>A SparkTokenService client for an operator, through this wallet's auth interceptor.</summary>
    internal SparkTokenService.SparkTokenServiceClient GetTokenClient(string soAddress) =>
        new(AuthenticatedInvoker(soAddress, _authenticatedInvokers, Pool.GetCallInvoker));

    /// <summary>A SparkService client on the operator's large-message channel (recovery snapshots).</summary>
    internal SparkService.SparkServiceClient GetLargeMessageSparkClient(string soAddress) =>
        new(AuthenticatedInvoker(soAddress, _authenticatedLargeInvokers, Pool.GetLargeMessageCallInvoker));

    private CallInvoker AuthenticatedInvoker(
        string soAddress,
        ConcurrentDictionary<string, CallInvoker> cache,
        Func<string, CallInvoker> transport)
    {
        return cache.GetOrAdd(soAddress, address => transport(address).Intercept(new AuthRetryInterceptor(
            ct => GetTokenAsync(address, ct),
            token => Authenticator.Invalidate(address, _identityPublicKey, token))));
    }
}
