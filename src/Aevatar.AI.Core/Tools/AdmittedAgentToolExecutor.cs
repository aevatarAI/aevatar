using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Auditing;
using Aevatar.AI.Core.Observability;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Ports;

namespace Aevatar.AI.Core.Tools;

public sealed class AdmittedAgentToolExecutor : IAgentToolExecutionPort
{
    private readonly IAuditTrailAppender _auditTrailAppender;
    private readonly ToolAuditRecordFactory _auditRecordFactory;

    public AdmittedAgentToolExecutor(
        IAuditTrailAppender auditTrailAppender,
        IAuditActorIdentityHasher identityHasher,
        TimeProvider? timeProvider = null)
    {
        _auditTrailAppender = auditTrailAppender ?? throw new ArgumentNullException(nameof(auditTrailAppender));
        _auditRecordFactory = new ToolAuditRecordFactory(
            identityHasher ?? throw new ArgumentNullException(nameof(identityHasher)),
            timeProvider);
    }

    public async Task<AgentToolExecutionOutcome> ExecuteAsync(
        AgentToolExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Tool);
        ArgumentNullException.ThrowIfNull(request.ExecutionContext);

        var tool = request.Tool;
        var toolName = NormalizeIdentity(tool.Name);
        var requestId = NormalizeIdentity(request.ExecutionContext.Request.RequestId);
        var toolCallId = NormalizeIdentity(request.ExecutionContext.Request.CallId);
        var argumentsJson = AgentToolArgumentsDigest.Freeze(request.ArgumentsJson);
        var argumentsSha256 = AgentToolArgumentsDigest.ComputeSha256(argumentsJson);
        var fallbackSafety = new AgentToolCallSafety(true, false, true);

        if (toolName is null || requestId is null || toolCallId is null)
        {
            return CreateUnauditedFailure(
                tool,
                toolName ?? "unknown_tool",
                toolCallId ?? string.Empty,
                fallbackSafety,
                "invalid_tool_execution_identity",
                "Tool execution requires non-empty request, call, and tool identities.",
                AgentToolExecutionFailureStage.RequestValidation);
        }

        AgentToolCallSafety callSafety;
        try
        {
            callSafety = tool.GetCallSafety(argumentsJson)
                ?? throw new InvalidOperationException("Tool safety classification is required.");
        }
        catch (Exception ex)
        {
            var failed = CreateFailure(
                tool,
                toolName,
                toolCallId,
                fallbackSafety,
                isMutation: true,
                "tool_classification_failed",
                SafeExceptionClass(ex),
                AgentToolExecutionFailureStage.Classification,
                terminalInvoked: false,
                retryable: false,
                auditCompleted: false);
            return await CompleteBeforeTerminalAsync(
                tool,
                failed,
                request.ExecutionContext,
                AgentToolCredentialSource.System,
                requestId,
                toolName,
                toolCallId,
                argumentsSha256,
                fallbackSafety,
                ct).ConfigureAwait(false);
        }

        var isMutation = AgentToolCredentialPolicy.IsMutation(tool, callSafety);
        var credentialDecision = ResolveCredentials(request.ExecutionContext, isMutation, toolName);
        if (!credentialDecision.Allowed)
        {
            var denied = CreateDenied(
                tool,
                toolName,
                toolCallId,
                callSafety,
                isMutation,
                "credential_denied",
                credentialDecision.Message,
                AgentToolExecutionFailureStage.CredentialPolicy);
            return await CompleteBeforeTerminalAsync(
                tool,
                denied,
                request.ExecutionContext,
                credentialDecision.CredentialSource,
                requestId,
                toolName,
                toolCallId,
                argumentsSha256,
                callSafety,
                ct).ConfigureAwait(false);
        }

        var approvalRequestId = CreateApprovalRequestId(
            requestId,
            toolName,
            toolCallId,
            argumentsSha256);
        var requiresApproval = RequiresApproval(tool, callSafety);
        if (request.ApprovalGrant is not null &&
            !MatchesGrant(
                request.ApprovalGrant,
                request.ApprovalContinuationMode,
                approvalRequestId,
                requestId,
                toolName,
                toolCallId,
                argumentsSha256))
        {
            var denied = CreateDenied(
                tool,
                toolName,
                toolCallId,
                callSafety,
                isMutation,
                "approval_grant_mismatch",
                "The durable approval grant does not match this exact tool call.",
                AgentToolExecutionFailureStage.Approval,
                approvalRequestId);
            return await CompleteBeforeTerminalAsync(
                tool,
                denied,
                credentialDecision.ExecutionContext,
                credentialDecision.CredentialSource,
                requestId,
                toolName,
                toolCallId,
                argumentsSha256,
                callSafety,
                ct).ConfigureAwait(false);
        }

