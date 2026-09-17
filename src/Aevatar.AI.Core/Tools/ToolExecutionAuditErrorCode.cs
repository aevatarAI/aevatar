namespace Aevatar.AI.Core.Tools;

internal static class ToolExecutionAuditErrorCode
{
    private const string CodeExecuteToolName = "code_execute";

    private static readonly HashSet<string> TimeoutCodes = new(StringComparer.Ordinal)
    {
        "code_execution_submit_recovery_expired",
        "code_execution_timed_out",
        "codex_execution_timed_out",
        "OPERATION_EXPIRED",
        "SANDBOX_TIMEOUT",
        "managed_proxy_timeout",
        "managed_upstream_codex_command_timeout",
        "managed_upstream_codex_execution_timeout",
        "managed_upstream_codex_opensandbox_timeout",
        "managed_upstream_codex_sandbox_ready_timeout",
    };

    private static readonly HashSet<string> CancelledCodes = new(StringComparer.Ordinal)
    {
        "code_execution_cancelled",
        "EXECUTION_CANCELLED",
        "managed_execution_cancelled",
    };

    private static readonly HashSet<string> AllowedCodes = new(StringComparer.Ordinal)
    {
        "code_execution_credential_unavailable",
        "code_execution_admission_invalid",
        "code_execution_failed",
        "code_execution_outcome_invalid",
        "code_execution_request_invalid",
        "code_execution_response_invalid",
        "code_execution_response_too_large",
        "code_execution_route_resolution_failed",
        "code_execution_route_access_denied",
        "code_execution_route_ambiguous",
        "code_execution_route_inactive",
        "code_execution_route_missing",
        "code_execution_route_policy_mismatch",
        "code_execution_timed_out",
        "code_execution_transport_unavailable",
        "DEPENDENCY_INSTALL_FAILED",
        "EXECUTION_FAILED",
        "INTERNAL_ERROR",
        "INVALID_REQUEST",
        "SANDBOX_CREATION_FAILED",
        "SANDBOX_TIMEOUT",
        "SANDBOX_UNREACHABLE",
        "managed_credential_expired",
        "managed_credential_inactive",
        "managed_credential_invalid",
        "managed_credential_not_provisioned",
        "managed_credential_owner_invalid",
        "managed_credential_reference_invalid",
        "managed_credential_service_binding_invalid",
        "managed_credential_unavailable",
        "managed_execution_cancelled",
        "managed_execution_failed",
        "managed_execution_nonzero_exit",
        "managed_feature_not_enabled",
        "managed_identity_unavailable",
        "managed_proxy_authentication_failed",
        "managed_proxy_authorization_denied",
        "managed_proxy_failed",
        "managed_proxy_target_unavailable",
        "managed_proxy_timeout",
        "managed_proxy_unavailable",
        "managed_request_invalid",
        "managed_response_invalid",
        "managed_response_too_large",
        "managed_target_disabled",
        "managed_upstream_codex_agent_message_missing",
        "managed_upstream_codex_caller_credential_forwarded",
        "managed_upstream_codex_capacity_unavailable",
        "managed_upstream_codex_cleanup_unconfirmed",
        "managed_upstream_codex_command_failed",
        "managed_upstream_codex_command_timeout",
        "managed_upstream_codex_config_invalid",
        "managed_upstream_codex_delegation_actor_invalid",
        "managed_upstream_codex_delegation_audience_invalid",
        "managed_upstream_codex_delegation_duplicated",
        "managed_upstream_codex_delegation_expired",
        "managed_upstream_codex_delegation_invalid",
        "managed_upstream_codex_delegation_issuer_invalid",
        "managed_upstream_codex_delegation_marker_invalid",
        "managed_upstream_codex_delegation_missing",
        "managed_upstream_codex_delegation_not_yet_valid",
        "managed_upstream_codex_delegation_scope_invalid",
        "managed_upstream_codex_delegation_subject_invalid",
        "managed_upstream_codex_delegation_type_invalid",
        "managed_upstream_codex_delegation_verifier_unavailable",
        "managed_upstream_codex_execd_terminal_missing",
        "managed_upstream_codex_execution_timeout",
        "managed_upstream_codex_feature_disabled",
        "managed_upstream_codex_opensandbox_timeout",
        "managed_upstream_codex_opensandbox_unavailable",
        "managed_upstream_codex_output_invalid",
        "managed_upstream_codex_output_too_large",
        "managed_upstream_codex_prompt_invalid",
        "managed_upstream_codex_prompt_too_large",
        "managed_upstream_codex_request_invalid",
        "managed_upstream_codex_sandbox_creation_failed",
        "managed_upstream_codex_sandbox_ready_timeout",
        "managed_upstream_codex_timeout_invalid",
        "managed_upstream_codex_turn_failed",
        "managed_upstream_codex_turn_terminal_missing",
        "managed_upstream_codex_workspace_invalid",
        "managed_upstream_codex_workspace_preparation_failed",
        "NYXID_PROXY_FORBIDDEN",
        "NYXID_PROXY_HTTP_404",
        "NYXID_PROXY_HTTP_429",
        "NYXID_PROXY_HTTP_502",
        "NYXID_PROXY_UNAUTHORIZED",
    };

    private static readonly HashSet<string> CodeExecuteOnlyCodes = new(StringComparer.Ordinal)
    {
        "code_execution_cancel_outcome_uncertain",
        "code_execution_cancelled",
        "code_execution_cancellation_requested",
        "code_execution_cancellation_unconfirmed",
        "code_execution_durable_context_invalid",
        "code_execution_durable_transport_unavailable",
        "code_execution_outcome_uncertain",
        "code_execution_route_not_ready",
        "code_execution_submit_recovery_expired",
        "durable_code_execution_operation_not_found",
        "durable_code_execution_operation_request_invalid",
        "durable_code_execution_public_api_not_configured",
        "durable_code_execution_response_too_large",
        "durable_code_execution_result_invalid",
        "durable_code_execution_status_etag_missing",
        "durable_code_execution_status_invalid",
        "durable_code_execution_target_not_found",
        "EXECUTION_CANCELLED",
        "EXECUTION_PAYLOAD_TOO_LARGE",
        "EXECUTION_RESULT_TOO_LARGE",
        "EXECUTION_STORED_DATA_INVALID",
        "FORBIDDEN",
        "IDEMPOTENCY_KEY_REUSE",
        "OPERATION_EXPIRED",
        "OUTCOME_UNCERTAIN",
        "UNAUTHENTICATED",
    };

    public static string? Resolve(string? value) =>
        value is not null && AllowedCodes.Contains(value) ? value : null;

    public static string? ResolveForTool(string? toolName, string? value) =>
        string.Equals(toolName, CodeExecuteToolName, StringComparison.Ordinal) &&
        value is not null &&
        CodeExecuteOnlyCodes.Contains(value)
            ? value
            : Resolve(value);

    public static bool IsTimeout(string? value) =>
        value is not null && TimeoutCodes.Contains(value);

    public static bool IsCancelled(string? value) =>
        value is not null && CancelledCodes.Contains(value);
}
