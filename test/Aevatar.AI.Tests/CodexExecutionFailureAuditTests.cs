using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
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
using Microsoft.Extensions.Logging;

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
        CodexExecutionFailureKind.AdmissionDenied,
        "provider_detail\nunsafe",
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
        CodexExecutionFailureKind.CapacityUnavailable,
        "managed_upstream_codex_sandbox_creation_failed",
        "managed_upstream_codex_sandbox_creation_failed",
        AuditTerminalOutcome.Failed,
        AuditOutcome.Error,
        AuditFailureCategory.Execution)]
    [InlineData(
        CodexExecutionFailureKind.CapacityUnavailable,
        "managed_upstream_provider_capacity_unavailable",
        "codex_execution_capacity_unavailable",
        AuditTerminalOutcome.Failed,
        AuditOutcome.Error,
        AuditFailureCategory.Execution)]
    [InlineData(
        CodexExecutionFailureKind.CapacityUnavailable,
        "managed_upstream_codex_",
        "codex_execution_capacity_unavailable",
        AuditTerminalOutcome.Failed,
        AuditOutcome.Error,
        AuditFailureCategory.Execution)]
    [InlineData(
        CodexExecutionFailureKind.CapacityUnavailable,
        " managed_upstream_codex_capacity_unavailable",
        "codex_execution_capacity_unavailable",
        AuditTerminalOutcome.Failed,
        AuditOutcome.Error,
        AuditFailureCategory.Execution)]
    [InlineData(
        CodexExecutionFailureKind.CapacityUnavailable,
        "managed_upstream_codex_sandbox_creation_failed\nunsafe",
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
        var logger = new RecordingLogger<AdmittedAgentToolExecutor>();
        var executor = new AdmittedAgentToolExecutor(
            AlwaysStartingAgentToolAdmissionLedger.Instance,
            appender,
            new StableAuditIdentityHasher(),
            logger: logger);
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
                WorkflowRuntime = new AgentWorkflowRuntimeContext(
                    "parent-actor",
                    "parent-run",
                    "parent-step",
                    "sensitive-root-run-id",
                    0),
            },
            AgentToolApprovalContinuationMode.None,
            null));

        outcome.Receipt.ErrorCode.Should().Be(expectedAuditCode);
        outcome.Receipt.FailureOutcome.Should().Be(
            expectedCategory == AuditFailureCategory.Timeout ||
            expectedAuditCode == "tool_execution_exception"
                ? AgentToolFailureOutcome.OutcomeUncertain
                : AgentToolFailureOutcome.CalleeConfirmed);
        outcome.Receipt.ErrorMessage.Should().Be(
            expectedAuditCode == providerCode
                ? "Provider-owned detail must not become audit classification."
                : ExpectedCanonicalMessage(failureKind));
        using (var result = System.Text.Json.JsonDocument.Parse(outcome.Receipt.ResultJson))
        {
            result.RootElement.GetProperty("code").GetString().Should().Be(expectedAuditCode);
            result.RootElement.GetProperty("message").GetString().Should()
                .Be(outcome.Receipt.ErrorMessage);
        }
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
        if (expectedAuditCode == providerCode)
            audit.ToString().Should().Contain(providerCode);
        else
            audit.ToString().Should().NotContain(providerCode);
        var warning = logger.Entries.Should().ContainSingle().Which;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain($"failureKind={failureKind}");
        var normalizedDiagnosticCode = providerCode.Trim();
        var expectedDiagnosticCode = normalizedDiagnosticCode.Length is > 0 and <= 96 &&
                                     char.IsAsciiLetterLower(normalizedDiagnosticCode[0]) &&
                                     normalizedDiagnosticCode.All(character =>
                                         char.IsAsciiLetterLower(character) ||
                                         char.IsAsciiDigit(character) ||
                                         character == '_')
            ? normalizedDiagnosticCode
            : "unclassified";
        warning.Message.Should().Contain($"failureCode={expectedDiagnosticCode}");
        if (expectedDiagnosticCode == "unclassified")
            warning.Message.Should().NotContain(providerCode);
        warning.Message.Should().Contain("runHash=0b42f7cb2207");
        warning.Message.Should().NotContain("sensitive-root-run-id");
    }

    [Fact]
    public async Task StreamingExecution_WhenManagedFailureIsOwned_PreservesSafeTypedEvidence()
    {
        var failure = new CodexExecutionFailure(
            CodexExecutionFailureKind.MalformedOutput,
            "managed_response_invalid",
            "Managed Codex returned an invalid response.",
            "managed-diag-17");
        var tools = new ToolManager();
        tools.Register(new NyxIdCodexExecTool(
            [new FailingManagedPort(failure)],
            new NyxIdToolOptions()));
        var executor = new AdmittedAgentToolExecutor(
            AlwaysStartingAgentToolAdmissionLedger.Instance,
            new RecordingAuditTrailAppender(),
            new StableAuditIdentityHasher());

        var outcome = await executor.ExecuteAsync(new AgentToolExecutionRequest(
            tools.Get("codex_exec")!,
            """{"target":{"kind":"managed_sandbox"},"workspace":{"kind":"empty_git"},"prompt":"task"}""",
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-codex", "call-codex"),
                ExecutionOwner = AgentToolExecutionOwners.Actor("actor-codex"),
            },
            AgentToolApprovalContinuationMode.None,
            null));

        outcome.Receipt.ErrorCode.Should().Be("managed_response_invalid");
        outcome.Receipt.ErrorMessage.Should().Be("Managed Codex returned an invalid response.");
        using var result = System.Text.Json.JsonDocument.Parse(outcome.Receipt.ResultJson);
        result.RootElement.GetProperty("diagnostic_id").GetString().Should().Be("managed-diag-17");
    }

    private static string ExpectedCanonicalMessage(CodexExecutionFailureKind kind) =>
        kind switch
        {
            CodexExecutionFailureKind.TargetNotConfigured => "Codex execution target is not configured.",
            CodexExecutionFailureKind.AdmissionDenied => "Codex execution was not admitted.",
            CodexExecutionFailureKind.LlmProviderNotConnected => "Codex LLM provider is not connected.",
            CodexExecutionFailureKind.CapacityUnavailable => "Codex execution capacity is unavailable.",
            CodexExecutionFailureKind.ProvisioningFailed => "Codex execution provisioning failed.",
            CodexExecutionFailureKind.ReadinessFailed => "Codex execution target is not ready.",
            CodexExecutionFailureKind.IsolationUnavailable => "Codex execution isolation is unavailable.",
            CodexExecutionFailureKind.MalformedOutput => "Codex execution returned malformed output.",
            CodexExecutionFailureKind.TimedOut => "Codex execution timed out.",
            CodexExecutionFailureKind.Cancelled => "Codex execution was cancelled.",
            CodexExecutionFailureKind.CleanupFailed => "Codex execution cleanup failed.",
            _ => "Codex execution failed.",
        };

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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
