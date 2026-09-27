using System.Globalization;
using System.Text;
using NSpark.Exceptions;

namespace NSpark.Services;

/// <summary>Bitcoin network a BOLT-11 invoice was issued for (its human-readable-part currency prefix).</summary>
internal enum Bolt11Network
{
    /// <summary><c>lnbc</c>.</summary>
    Mainnet,

    /// <summary><c>lntb</c>.</summary>
    Testnet,

    /// <summary><c>lntbs</c>.</summary>
    Signet,

    /// <summary><c>lnbcrt</c>.</summary>
    Regtest,
}

/// <summary>
/// A decoded BOLT-11 payment request.
/// </summary>
/// <remarks>
/// The decoder follows the reader rules of BOLT-11: bech32 checksum, all-lower or all-upper
/// case, a recognised currency prefix, an integer amount with an optional
/// <c>m</c>/<c>u</c>/<c>n</c>/<c>p</c> multiplier (<c>p</c> amounts must be a multiple of 10),
/// and tagged fields whose lengths are validated. Amount arithmetic is overflow-checked so a
/// hostile string throws <see cref="InvalidBolt11Exception"/> instead of misparsing. The
/// signature is not verified here; the SSP and the operators verify it before anything is
/// paid. Mirrors the Swift SDK's <c>Bolt11Invoice</c> and the reference SDK's
/// <c>decodeInvoice</c>.
/// </remarks>
/// <param name="Network">Network the invoice belongs to.</param>
/// <param name="PaymentHash">32-byte payment hash (tag <c>p</c>).</param>
/// <param name="AmountMsat">Amount in millisatoshi, <c>null</c> for an amountless invoice.</param>
/// <param name="Timestamp">Invoice creation time, seconds since the Unix epoch.</param>
/// <param name="ExpirySeconds">Seconds after <paramref name="Timestamp"/> until the invoice expires (tag <c>x</c>, default 3600).</param>
/// <param name="PaymentSecret">
/// Payment secret (tag <c>s</c>). BOLT-11 readers must fail a payment without one, and the
/// reference SDK refuses such invoices, so decoding does too.
/// </param>
/// <param name="Description">Short description (tag <c>d</c>), if present.</param>
/// <param name="SparkFallback">
/// A Spark payment target embedded in the invoice, decoded as the reference SDK does: a Spark
/// invoice in a fallback-address field (tag <c>f</c>, version 31), or else a Spark identity public
/// key (hex) in a route hint whose short channel id is the sentinel <c>f42400f424000001</c>.
/// </param>
internal sealed record Bolt11Invoice(
    Bolt11Network Network,
    byte[] PaymentHash,
    ulong? AmountMsat,
    ulong Timestamp,
    ulong ExpirySeconds,
    byte[] PaymentSecret,
    string? Description,
    string? SparkFallback = null)
{
    /// <summary>Fallback-address version the reference SDK uses for an embedded Spark invoice.</summary>
    private const byte SparkInvoiceFallbackVersion = 31;

    /// <summary>Short channel id of the route hint hop that carries a Spark identity public key.</summary>
    private static readonly byte[] s_sparkIdentityShortChannelId = [0xF4, 0x24, 0x00, 0xF4, 0x24, 0x00, 0x00, 0x01];

    private const string Operation = "lightning.invoice.decode";
    private const int SignatureWords = 104; // 65 bytes of recoverable signature
    private const int TimestampWords = 7;
    private const ulong TotalSupplyMsat = 21_000_000UL * 100_000_000UL * 1_000UL;
    private const ulong MaxUnixSeconds = 253_402_300_799UL; // 9999-12-31T23:59:59Z

    /// <summary>When the invoice expires: <see cref="Timestamp"/> plus <see cref="ExpirySeconds"/>.</summary>
    public DateTimeOffset ExpiresAt
    {
        get
        {
            // Timestamp is at most 35 bits and the expiry at most 60, so the sum cannot overflow.
            var seconds = Timestamp + ExpirySeconds;
            return seconds > MaxUnixSeconds
                ? DateTimeOffset.MaxValue
                : DateTimeOffset.FromUnixTimeSeconds((long)seconds);
        }
    }

    /// <summary>Lower-case hex of <see cref="PaymentHash"/>.</summary>
    public string PaymentHashHex => Convert.ToHexString(PaymentHash).ToLowerInvariant();

    /// <summary>Whether this invoice belongs to the wallet's network.</summary>
    public bool BelongsTo(SparkNetwork network) => (Network, network) switch
    {
        (Bolt11Network.Mainnet, SparkNetwork.Mainnet) => true,
        (Bolt11Network.Regtest, SparkNetwork.Regtest) => true,
        _ => false,
    };

    /// <summary>
    /// Decode a BOLT-11 payment request.
    /// </summary>
    /// <exception cref="InvalidBolt11Exception">The string is not a well-formed BOLT-11 invoice.</exception>
    public static Bolt11Invoice Decode(string paymentRequest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentRequest);
        var trimmed = paymentRequest.Trim();

        string hrp;
        byte[] words;
        Bech32Variant variant;
        try
        {
            (hrp, words, variant) = Bech32mHelper.DecodeWords(trimmed);
        }
        catch (SparkConfigurationException ex)
        {
            throw Invalid(trimmed, ex.Message, ex);
        }

        if (variant != Bech32Variant.Bech32)
        {
            throw Invalid(trimmed, "BOLT-11 invoices use the bech32 checksum, not bech32m.");
        }

        var (network, amountMsat) = ParseHrp(trimmed, hrp);

        if (words.Length < TimestampWords + SignatureWords)
        {
            throw Invalid(trimmed, $"Data part too short ({words.Length} words).");
        }

        ulong timestamp = 0;
        for (int i = 0; i < TimestampWords; i++)
        {
            timestamp = (timestamp << 5) | words[i];
        }

        var fields = words.AsSpan(TimestampWords, words.Length - TimestampWords - SignatureWords);
        byte[]? paymentHash = null;
        byte[]? paymentSecret = null;
        ulong? expiry = null;
        string? description = null;
        string? sparkInvoiceFallback = null;
        string? routeHintFallback = null;

        var pos = 0;
        while (pos + 3 <= fields.Length)
        {
            int type = fields[pos];
            int length = (fields[pos + 1] * 32) + fields[pos + 2];
            pos += 3;
            if (pos + length > fields.Length)
            {
                throw Invalid(trimmed, $"Tagged field {type} runs past the end of the invoice.");
            }

            var data = fields.Slice(pos, length);
            pos += length;

            switch (type)
            {
                case 1 when length == 52: // p: payment hash
                    paymentHash ??= BytesFromWords(data, 32);
                    break;
                case 16 when length == 52: // s: payment secret
                    paymentSecret ??= BytesFromWords(data, 32);
                    break;
                case 6: // x: expiry
                {
                    if (length > 12)
                    {
                        throw Invalid(trimmed, "Expiry field too long.");
                    }
                    ulong value = 0;
                    foreach (var w in data)
                    {
                        value = (value << 5) | w;
                    }
                    expiry = value;
                    break;
                }
                case 13: // d: description
                {
                    var bytes = Bech32mHelper.ConvertBits(data, fromBits: 5, toBits: 8, pad: false);
                    if (bytes is not null)
                    {
                        description = Encoding.UTF8.GetString(bytes);
                    }
                    break;
                }
                case 9 when sparkInvoiceFallback is null && length > 0 && data[0] == SparkInvoiceFallbackVersion:
                    // f: fallback address; version 31 carries a Spark invoice. Lossy on purpose,
                    // like the reference SDK's TextDecoder: a version-31 field counts as a Spark
                    // fallback even when its bytes are not valid UTF-8.
                    sparkInvoiceFallback = Encoding.UTF8.GetString(LenientBytesFromWords(data[1..]));
                    break;
                case 3 when routeHintFallback is null:
                    // r: route hints; the sentinel hop names a Spark identity.
                    routeHintFallback = SparkIdentityInRouteHint(LenientBytesFromWords(data));
                    break;
                default:
                    // Unknown or wrongly sized fields are skipped, as BOLT-11 requires of readers.
                    break;
            }
        }

        if (pos != fields.Length)
        {
            throw Invalid(trimmed, "Trailing words after the last tagged field.");
        }

        if (paymentHash is null)
        {
            throw Invalid(trimmed, "Missing payment hash (p field).");
        }

        if (paymentSecret is null)
        {
            throw Invalid(trimmed, "Missing payment secret (s field).");
        }

        return new Bolt11Invoice(
            network,
            paymentHash,
            amountMsat,
            timestamp,
            expiry ?? 3600,
            paymentSecret,
            description,
            sparkInvoiceFallback ?? routeHintFallback);
    }

    /// <summary><c>ln</c> + currency + optional amount + optional multiplier.</summary>
    private static (Bolt11Network Network, ulong? AmountMsat) ParseHrp(string invoice, string hrp)
    {
        if (!hrp.StartsWith("ln", StringComparison.Ordinal))
        {
            throw Invalid(invoice, $"Not a lightning invoice (prefix '{hrp}').");
        }

        var rest = hrp.AsSpan(2);
        Bolt11Network network;
        ReadOnlySpan<char> amountPart;

        // Longest prefixes first: "bcrt" before "bc", "tbs" before "tb".
        if (rest.StartsWith("bcrt", StringComparison.Ordinal))
        {
            network = Bolt11Network.Regtest;
            amountPart = rest[4..];
        }
        else if (rest.StartsWith("tbs", StringComparison.Ordinal))
        {
            network = Bolt11Network.Signet;
            amountPart = rest[3..];
        }
        else if (rest.StartsWith("tb", StringComparison.Ordinal))
        {
            network = Bolt11Network.Testnet;
            amountPart = rest[2..];
        }
        else if (rest.StartsWith("bc", StringComparison.Ordinal))
        {
            network = Bolt11Network.Mainnet;
            amountPart = rest[2..];
        }
        else
        {
            throw Invalid(invoice, $"Unknown currency prefix '{hrp}'.");
        }

        if (amountPart.IsEmpty)
        {
            return (network, null);
        }

        var amountText = amountPart.ToString();
        var digits = amountPart;
        char? multiplier = null;
        if (!char.IsAsciiDigit(digits[^1]))
        {
            multiplier = digits[^1];
            digits = digits[..^1];
        }

        if (digits.IsEmpty || !AllAsciiDigits(digits))
        {
            throw Invalid(invoice, $"Malformed amount '{amountText}'.");
        }
        if (digits[0] == '0')
        {
            throw Invalid(invoice, "Amount has a leading zero.");
        }
        if (digits.Length > 19
            || !ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number == 0)
        {
            throw Invalid(invoice, $"Amount '{amountText}' is out of range.");
        }

        // msat per unit: BTC = 1e11, m = 1e8, u = 1e5, n = 1e2, p = 1e-1.
        ulong msat;
        switch (multiplier)
        {
            case null:
                msat = CheckedMultiply(invoice, number, 100_000_000_000UL, amountText);
                break;
            case 'm':
                msat = CheckedMultiply(invoice, number, 100_000_000UL, amountText);
                break;
            case 'u':
                msat = CheckedMultiply(invoice, number, 100_000UL, amountText);
                break;
            case 'n':
                msat = CheckedMultiply(invoice, number, 100UL, amountText);
                break;
            case 'p':
                if (number % 10 != 0)
                {
                    throw Invalid(invoice, $"Pico-bitcoin amount '{amountText}' has sub-millisatoshi precision.");
                }
                msat = number / 10;
                break;
            default:
                throw Invalid(invoice, $"Invalid amount multiplier '{multiplier}'.");
        }

        return (network, msat);
    }

    private static bool AllAsciiDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    private static ulong CheckedMultiply(string invoice, ulong a, ulong b, string amountText)
    {
        ulong product;
        try
        {
            product = checked(a * b);
        }
        catch (OverflowException ex)
        {
            throw Invalid(invoice, $"Amount '{amountText}' exceeds the total bitcoin supply.", ex);
        }

        if (product > TotalSupplyMsat)
        {
            throw Invalid(invoice, $"Amount '{amountText}' exceeds the total bitcoin supply.");
        }
        return product;
    }

    /// <summary>
    /// The public key (hex) of the first route-hint hop with the Spark sentinel short channel id.
    /// A hop is 51 bytes: pubkey (33), short channel id (8), base fee (4), proportional fee (4),
    /// CLTV delta (2).
    /// </summary>
    private static string? SparkIdentityInRouteHint(byte[] bytes)
    {
        for (var offset = 0; offset + 51 <= bytes.Length; offset += 51)
        {
            if (bytes.AsSpan(offset + 33, 8).SequenceEqual(s_sparkIdentityShortChannelId))
            {
                return Convert.ToHexString(bytes, offset, 33).ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>
    /// 5-bit words to bytes, ignoring leftover padding bits (the reference SDK's
    /// <c>fromWordsLenient</c>).
    /// </summary>
    private static byte[] LenientBytesFromWords(ReadOnlySpan<byte> words)
    {
        var accumulator = 0;
        var bits = 0;
        var result = new List<byte>((words.Length * 5 / 8) + 1);
        foreach (var word in words)
        {
            accumulator = ((accumulator << 5) | (word & 0x1F)) & 0xFFFF;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                result.Add((byte)((accumulator >> bits) & 0xFF));
            }
        }

        return [.. result];
    }

    /// <summary>Fixed-size byte field carried as 5-bit words (trailing padding bits are ignored).</summary>
    private static byte[]? BytesFromWords(ReadOnlySpan<byte> words, int count)
    {
        var bytes = Bech32mHelper.ConvertBits(words, fromBits: 5, toBits: 8, pad: true);
        if (bytes is null || bytes.Length < count)
        {
            return null;
        }
        return bytes[..count];
    }

    private static InvalidBolt11Exception Invalid(string invoice, string message, Exception? inner = null)
    {
        return inner is null
            ? new InvalidBolt11Exception(Operation, message) { PaymentRequest = invoice }
            : new InvalidBolt11Exception(Operation, message, inner) { PaymentRequest = invoice };
    }
}

/// <summary>
/// A Lightning payment <c>PayLightningInvoiceAsync</c> has checked before touching a leaf: a
/// BOLT-11 invoice for the wallet's network, the amount to pay and the fee cap.
/// </summary>
internal sealed record LightningPayment
{
    private const string Operation = "lightning.pay";

    public LightningPayment(string paymentRequest, long maxFeeSats, long? amountSats, string? idempotencyKey, SparkNetwork network)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentRequest);
        if (maxFeeSats < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFeeSats), maxFeeSats, "maxFeeSats must not be negative.");
        }

        var trimmed = paymentRequest.Trim();
        var invoice = Bolt11Invoice.Decode(trimmed);
        if (!invoice.BelongsTo(network))
        {
            throw new InvalidBolt11Exception(
                Operation, $"The invoice is for {invoice.Network}; the wallet is on {network}.")
            {
                PaymentRequest = paymentRequest,
            };
        }

        // Decoded before lower-casing, so a mixed-case string is still refused.
        EncodedInvoice = trimmed.ToLowerInvariant();
        Invoice = invoice;
        AmountSats = LightningValidator.ResolvePaymentAmountSats(invoice.AmountMsat, amountSats);
        MaxFeeSats = maxFeeSats;
        IdempotencyKey = idempotencyKey;
    }

    /// <summary>
    /// The invoice as validated, in the form sent on: surrounding whitespace dropped and lower
    /// case (the reference SDK lower-cases it before use; the SSP refuses anything else).
    /// </summary>
    public string EncodedInvoice { get; }

    public Bolt11Invoice Invoice { get; }

    /// <summary>Sats the invoice is paid with: its own amount, or the caller's for an amountless invoice.</summary>
    public long AmountSats { get; }

    public long MaxFeeSats { get; }

    public string? IdempotencyKey { get; }

    /// <summary>The caller's amount for an amountless invoice — the only case the SSP is told one.</summary>
    public long? AmountlessInvoiceAmountSats => Invoice.AmountMsat is null ? AmountSats : null;
}

