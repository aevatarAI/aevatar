using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Tools;

public sealed class AdmittedAgentToolExecutorTests
{
    [Fact]
    public void ArgumentsDigest_ShouldPreserveExactInput()
    {
        AgentToolArgumentsDigest.Freeze(null).Should().BeEmpty();
        AgentToolArgumentsDigest.Freeze("  \t").Should().Be("  \t");
        AgentToolArgumentsDigest.Freeze(" { \"b\": 2, \"a\": 1 } ")
            .Should().Be(" { \"b\": 2, \"a\": 1 } ");

        AgentToolArgumentsDigest.ComputeSha256("{\"a\":1}")
            .Should().NotBe(AgentToolArgumentsDigest.ComputeSha256("{ \"a\": 1 }"));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFreezeOneExactArgumentString()
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(new AgentToolCallSafety(true, false, false));
        var executor = CreateExecutor(appender);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool) with { ArgumentsJson = " \t" });

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        tool.SafetyArguments.Should().Equal(" \t");
        tool.ExecutionArguments.Should().Equal(" \t");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldClassifyOnceAndExecuteAfterRunningAudit()
    {
        var events = new List<string>();
        var appender = new RecordingAuditTrailAppender((record, _) =>
        {
            events.Add(record.Annotations["execution_phase"]);
            return AuditTrailAppendResult.Appended(record.AuditId);
        });
        var tool = new RecordingTool(
            new AgentToolCallSafety(false, true, false),
            _ =>
            {
                events.Add("terminal");
                return "{\"ok\":true}";
            });
        var executor = CreateExecutor(appender);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.ResultJson.Should().Be("{\"ok\":true}");
        outcome.TerminalInvoked.Should().BeTrue();
        outcome.Retryable.Should().BeFalse();
        outcome.AuditCompleted.Should().BeTrue();
        tool.SafetyCalls.Should().Be(1);
        tool.ExecutionCalls.Should().Be(1);
        events.Should().Equal("running", "terminal", "terminal");
    }

    [Fact]
    public async Task ExecuteAsync_WhenRunningAuditIsDuplicate_ShouldNotReplayTerminal()
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            record.Annotations["execution_phase"] == "running"
                ? AuditTrailAppendResult.Duplicate(record.AuditId)
                : AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(new AgentToolCallSafety(false, true, false));
        var executor = CreateExecutor(appender);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Failed);
        outcome.FailureCode.Should().Be("tool_execution_already_started");
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.AuditIntent);
        outcome.TerminalInvoked.Should().BeFalse();
        outcome.Retryable.Should().BeFalse();
        outcome.AuditCompleted.Should().BeTrue();
        tool.ExecutionCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(AuditTrailAppendStatus.Conflict, "audit_intent_conflict", false)]
    [InlineData(AuditTrailAppendStatus.StoreUnavailable, "audit_unavailable", true)]
    public async Task ExecuteAsync_WhenRunningAuditDoesNotAppend_ShouldFailBeforeTerminal(
        AuditTrailAppendStatus appendStatus,
        string failureCode,
        bool retryable)
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            record.Annotations["execution_phase"] == "running"
                ? CreateAppendResult(appendStatus, record.AuditId)
                : AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(new AgentToolCallSafety(false, true, false));
        var executor = CreateExecutor(appender);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Failed);
        outcome.FailureCode.Should().Be(failureCode);
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.AuditIntent);
        outcome.TerminalInvoked.Should().BeFalse();
        outcome.Retryable.Should().Be(retryable);
        outcome.AuditCompleted.Should().BeFalse();
        tool.ExecutionCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTerminalAuditIsUnavailable_ShouldPreserveActualResultWithoutRetry()
    {
        var appendCount = 0;
        var appender = new RecordingAuditTrailAppender((record, _) =>
        {
            appendCount++;
            return record.Annotations["execution_phase"] == "terminal"
                ? AuditTrailAppendResult.StoreUnavailable(record.AuditId, "offline")
                : AuditTrailAppendResult.Appended(record.AuditId);
        });
        var tool = new RecordingTool(
            new AgentToolCallSafety(false, true, false),
            _ => "{\"changed\":true}");
        var executor = CreateExecutor(appender);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.ExecutedAuditIncomplete);
        outcome.ResultJson.Should().Be("{\"changed\":true}");
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.TerminalAudit);
        outcome.TerminalInvoked.Should().BeTrue();
        outcome.Retryable.Should().BeFalse();
        outcome.AuditCompleted.Should().BeFalse();
        tool.ExecutionCalls.Should().Be(1);
        appendCount.Should().Be(2);
    }

    [Theory]
    [InlineData(AuditTrailAppendStatus.Conflict)]
    [InlineData(AuditTrailAppendStatus.StoreUnavailable)]
    public async Task ExecuteAsync_WhenTerminalFailsAndAuditDoesNotAppend_ShouldPreserveFailureWithoutRetry(
        AuditTrailAppendStatus appendStatus)
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            record.Annotations["execution_phase"] == "terminal"
                ? CreateAppendResult(appendStatus, record.AuditId)
                : AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(
            new AgentToolCallSafety(false, true, false),
            _ => throw new InvalidOperationException("terminal failed"));
        var executor = CreateExecutor(appender);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Failed);
        outcome.FailureCode.Should().Be("tool_execution_exception");
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.TerminalExecution);
        outcome.TerminalInvoked.Should().BeTrue();
        outcome.Retryable.Should().BeFalse();
        outcome.AuditCompleted.Should().BeFalse();
        tool.ExecutionCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTerminalThrows_ShouldRecordSafeFailedTerminal()
    {
        const string rawException = "terminal failed with bearer-secret";
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(
            new AgentToolCallSafety(false, true, false),
            _ => throw new InvalidOperationException(rawException));
        var executor = CreateExecutor(appender);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Failed);
        outcome.FailureCode.Should().Be("tool_execution_exception");
        outcome.SafeMessage.Should().Be(nameof(InvalidOperationException));
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.TerminalExecution);
        outcome.TerminalInvoked.Should().BeTrue();
        outcome.Retryable.Should().BeFalse();
        outcome.AuditCompleted.Should().BeTrue();
        outcome.ResultJson.Should().NotContain(rawException).And.NotContain("bearer-secret");
        outcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Error);
        outcome.Receipt.ErrorCode.Should().Be("tool_execution_exception");
        outcome.Receipt.ErrorMessage.Should().Be(nameof(InvalidOperationException));
        outcome.Receipt.ResultJson.Should().Be(outcome.ResultJson);
        tool.ExecutionCalls.Should().Be(1);
        appender.Records.Select(record => record.Annotations["execution_phase"])
            .Should().Equal("running", "terminal");
    }

    [Fact]
    public async Task ExecuteAsync_WhenReceiptCreationThrows_ShouldReturnAuditedSafeUnknown()
    {
        const string rawResult = "{\"secret\":\"provider-secret\"}";
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(
            new AgentToolCallSafety(true, false, false),
            _ => rawResult,
            throwOnReceipt: true);
        var executor = CreateExecutor(appender);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.ResultJson.Should().Be(
            "{\"status\":\"unknown\",\"message\":\"The tool outcome could not be verified.\"}");
        outcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Unspecified);
        outcome.Receipt.ErrorCode.Should().Be("tool_outcome_unknown");
        outcome.Receipt.ErrorMessage.Should().Be("The tool outcome could not be verified.");
        outcome.Receipt.ResultJson.Should().Be(outcome.ResultJson);
        outcome.ResultJson.Should().NotContain("provider-secret");
        outcome.TerminalInvoked.Should().BeTrue();
        outcome.Retryable.Should().BeFalse();
        outcome.AuditCompleted.Should().BeTrue();
        tool.ExecutionCalls.Should().Be(1);
        appender.Records.Select(record => record.Annotations["execution_phase"])
            .Should().Equal("running", "terminal");
    }

    [Fact]
    public async Task ExecuteAsync_WhenProviderReturnsSafeErrorReceipt_ShouldNotExposeRawResult()
    {
        const string rawResult = "{\"error\":true,\"body\":\"bearer-secret\"}";
        const string safeResult = "{\"error\":\"provider_request_failed\"}";
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(
            new AgentToolCallSafety(true, false, false),
            _ => rawResult,
            createReceipt: _ => new AgentToolReceipt
            {
                Status = AgentToolReceiptStatus.Error,
                ErrorCode = "provider_request_failed",
                ErrorMessage = "The provider request failed.",
                ResultJson = safeResult,
            });
        var executor = CreateExecutor(appender);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.ResultJson.Should().Be(safeResult);
        outcome.ResultJson.Should().NotContain("bearer-secret");
        outcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Error);
        outcome.Receipt.ResultJson.Should().Be(safeResult);
        outcome.TerminalInvoked.Should().BeTrue();
        outcome.AuditCompleted.Should().BeTrue();
        tool.ExecutionCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenProviderErrorReceiptHasNoSafeResult_ShouldNotFallBackToRawResult()
    {
        const string rawResult = "{\"error\":true,\"body\":\"bearer-secret\"}";
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(
            new AgentToolCallSafety(true, false, false),
            _ => rawResult,
            createReceipt: _ => new AgentToolReceipt
            {
                Status = AgentToolReceiptStatus.Error,
                ErrorCode = "provider_request_failed",
                ErrorMessage = "The provider request failed.",
            });
        var executor = CreateExecutor(appender);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.ResultJson.Should().BeEmpty();
        outcome.ResultJson.Should().NotContain("bearer-secret");
        outcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Error);
        outcome.Receipt.ResultJson.Should().BeEmpty();
        outcome.TerminalInvoked.Should().BeTrue();
        outcome.AuditCompleted.Should().BeTrue();
        tool.ExecutionCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenActorOwnedApprovalHasNoGrant_ShouldYieldWithoutExecuting()
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(new AgentToolCallSafety(true, false, true))
        {
            ApprovalMode = ToolApprovalMode.Auto,
        };
        var executor = CreateExecutor(appender);
        var request = CreateRequest(tool) with
        {
            ApprovalContinuationMode = AgentToolApprovalContinuationMode.ActorOwned,
        };

        var outcome = await executor.ExecuteAsync(request);

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.ApprovalRequired);
        outcome.Receipt.ApprovalRequestId.Should().StartWith("tool-approval:v1:");
        outcome.TerminalInvoked.Should().BeFalse();
        outcome.AuditCompleted.Should().BeTrue();
        tool.ExecutionCalls.Should().Be(0);
        appender.Records.Should().ContainSingle();
        appender.Records[0].Annotations["execution_phase"].Should().Be("waiting_approval");
    }

    [Theory]
    [InlineData(AuditTrailAppendStatus.Conflict, "audit_intent_conflict", false)]
    [InlineData(AuditTrailAppendStatus.StoreUnavailable, "audit_unavailable", true)]
    public async Task ExecuteAsync_WhenWaitingApprovalAuditDoesNotAppend_ShouldFailWithoutExecuting(
        AuditTrailAppendStatus appendStatus,
        string failureCode,
        bool retryable)
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            record.Annotations["execution_phase"] == "waiting_approval"
                ? CreateAppendResult(appendStatus, record.AuditId)
                : AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(new AgentToolCallSafety(true, false, true))
        {
            ApprovalMode = ToolApprovalMode.Auto,
        };
        var executor = CreateExecutor(appender);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool) with
        {
            ApprovalContinuationMode = AgentToolApprovalContinuationMode.ActorOwned,
        });

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Failed);
        outcome.FailureCode.Should().Be(failureCode);
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.AuditIntent);
        outcome.TerminalInvoked.Should().BeFalse();
        outcome.Retryable.Should().Be(retryable);
        outcome.AuditCompleted.Should().BeFalse();
        tool.ExecutionCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("approval-request-id")]
    [InlineData("request-id")]
    [InlineData("tool-name")]
    [InlineData("tool-call-id")]
    [InlineData("arguments-sha256")]
    public async Task ExecuteAsync_WhenGrantFieldDoesNotMatch_ShouldFailClosed(string mismatchedField)
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(new AgentToolCallSafety(true, false, true))
        {
            ApprovalMode = ToolApprovalMode.Auto,
        };
        var executor = CreateExecutor(appender);
        var initial = await executor.ExecuteAsync(CreateRequest(tool) with
        {
            ApprovalContinuationMode = AgentToolApprovalContinuationMode.ActorOwned,
        });
        var digest = AgentToolArgumentsDigest.ComputeSha256("{}");
        var grant = new AgentToolApprovalGrant(
            initial.Receipt.ApprovalRequestId,
            "request-1",
            tool.Name,
            "call-1",
            digest);
        grant = mismatchedField switch
        {
            "approval-request-id" => grant with { ApprovalRequestId = "wrong" },
            "request-id" => grant with { RequestId = "wrong" },
            "tool-name" => grant with { ToolName = "wrong" },
            "tool-call-id" => grant with { ToolCallId = "wrong" },
            "arguments-sha256" => grant with { ArgumentsSha256 = new string('0', 64) },
            _ => grant,
        };

        var outcome = await executor.ExecuteAsync(CreateRequest(tool) with
        {
            ApprovalContinuationMode = AgentToolApprovalContinuationMode.ActorOwned,
            ApprovalGrant = grant,
        });

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Denied);
        outcome.FailureCode.Should().Be("approval_grant_mismatch");
        outcome.TerminalInvoked.Should().BeFalse();
        tool.ExecutionCalls.Should().Be(0);
    }

    private static AdmittedAgentToolExecutor CreateExecutor(IAuditTrailAppender appender) =>
        new(appender, new StableIdentityHasher());

    private static AuditTrailAppendResult CreateAppendResult(
        AuditTrailAppendStatus status,
        string auditId) => status switch
        {
            AuditTrailAppendStatus.Conflict => AuditTrailAppendResult.Conflict(auditId, "conflict"),
            AuditTrailAppendStatus.StoreUnavailable => AuditTrailAppendResult.StoreUnavailable(auditId, "offline"),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };

    private static AgentToolExecutionRequest CreateRequest(IAgentTool tool) =>
        new(
            tool,
            "{}",
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-1", "call-1"),
            },
            AgentToolApprovalContinuationMode.None,
            null);

    private sealed class RecordingTool(
        AgentToolCallSafety safety,
        Func<string, string>? execute = null,
        bool throwOnReceipt = false,
        Func<string, AgentToolReceipt?>? createReceipt = null) : IAgentTool
    {
        private readonly Func<string, string> _execute = execute ?? (_ => "{}");

        public string Name => "test_tool";
        public string Description => "test";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode { get; init; } = ToolApprovalMode.NeverRequire;
        public int SafetyCalls { get; private set; }
        public int ExecutionCalls { get; private set; }
        public List<string> SafetyArguments { get; } = [];
        public List<string> ExecutionArguments { get; } = [];

        public AgentToolCallSafety GetCallSafety(string argumentsJson)
        {
            SafetyCalls++;
            SafetyArguments.Add(argumentsJson);
            return safety;
        }

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson)
        {
            if (throwOnReceipt)
                throw new InvalidOperationException("receipt failed with provider-secret");

            if (createReceipt is not null)
                return createReceipt(resultJson);

            return new AgentToolReceipt
            {
                CallId = callId,
                ToolName = toolName,
                Status = AgentToolReceiptStatus.Success,
                ResultJson = resultJson,
            };
        }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecutionCalls++;
            ExecutionArguments.Add(argumentsJson);
            return Task.FromResult(_execute(argumentsJson));
        }
    }

    private sealed class RecordingAuditTrailAppender(
        Func<AuditRecord, int, AuditTrailAppendResult> append) : IAuditTrailAppender
    {
        public List<AuditRecord> Records { get; } = [];

        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.FromResult(append(record, Records.Count));
        }
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }
}
