using System.Text.Json.Serialization;

namespace NSpark.GraphQL;

internal sealed record GetChallengeResponse(
    [property: JsonPropertyName("get_challenge")] ChallengeData GetChallenge);

internal sealed record VerifyChallengeResponse(
    [property: JsonPropertyName("verify_challenge")] VerifyChallengeData VerifyChallenge);

internal sealed record ChallengeData(
    [property: JsonPropertyName("protected_challenge")] string ProtectedChallenge);

internal sealed record VerifyChallengeData(
    [property: JsonPropertyName("valid_until")] string ValidUntil,
    [property: JsonPropertyName("session_token")] string SessionToken);

// request_lightning_receive response chain
internal sealed record RequestLightningReceiveResponse(
    [property: JsonPropertyName("request_lightning_receive")] LightningReceiveWrapper RequestLightningReceive);

internal sealed record LightningReceiveWrapper(
    [property: JsonPropertyName("request")] LightningReceiveRequestData Request);

internal sealed record LightningReceiveRequestData(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("invoice")] InvoiceData Invoice,
    [property: JsonPropertyName("status")] string? Status);

internal sealed record InvoiceData(
    [property: JsonPropertyName("encoded_invoice")] string EncodedInvoice,
    [property: JsonPropertyName("payment_hash")] string PaymentHash,
    [property: JsonPropertyName("expires_at")] string ExpiresAt);

// request_lightning_send response chain
internal sealed record RequestLightningSendResponse(
    [property: JsonPropertyName("request_lightning_send")] LightningSendWrapper RequestLightningSend);

internal sealed record LightningSendWrapper(
    [property: JsonPropertyName("request")] LightningSendRequestData Request);

internal sealed record LightningSendRequestData(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status);

internal sealed record GetFeeEstimateResponse(
    [property: JsonPropertyName("get_fee_estimate")] FeeEstimateData GetFeeEstimate);

internal sealed record FeeEstimateData(
    [property: JsonPropertyName("fee_sats")] long FeeSats,
    [property: JsonPropertyName("fee_rate_sats_per_vbyte")] long FeeRateSatsPerVbyte);

// coop_exit_fee_estimates response chain
internal sealed record CoopExitFeeEstimateResponse(
    [property: JsonPropertyName("coop_exit_fee_estimates")] CoopExitFeeEstimateData CoopExitFeeEstimates);

internal sealed record CoopExitFeeEstimateData(
    [property: JsonPropertyName("speed_fast")] CoopExitSpeedData SpeedFast);

internal sealed record CoopExitSpeedData(
    [property: JsonPropertyName("user_fee")] CoopExitFeeValue UserFee,
    [property: JsonPropertyName("l1_broadcast_fee")] CoopExitFeeValue L1BroadcastFee);

internal sealed record CoopExitFeeValue(
    [property: JsonPropertyName("original_value")] long OriginalValue,
    [property: JsonPropertyName("original_unit")] string OriginalUnit);

// request_coop_exit response chain
internal sealed record RequestCoopExitResponse(
    [property: JsonPropertyName("request_coop_exit")] RequestCoopExitWrapper RequestCoopExit);

internal sealed record RequestCoopExitWrapper(
    [property: JsonPropertyName("request")] CoopExitRequestData Request);

internal sealed record CoopExitRequestData(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("raw_connector_transaction")] string RawConnectorTransaction,
    [property: JsonPropertyName("raw_coop_exit_transaction")] string? RawCoopExitTransaction,
    [property: JsonPropertyName("coop_exit_txid")] string CoopExitTxid,
    [property: JsonPropertyName("status")] string Status);

// complete_coop_exit response chain
internal sealed record CompleteCoopExitResponse(
    [property: JsonPropertyName("complete_coop_exit")] CompleteCoopExitWrapper CompleteCoopExit);

internal sealed record CompleteCoopExitWrapper(
    [property: JsonPropertyName("request")] CoopExitStatusData Request);

internal sealed record CoopExitStatusData(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status);

