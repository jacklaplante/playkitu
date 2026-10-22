namespace Playreel.Client
{
    // One finite code vocabulary for proof and gameplay health; never persist arbitrary exception/response text.
    public static class RecordingDiagnostics
    {
        // Diagnostic-only issuer vocabulary. These codes must not change retry,
        // authentication, admission or quota decisions in RecordingSessionClient.
        public static bool IsIssuerLimitCode(string code)
        {
            switch (code) {
                case "issuer_busy": case "admission_capacity": case "admission_rate":
                case "session_quota": case "authorization_rate": return true;
                default: return false;
            }
        }
        public static string SafeError(string code, bool proof = false)
        {
            if (code == null) return null;
            if (IsIssuerLimitCode(code)) return code;
            // Codes only, never HTTP responses, native exception text or Steam
            // tickets/temporary credentials. Unknown future failures stay opaque.
            switch (code) {
                case "consent_required": case "invalid_build": case "credential_unavailable": case "invalid_clock":
                case "admission_exhausted": case "admission_unavailable": case "authorization_unavailable": case "consumer_failed":
                case "issuer_unavailable": case "recording_disabled": case "ticket_unavailable": case "admission_denied":
                case "log_quota": case "log_authorization_unavailable": case "close_unavailable":
                case "native_missing": case "native_load_failed": case "native_abi_mismatch": case "native_unavailable":
                case "native_clock_capability_mismatch": case "native_clock_failed": case "native_create_failed": case "native_status_failed":
                case "native_clock_enable_failed": case "native_created_clock_not_empty": case "native_created_not_empty": case "native_stop_failed":
                case "native_smoke_failed": case "native_credential_size": case "native_authorization_rejected": case "native_admission_deadline":
                case "native_video_degraded": case "native_video_ended": case "native_video_unavailable":
                case "native_cancelled": case "native_initialization_failed": case "native_startup_timeout":
                case "native_media_failed": case "native_byte_limit": case "native_flush_timeout":
                case "native_credentials_failed": case "native_retirement_failed":
                case "compression_failed": case "post_failed": case "drain_timeout": case "timing_unavailable":
                case "steam_unavailable": case "unexpected_steam_owner": case "proof_start_failed": case "proof_tick_failed":
                case "proof_output_required": case "fresh_private_output_required": case "unexpected_game_initializer":
                case "meta_guard_failed": case "live_provider_missing": case "offline_arguments_only": case "invalid_proof_arguments":
                case "interactive_consent_required": case "live_player_only":
                case "recording_start_failed": case "recording_unavailable": case "steam_app_context_unavailable":
                case "native_external_abi_mismatch": case "native_external_enable_failed": case "native_external_status_failed":
                case "native_external_drain_failed": case "native_module_mismatch": case "native_export_missing":
                case "gpu_readback_unsupported": case "gpu_readback_unavailable": case "gpu_capture_unavailable":
                case "audio_format_unsupported": case "audio_configuration_changed": case "audio_block_invalid":
                case "audio_clock_mismatch": case "multiple_audio_listeners": case "media_input_unavailable":
                case "graphics_api_unsupported": case "game_view_size_unsupported":
                case "fresh_ticket_required": case "active": case "pending": case "opaque_response": case "transport_error":
                case "missing_response": case "valve_error": case "missing_params": case "result_not_ok": case "missing_steamid":
                    return code;
                default: return RecordingSessionClient.SanitizeTicketFailure(code) == code ? code : proof ? "proof_unavailable" : "recording_unavailable";
            }
        }
    }
}