        if (requiresApproval && request.ApprovalGrant is null)
        {
            if (request.ApprovalContinuationMode != AgentToolApprovalContinuationMode.ActorOwned)
            {
                var denied = CreateDenied(
                    tool,
                    toolName,
                    toolCallId,
                    callSafety,
                    isMutation,
                    "approval_required_without_actor_continuation",
                    "This tool call requires an actor-owned durable approval continuation.",
                    AgentToolExecutionFailureStage.Approval,
                    approvalRequestId);
                return await CompleteBeforeTerminalAsync(
                    tool,
                    denied,
                    credentialDecision.ExecutionContext,
                    credentialDecision.CredentialSource,
                    requestId,
                    toolName,
                    toolCallId,
                    argumentsSha256,
                    callSafety,
                    ct).ConfigureAwait(false);
            }

            return await CreateApprovalRequiredAsync(
                tool,
                toolName,
                toolCallId,
                callSafety,
                isMutation,
                approvalRequestId,
                requestId,
                argumentsSha256,
                credentialDecision,
                ct).ConfigureAwait(false);
        }

        var runningReceipt = AgentToolReceiptFactory.CreateRunning(
            tool,
            toolCallId,
            toolName,
            callSafety);
        var runningAppend = await AppendAsync(
            CreateRunningAuditId(requestId, toolCallId),
            "running",
            tool,
            toolName,
            toolCallId,
            argumentsSha256,
            callSafety,
            credentialDecision.ExecutionContext,
            credentialDecision.CredentialSource,
            runningReceipt,
            AuditOutcome.Accepted,
            isMutation,
            ct).ConfigureAwait(false);
        if (runningAppend.Status != AuditTrailAppendStatus.Appended)
        {
            return runningAppend.Status switch
            {
                AuditTrailAppendStatus.Duplicate => CreateFailure(
                    tool,
                    toolName,
                    toolCallId,
                    callSafety,
                    isMutation,
                    "tool_execution_already_started",
                    "This exact tool call already obtained execution permission and will not be replayed.",
                    AgentToolExecutionFailureStage.AuditIntent,
                    terminalInvoked: false,
                    retryable: false,
                    auditCompleted: true),
                AuditTrailAppendStatus.Conflict => CreateAuditFailure(
                    tool,
                    toolName,
                    toolCallId,
                    callSafety,
                    isMutation,
                    "audit_intent_conflict",
                    retryable: false,
                    AgentToolExecutionFailureStage.AuditIntent),
                _ => CreateAuditFailure(
                    tool,
                    toolName,
                    toolCallId,
                    callSafety,
                    isMutation,
                    "audit_unavailable",
                    retryable: true,
                    AgentToolExecutionFailureStage.AuditIntent),
            };
        }

