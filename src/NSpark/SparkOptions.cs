namespace NSpark;

/// <summary>The Spark network NSpark connects to.</summary>
public enum SparkNetwork
{
    /// <summary>The production Bitcoin mainnet Spark network.</summary>
    Mainnet,

    /// <summary>A local-development regtest cluster (typically <c>http://localhost:900x</c>).</summary>
    Regtest,
}

/// <summary>
/// Signing Operator configuration: address, identifier, and identity public key.
/// The identifier is used as the map key in gRPC requests.
/// The identity public key is used for ECIES encryption of secret shares.
/// </summary>
/// <param name="Address">Operator URL (HTTPS in production).</param>
/// <param name="Identifier">32-byte hex identifier used by the SO coordination map.</param>
/// <param name="IdentityPublicKeyHex">Operator's secp256k1 identity public key (hex), used for ECIES encryption of secret shares.</param>
public sealed record SigningOperatorConfig(string Address, string Identifier, string IdentityPublicKeyHex);

/// <summary>
/// Top-level configuration for an <see cref="SparkConnection"/>. Values may be supplied
/// inline or bound from <c>IConfiguration</c> via the standard options pattern.
/// </summary>
/// <remarks>
/// The default values target Spark mainnet with Lightspark's SSP and the three production
/// Signing Operators. See <c>docs/trust-model.md</c> for the explicit trust assumptions
/// these defaults imply.
/// </remarks>
public sealed class SparkOptions
{
    /// <summary>The Spark network to connect to.</summary>
    public SparkNetwork Network { get; set; } = SparkNetwork.Mainnet;

    /// <summary>
    /// The Signing Operators NSpark will talk to. Defaults to the production
    /// operators for the selected <see cref="Network"/>.
    /// </summary>
    public SigningOperatorConfig[] SigningOperators { get; set; } = GetDefaultOperators(SparkNetwork.Mainnet);

    /// <summary>Convenience accessor that exposes only the SO URLs.</summary>
    public string[] SigningOperatorAddresses => SigningOperators.Select(o => o.Address).ToArray();

    /// <summary>The Spark Service Provider GraphQL endpoint NSpark routes Lightning operations through.</summary>
    public string SspUrl { get; set; } = "https://api.lightspark.com/graphql/spark/2025-03-19";

    /// <summary>
    /// SSP (Spark Service Provider) identity public key, used as the HTLC hashlock destination
    /// and receiver identity in Lightning swap flows. Network-specific hardcoded values from the SDK.
    /// </summary>
    public string SspIdentityPublicKeyHex { get; set; } = GetSspIdentityPublicKey(SparkNetwork.Mainnet);

    /// <summary>
    /// FROST signing threshold the operators enforce. <c>null</c> (the default) derives it from
    /// the number of configured operators the way the Spark deployments do: 2 of 3 on mainnet,
    /// 3 of 5. Set it explicitly only for a custom operator set.
    /// </summary>
    public uint? SigningThreshold { get; set; }

    /// <summary>
    /// Withdraw bond, in satoshis, the coordinator is expected to set on every token output it
    /// finalises. A final token transaction carrying a different bond is refused before the
    /// wallet signs it (reference SDK default: 10 000).
    /// </summary>
    public ulong ExpectedWithdrawBondSats { get; set; } = 10_000;

    /// <summary>
    /// Relative block locktime the coordinator is expected to set on every token output it
    /// finalises. A final token transaction carrying a different locktime is refused before the
    /// wallet signs it (reference SDK default: 1 000).
    /// </summary>
    public ulong ExpectedWithdrawRelativeBlockLocktime { get; set; } = 1_000;

    /// <summary>
    /// The signing threshold in effect: <see cref="SigningThreshold"/> when set, otherwise the
    /// deployment default for the configured operator count.
    /// </summary>
    public uint EffectiveSigningThreshold => SigningThreshold ?? DefaultSigningThreshold(SigningOperators.Length);

    /// <summary>The threshold the Spark deployments use for a given operator count (2 of 3, 3 of 5).</summary>
    public static uint DefaultSigningThreshold(int operatorCount) =>
        Math.Max(2u, ((uint)Math.Max(operatorCount, 0) + 2u) / 2u);

    /// <summary>Return the canonical SSP identity public key for the given network.</summary>
    public static string GetSspIdentityPublicKey(SparkNetwork network) => network switch
    {
        SparkNetwork.Mainnet => "023e33e2920326f64ea31058d44777442d97d7d5cbfcf54e3060bc1695e5261c93",
        SparkNetwork.Regtest => "022bf283544b16c0622daecb79422007d167eca6ce9f0c98c0c49833b1f7170bfe",
        _ => throw new ArgumentOutOfRangeException(nameof(network)),
    };

    /// <summary>Return the canonical Signing Operator configuration for the given network.</summary>
    public static SigningOperatorConfig[] GetDefaultOperators(SparkNetwork network) => network switch
    {
        SparkNetwork.Mainnet =>
        [
            new("https://0.spark.lightspark.com",
                "0000000000000000000000000000000000000000000000000000000000000001",
                "03dfbdff4b6332c220f8fa2ba8ed496c698ceada563fa01b67d9983bfc5c95e763"),
            new("https://spark-operator.breez.technology",
                "0000000000000000000000000000000000000000000000000000000000000002",
                "03e625e9768651c9be268e287245cc33f96a68ce9141b0b4769205db027ee8ed77"),
            new("https://2.spark.flashnet.xyz",
                "0000000000000000000000000000000000000000000000000000000000000003",
                "022eda13465a59205413086130a65dc0ed1b8f8e51937043161f8be0c369b1a410"),
        ],
        SparkNetwork.Regtest =>
        [
            new("http://localhost:9001",
                "0000000000000000000000000000000000000000000000000000000000000001",
                ""),
            new("http://localhost:9002",
                "0000000000000000000000000000000000000000000000000000000000000002",
                ""),
            new("http://localhost:9003",
                "0000000000000000000000000000000000000000000000000000000000000003",
                ""),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(network)),
    };
}