/// <summary>Client-side checks around Lightning payments and invoices.</summary>
internal static class LightningValidator
{
    /// <summary>
    /// The fee a Lightning send offers the SSP: its estimate, refused above the caller's cap — the
    /// reference SDK's <c>maxFeeSats &lt; feeEstimate</c> check. No floor: an estimate of 0 is
    /// offered as 0, so <c>maxFeeSats: estimate</c> always goes through.
    /// </summary>
    public static ulong SendFeeSats(long estimate, long maxFeeSats)
    {
        if (estimate < 0)
        {
            throw new SparkUntrustedResponseException("lightning.fee", $"The SSP returned a negative Lightning fee estimate: {estimate}.");
        }

        if (estimate > maxFeeSats)
        {
            throw new FeeExceedsLimitException("lightning.pay", estimate, maxFeeSats);
        }

        return (ulong)estimate;
    }

    /// <summary>
    /// Check the Lightning send the coordinator holds under a transfer id the caller is resuming,
    /// before the SSP is asked to pay from it: this wallet's HTLC to the SSP for this invoice's
    /// payment hash, neither returned nor expired, whose leaves cover the amount with at most
    /// <c>maxFeeSats</c> on top — what the SSP keeps beyond the invoice.
    /// </summary>
    public static void VerifyHeldSend(
        Proto.PreimageRequestWithTransfer held,
        string transferId,
        LightningPayment payment,
        byte[] identityPublicKey,
        byte[] sspIdentityPublicKey)
    {
        var transfer = held.Transfer;
        if (transfer is null
            || !string.Equals(transfer.Id, transferId, StringComparison.OrdinalIgnoreCase)
            || transfer.Type != Proto.TransferType.PreimageSwap
            || !held.SenderIdentityPubkey.Span.SequenceEqual(identityPublicKey)
            || !transfer.SenderIdentityPublicKey.Span.SequenceEqual(identityPublicKey)
            || !held.ReceiverIdentityPubkey.Span.SequenceEqual(sspIdentityPublicKey)
            || !transfer.ReceiverIdentityPublicKey.Span.SequenceEqual(sspIdentityPublicKey))
        {
            throw new ArgumentException(
                $"Transfer {transferId} is not a Lightning send from this wallet to the SSP.", nameof(transferId));
        }

        if (!held.PaymentHash.Span.SequenceEqual(payment.Invoice.PaymentHash))
        {
            throw new ArgumentException(
                $"Transfer {transferId} pays another invoice (payment hash {Convert.ToHexString(held.PaymentHash.Span).ToLowerInvariant()}).",
                nameof(transferId));
        }

        if (held.Status == Proto.PreimageRequestStatus.Returned
            || transfer.Status is Proto.TransferStatus.Returned or Proto.TransferStatus.Expired)
        {
            throw new ArgumentException(
                $"The Lightning send of transfer {transferId} failed and was returned; pay again with a new transferId.",
                nameof(transferId));
        }

        if (transfer.TotalValue < (ulong)payment.AmountSats)
        {
            throw new ArgumentException(
                $"Transfer {transferId} holds {transfer.TotalValue} sats, less than the {payment.AmountSats} sats to pay.",
                nameof(transferId));
        }

        var feeSats = transfer.TotalValue - (ulong)payment.AmountSats;
        if (feeSats > (ulong)payment.MaxFeeSats)
        {
            throw new FeeExceedsLimitException("lightning.pay", TransferMapping.ReportedSats(feeSats), payment.MaxFeeSats);
        }
    }

