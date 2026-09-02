using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class CodexExecutionFailureAuditTests
{
    [Theory]
    [InlineData(
        CodexExecutionFailureKind.TargetNotConfigured,
        "provider_detail_01",
        "codex_execution_target_not_configured",
        AuditTerminalOutcome.Failed,
        AuditOutcome.Error,
        AuditFailureCategory.Execution)]
    [InlineData(
        CodexExecutionFailureKind.AdmissionDenied,
        "provider_detail_02",
        "codex_execution_admission_denied",
        AuditTerminalOutcome.Failed,
        AuditOutcome.Error,
        AuditFailureCategory.Execution)]
    [InlineData(
        CodexExecutionFailureKind.LlmProviderNotConnected,
        "provider_detail_03",
        "codex_execution_llm_provider_not_connected",
        AuditTerminalOutcome.Failed,
        AuditOutcome.Error,
        AuditFailureCategory.Execution)]
    [InlineData(
        CodexExecutionFailureKind.CapacityUnavailable,
        "provider_detail_04",
        "codex_execution_capacity_unavailable",
        AuditTerminalOutcome.Failed,
        AuditOutcome.Error,
        AuditFailureCategory.Execution)]
    [InlineData(
        CodexExecutionFailureKind.ProvisioningFailed,
        "provider_detail_05",
        "codex_execution_provisioning_failed",
        AuditTerminalOutcome.Failed,
        AuditOutcome.Error,
        AuditFailureCategory.Execution)]
    [InlineData(
        CodexExecutionFailureKind.ReadinessFailed,
        "provider_detail_06",
        "codex_execution_readiness_failed",
        AuditTerminalOutcome.Failed,
        AuditOutcome.Error,
        AuditFailureCategory.Execution)]
    [InlineData(
        CodexExecutionFailureKind.IsolationUnavailable,
        "provider_detail_07",
        "codex_execution_isolation_unavailable",
        AuditTerminalOutcome.Failed,
        AuditOutcome.Error,
        AuditFailureCategory.Execution)]
    [InlineData(
        CodexExecutionFailureKind.MalformedOutput,
        "provider_detail_08",
        "codex_execution_malformed_output",
        AuditTerminalOutcome.Failed,
        AuditOutcome.Error,
        AuditFailureCategory.Execution)]
    [InlineData(
        CodexExecutionFailureKind.TerminalFailure,
        "provider_detail_09",
        "codex_execution_terminal_failure",
        AuditTerminalOutcome.Failed,
        AuditOutcome.Error,
        AuditFailureCategory.Execution)]
    [InlineData(
        CodexExecutionFailureKind.TimedOut,
        "provider_detail_10",
        "codex_execution_timed_out",
        AuditTerminalOutcome.TimedOut,
        AuditOutcome.Error,
        AuditFailureCategory.Timeout)]
    [InlineData(
        CodexExecutionFailureKind.Cancelled,
        "provider_detail_11",
        "codex_execution_cancelled",
        AuditTerminalOutcome.Cancelled,
        AuditOutcome.Cancelled,
        null)]
    [InlineData(
        CodexExecutionFailureKind.CleanupFailed,
        "provider_detail_12",
        "codex_execution_cleanup_failed",
        AuditTerminalOutcome.Failed,
        AuditOutcome.Error,
        AuditFailureCategory.Execution)]
    [InlineData(
        (CodexExecutionFailureKind)999,
        "provider_detail_999",
        "tool_execution_exception",
        AuditTerminalOutcome.Failed,
        AuditOutcome.Error,
        AuditFailureCategory.Execution)]
    public async Task StreamingExecution_WhenManagedPortFails_ShouldPreserveClosedFailureClassInAudit(
        CodexExecutionFailureKind failureKind,
        string providerCode,
        string expectedAuditCode,
        AuditTerminalOutcome expectedTerminalOutcome,
        AuditOutcome expectedOutcome,
        AuditFailureCategory? expectedCategory)
    {
        var failure = new CodexExecutionFailure(
            failureKind,
            providerCode,
            "Provider-owned detail must not become audit classification.");
        var tools = new ToolManager();
        tools.Register(new NyxIdCodexExecTool(
            [new FailingManagedPort(failure)],
            new NyxIdToolOptions()));
        var appender = new RecordingAuditTrailAppender();
        var executor = new AdmittedAgentToolExecutor(
            AlwaysStartingAgentToolAdmissionLedger.Instance,
            appender,
            new StableAuditIdentityHasher());
        const string argumentsJson = """
                {
                  "target": { "kind": "managed_sandbox" },
                  "workspace": { "kind": "empty_git" },
                  "prompt": "task"
                }
                """;
        var outcome = await executor.ExecuteAsync(new AgentToolExecutionRequest(
            tools.Get("codex_exec")!,
            argumentsJson,
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-codex", "call-codex"),
                ExecutionOwner = AgentToolExecutionOwners.Actor("actor-codex"),
            },
            AgentToolApprovalContinuationMode.None,
            null));

        outcome.Receipt.ErrorCode.Should().Be(expectedAuditCode);
        outcome.Receipt.ErrorMessage.Should().Be(nameof(CodexExecutionException));
        var audit = appender.Records.Should().ContainSingle(record =>
            record.LifecyclePhase == AuditLifecyclePhase.Terminal).Which;
        audit.ErrorCode.Should().Be(expectedAuditCode);
        audit.Outcome.Should().Be(expectedOutcome);
        audit.TerminalOutcome.Should().Be(expectedTerminalOutcome);
        if (expectedCategory is null)
        {
            audit.Failure.Should().BeNull();
        }
        else
        {
            audit.Failure.Should().NotBeNull();
            audit.Failure!.Code.Should().Be(expectedAuditCode);
            audit.Failure.Category.Should().Be(expectedCategory.Value);
            audit.Failure.SanitizedMessage.Should().Be(expectedAuditCode);
        }
        audit.ToString().Should().NotContain(providerCode);
    }

    private sealed class FailingManagedPort(CodexExecutionFailure failure) : ICodexExecutionPort
    {
        public CodexExecutionTarget.TargetOneofCase TargetKind =>
            CodexExecutionTarget.TargetOneofCase.ManagedSandbox;

        public async IAsyncEnumerable<CodexExecutionEvent> ExecuteAsync(
            CodexExecutionRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return CodexExecutionEvent.Failed(failure);
        }
    }

    private sealed class RecordingAuditTrailAppender : IAuditTrailAppender
    {
        public List<AuditRecord> Records { get; } = [];

        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
        }
    }

    private sealed class StableAuditIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) =>
            new("audit-actor", "audit-key");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) =>
            auditActorId == "audit-actor" && identityKeyId == "audit-key";
    }
}
