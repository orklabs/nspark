using System.Text.Json;

namespace NSpark.Services;

/// <inheritdoc/>
public static class LightningAddressService
{
    /// <summary>
    /// Resolve a Lightning address (<c>user@domain</c>) to a concrete BOLT11 invoice for a given
    /// amount, by walking the LNURL-pay protocol:
    /// <list type="number">
    ///   <item>GET <c>https://domain/.well-known/lnurlp/user</c> → <c>{ callback, minSendable, maxSendable }</c></item>
    ///   <item>GET <c>callback?amount={millisats}</c> → <c>{ pr: "lnbc..." }</c></item>
    /// </list>
    /// Useful for callers that want to display a fee estimate or otherwise inspect the BOLT11
    /// before sending. Pair with <see cref="LightningService.GetLightningSendFeeEstimateAsync"/>
    /// to show the user a final invoice fee before they confirm.
    /// </summary>
    /// <param name="wallet">The Spark wallet (used only for its <see cref="SparkConnection.HttpClient"/>; no signing).</param>
    /// <param name="lightningAddress">Address in <c>user@domain</c> form.</param>
    /// <param name="amountSats">Amount to encode in the requested BOLT11.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The BOLT11 payment request string returned by the LNURL-pay callback.</returns>
    public static async Task<string> ResolveLightningAddressAsync(
        this SparkWallet wallet,
        string lightningAddress,
        long amountSats,
        CancellationToken ct = default)
    {
        var parts = lightningAddress.Split('@', 2);
        if (parts.Length != 2)
        {
            throw new ArgumentException($"Invalid Lightning address format: {lightningAddress}");
        }

        var user = parts[0];
        var domain = parts[1];
        var http = wallet.Client.HttpClient;

        // Step 1: Fetch LNURL-pay metadata
        var metadataUrl = $"https://{domain}/.well-known/lnurlp/{user}";
        var metadataJson = await http.GetStringAsync(metadataUrl, ct).ConfigureAwait(false);
        using var metadata = JsonDocument.Parse(metadataJson);
        var root = metadata.RootElement;

        var callback = root.GetProperty("callback").GetString()
            ?? throw new InvalidOperationException("LNURL-pay response missing 'callback'.");
        var minSendable = root.GetProperty("minSendable").GetInt64();
        var maxSendable = root.GetProperty("maxSendable").GetInt64();

        var amountMsats = amountSats * 1000;
        if (amountMsats < minSendable || amountMsats > maxSendable)
        {
            throw new InvalidOperationException(
                $"Amount {amountSats} sats ({amountMsats} msats) outside allowed range [{minSendable}, {maxSendable}] msats.");
        }

        // Step 2: Request invoice from callback
        var separator = callback.Contains('?') ? "&" : "?";
        var invoiceUrl = $"{callback}{separator}amount={amountMsats}";
        var invoiceJson = await http.GetStringAsync(invoiceUrl, ct).ConfigureAwait(false);
        using var invoiceDoc = JsonDocument.Parse(invoiceJson);

        return invoiceDoc.RootElement.GetProperty("pr").GetString()
            ?? throw new InvalidOperationException("LNURL-pay callback response missing 'pr'.");
    }

    /// <summary>
    /// Pay a Lightning address (<c>user@domain</c>): resolve via LNURL-pay then pay the resulting
    /// BOLT11 via <see cref="LightningService.PayLightningInvoiceAsync"/>. The issued invoice
    /// must carry <paramref name="amountSats"/>; the routing fee is bounded by
    /// <paramref name="maxFeeSats"/> like any other Lightning send.
    /// </summary>
    public static async Task<string> PayLightningAddressAsync(
        this SparkWallet wallet,
        string lightningAddress,
        long amountSats,
        long maxFeeSats,
        CancellationToken ct = default)
    {
        var pr = await wallet.ResolveLightningAddressAsync(lightningAddress, amountSats, ct).ConfigureAwait(false);
        return await wallet.PayLightningInvoiceAsync(pr, maxFeeSats, ct: ct).ConfigureAwait(false);
    }
}
