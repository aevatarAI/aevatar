using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Auditing;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Core.Sanitization;
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
    public void Finalize_CodexExecutionException_ShouldPreserveClosedFailureClassInAudit(
        CodexExecutionFailureKind failureKind,
        string providerCode,
        string expectedAuditCode,
        AuditTerminalOutcome expectedTerminalOutcome,
        AuditFailureCategory expectedCategory)
    {
        var context = new ToolCallContext
        {
            Tool = new TestCodexTool(),
            ToolName = "codex_exec",
            ToolCallId = "call-codex",
            ArgumentsJson = "{}",
            ExecutionContext = AgentToolExecutionContext.Empty,
        };
        var exception = new CodexExecutionException(new CodexExecutionFailure(
            failureKind,
            providerCode,
            "Provider-owned detail must not become audit classification."));

        var finalized = ToolCallReceiptFinalizer.Finalize(context, exception);
        var audit = new ToolAuditRecordFactory(new StableAuditIdentityHasher())
            .Create(context, finalized);

        finalized.IsSynthetic.Should().BeTrue();
        finalized.Receipt.ErrorCode.Should().Be(expectedAuditCode);
        finalized.Receipt.ErrorMessage.Should().Be(nameof(CodexExecutionException));
        audit.ErrorCode.Should().Be(expectedAuditCode);
        audit.TerminalOutcome.Should().Be(expectedTerminalOutcome);
        audit.Failure.Should().NotBeNull();
        audit.Failure!.Code.Should().Be(expectedAuditCode);
        audit.Failure.Category.Should().Be(expectedCategory);
        audit.Failure.SanitizedMessage.Should().Be(nameof(CodexExecutionException));
    }

    [Fact]
    public void Finalize_CancelledCodexExecution_ShouldProduceValidCancelledAudit()
    {
        var context = CreateContext();
        var exception = new CodexExecutionException(new CodexExecutionFailure(
            CodexExecutionFailureKind.Cancelled,
            "managed_execution_cancelled",
            "Managed Codex execution was cancelled."));

        var audit = CreateAudit(context, exception);

        audit.ErrorCode.Should().BeEmpty();
        audit.Outcome.Should().Be(AuditOutcome.Cancelled);
        audit.TerminalOutcome.Should().Be(AuditTerminalOutcome.Cancelled);
        audit.Failure.Should().BeNull();
        new AuditRecordSanitizer().Sanitize(audit).Should().BeEquivalentTo(audit);
    }

    [Fact]
    public void Finalize_UnknownCodexFailureKind_ShouldUseGenericExceptionClassification()
    {
        var context = CreateContext();
        var exception = new CodexExecutionException(new CodexExecutionFailure(
            (CodexExecutionFailureKind)int.MaxValue,
            "provider_detail_must_not_escape",
            "Provider-owned detail must not escape."));

        var finalized = ToolCallReceiptFinalizer.Finalize(context, exception);
        var audit = new ToolAuditRecordFactory(new StableAuditIdentityHasher())
            .Create(context, finalized);

        finalized.Receipt.ErrorCode.Should().Be("tool_execution_exception");
        audit.ErrorCode.Should().Be("tool_execution_exception");
        audit.ToString().Should().NotContain("provider_detail_must_not_escape");
    }

    private static ToolCallContext CreateContext() => new()
    {
        Tool = new TestCodexTool(),
        ToolName = "codex_exec",
        ToolCallId = "call-codex",
        ArgumentsJson = "{}",
        ExecutionContext = AgentToolExecutionContext.Empty,
    };

    private static AuditRecord CreateAudit(ToolCallContext context, Exception exception) =>
        new ToolAuditRecordFactory(new StableAuditIdentityHasher()).Create(
            context,
            ToolCallReceiptFinalizer.Finalize(context, exception));

    private sealed class TestCodexTool : IAgentTool
    {
        public string Name => "codex_exec";

        public string Description => "Test Codex tool.";

        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }

    private sealed class StableAuditIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) =>
            new("audit-actor", "audit-key");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) =>
            auditActorId == "audit-actor" && identityKeyId == "audit-key";
    }
}
