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
        CodexExecutionFailureKind.MalformedOutput,
        "managed_response_invalid",
        "codex_execution_malformed_output",
        AuditTerminalOutcome.Failed,
        AuditFailureCategory.Execution)]
    [InlineData(
        CodexExecutionFailureKind.ProvisioningFailed,
        "managed_credential_commit_timeout",
        "codex_execution_provisioning_failed",
        AuditTerminalOutcome.Failed,
        AuditFailureCategory.Execution)]
    [InlineData(
        CodexExecutionFailureKind.TimedOut,
        "managed_proxy_timeout",
        "codex_execution_timed_out",
        AuditTerminalOutcome.TimedOut,
        AuditFailureCategory.Timeout)]
    [InlineData(
        CodexExecutionFailureKind.CapacityUnavailable,
        "managed_proxy_unavailable",
        "codex_execution_capacity_unavailable",
        AuditTerminalOutcome.Failed,
        AuditFailureCategory.Execution)]
    public async Task StreamingExecution_WhenManagedPortFails_ShouldPreserveClosedFailureClassInAudit(
        CodexExecutionFailureKind failureKind,
        string providerCode,
        string expectedAuditCode,
        AuditTerminalOutcome expectedTerminalOutcome,
        AuditFailureCategory expectedCategory)
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
            },
            AgentToolApprovalContinuationMode.None,
            null));

        outcome.Receipt.ErrorCode.Should().Be(expectedAuditCode);
        outcome.Receipt.ErrorMessage.Should().Be(nameof(CodexExecutionException));
        var audit = appender.Records.Should().ContainSingle(record =>
            record.LifecyclePhase == AuditLifecyclePhase.Terminal).Which;
        audit.ErrorCode.Should().Be(expectedAuditCode);
        audit.TerminalOutcome.Should().Be(expectedTerminalOutcome);
        audit.Failure.Should().NotBeNull();
        audit.Failure!.Code.Should().Be(expectedAuditCode);
        audit.Failure.Category.Should().Be(expectedCategory);
        audit.Failure.SanitizedMessage.Should().Be(expectedAuditCode);
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
