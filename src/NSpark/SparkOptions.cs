namespace NSpark;

/// <summary>The Spark network NSpark connects to.</summary>
public enum SparkNetwork
{
    /// <summary>The production Bitcoin mainnet Spark network.</summary>
    Mainnet,

    /// <summary>
    /// Regtest. The preset uses the hosted operators, which serve both networks under the same
    /// keys, as the reference SDK's REGTEST preset does; a local cluster can be configured through
    /// <see cref="SparkOptions.SigningOperators"/> (plaintext <c>http://</c> is allowed on regtest).
    /// </summary>
    Regtest,
}

/// <summary>How token transactions are sent to the operators.</summary>
public enum TokenTransactionVersion
{
    /// <summary>
    /// One <c>broadcast_transaction</c> call, signed over the protohash of the partial
    /// transaction: the reference SDK's default, and the format the operators are moving to.
    /// </summary>
    V3,

    /// <summary>
    /// <c>start_transaction</c> then <c>commit_transaction</c>, signed over the V2 hashes. Kept
    /// while the operators accept it, as the reference SDK keeps it.
    /// </summary>
    V2,
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

    /// <summary>Lightspark's hosted SSP, the default <see cref="SspUrl"/>.</summary>
    public const string DefaultSspUrl = "https://api.lightspark.com/graphql/spark/2025-03-19";

    /// <summary>The Spark Service Provider GraphQL endpoint NSpark routes Lightning operations through.</summary>
    public string SspUrl { get; set; } = DefaultSspUrl;

    /// <summary>
    /// The SSP's identity public key (hex): the receiver of every transfer to the SSP — Lightning
    /// sends, leaf swaps, cooperative exits. <c>null</c> (the default) uses the default SSP's key
    /// for the <see cref="Network"/>, but only while <see cref="SspUrl"/> is the default SSP: a
    /// custom SSP needs its own key, and without one those operations refuse to run rather than
    /// address Lightspark's key while another SSP is asked to act, as the reference SDK takes the
    /// SSP's URL and key together.
    /// </summary>
    public string? SspIdentityPublicKeyHex { get; set; }

    /// <summary>How token transactions are sent: <see cref="TokenTransactionVersion.V3"/> by default, as in the reference SDK.</summary>
    public TokenTransactionVersion TokenTransactionVersion { get; set; } = TokenTransactionVersion.V3;

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

    /// <summary>
    /// The SSP identity key in effect: <see cref="SspIdentityPublicKeyHex"/> when set, else the
    /// default SSP's key for the network when <see cref="SspUrl"/> is the default SSP, else
    /// <c>null</c>.
    /// </summary>
    public string? EffectiveSspIdentityPublicKeyHex =>
        SspIdentityPublicKeyHex
        ?? (string.Equals(SspUrl, DefaultSspUrl, StringComparison.Ordinal) ? GetSspIdentityPublicKey(Network) : null);

    /// <summary>
    /// The SSP's identity key for a transfer to it. Throws when there is none (a custom
    /// <see cref="SspUrl"/> without <see cref="SspIdentityPublicKeyHex"/>) or it is not a
    /// compressed secp256k1 key, before any leaf moves.
    /// </summary>
    internal byte[] RequireSspIdentityPublicKey()
    {
        var hex = EffectiveSspIdentityPublicKeyHex;
        byte[]? key = null;
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                key = Convert.FromHexString(hex.Trim());
            }
            catch (FormatException)
            {
                key = null;
            }
        }

        if (key is not { Length: 33 } || (key[0] != 0x02 && key[0] != 0x03))
        {
            throw new Exceptions.SparkConfigurationException(
                "ssp.identity",
                "No valid SSP identity key: a custom SspUrl needs SspIdentityPublicKeyHex, the SSP's compressed public key.");
        }

        return key;
    }

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

    /// <summary>
    /// Return the canonical Signing Operator configuration for the given network: the hosted
    /// operators, which serve mainnet and regtest under the same keys (the reference SDK's
    /// REGTEST preset uses them too).
    /// </summary>
    public static SigningOperatorConfig[] GetDefaultOperators(SparkNetwork network) => network switch
    {
        SparkNetwork.Mainnet or SparkNetwork.Regtest =>
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
        _ => throw new ArgumentOutOfRangeException(nameof(network)),
    };
}
