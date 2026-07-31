using System.Diagnostics;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Auditing;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Auditing;

public sealed class ToolAuditRecordFactoryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-31T08:30:00Z");

    [Fact]
    public void Create_ChannelExecution_ShouldMapSenderActorAndOmitSensitivePayloads()
    {
        const string secret = "provider-secret-must-not-appear";
        var factory = CreateFactory();
        var context = BaseContext() with
        {
            Caller = new AgentToolCallerContext("scope-fallback", "owner-ignored", "session-1"),
            Channel = new AgentToolChannelContext(
                " lark ",
                " sender-1 ",
                " registration-1 ",
                "message-1",
                "platform-message-1"),
        };
        var receipt = SuccessReceipt();
        receipt.SubjectKind = "record";
        receipt.SubjectId = "record-1";
        receipt.SubjectVersion = "v2";
        receipt.SubjectHash = "sha256:record";
        receipt.ResultJson = $"{{\"secret\":\"{secret}\"}}";

        var record = factory.Create(
            "audit-1",
            "terminal",
            new TestTool("channel_write", "channel.message"),
            "channel_write",
            "call-1",
            "arguments-hash",
            new AgentToolCallSafety(false, false, true),
            context,
            AgentToolCredentialSource.ChannelRegistration,
            receipt,
            AuditOutcome.Success,
            isMutation: true);

        record.AuditActorId.Should().Be("hash:channel:lark:registration-1:sender-1");
        record.IdentityKeyId.Should().Be("test-key");
        record.ActorKind.Should().Be(AuditActorKind.ChannelSender);
        record.ScopeId.Should().Be("scope-fallback");
        record.CredentialSource.Should().Be(AuditCredentialSource.ChannelRegistration);
        record.Target.Kind.Should().Be("record");
        record.Target.Id.Should().Be("record-1");
        record.Annotations.Should().Contain("channel_platform", "lark");
        record.Annotations.Should().Contain("side_effect_kind", "channel.message");
        record.Annotations.Should().Contain("subject_version", "v2");
        record.Annotations.Should().Contain("subject_hash", "sha256:record");
        record.Redaction.ValuesSanitized.Should().BeTrue();
        record.Redaction.OmittedFields.Should().Equal("model.prompt", "tool.arguments", "tool.result");
        record.RequestSummary.Should().BeEmpty();
        record.ResultSummary.Should().BeEmpty();
        record.ToString().Should().NotContain(secret);
    }

    [Fact]
    public void Create_WithWorkflowReceiptAndActivity_ShouldMapCompleteCorrelation()
    {
        using var activity = new Activity("tool-audit-test")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();
        activity.Should().NotBeNull();
        var context = BaseContext() with
        {
            Caller = new AgentToolCallerContext("scope-1", "owner-1", "session-1"),
            WorkflowRuntime = new AgentWorkflowRuntimeContext(
                "parent-actor",
                "parent-run",
                "parent-step",
                "root-run",
                1),
        };
        var receipt = SuccessReceipt();
        receipt.ApprovalRequestId = "approval-1";
        receipt.WorkflowRunDelivery = new WorkflowRunBackgroundDeliveryReceipt
        {
            WorkflowCommandId = "command-1",
            WorkflowCorrelationId = "correlation-1",
        };

        var record = CreateFactory().Create(
            "audit-2",
            "terminal",
            new TestTool("workflow_tool"),
            "workflow_tool",
            "call-1",
            "arguments-hash",
            new AgentToolCallSafety(false, true, false),
            context,
            AgentToolCredentialSource.BearerToken,
            receipt,
            AuditOutcome.Success,
            isMutation: false);

        record.Correlation.TraceId.Should().Be(activity!.TraceId.ToString());
        record.Correlation.SpanId.Should().Be(activity.SpanId.ToString());
        record.Correlation.Traceparent.Should().Be(activity.Id);
        record.Correlation.RequestId.Should().Be("request-1");
        record.Correlation.CommandId.Should().Be("command-1");
        record.Correlation.CallId.Should().Be("call-1");
        record.Correlation.SessionId.Should().Be("session-1");
        record.Correlation.WorkflowRunId.Should().Be("parent-run");
        record.Correlation.ApprovalId.Should().Be("approval-1");
        record.Correlation.CorrelationId.Should().Be("correlation-1");
        record.Provenance.RunId.Should().Be("parent-run");
        record.Provenance.CorrelationId.Should().Be("correlation-1");
    }

    [Fact]
    public void Create_ScheduledExecution_ShouldPreferScheduleActorAndAnnotation()
    {
        var context = BaseContext() with
        {
            Caller = new AgentToolCallerContext("scope-1", "owner-1", null),
            Channel = new AgentToolChannelContext(
                "lark",
                "sender-1",
                "registration-1",
                null,
                null),
            Schedule = new AgentToolScheduleContext(" schedule-1 "),
        };

        var record = CreateFactory().Create(
            "audit-3",
            "terminal",
            new TestTool("scheduled_tool"),
            "scheduled_tool",
            "call-1",
            "arguments-hash",
            new AgentToolCallSafety(false, false, false),
            context,
            AgentToolCredentialSource.ScheduledRun,
            SuccessReceipt(),
            AuditOutcome.Success,
            isMutation: true);

        record.AuditActorId.Should().Be("hash:schedule:schedule-1");
        record.ActorKind.Should().Be(AuditActorKind.Schedule);
        record.CredentialSource.Should().Be(AuditCredentialSource.ScheduledRun);
        record.Annotations.Should().Contain("schedule_id", "schedule-1");
        record.OccurredAt.ToDateTimeOffset().Should().Be(Now);
        record.RecordedAt.ToDateTimeOffset().Should().Be(Now);
    }

    [Fact]
    public void Create_DeniedApproval_ShouldMapSanitizedAuthorizationFailure()
    {
        var receipt = new AgentToolReceipt
        {
            CallId = "call-1",
            ToolName = "dangerous_tool",
            Status = AgentToolReceiptStatus.Denied,
            ApprovalRequestId = "approval-1",
            ErrorCode = "approval_denied",
            ErrorMessage = "provider-secret-must-not-appear",
        };

        var record = CreateFactory().Create(
            "audit-4",
            "terminal",
            new TestTool("dangerous_tool"),
            "dangerous_tool",
            "call-1",
            "arguments-hash",
            new AgentToolCallSafety(true, false, true),
            BaseContext(),
            AgentToolCredentialSource.System,
            receipt,
            AuditOutcome.Denied,
            isMutation: true);

        record.Outcome.Should().Be(AuditOutcome.Denied);
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Failed);
        record.ErrorCode.Should().Be("approval_denied");
        record.ErrorSummary.Should().Be("approval_denied");
        record.Failure.Should().NotBeNull();
        record.Failure.Code.Should().Be("approval_denied");
        record.Failure.Category.Should().Be(AuditFailureCategory.Authorization);
        record.Failure.Retryability.Should().Be(AuditRetryability.NotRetryable);
        record.Failure.FailedPhase.Should().Be(AuditLifecyclePhase.WaitingApproval);
        record.Failure.SanitizedMessage.Should().Be("approval_denied");
        record.ToString().Should().NotContain("provider-secret-must-not-appear");
    }

    private static ToolAuditRecordFactory CreateFactory() =>
        new(new StableIdentityHasher(), new FixedTimeProvider(Now));

    private static AgentToolExecutionContext BaseContext() =>
        AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-1", "call-1"),
        };

    private static AgentToolReceipt SuccessReceipt() => new()
    {
        CallId = "call-1",
        ToolName = "test_tool",
        Status = AgentToolReceiptStatus.Success,
        ResultJson = "{}",
    };

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) =>
            new($"hash:{canonicalActorKey}", "test-key");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) =>
            auditActorId == $"hash:{canonicalActorKey}" && identityKeyId == "test-key";
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestTool(string name, string sideEffectKind = "") : IAgentTool
    {
        public string Name => name;
        public string Description => "test";
        public string ParametersSchema => "{}";
        public string SideEffectKind => sideEffectKind;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }
}