// static_deposit_quote response chain
internal sealed record StaticDepositQuoteResponse(
    [property: JsonPropertyName("static_deposit_quote")] StaticDepositQuoteData StaticDepositQuote);

internal sealed record StaticDepositQuoteData(
    [property: JsonPropertyName("credit_amount_sats")] long CreditAmountSats,
    [property: JsonPropertyName("signature")] string Signature);

// claim_static_deposit response chain
internal sealed record ClaimStaticDepositResponse(
    [property: JsonPropertyName("claim_static_deposit")] ClaimStaticDepositData ClaimStaticDeposit);

internal sealed record ClaimStaticDepositData(
    [property: JsonPropertyName("claim_static_deposit_output_transfer_id")] string? TransferId);

// lightning_send_fee_estimate response chain
internal sealed record LightningSendFeeEstimateResponse(
    [property: JsonPropertyName("lightning_send_fee_estimate")] LightningSendFeeEstimateData LightningSendFeeEstimate);

internal sealed record LightningSendFeeEstimateData(
    [property: JsonPropertyName("fee_estimate")] LightningSendFeeEstimateValue FeeEstimate);

internal sealed record LightningSendFeeEstimateValue(
    [property: JsonPropertyName("original_value")] long OriginalValue,
    [property: JsonPropertyName("original_unit")] string? OriginalUnit);

// request_swap response chain
internal sealed record RequestSwapResponse(
    [property: JsonPropertyName("request_swap")] RequestSwapWrapper RequestSwap);

internal sealed record RequestSwapWrapper(
    [property: JsonPropertyName("request")] SwapRequestData Request);

internal sealed record SwapRequestData(
    [property: JsonPropertyName("leaves_swap_request_id")] string Id,
    [property: JsonPropertyName("leaves_swap_request_status")] string Status,
    [property: JsonPropertyName("leaves_swap_request_inbound_transfer")] SwapInboundTransferData? InboundTransfer,
    [property: JsonPropertyName("leaves_swap_request_swap_leaves")] List<SwapLeafData>? SwapLeaves);

internal sealed record SwapInboundTransferData(
    [property: JsonPropertyName("transfer_spark_id")] string SparkId);

internal sealed record SwapLeafData(
    [property: JsonPropertyName("swap_leaf_leaf_id")] string LeafId);

// user_request response chain (polymorphic — LightningReceiveRequest + LightningSendRequest)
internal sealed record GetUserRequestResponse(
    [property: JsonPropertyName("user_request")] UserRequestData? UserRequest);

internal sealed record UserRequestData(
    [property: JsonPropertyName("__typename")] string TypeName,
    // LightningReceiveRequest fields (aliased server-side to the lightning_receive_request_* keys)
    [property: JsonPropertyName("lightning_receive_request_id")] string? ReceiveId,
    [property: JsonPropertyName("lightning_receive_request_status")] string? ReceiveStatus,
    [property: JsonPropertyName("lightning_receive_request_receiver_identity_public_key")] string? ReceiverIdentityPublicKey,
    // LightningSendRequest fields
    [property: JsonPropertyName("lightning_send_request_id")] string? SendId,
    [property: JsonPropertyName("lightning_send_request_status")] string? SendStatus,
    [property: JsonPropertyName("lightning_send_request_encoded_invoice")] string? SendEncodedInvoice,
    [property: JsonPropertyName("lightning_send_request_fee")] CurrencyAmountData? SendFee,
    [property: JsonPropertyName("lightning_send_request_idempotency_key")] string? SendIdempotencyKey,
    [property: JsonPropertyName("lightning_send_request_payment_preimage")] string? SendPaymentPreimage)
{
    // Back-compat shims so older internal call sites that read `.Status`/`.Id` still compile.
    [JsonIgnore] public string? Status => ReceiveStatus ?? SendStatus;
    [JsonIgnore] public string? Id => ReceiveId ?? SendId;
}

internal sealed record CurrencyAmountData(
    [property: JsonPropertyName("original_value")] long OriginalValue,
    [property: JsonPropertyName("original_unit")] string? OriginalUnit);