        return await ExecuteTerminalAsync(
            tool,
            toolName,
            toolCallId,
            argumentsJson,
            argumentsSha256,
            requestId,
            callSafety,
            isMutation,
            credentialDecision,
            ct).ConfigureAwait(false);
    }

    private async Task<AgentToolExecutionOutcome> ExecuteTerminalAsync(
        IAgentTool tool,
        string toolName,
        string toolCallId,
        string argumentsJson,
        string argumentsSha256,
        string requestId,
        AgentToolCallSafety callSafety,
        bool isMutation,
        CredentialDecision credentialDecision,
        CancellationToken ct)
    {
        using var activity = GenAIActivitySource.StartExecuteTool(toolName, toolCallId);
        var startedAt = Stopwatch.GetTimestamp();
        AgentToolExecutionOutcome outcome;
        try
        {
            using var contextScope = AgentToolContextScope.Push(credentialDecision.ExecutionContext);
            var resultJson = await tool.ExecuteAsync(argumentsJson, ct).ConfigureAwait(false);
            var receipt = AgentToolReceiptFactory.CreateResult(
                tool,
                toolCallId,
                toolName,
                callSafety,
                resultJson,
                argumentsJson);
            var safeResultJson = receipt.ResultJson ?? string.Empty;
            outcome = new AgentToolExecutionOutcome(
                AgentToolExecutionOutcomeKind.Executed,
                safeResultJson,
                receipt,
                isMutation,
                string.Empty,
                string.Empty,
                AgentToolExecutionFailureStage.None,
                TerminalInvoked: true,
                Retryable: false,
                AuditCompleted: false);
            activity?.SetTag("gen_ai.tool.status", "ok");
        }
        catch (Exception ex)
        {
            outcome = CreateFailure(
                tool,
                toolName,
                toolCallId,
                callSafety,
                isMutation,
                ResolveExceptionErrorCode(ex),
                SafeExceptionClass(ex),
                AgentToolExecutionFailureStage.TerminalExecution,
                terminalInvoked: true,
                retryable: false,
                auditCompleted: false);
            activity?.SetTag("gen_ai.tool.status", "error");
            activity?.SetTag("error.type", ex.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, outcome.SafeMessage);
        }
        finally
        {
            GenAIActivitySource.ToolInvocationDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                new KeyValuePair<string, object?>("gen_ai.tool.name", toolName));
        }

        var terminalAppend = await AppendAsync(
            CreateTerminalAuditId(requestId, toolCallId),
            "terminal",
            tool,
            toolName,
            toolCallId,
            argumentsSha256,
            callSafety,
            credentialDecision.ExecutionContext,
            credentialDecision.CredentialSource,
            outcome.Receipt,
            MapAuditOutcome(outcome),
            isMutation,
            ct).ConfigureAwait(false);
        if (terminalAppend.Status is AuditTrailAppendStatus.Appended or AuditTrailAppendStatus.Duplicate)
            return outcome with { AuditCompleted = true };

        if (outcome.Kind == AgentToolExecutionOutcomeKind.Executed)
        {
            return outcome with
            {
                Kind = AgentToolExecutionOutcomeKind.ExecutedAuditIncomplete,
                FailureCode = terminalAppend.Status == AuditTrailAppendStatus.Conflict
                    ? "audit_intent_conflict"
                    : "audit_unavailable",
                SafeMessage = "Tool execution completed, but the terminal audit fact was not durably recorded.",
                FailureStage = AgentToolExecutionFailureStage.TerminalAudit,
                AuditCompleted = false,
            };
        }

        return outcome with { AuditCompleted = false };
    }

    private async Task<AgentToolExecutionOutcome> CreateApprovalRequiredAsync(
        IAgentTool tool,
        string toolName,
        string toolCallId,
        AgentToolCallSafety callSafety,
        bool isMutation,
        string approvalRequestId,
        string requestId,
        string argumentsSha256,
        CredentialDecision credentialDecision,
        CancellationToken ct)
    {
        var resultJson = JsonSerializer.Serialize(new
        {
            approval_required = true,
            request_id = approvalRequestId,
            tool_name = toolName,
            tool_call_id = toolCallId,
            message = "This tool requires durable approval before execution.",
        });
        var receipt = AgentToolReceiptFactory.CreateApprovalRequired(
            tool,
            toolCallId,
            toolName,
            callSafety,
            resultJson,
            approvalRequestId);
        var outcome = new AgentToolExecutionOutcome(
            AgentToolExecutionOutcomeKind.ApprovalRequired,
            resultJson,
            receipt,
            isMutation,
            string.Empty,
            "Tool execution is waiting for durable approval.",
            AgentToolExecutionFailureStage.Approval,
            TerminalInvoked: false,
            Retryable: false,
            AuditCompleted: false);
        var append = await AppendAsync(
            CreateWaitingApprovalAuditId(requestId, toolCallId, approvalRequestId),
            "waiting_approval",
            tool,
            toolName,
            toolCallId,
            argumentsSha256,
            callSafety,
            credentialDecision.ExecutionContext,
            credentialDecision.CredentialSource,
            receipt,
            AuditOutcome.Accepted,
            isMutation,
            ct).ConfigureAwait(false);
        return append.Status switch
        {
            AuditTrailAppendStatus.Appended or AuditTrailAppendStatus.Duplicate => outcome with { AuditCompleted = true },
            AuditTrailAppendStatus.Conflict => CreateAuditFailure(
                tool,
                toolName,
                toolCallId,
                callSafety,
                isMutation,
                "audit_intent_conflict",
                retryable: false,
                AgentToolExecutionFailureStage.AuditIntent),
            _ => CreateAuditFailure(
                tool,
                toolName,
                toolCallId,
                callSafety,
                isMutation,
                "audit_unavailable",
                retryable: true,
                AgentToolExecutionFailureStage.AuditIntent),
        };
    }

    private async Task<AgentToolExecutionOutcome> CompleteBeforeTerminalAsync(
        IAgentTool tool,
        AgentToolExecutionOutcome outcome,
        AgentToolExecutionContext executionContext,
        AgentToolCredentialSource credentialSource,
        string requestId,
        string toolName,
        string toolCallId,
        string argumentsSha256,
        AgentToolCallSafety callSafety,
        CancellationToken ct)
    {
        var append = await AppendAsync(
            CreateTerminalAuditId(requestId, toolCallId),
            "terminal",
            tool,
            toolName,
            toolCallId,
            argumentsSha256,
            callSafety,
            executionContext,
            credentialSource,
            outcome.Receipt,
            MapAuditOutcome(outcome),
            outcome.IsMutation,
            ct).ConfigureAwait(false);

        return append.Status switch
        {
            AuditTrailAppendStatus.Appended or AuditTrailAppendStatus.Duplicate => outcome with { AuditCompleted = true },
            AuditTrailAppendStatus.Conflict => CreateAuditFailure(
                tool,
                toolName,
                toolCallId,
                callSafety,
                outcome.IsMutation,
                "audit_intent_conflict",
                retryable: false,
                AgentToolExecutionFailureStage.TerminalAudit),
            _ => CreateAuditFailure(
                tool,
                toolName,
                toolCallId,
                callSafety,
                outcome.IsMutation,
                "audit_unavailable",
                retryable: true,
                AgentToolExecutionFailureStage.TerminalAudit),
        };
    }

    private async Task<AuditTrailAppendResult> AppendAsync(
        string auditId,
        string executionPhase,
        IAgentTool tool,
        string toolName,
        string toolCallId,
        string argumentsSha256,
        AgentToolCallSafety callSafety,
        AgentToolExecutionContext executionContext,
        AgentToolCredentialSource credentialSource,
        AgentToolReceipt receipt,
        AuditOutcome outcome,
        bool isMutation,
        CancellationToken ct)
    {
        try
        {
            var record = _auditRecordFactory.Create(
                auditId,
                executionPhase,
                tool,
                toolName,
                toolCallId,
                argumentsSha256,
                callSafety,
                executionContext,
                credentialSource,
                receipt,
                outcome,
                isMutation);
            return await _auditTrailAppender.AppendAsync(record, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return AuditTrailAppendResult.StoreUnavailable(auditId, SafeExceptionClass(ex));
        }
    }

    private static CredentialDecision ResolveCredentials(
        AgentToolExecutionContext context,
        bool isMutation,
        string toolName)
    {
        var senderBindingId = NormalizeIdentity(context.SenderBinding.BindingId);
        if (senderBindingId is null)
        {
            var isChannelMediated = NormalizeIdentity(context.Channel.SenderId) is not null;
            if (!isChannelMediated || !isMutation)
            {
                return new CredentialDecision(
                    true,
                    context,
                    ResolveDirectCredentialSource(context),
                    string.Empty);
            }

            return new CredentialDecision(
                false,
                context,
                AgentToolCredentialSource.ChannelRegistration,
                $"Tool '{toolName}' was not executed because the channel sender is not bound to a NyxID account.");
        }

        var senderToken = NormalizeIdentity(context.Credentials.SenderNyxIdAccessToken);
        if (senderToken is not null)
        {
            var senderContext = context with
            {
                Credentials = context.Credentials with
                {
                    NyxIdAccessToken = senderToken,
                    NyxIdOrgToken = senderToken,
                    SenderNyxIdAccessToken = senderToken,
                },
            };
            return new CredentialDecision(
                true,
                senderContext,
                ResolveCredentialSource(senderContext),
                string.Empty);
        }

        if (!isMutation)
            return new CredentialDecision(true, context, ResolveDirectCredentialSource(context), string.Empty);

        return new CredentialDecision(
            false,
            context,
            AgentToolCredentialSource.ChannelRegistration,
            $"Tool '{toolName}' was not executed because sender binding '{senderBindingId}' has no valid NyxID credential.");
    }

    private static bool RequiresApproval(IAgentTool tool, AgentToolCallSafety callSafety)
    {
        if (tool.ApprovalMode == ToolApprovalMode.NeverRequire)
            return false;
        if (callSafety.RequiresApproval.HasValue)
            return callSafety.RequiresApproval.Value;
        if (tool.ApprovalMode == ToolApprovalMode.AlwaysRequire)
            return true;
        return !callSafety.IsReadOnly && callSafety.IsDestructive;
    }

    private static bool MatchesGrant(
        AgentToolApprovalGrant grant,
        AgentToolApprovalContinuationMode continuationMode,
        string approvalRequestId,
        string requestId,
        string toolName,
        string toolCallId,
        string argumentsSha256) =>
        continuationMode == AgentToolApprovalContinuationMode.ActorOwned &&
        string.Equals(NormalizeIdentity(grant.ApprovalRequestId), approvalRequestId, StringComparison.Ordinal) &&
        string.Equals(NormalizeIdentity(grant.RequestId), requestId, StringComparison.Ordinal) &&
        string.Equals(NormalizeIdentity(grant.ToolName), toolName, StringComparison.Ordinal) &&
        string.Equals(NormalizeIdentity(grant.ToolCallId), toolCallId, StringComparison.Ordinal) &&
        string.Equals(NormalizeIdentity(grant.ArgumentsSha256), argumentsSha256, StringComparison.Ordinal);

    private static AgentToolExecutionOutcome CreateDenied(
        IAgentTool tool,
        string toolName,
        string toolCallId,
        AgentToolCallSafety callSafety,
        bool isMutation,
        string failureCode,
        string safeMessage,
        AgentToolExecutionFailureStage failureStage,
        string approvalRequestId = "")
    {
        var resultJson = BuildFailureJson(failureCode, safeMessage, toolName);
        var receipt = AgentToolReceiptFactory.CreateDenied(
            tool,
            toolCallId,
            toolName,
            callSafety,
            resultJson,
            failureCode,
            safeMessage,
            approvalRequestId);
        return new AgentToolExecutionOutcome(
            AgentToolExecutionOutcomeKind.Denied,
            resultJson,
            receipt,
            isMutation,
            failureCode,
            safeMessage,
            failureStage,
            TerminalInvoked: false,
            Retryable: false,
            AuditCompleted: false);
    }

    private static AgentToolExecutionOutcome CreateFailure(
        IAgentTool tool,
        string toolName,
        string toolCallId,
        AgentToolCallSafety callSafety,
        bool isMutation,
        string failureCode,
        string safeMessage,
        AgentToolExecutionFailureStage failureStage,
        bool terminalInvoked,
        bool retryable,
        bool auditCompleted)
    {
        var resultJson = BuildFailureJson(failureCode, safeMessage, toolName);
        var receipt = AgentToolReceiptFactory.CreateError(
            tool,
            toolCallId,
            toolName,
            callSafety,
            resultJson,
            failureCode,
            safeMessage);
        return new AgentToolExecutionOutcome(
            AgentToolExecutionOutcomeKind.Failed,
            resultJson,
            receipt,
            isMutation,
            failureCode,
            safeMessage,
            failureStage,
            terminalInvoked,
            retryable,
            auditCompleted);
    }

    private static AgentToolExecutionOutcome CreateAuditFailure(
        IAgentTool tool,
        string toolName,
        string toolCallId,
        AgentToolCallSafety callSafety,
        bool isMutation,
        string failureCode,
        bool retryable,
        AgentToolExecutionFailureStage failureStage) =>
        CreateFailure(
            tool,
            toolName,
            toolCallId,
            callSafety,
            isMutation,
            failureCode,
            failureCode == "audit_unavailable"
                ? "The durable tool audit store is unavailable."
                : "The durable tool audit intent conflicts with an existing fact.",
            failureStage,
            terminalInvoked: false,
            retryable,
            auditCompleted: false);

    private static AgentToolExecutionOutcome CreateUnauditedFailure(
        IAgentTool tool,
        string toolName,
        string toolCallId,
        AgentToolCallSafety callSafety,
        string failureCode,
        string safeMessage,
        AgentToolExecutionFailureStage failureStage) =>
        CreateFailure(
            tool,
            toolName,
            toolCallId,
            callSafety,
            isMutation: true,
            failureCode,
            safeMessage,
            failureStage,
            terminalInvoked: false,
            retryable: false,
            auditCompleted: false);

    private static AuditOutcome MapAuditOutcome(AgentToolExecutionOutcome outcome) =>
        outcome.Kind switch
        {
            AgentToolExecutionOutcomeKind.Executed
                when outcome.Receipt.Status == AgentToolReceiptStatus.Success => AuditOutcome.Success,
            AgentToolExecutionOutcomeKind.ApprovalRequired => AuditOutcome.Accepted,
            AgentToolExecutionOutcomeKind.Denied => AuditOutcome.Denied,
            _ => AuditOutcome.Error,
        };

    private static AgentToolCredentialSource ResolveDirectCredentialSource(AgentToolExecutionContext context)
    {
        if (context.CredentialSource != AgentToolCredentialSource.Unspecified)
            return context.CredentialSource;
        if (!string.IsNullOrWhiteSpace(context.Schedule.ScheduleId))
            return AgentToolCredentialSource.ScheduledRun;
        return string.IsNullOrWhiteSpace(context.Credentials.NyxIdAccessToken)
            ? AgentToolCredentialSource.System
            : AgentToolCredentialSource.BearerToken;
    }

    private static AgentToolCredentialSource ResolveCredentialSource(AgentToolExecutionContext context)
    {
        if (context.CredentialSource != AgentToolCredentialSource.Unspecified)
            return context.CredentialSource;
        if (!string.IsNullOrWhiteSpace(context.Schedule.ScheduleId))
            return AgentToolCredentialSource.ScheduledRun;
        return string.IsNullOrWhiteSpace(context.SenderBinding.BindingId)
            ? ResolveDirectCredentialSource(context)
            : AgentToolCredentialSource.ChannelRegistration;
    }

    private static string CreateApprovalRequestId(
        string requestId,
        string toolName,
        string toolCallId,
        string argumentsSha256) =>
        "tool-approval:v1:" + HashLengthPrefixed(requestId, toolName, toolCallId, argumentsSha256);

    private static string CreateWaitingApprovalAuditId(
        string requestId,
        string toolCallId,
        string approvalRequestId) =>
        "tool:v1:waiting-approval:" + HashLengthPrefixed(requestId, toolCallId, approvalRequestId);

    private static string CreateRunningAuditId(string requestId, string toolCallId) =>
        "tool:v1:running:" + HashLengthPrefixed(requestId, toolCallId);

    private static string CreateTerminalAuditId(string requestId, string toolCallId) =>
        "tool:v1:terminal:" + HashLengthPrefixed(requestId, toolCallId);

    private static string HashLengthPrefixed(params string[] values)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[sizeof(uint)];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            stream.Write(length);
            stream.Write(bytes);
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static string BuildFailureJson(string code, string message, string toolName) =>
        JsonSerializer.Serialize(new { error = code, code, message, tool_name = toolName });

    private static string SafeExceptionClass(Exception ex) => ex.GetType().Name;

    private static string ResolveExceptionErrorCode(Exception exception) =>
        exception is CodexExecutionException codexException
            ? codexException.Failure.Kind switch
            {
                CodexExecutionFailureKind.TargetNotConfigured => "codex_execution_target_not_configured",
                CodexExecutionFailureKind.AdmissionDenied => "codex_execution_admission_denied",
                CodexExecutionFailureKind.LlmProviderNotConnected => "codex_execution_llm_provider_not_connected",
                CodexExecutionFailureKind.CapacityUnavailable => "codex_execution_capacity_unavailable",
                CodexExecutionFailureKind.ProvisioningFailed => "codex_execution_provisioning_failed",
                CodexExecutionFailureKind.ReadinessFailed => "codex_execution_readiness_failed",
                CodexExecutionFailureKind.IsolationUnavailable => "codex_execution_isolation_unavailable",
                CodexExecutionFailureKind.MalformedOutput => "codex_execution_malformed_output",
                CodexExecutionFailureKind.TerminalFailure => "codex_execution_terminal_failure",
                CodexExecutionFailureKind.TimedOut => "codex_execution_timed_out",
                CodexExecutionFailureKind.Cancelled => "codex_execution_cancelled",
                CodexExecutionFailureKind.CleanupFailed => "codex_execution_cleanup_failed",
                _ => "tool_execution_exception",
            }
            : "tool_execution_exception";

    private static string? NormalizeIdentity(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record CredentialDecision(
        bool Allowed,
        AgentToolExecutionContext ExecutionContext,
        AgentToolCredentialSource CredentialSource,
        string Message);
}
