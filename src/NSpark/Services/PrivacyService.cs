using NSpark.Models;
using NSpark.Proto;

namespace NSpark.Services;

/// <summary>
/// Extension methods on <see cref="SparkWallet"/> for managing the server-side
/// wallet settings stored with the Signing Operators (privacy toggles, etc.).
/// </summary>
public static class PrivacyService
{
    /// <summary>
    /// Toggle the server-side privacy flag for the wallet. When enabled, the
    /// Signing Operators apply additional privacy heuristics to the wallet's
    /// transfers; see <c>docs/configuration.md</c> for the per-operator
    /// behaviour.
    /// </summary>
    /// <param name="wallet">The wallet whose setting to update.</param>
    /// <param name="enabled">New privacy flag value.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated <see cref="WalletSettings"/> as confirmed by the SO.</returns>
    public static async Task<WalletSettings> SetPrivacyEnabledAsync(
        this SparkWallet wallet, bool enabled, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var client = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);

        var request = new UpdateWalletSettingRequest
        {
            PrivateEnabled = enabled,
        };

        var response = await client.update_wallet_settingAsync(request, headers, cancellationToken: ct).ConfigureAwait(false);

        return ToModel(response.WalletSetting);
    }

    /// <summary>
    /// Read the current server-side wallet settings from the Signing Operators.
    /// </summary>
    /// <param name="wallet">The wallet to read settings for.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<WalletSettings> GetWalletSettingsAsync(
        this SparkWallet wallet, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var client = wallet.GetCoordinatorClient();
        var headers = await wallet.GetCoordinatorAuthMetadataAsync(ct).ConfigureAwait(false);

        var response = await client.query_wallet_settingAsync(
            new QueryWalletSettingRequest(), headers, cancellationToken: ct).ConfigureAwait(false);

        return ToModel(response.WalletSetting);
    }

    private static WalletSettings ToModel(WalletSetting setting)
    {
        var ownerHex = Convert.ToHexString(setting.OwnerIdentityPublicKey.ToByteArray()).ToLowerInvariant();

        string? masterHex = null;
        if (setting.HasMasterIdentityPublicKey && !setting.MasterIdentityPublicKey.IsEmpty)
        {
            masterHex = Convert.ToHexString(setting.MasterIdentityPublicKey.ToByteArray()).ToLowerInvariant();
        }

        return new WalletSettings(ownerHex, setting.PrivateEnabled, masterHex);
    }
}