    /// <summary>
    /// Resolve the amount to pay: the invoice amount (rounded up to whole sats), or the caller's
    /// amount for an amountless invoice. A caller amount that contradicts the invoice is refused.
    /// </summary>
    public static long ResolvePaymentAmountSats(ulong? invoiceAmountMsat, long? amountSats)
    {
        if (invoiceAmountMsat is { } msat)
        {
            var sats = (msat + 999) / 1000;
            if (sats == 0 || sats > long.MaxValue)
            {
                throw new InvalidBolt11Exception("lightning.pay", $"Invoice amount {msat} msat is out of range.");
            }
            if (amountSats is { } requested && requested != (long)sats)
            {
                throw new ArgumentException(
                    $"amountSats ({requested}) does not match the invoice amount ({sats} sats); omit it for invoices that carry an amount.",
                    nameof(amountSats));
            }
            return (long)sats;
        }

        if (amountSats is not { } amount)
        {
            throw new ArgumentException("The invoice has no amount; pass amountSats.", nameof(amountSats));
        }
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amountSats), amount, "amountSats must be positive.");
        }
        return amount;
    }

    /// <summary>
    /// Verify that an invoice the SSP created is the one we asked for: same network, same
    /// payment hash, same amount. Run before preimage shares are stored and before the invoice
    /// is handed out. Mirrors the reference SDK's <c>validateAndCreateLightningInvoice</c>.
    /// </summary>
    public static Bolt11Invoice VerifyCreatedInvoice(
        string encodedInvoice,
        string? reportedPaymentHashHex,
        byte[] expectedPaymentHash,
        long expectedAmountSats,
        SparkNetwork network)
    {
        const string operation = "lightning.invoice";

        Bolt11Invoice invoice;
        try
        {
            invoice = Bolt11Invoice.Decode(encodedInvoice);
        }
        catch (InvalidBolt11Exception ex)
        {
            throw new SparkUntrustedResponseException(operation, $"The SSP returned an invoice that does not decode: {ex.Message}", ex);
        }

        if (!invoice.BelongsTo(network))
        {
            throw new SparkUntrustedResponseException(operation, $"The SSP returned an invoice for {invoice.Network}; the wallet is on {network}.");
        }

        var expectedHex = Convert.ToHexString(expectedPaymentHash).ToLowerInvariant();
        if (!invoice.PaymentHash.AsSpan().SequenceEqual(expectedPaymentHash))
        {
            throw new SparkUntrustedResponseException(
                operation, $"The SSP invoice's payment hash {invoice.PaymentHashHex} does not match ours ({expectedHex}).");
        }
        if (reportedPaymentHashHex is not null
            && !string.Equals(reportedPaymentHashHex.Trim(), expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new SparkUntrustedResponseException(
                operation, $"The SSP reported payment hash {reportedPaymentHashHex}, which does not match ours ({expectedHex}).");
        }

        // The wallet never asks for a Spark fallback. One in the invoice would let a payer that
        // prefers Spark pay whoever it names instead of this wallet (reference SDK: "Spark
        // fallback address found in lightning invoice but includeSparkAddress is false").
        if (invoice.SparkFallback is { } sparkFallback)
        {
            throw new SparkUntrustedResponseException(
                operation, $"The SSP invoice carries a Spark fallback ({sparkFallback}) the wallet did not ask for.");
        }

        if (expectedAmountSats == 0)
        {
            if (invoice.AmountMsat is { } msat && msat != 0)
            {
                throw new SparkUntrustedResponseException(
                    operation, $"The SSP invoice carries an amount ({msat} msat) but an amountless invoice was requested.");
            }
        }
        else
        {
            var expectedMsat = checked((ulong)expectedAmountSats * 1000UL);
            if (invoice.AmountMsat != expectedMsat)
            {
                throw new SparkUntrustedResponseException(
                    operation,
                    $"The SSP invoice amount ({invoice.AmountMsat?.ToString(CultureInfo.InvariantCulture) ?? "none"} msat) does not match the requested {expectedAmountSats} sats.");
            }
        }

        return invoice;
    }

    /// <summary>Canonical (hyphenated, lower-case) form of a caller-supplied transfer id used to resume a Lightning send.</summary>
    public static string? NormalizeTransferId(string? transferId)
    {
        if (transferId is null)
        {
            return null;
        }
        if (!Guid.TryParse(transferId, out var guid))
        {
            throw new ArgumentException($"transferId must be a UUID, got '{transferId}'.", nameof(transferId));
        }
        return guid.ToString("D");
    }
}
