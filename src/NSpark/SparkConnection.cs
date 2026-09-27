using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSpark.Connection;
using NSpark.Diagnostics;
using NSpark.Signer;

namespace NSpark;

/// <summary>
/// Singleton host that owns the shared transport infrastructure for one or more
/// <see cref="SparkWallet"/> instances: gRPC channels to Signing Operators, the
/// HTTP client used for SSP GraphQL, and the auth-token cache. One instance per
/// application is the recommended deployment.
/// </summary>
/// <remarks>
/// The type is named <c>SparkConnection</c> for legacy compatibility; it will be
/// renamed to <c>SparkConnection</c> when the namespace becomes <c>NSpark</c>
/// in the v1 release (release plan, Phase 6).
/// </remarks>
public sealed class SparkConnection : IDisposable, IAsyncDisposable
{
    private readonly GrpcConnectionPool _pool;
    private readonly HttpClient _httpClient;
    private readonly SparkOptions _options;
    private readonly SparkAuthenticator _authenticator;
    private readonly ServerTimeSync _timeSync;
    private readonly SspAuthenticator _sspAuthenticator;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SparkConnection> _logger;
    private int _disposed;

    /// <summary>
    /// Construct a new client with the given options and an HTTP client provided by
    /// <c>IHttpClientFactory</c>. Use <see cref="ServiceCollectionExtensions.AddSpark"/>
    /// to register the standard set of dependencies.
    /// </summary>
    public SparkConnection(IOptions<SparkOptions> options, HttpClient httpClient)
        : this(options, httpClient, NullLoggerFactory.Instance)
    {
    }

    /// <summary>
    /// Construct a new client with explicit logger factory wiring.
    /// </summary>
    public SparkConnection(
        IOptions<SparkOptions> options,
        HttpClient httpClient,
        ILoggerFactory loggerFactory)
        : this(options, httpClient, loggerFactory, operatorHandler: null)
    {
    }

    /// <summary>
    /// Construct a client whose operator channels send over <paramref name="operatorHandler"/>
    /// instead of the network, when it is set — the seam unit tests use to stand in for operators.
    /// </summary>
    internal SparkConnection(
        IOptions<SparkOptions> options,
        HttpClient httpClient,
        ILoggerFactory loggerFactory,
        HttpMessageHandler? operatorHandler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options.Value;
        _httpClient = httpClient;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SparkConnection>();

        // One clock for every operator: fed by their answers, it times session tokens and
        // token transactions (see ServerTimeSync).
        _timeSync = new ServerTimeSync();
        // Operator traffic carries session tokens and signing material: TLS only on mainnet.
        _pool = new GrpcConnectionPool(
            _options.SigningOperatorAddresses,
            allowPlaintext: _options.Network != SparkNetwork.Mainnet,
            clock: _timeSync,
            httpHandler: operatorHandler);
        _authenticator = new SparkAuthenticator(
            loggerFactory.CreateLogger<SparkAuthenticator>(),
            clock: _timeSync);
        _sspAuthenticator = new SspAuthenticator(_httpClient, _options.SspUrl);

        _logger.LogInformation(
            LogEvents.ClientReady,
            "NSpark client ready on {Network} with {SoCount} signing operators.",
            _options.Network,
            _options.SigningOperators.Length);
    }

    internal GrpcConnectionPool Pool => _pool;

    internal HttpClient HttpClient => _httpClient;

    internal SparkAuthenticator Authenticator => _authenticator;

    internal ServerTimeSync TimeSync => _timeSync;

    internal SspAuthenticator SspAuthenticator => _sspAuthenticator;

    /// <summary>Cancelled when the connection is disposed: long-running work (event streams) stops with it.</summary>
    internal CancellationToken Lifetime => _lifetime.Token;

    internal ILoggerFactory LoggerFactory => _loggerFactory;

    /// <summary>The configured options for this client.</summary>
    public SparkOptions Options => _options;

    /// <summary>
    /// Create a wallet instance from a BIP-39 mnemonic. The wallet caches the identity
    /// and deposit public keys via one round-trip to the signer at construction time.
    /// </summary>
    /// <remarks>
    /// The phrase is validated against the BIP-39 English wordlist and checksum first: a mistyped
    /// phrase would otherwise derive a different, empty wallet. For a phrase known to be
    /// non-standard, build the signer with
    /// <c>SparkSigner.FromMnemonic(mnemonic, account, passphrase, validateMnemonic: false)</c> and
    /// pass it to <see cref="CreateWalletAsync(ISparkSigner, CancellationToken)"/>.
    /// </remarks>
    /// <exception cref="Exceptions.SparkConfigurationException">The mnemonic fails BIP-39 validation.</exception>
#pragma warning disable RS0026 // Optional parameters on parallel overloads — alpha API.
    public Task<SparkWallet> CreateWalletAsync(
        string mnemonic,
        int? account = null,
        string? passphrase = null,
        CancellationToken ct = default)
#pragma warning restore RS0026
    {
        ArgumentException.ThrowIfNullOrEmpty(mnemonic);
        var effectiveAccount = account ?? (_options.Network == SparkNetwork.Regtest ? 0 : 1);
        var signer = SparkSigner.FromMnemonic(mnemonic, effectiveAccount, passphrase);
        return CreateWalletAsync(signer, ct);
    }

    /// <summary>
    /// Create a wallet instance from an existing signer. Performs one round-trip to the
    /// signer to fetch and cache the identity and deposit public keys.
    /// </summary>
#pragma warning disable RS0026
    public async Task<SparkWallet> CreateWalletAsync(ISparkSigner signer, CancellationToken ct = default)
#pragma warning restore RS0026
    {
        ArgumentNullException.ThrowIfNull(signer);

        var identityPubKey = await signer.GetIdentityPublicKeyAsync(ct).ConfigureAwait(false);
        var depositPubKey = await signer.GetDepositPublicKeyAsync(ct).ConfigureAwait(false);

        var sspClient = new SspGraphQLClient(
            _httpClient,
            _options.SspUrl,
            innerCt => _sspAuthenticator.GetTokenAsync(signer, identityPubKey, innerCt),
            token => _sspAuthenticator.Invalidate(identityPubKey, token));

        return new SparkWallet(this, signer, sspClient, identityPubKey, depositPubKey);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _logger.LogInformation(LogEvents.ClientDisposing, "NSpark client disposing.");
        _lifetime.Cancel();
        _authenticator.ClearCache();
        _pool.Dispose();
        _lifetime.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // No asynchronous resources yet — both gRPC channels and HttpClient
        // are managed by the IHttpClientFactory pipeline. This API exists so
        // hosts that always-await disposal can do so cleanly.
        Dispose();
        return ValueTask.CompletedTask;
    }
}
