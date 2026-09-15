namespace NSpark.GraphQL;

internal static class Mutations
{
    public const string GetChallenge = """
        mutation GetChallenge($public_key: PublicKey!) {
            get_challenge(input: { public_key: $public_key }) {
                protected_challenge
            }
        }
        """;

    public const string VerifyChallenge = """
        mutation VerifyChallenge(
            $protected_challenge: String!,
            $signature: String!,
            $identity_public_key: PublicKey!
        ) {
            verify_challenge(input: {
                protected_challenge: $protected_challenge,
                signature: $signature,
                identity_public_key: $identity_public_key
            }) {
                valid_until
                session_token
            }
        }
        """;

    public const string RequestLightningReceive = """
        mutation RequestLightningReceive(
            $network: BitcoinNetwork!,
            $amount_sats: Long!,
            $payment_hash: Hash32!,
            $expiry_secs: Int,
            $memo: String,
            $description_hash: Hash32,
            $receiver_identity_pubkey: PublicKey
        ) {
            request_lightning_receive(input: {
                network: $network,
                amount_sats: $amount_sats,
                payment_hash: $payment_hash,
                expiry_secs: $expiry_secs,
                memo: $memo,
                description_hash: $description_hash,
                receiver_identity_pubkey: $receiver_identity_pubkey
            }) {
                request {
                    id
                    invoice {
                        encoded_invoice
                        payment_hash
                        expires_at
                    }
                    status
                }
            }
        }
        """;

    public const string RequestLightningSend = """
        mutation RequestLightningSend(
            $encoded_invoice: String!,
            $idempotency_key: String,
            $amount_sats: Long,
            $user_outbound_transfer_external_id: UUID
        ) {
            request_lightning_send(input: {
                encoded_invoice: $encoded_invoice
                idempotency_key: $idempotency_key
                amount_sats: $amount_sats
                user_outbound_transfer_external_id: $user_outbound_transfer_external_id
            }) {
                request {
                    id
                    status
                }
            }
        }
        """;

    public const string GetFeeEstimate = """
        mutation GetFeeEstimate($input: FeeEstimateInput!) {
            get_fee_estimate(input: $input) {
                fee_sats
                fee_rate_sats_per_vbyte
            }
        }
        """;

    public const string CoopExitFeeEstimate = """
        query CoopExitFeeEstimate(
            $leaf_external_ids: [UUID!]!,
            $withdrawal_address: String!
        ) {
            coop_exit_fee_estimates(input: {
                leaf_external_ids: $leaf_external_ids,
                withdrawal_address: $withdrawal_address
            }) {
                speed_fast {
                    user_fee { original_value original_unit }
                    l1_broadcast_fee { original_value original_unit }
                }
            }
        }
        """;

    public const string RequestCoopExit = """
        mutation RequestCoopExit(
            $leaf_external_ids: [UUID!]!,
            $withdrawal_address: String!,
            $exit_speed: ExitSpeed!,
            $withdraw_all: Boolean,
            $user_outbound_transfer_external_id: UUID
        ) {
            request_coop_exit(input: {
                leaf_external_ids: $leaf_external_ids,
                withdrawal_address: $withdrawal_address,
                exit_speed: $exit_speed,
                withdraw_all: $withdraw_all,
                user_outbound_transfer_external_id: $user_outbound_transfer_external_id
            }) {
                request {
                    id
                    raw_connector_transaction
                    raw_coop_exit_transaction
                    coop_exit_txid
                    status
                }
            }
        }
        """;

    public const string CompleteCoopExit = """
        mutation CompleteCoopExit(
            $user_outbound_transfer_external_id: UUID!
        ) {
            complete_coop_exit(input: {
                user_outbound_transfer_external_id: $user_outbound_transfer_external_id
            }) {
                request {
                    id
                    status
                }
            }
        }
        """;

    public const string StaticDepositQuote = """
        query StaticDepositQuote(
            $transaction_id: String!,
            $output_index: Int!,
            $network: BitcoinNetwork!
        ) {
            static_deposit_quote(input: {
                transaction_id: $transaction_id,
                output_index: $output_index,
                network: $network
            }) {
                credit_amount_sats
                signature
            }
        }
        """;

    public const string ClaimStaticDeposit = """
        mutation ClaimStaticDeposit(
            $transaction_id: String!,
            $output_index: Int!,
            $network: BitcoinNetwork!,
            $request_type: ClaimStaticDepositRequestType!,
            $credit_amount_sats: Long,
            $deposit_secret_key: String!,
            $signature: String!,
            $quote_signature: String!
        ) {
            claim_static_deposit(input: {
                transaction_id: $transaction_id,
                output_index: $output_index,
                network: $network,
                request_type: $request_type,
                credit_amount_sats: $credit_amount_sats,
                max_fee_sats: null,
                deposit_secret_key: $deposit_secret_key,
                signature: $signature,
                quote_signature: $quote_signature
            }) {
                claim_static_deposit_output_transfer_id: transfer_id
            }
        }
        """;

    public const string RequestSwap = """
        mutation RequestSwap(
            $adaptor_pubkey: PublicKey!,
            $total_amount_sats: Long!,
            $target_amount_sats: [Long!]!,
            $fee_sats: Long!,
            $user_leaves: [UserLeafInput!]!,
            $user_outbound_transfer_external_id: UUID!
        ) {
            request_swap(input: {
                adaptor_pubkey: $adaptor_pubkey,
                total_amount_sats: $total_amount_sats,
                target_amount_sats: $target_amount_sats,
                fee_sats: $fee_sats,
                user_leaves: $user_leaves,
                user_outbound_transfer_external_id: $user_outbound_transfer_external_id
            }) {
                request {
                    leaves_swap_request_id: id
                    leaves_swap_request_status: status
                    leaves_swap_request_inbound_transfer: inbound_transfer {
                        transfer_spark_id: spark_id
                    }
                    leaves_swap_request_swap_leaves: swap_leaves {
                        swap_leaf_leaf_id: leaf_id
                    }
                }
            }
        }
        """;
}
