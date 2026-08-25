using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Audit.Core.Sanitization;
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

    [Theory]
    [InlineData("owner")]
    [InlineData("request")]
    [InlineData("call")]
    [InlineData("tool")]
    public async Task ExecuteAsync_WhenStableIdentityIsMissing_ShouldFailBeforeAnyCollaborator(
        string missingIdentity)
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Started);
        var tool = new RecordingTool(
            new AgentToolCallSafety(false, true, false),
            name: missingIdentity == "tool" ? " " : "test_tool");
        var executor = CreateExecutor(appender, ledger);
        var request = CreateRequest(tool) with
        {
            ExecutionContext = CreateTestExecutionContext() with
            {
                ExecutionOwner = missingIdentity == "owner"
                    ? new AgentToolExecutionOwner()
                    : AgentToolExecutionOwners.Actor("actor-test"),
                Request = new AgentToolRequestIdentity(
                    missingIdentity == "request" ? " " : "request-1",
                    missingIdentity == "call" ? " " : "call-1"),
            },
        };

        var outcome = await executor.ExecuteAsync(request);

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Failed);
        outcome.FailureCode.Should().Be("invalid_tool_execution_identity");
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.RequestValidation);
        outcome.TerminalInvoked.Should().BeFalse();
        outcome.AuditCompleted.Should().BeFalse();
        outcome.Retryable.Should().BeFalse();
        tool.SafetyCalls.Should().Be(0);
        tool.ExecutionCalls.Should().Be(0);
        ledger.Facts.Should().BeEmpty();
        appender.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenSafetyClassificationThrows_ShouldFailClosedWithoutLeakingException()
    {
        const string rawException = "classification failed with bearer-secret";

        await AssertClassificationFailureAsync(
            _ => throw new InvalidOperationException(rawException),
            rawException);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSafetyClassificationReturnsNull_ShouldFailClosedBeforeAdmission()
    {
        const string internalFailureDetail = "Tool safety classification is required.";

        await AssertClassificationFailureAsync(_ => null!, internalFailureDetail);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTerminalSucceeds_ShouldEmitSafeSpanAndDurationMetric()
    {
        const string secret = "telemetry-secret";
        var activities = new List<Activity>();
        var measurements = new List<(double Value, KeyValuePair<string, object?>[] Tags)>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Aevatar.GenAI",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity),
        };
        ActivitySource.AddActivityListener(activityListener);
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Aevatar.GenAI" &&
                instrument.Name == "aevatar.tool.invocation.duration")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            measurements.Add((value, tags.ToArray())));
        meterListener.Start();
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(
            new AgentToolCallSafety(false, true, false),
            _ => $"{{\"value\":\"{secret}\"}}");
        var executor = CreateExecutor(appender);
        var request = CreateRequest(tool) with
        {
            ArgumentsJson = $"{{\"token\":\"{secret}\"}}",
            ExecutionContext = CreateTestExecutionContext() with
            {
                Request = new AgentToolRequestIdentity("request-observable-success", "call-observable-success"),
            },
        };

        var outcome = await executor.ExecuteAsync(request);

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        var activity = activities.Single(item =>
            Equals(item.GetTagItem("gen_ai.tool.call_id"), "call-observable-success"));
        activity.Kind.Should().Be(ActivityKind.Internal);
        activity.GetTagItem("gen_ai.operation.name").Should().Be("execute_tool");
        activity.GetTagItem("gen_ai.tool.name").Should().Be("test_tool");
        activity.GetTagItem("gen_ai.tool.status").Should().Be("ok");
        activity.GetTagItem("gen_ai.tool.arguments").Should().BeNull();
        activity.GetTagItem("gen_ai.tool.result").Should().BeNull();
        activity.ToString().Should().NotContain(secret);
        measurements.Should().Contain(item =>
            item.Value >= 0 &&
            item.Tags.Any(tag =>
                tag.Key == "gen_ai.tool.name" && Equals(tag.Value, "test_tool")));
    }

    [Fact]
    public async Task ExecuteAsync_WhenTerminalThrows_ShouldEmitSafeErrorSpanWithoutPayload()
    {
        const string secret = "telemetry-secret";
        var activities = new List<Activity>();
        var measurements = new List<(double Value, KeyValuePair<string, object?>[] Tags)>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Aevatar.GenAI",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity),
        };
        ActivitySource.AddActivityListener(activityListener);
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Aevatar.GenAI" &&
                instrument.Name == "aevatar.tool.invocation.duration")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            measurements.Add((value, tags.ToArray())));
        meterListener.Start();
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(
            new AgentToolCallSafety(false, true, false),
            _ => throw new InvalidOperationException($"terminal failed with {secret}"));
        var executor = CreateExecutor(appender);
        var request = CreateRequest(tool) with
        {
            ArgumentsJson = $"{{\"token\":\"{secret}\"}}",
            ExecutionContext = CreateTestExecutionContext() with
            {
                Request = new AgentToolRequestIdentity("request-observable-error", "call-observable-error"),
            },
        };

        var outcome = await executor.ExecuteAsync(request);

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Failed);
        var activity = activities.Single(item =>
            Equals(item.GetTagItem("gen_ai.tool.call_id"), "call-observable-error"));
        activity.GetTagItem("gen_ai.tool.status").Should().Be("error");
        activity.GetTagItem("error.type").Should().Be(typeof(InvalidOperationException).FullName);
        activity.GetTagItem("error.message").Should().BeNull();
        activity.GetTagItem("gen_ai.tool.arguments").Should().BeNull();
        activity.GetTagItem("gen_ai.tool.result").Should().BeNull();
        activity.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().Be(nameof(InvalidOperationException));
        activity.ToString().Should().NotContain(secret);
        measurements.Should().Contain(item =>
            item.Value >= 0 &&
            item.Tags.Any(tag =>
                tag.Key == "gen_ai.tool.name" && Equals(tag.Value, "test_tool")));
    }

    [Theory]
    [InlineData(null, null, true, true)]
    [InlineData(null, null, false, false)]
    [InlineData("binding-1", null, true, true)]
    [InlineData("binding-1", null, false, false)]
    [InlineData("binding-1", "sender-token", true, true)]
    [InlineData("binding-1", "sender-token", false, true)]
    public async Task ExecuteAsync_ChannelSenderCredentialMatrix_ShouldEnforceMutationIsolation(
        string? bindingId,
        string? senderToken,
        bool isReadOnly,
        bool expectedExecution)
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(new AgentToolCallSafety(false, isReadOnly, false));
        var executor = CreateExecutor(appender);
        var context = CreateTestExecutionContext() with
        {
            Request = new AgentToolRequestIdentity("request-1", "call-1"),
            Credentials = new AgentToolCredentials("owner-token", "org-token", senderToken),
            Channel = new AgentToolChannelContext(
                "lark",
                "sender-1",
                "registration-1",
                "message-1",
                "platform-message-1"),
            SenderBinding = new AgentToolSenderBindingContext(bindingId),
        };

        var outcome = await executor.ExecuteAsync(CreateRequest(tool) with { ExecutionContext = context });

        if (expectedExecution)
        {
            outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
            outcome.FailureCode.Should().BeEmpty();
            tool.ExecutionCalls.Should().Be(1);
            return;
        }

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Denied);
        outcome.FailureCode.Should().Be("credential_denied");
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.CredentialPolicy);
        outcome.TerminalInvoked.Should().BeFalse();
        tool.ExecutionCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSenderTokenExists_ShouldReplaceOwnerCredentialsInsideTerminal()
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(new AgentToolCallSafety(false, false, false));
        var executor = CreateExecutor(appender);
        var context = CreateTestExecutionContext() with
        {
            Request = new AgentToolRequestIdentity("request-1", "call-1"),
            Credentials = new AgentToolCredentials("owner-token", "org-token", " sender-token "),
            Channel = new AgentToolChannelContext(
                "lark",
                "sender-1",
                "registration-1",
                "message-1",
                "platform-message-1"),
            SenderBinding = new AgentToolSenderBindingContext("binding-1"),
        };

        var outcome = await executor.ExecuteAsync(CreateRequest(tool) with { ExecutionContext = context });

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        var executedContext = tool.ExecutionContexts.Should().ContainSingle().Subject;
        executedContext.Credentials.NyxIdAccessToken.Should().Be("sender-token");
        executedContext.Credentials.NyxIdOrgToken.Should().Be("sender-token");
        executedContext.Credentials.SenderNyxIdAccessToken.Should().Be("sender-token");
        executedContext.Credentials.NyxIdCredentialKind
            .Should().Be(AgentToolNyxIdCredentialKind.SourceReadableUserBearer);
        AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(executedContext.Credentials)
            .Should().Be("sender-token");
        appender.Records.Single(record => record.ToolExecution.ExecutionPhase == AuditToolExecutionPhase.Terminal)
            .CredentialSource.Should().Be(AuditCredentialSource.ChannelRegistration);
    }

    [Fact]
    public async Task ExecuteAsync_WhenBoundSenderHasProxyDelegation_ShouldPreserveCredentialPurposes()
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(new AgentToolCallSafety(false, false, false));
        var executor = CreateExecutor(appender);
        var context = CreateTestExecutionContext() with
        {
            Request = new AgentToolRequestIdentity("request-1", "call-1"),
            Credentials = new AgentToolCredentials(
                " delegated-token ",
                " owner-org-token ",
                " sender-token ",
                AgentToolNyxIdCredentialKind.ProxyDelegation,
                " source-token "),
            Channel = new AgentToolChannelContext(
                "lark",
                "sender-1",
                "registration-1",
                "message-1",
                "platform-message-1"),
            SenderBinding = new AgentToolSenderBindingContext("binding-1"),
        };

        var outcome = await executor.ExecuteAsync(CreateRequest(tool) with { ExecutionContext = context });

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        var executedContext = tool.ExecutionContexts.Should().ContainSingle().Subject;
        executedContext.Credentials.NyxIdAccessToken.Should().Be("delegated-token");
        executedContext.Credentials.NyxIdOrgToken.Should().BeNull();
        executedContext.Credentials.SenderNyxIdAccessToken.Should().BeNull();
        executedContext.Credentials.SourceReadableNyxIdAccessToken.Should().Be("source-token");
        executedContext.Credentials.NyxIdCredentialKind.Should().Be(AgentToolNyxIdCredentialKind.ProxyDelegation);
        appender.Records.Single(record => record.ToolExecution.ExecutionPhase == AuditToolExecutionPhase.Terminal)
            .CredentialSource.Should().Be(AuditCredentialSource.ChannelRegistration);
    }

    [Fact]
    public async Task ExecuteAsync_WhenProxyDelegationHasNoPrimaryToken_ShouldDenyWithoutFallback()
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(new AgentToolCallSafety(false, true, false));
        var executor = CreateExecutor(appender);
        var context = CreateTestExecutionContext() with
        {
            Request = new AgentToolRequestIdentity("request-1", "call-1"),
            Credentials = new AgentToolCredentials(
                " ",
                "owner-org-token",
                "sender-token",
                AgentToolNyxIdCredentialKind.ProxyDelegation,
                "source-token"),
            Channel = new AgentToolChannelContext(
                "lark",
                "sender-1",
                "registration-1",
                "message-1",
                "platform-message-1"),
            SenderBinding = new AgentToolSenderBindingContext("binding-1"),
        };

        var outcome = await executor.ExecuteAsync(CreateRequest(tool) with { ExecutionContext = context });

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Denied);
        outcome.FailureCode.Should().Be("credential_denied");
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.CredentialPolicy);
        outcome.TerminalInvoked.Should().BeFalse();
        tool.ExecutionCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("direct", AuditCredentialSource.BearerToken)]
    [InlineData("system", AuditCredentialSource.System)]
    [InlineData("scheduled", AuditCredentialSource.ScheduledRun)]
    [InlineData("explicit", AuditCredentialSource.ServiceAccount)]
    public async Task ExecuteAsync_CredentialSourceRoutes_ShouldAuditTypedSource(
        string route,
        AuditCredentialSource expectedSource)
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(new AgentToolCallSafety(false, true, false));
        var executor = CreateExecutor(appender);
        var context = CreateTestExecutionContext() with
        {
            Request = new AgentToolRequestIdentity("request-1", "call-1"),
        };
        context = route switch
        {
            "direct" => context with
            {
                Credentials = new AgentToolCredentials("owner-token", null, null),
            },
            "scheduled" => context with
            {
                Schedule = new AgentToolScheduleContext("schedule-1"),
            },
            "explicit" => context with
            {
                CredentialSource = AgentToolCredentialSource.ServiceAccount,
            },
            _ => context,
        };

        var outcome = await executor.ExecuteAsync(CreateRequest(tool) with { ExecutionContext = context });

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        appender.Records.Single(record => record.ToolExecution.ExecutionPhase == AuditToolExecutionPhase.Terminal)
            .CredentialSource.Should().Be(expectedSource);
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
            events.Add(record.ToolExecution.ExecutionPhase switch
            {
                AuditToolExecutionPhase.Running => "running",
                AuditToolExecutionPhase.Terminal => "terminal",
                _ => record.ToolExecution.ExecutionPhase.ToString(),
            });
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
    public async Task ExecuteAsync_ShouldExposeTypedExecutionContextDuringClassification()
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(
            new AgentToolCallSafety(null, false, false),
            classify: _ => string.Equals(
                    AgentToolRequestContext.Current?.Caller.OwnerSubject,
                    "classification-owner",
                    StringComparison.Ordinal)
                ? new AgentToolCallSafety(true, false, true)
                : new AgentToolCallSafety(null, false, false))
        {
            ApprovalMode = ToolApprovalMode.Auto,
        };
        var executor = CreateExecutor(appender);
        var request = CreateRequest(tool) with
        {
            ApprovalContinuationMode = AgentToolApprovalContinuationMode.ActorOwned,
            ExecutionContext = CreateTestExecutionContext() with
            {
                Request = new AgentToolRequestIdentity("request-1", "call-1"),
                Caller = new AgentToolCallerContext("scope-1", "classification-owner", null),
                ExecutionOwner = AgentToolExecutionOwners.Actor("actor-test"),
            },
        };

        var outcome = await executor.ExecuteAsync(request);

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.ApprovalRequired);
        outcome.TerminalInvoked.Should().BeFalse();
        tool.SafetyCalls.Should().Be(1);
        tool.ExecutionCalls.Should().Be(0);
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenRunningAuditIsDuplicate_ShouldExecuteAfterAdmissionStarts()
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            record.ToolExecution.ExecutionPhase == AuditToolExecutionPhase.Running
                ? AuditTrailAppendResult.Duplicate(record.AuditId)
                : AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(new AgentToolCallSafety(false, true, false));
        var executor = CreateExecutor(appender);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.FailureCode.Should().BeEmpty();
        outcome.TerminalInvoked.Should().BeTrue();
        outcome.Retryable.Should().BeFalse();
        outcome.AuditCompleted.Should().BeTrue();
        tool.ExecutionCalls.Should().Be(1);
    }

    [Theory]
    [InlineData(AuditTrailAppendStatus.Conflict, "audit_intent_conflict")]
    [InlineData(AuditTrailAppendStatus.StoreUnavailable, "audit_unavailable")]
    public async Task ExecuteAsync_WhenRunningAuditDoesNotAppend_ShouldPreserveExecution(
        AuditTrailAppendStatus appendStatus,
        string failureCode)
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            record.ToolExecution.ExecutionPhase == AuditToolExecutionPhase.Running
                ? CreateAppendResult(appendStatus, record.AuditId)
                : AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(new AgentToolCallSafety(false, true, false));
        var executor = CreateExecutor(appender);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.ExecutedAuditIncomplete);
        outcome.FailureCode.Should().Be(failureCode);
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.TerminalAudit);
        outcome.TerminalInvoked.Should().BeTrue();
        outcome.Retryable.Should().BeFalse();
        outcome.AuditCompleted.Should().BeFalse();
        tool.ExecutionCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRunningAuditIsUnavailable_ShouldNotMakeAuditTheAdmissionAuthority()
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            record.ToolExecution.ExecutionPhase == AuditToolExecutionPhase.Running
                ? AuditTrailAppendResult.StoreUnavailable(record.AuditId, "offline")
                : AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(new AgentToolCallSafety(false, true, false));
        var executor = CreateExecutor(appender);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.ExecutedAuditIncomplete);
        outcome.TerminalInvoked.Should().BeTrue();
        outcome.Retryable.Should().BeFalse();
        outcome.AuditCompleted.Should().BeFalse();
        tool.ExecutionCalls.Should().Be(1);
    }

    [Theory]
    [InlineData((AgentToolAdmissionStatus)0, "tool_admission_invalid_status", false)]
    [InlineData(AgentToolAdmissionStatus.Duplicate, "tool_execution_already_started", false)]
    [InlineData(AgentToolAdmissionStatus.Conflict, "tool_admission_conflict", false)]
    [InlineData(AgentToolAdmissionStatus.StoreUnavailable, "tool_admission_unavailable", true)]
    [InlineData(AgentToolAdmissionStatus.InvalidFact, "tool_admission_invalid_fact", false)]
    [InlineData(AgentToolAdmissionStatus.Expired, "tool_admission_expired", false)]
    [InlineData((AgentToolAdmissionStatus)999, "tool_admission_invalid_status", false)]
    public async Task ExecuteAsync_WhenAdmissionDoesNotStart_ShouldFailBeforeAuditAndTerminal(
        AgentToolAdmissionStatus admissionStatus,
        string failureCode,
        bool retryable)
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var ledger = new RecordingAdmissionLedger(admissionStatus);
        var tool = new RecordingTool(new AgentToolCallSafety(false, true, false));
        var executor = CreateExecutor(appender, ledger);
        var request = CreateRequest(tool);

        var outcome = await executor.ExecuteAsync(request);

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Failed);
        outcome.FailureCode.Should().Be(failureCode);
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.Admission);
        outcome.TerminalInvoked.Should().BeFalse();
        outcome.Retryable.Should().Be(retryable);
        tool.ExecutionCalls.Should().Be(0);
        appender.Records.Should().BeEmpty();
        ledger.Facts.Should().ContainSingle().Which.Should().BeEquivalentTo(new AgentToolAdmissionFact
        {
            AdmissionId = ledger.Facts[0].AdmissionId,
            RequestId = "request-1",
            ToolCallId = "call-1",
            ToolName = "test_tool",
            ArgumentsSha256 = AgentToolArgumentsDigest.ComputeSha256("{}"),
            ExecutionOwner = AgentToolExecutionOwners.Actor("actor-test"),
            IssuedAtUnixMs = request.ExecutionContext.Request.IssuedAtUnixMs,
            OperationId = ledger.Facts[0].OperationId,
            ReplayPolicy = AgentToolReplayPolicy.ReadOnlyRetryable,
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenAdmissionLedgerThrows_ShouldFailClosedBeforeAuditAndTerminal()
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(new AgentToolCallSafety(false, true, false));
        var executor = CreateExecutor(
            appender,
            new ThrowingAdmissionLedger(new InvalidOperationException("ledger-secret")));

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Failed);
        outcome.FailureCode.Should().Be("tool_admission_unavailable");
        outcome.SafeMessage.Should().Be(nameof(InvalidOperationException));
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.Admission);
        outcome.TerminalInvoked.Should().BeFalse();
        outcome.Retryable.Should().BeTrue();
        outcome.AuditCompleted.Should().BeFalse();
        outcome.ResultJson.Should().NotContain("ledger-secret");
        tool.ExecutionCalls.Should().Be(0);
        appender.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenLogicalCallIsRedelivered_ShouldStartAndInvokeTerminalOnce()
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var ledger = new DeduplicatingAdmissionLedger();
        var tool = new RecordingTool(new AgentToolCallSafety(false, true, false));
        var executor = CreateExecutor(appender, ledger);
        var request = CreateRequest(tool);

        var first = await executor.ExecuteAsync(request);
        var replay = await executor.ExecuteAsync(request);

        first.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        replay.Kind.Should().Be(AgentToolExecutionOutcomeKind.Failed);
        replay.FailureCode.Should().Be("tool_execution_already_started");
        replay.TerminalInvoked.Should().BeFalse();
        ledger.Decisions.Should().Equal(
            AgentToolAdmissionStatus.Started,
            AgentToolAdmissionStatus.Duplicate);
        ledger.Facts.Select(fact => fact.AdmissionId).Should().OnlyContain(id => id == ledger.Facts[0].AdmissionId);
        tool.ExecutionCalls.Should().Be(1);
        appender.Records.Select(record => record.ToolExecution.ExecutionPhase)
            .Should().Equal(AuditToolExecutionPhase.Running, AuditToolExecutionPhase.Terminal);
    }

    [Fact]
    public async Task ExecuteAsync_DifferentActorsWithSameCallIdentity_ShouldNotShareAdmissionOrAuditIds()
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var ledger = new DeduplicatingAdmissionLedger();
        var executor = CreateExecutor(appender, ledger);
        var firstTool = new RecordingTool(new AgentToolCallSafety(false, true, false));
        var secondTool = new RecordingTool(new AgentToolCallSafety(false, true, false));

        var first = await executor.ExecuteAsync(CreateRequest(firstTool) with
        {
            ExecutionContext = CreateTestExecutionContext() with
            {
                Request = new AgentToolRequestIdentity("shared-request", "shared-call"),
                Caller = new AgentToolCallerContext("scope-alpha", "actor-alpha", null),
                ExecutionOwner = AgentToolExecutionOwners.Actor("actor-alpha"),
            },
        });
        var second = await executor.ExecuteAsync(CreateRequest(secondTool) with
        {
            ExecutionContext = CreateTestExecutionContext() with
            {
                Request = new AgentToolRequestIdentity("shared-request", "shared-call"),
                Caller = new AgentToolCallerContext("scope-beta", "actor-beta", null),
                ExecutionOwner = AgentToolExecutionOwners.Actor("actor-beta"),
            },
        });

        first.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        second.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        firstTool.ExecutionCalls.Should().Be(1);
        secondTool.ExecutionCalls.Should().Be(1);
        ledger.Facts.Select(fact => fact.AdmissionId).Should().OnlyHaveUniqueItems();
        appender.Records.Select(record => record.AuditId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ExecuteAsync_WhenAdmissionLedgerObservesCallerCancellation_ShouldPropagate()
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(new AgentToolCallSafety(false, true, false));
        var executor = CreateExecutor(
            appender,
            new ThrowingAdmissionLedger(new InvalidOperationException("must not be thrown")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var action = () => executor.ExecuteAsync(CreateRequest(tool), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        tool.ExecutionCalls.Should().Be(0);
        appender.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenRunningAuditObservesCallerCancellation_ShouldPropagateBeforeTerminal()
    {
        using var cancellation = new CancellationTokenSource();
        var appender = new RecordingAuditTrailAppender((record, _) =>
        {
            record.ToolExecution.ExecutionPhase.Should().Be(AuditToolExecutionPhase.Running);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var tool = new RecordingTool(new AgentToolCallSafety(false, true, false));
        var executor = CreateExecutor(appender);

        var action = () => executor.ExecuteAsync(CreateRequest(tool), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        tool.ExecutionCalls.Should().Be(0);
        appender.Records.Select(record => record.ToolExecution.ExecutionPhase)
            .Should().Equal(AuditToolExecutionPhase.Running);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRawTerminalObservesCallerCancellation_ShouldPropagateWithoutFailureOutput()
    {
        using var cancellation = new CancellationTokenSource();
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(
            new AgentToolCallSafety(false, true, false),
            _ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });
        var executor = CreateExecutor(appender);

        var action = () => executor.ExecuteAsync(CreateRequest(tool), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        tool.ExecutionCalls.Should().Be(1);
        appender.Records.Select(record => record.ToolExecution.ExecutionPhase)
            .Should().Equal(AuditToolExecutionPhase.Running);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTerminalAuditObservesCallerCancellation_ShouldPropagateAfterSingleTerminalCall()
    {
        using var cancellation = new CancellationTokenSource();
        var appender = new RecordingAuditTrailAppender((record, _) =>
        {
            if (record.ToolExecution.ExecutionPhase == AuditToolExecutionPhase.Terminal)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }

            return AuditTrailAppendResult.Appended(record.AuditId);
        });
        var tool = new RecordingTool(new AgentToolCallSafety(false, true, false));
        var executor = CreateExecutor(appender);

        var action = () => executor.ExecuteAsync(CreateRequest(tool), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        tool.ExecutionCalls.Should().Be(1);
        appender.Records.Select(record => record.ToolExecution.ExecutionPhase)
            .Should().Equal(AuditToolExecutionPhase.Running, AuditToolExecutionPhase.Terminal);
    }

    [Theory]
    [InlineData(AuditToolExecutionPhase.Running)]
    [InlineData(AuditToolExecutionPhase.Terminal)]
    public async Task ExecuteAsync_WhenAuditAppenderThrows_ShouldPreserveExecutionWithoutRetry(
        AuditToolExecutionPhase failingPhase)
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            record.ToolExecution.ExecutionPhase == failingPhase
                ? throw new InvalidOperationException("audit-secret")
                : AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(
            new AgentToolCallSafety(false, true, false),
            _ => "{\"changed\":true}");
        var executor = CreateExecutor(appender);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.ExecutedAuditIncomplete);
        outcome.ResultJson.Should().Be("{\"changed\":true}");
        outcome.FailureCode.Should().Be("audit_unavailable");
        outcome.SafeMessage.Should().NotContain("audit-secret");
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.TerminalAudit);
        outcome.TerminalInvoked.Should().BeTrue();
        outcome.Retryable.Should().BeFalse();
        outcome.AuditCompleted.Should().BeFalse();
        tool.ExecutionCalls.Should().Be(1);
        appender.Records.Select(record => record.ToolExecution.ExecutionPhase)
            .Should().Equal(AuditToolExecutionPhase.Running, AuditToolExecutionPhase.Terminal);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTerminalAuditIsUnavailable_ShouldPreserveActualResultWithoutRetry()
    {
        var appendCount = 0;
        var appender = new RecordingAuditTrailAppender((record, _) =>
        {
            appendCount++;
            return record.ToolExecution.ExecutionPhase == AuditToolExecutionPhase.Terminal
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
            record.ToolExecution.ExecutionPhase == AuditToolExecutionPhase.Terminal
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
        appender.Records.Select(record => record.ToolExecution.ExecutionPhase)
            .Should().Equal(AuditToolExecutionPhase.Running, AuditToolExecutionPhase.Terminal);
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
        appender.Records.Select(record => record.ToolExecution.ExecutionPhase)
            .Should().Equal(AuditToolExecutionPhase.Running, AuditToolExecutionPhase.Terminal);
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
    public async Task ExecuteAsync_WhenProviderReturnsTerminalOutcome_ShouldPreserveTypedReceipt()
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new TerminalOutcomeTool();
        var executor = CreateExecutor(appender);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.ResultJson.Should().Be("""{"error":true,"status":503,"body":"domain payload"}""");
        outcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        outcome.Receipt.CallId.Should().Be("call-1");
        outcome.Receipt.ToolName.Should().Be(tool.Name);
        outcome.Receipt.ApprovalMode.Should().Be(AgentToolReceiptApprovalMode.Auto);
        outcome.Receipt.IsDestructive.Should().BeTrue();
        outcome.Receipt.SideEffectKind.Should().Be("example.publish");
        outcome.Receipt.SubjectId.Should().Be("usvc-outcome");
        outcome.TerminalInvoked.Should().BeTrue();
        outcome.AuditCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldOverrideProviderReceiptExecutionIdentity()
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(
            new AgentToolCallSafety(false, true, false),
            createReceipt: resultJson => new AgentToolReceipt
            {
                CallId = "provider-call",
                ToolName = "provider-tool",
                ApprovalMode = AgentToolReceiptApprovalMode.AlwaysRequire,
                Status = AgentToolReceiptStatus.Success,
                ResultJson = resultJson,
            });
        var executor = CreateExecutor(appender);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.Receipt.CallId.Should().Be("call-1");
        outcome.Receipt.ToolName.Should().Be(tool.Name);
        outcome.Receipt.ApprovalMode.Should().Be(AgentToolReceiptApprovalMode.NeverRequire);
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
        appender.Records[0].ToolExecution.ExecutionPhase
            .Should().Be(AuditToolExecutionPhase.WaitingApproval);
    }

    [Fact]
    public async Task ExecuteAsync_WhenExactWebhookPermitMatches_ShouldExecuteWithoutHumanApproval()
    {
        var appender = SuccessfulAuditAppender();
        var tool = new RecordingTool(
            new AgentToolCallSafety(true, false, false),
            name: "nyxid_proxy")
        {
            ApprovalMode = ToolApprovalMode.Auto,
        };
        var admission = ExactUnattendedAdmission();
        var owner = AgentToolExecutionOwners.WorkflowRun("run-1");
        var context = CreateTestExecutionContext() with
        {
            ExecutionOwner = owner,
            Request = new AgentToolRequestIdentity("run-1", "call-1"),
            InvocationSurface = AgentToolInvocationSurface.WorkflowToolCall,
            OperationAdmission = admission,
            Credentials = new AgentToolCredentials(
                "jit-token",
                null,
                null,
                AgentToolNyxIdCredentialKind.ProxyDelegation),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "nyxid",
                "tenant-alpha",
                "owner-alpha",
                "proxy"),
        };
        var executor = CreateExecutor(appender);
        var request = new AgentToolExecutionRequest(
            tool,
            "{}",
            context,
            AgentToolApprovalContinuationMode.ActorOwned,
            ApprovalGrant: null,
            UnattendedAuthorization: new AgentToolUnattendedExecutionAuthorization(
                AgentToolUnattendedAuthorizationKind.WorkflowWebhookExact,
                "sha256:authorization",
                owner.Clone(),
                "run-1",
                "nyxid_proxy",
                "call-1",
                AgentToolArgumentsDigest.ComputeSha256("{}"),
                "workflow-1/create-approval",
                AgentToolOperationSelector.ComputeDigest(admission)));

        var outcome = await executor.ExecuteAsync(request);

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.TerminalInvoked.Should().BeTrue();
        tool.ExecutionCalls.Should().Be(1);
        appender.Records.Should().NotContain(record =>
            record.ToolExecution.ExecutionPhase == AuditToolExecutionPhase.WaitingApproval);
        var sanitizedExecutionRecords = appender.Records.Where(record =>
                record.ToolExecution.ExecutionPhase is
                    AuditToolExecutionPhase.Running or AuditToolExecutionPhase.Terminal)
            .Select(record => new AuditRecordSanitizer().Sanitize(record))
            .ToList();
        sanitizedExecutionRecords.Should().OnlyContain(record =>
            record.Annotations["unattended_mode"] == "unattended_exact" &&
            record.Annotations["unattended_permit_sha256"] == "sha256:authorization");
    }

    [Fact]
    public async Task ExecuteAsync_WhenWebhookPermitDoesNotMatchArguments_ShouldDenyWithoutHumanFallback()
    {
        var appender = SuccessfulAuditAppender();
        var tool = new RecordingTool(
            new AgentToolCallSafety(true, false, false),
            name: "nyxid_proxy")
        {
            ApprovalMode = ToolApprovalMode.Auto,
        };
        var admission = ExactUnattendedAdmission();
        var owner = AgentToolExecutionOwners.WorkflowRun("run-1");
        var context = CreateTestExecutionContext() with
        {
            ExecutionOwner = owner,
            Request = new AgentToolRequestIdentity("run-1", "call-1"),
            InvocationSurface = AgentToolInvocationSurface.WorkflowToolCall,
            OperationAdmission = admission,
            Credentials = new AgentToolCredentials(
                "jit-token",
                null,
                null,
                AgentToolNyxIdCredentialKind.ProxyDelegation),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "nyxid",
                "tenant-alpha",
                "owner-alpha",
                "proxy"),
        };
        var executor = CreateExecutor(appender);
        var request = new AgentToolExecutionRequest(
            tool,
            "{}",
            context,
            AgentToolApprovalContinuationMode.ActorOwned,
            ApprovalGrant: null,
            UnattendedAuthorization: new AgentToolUnattendedExecutionAuthorization(
                AgentToolUnattendedAuthorizationKind.WorkflowWebhookExact,
                "sha256:authorization",
                owner.Clone(),
                "run-1",
                "nyxid_proxy",
                "call-1",
                AgentToolArgumentsDigest.ComputeSha256("{\"changed\":true}"),
                "workflow-1/create-approval",
                AgentToolOperationSelector.ComputeDigest(admission)));

        var outcome = await executor.ExecuteAsync(request);

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Denied);
        outcome.FailureCode.Should().Be("unattended_authorization_mismatch");
        outcome.TerminalInvoked.Should().BeFalse();
        tool.ExecutionCalls.Should().Be(0);
        appender.Records.Should().NotContain(record =>
            record.ToolExecution.ExecutionPhase == AuditToolExecutionPhase.WaitingApproval);
    }

    [Theory]
    [InlineData(AuditTrailAppendStatus.Conflict)]
    [InlineData(AuditTrailAppendStatus.StoreUnavailable)]
    public async Task ExecuteAsync_WhenWaitingApprovalAuditDoesNotAppend_ShouldPreserveApprovalContinuation(
        AuditTrailAppendStatus appendStatus)
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            record.ToolExecution.ExecutionPhase == AuditToolExecutionPhase.WaitingApproval
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

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.ApprovalRequired);
        outcome.FailureCode.Should().BeEmpty();
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.Approval);
        outcome.TerminalInvoked.Should().BeFalse();
        outcome.Retryable.Should().BeFalse();
        outcome.AuditCompleted.Should().BeFalse();
        tool.ExecutionCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("owner")]
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
            AgentToolExecutionOwners.Actor("actor-test"),
            initial.Receipt.ApprovalRequestId,
            "request-1",
            tool.Name,
            "call-1",
            digest);
        grant = mismatchedField switch
        {
            "owner" => grant with { ExecutionOwner = AgentToolExecutionOwners.Actor("actor-other") },
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

    [Theory]
    [InlineData(AgentToolExecutionAttemptKind.Unspecified)]
    [InlineData((AgentToolExecutionAttemptKind)999)]
    public async Task ExecuteAsync_WhenAttemptKindIsInvalid_ShouldFailBeforeAdmission(
        AgentToolExecutionAttemptKind attemptKind)
    {
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Started);
        var tool = new RecordingTool(new AgentToolCallSafety(false, true, false));
        var executor = CreateExecutor(appender, ledger);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool) with
        {
            ExecutionAttemptKind = attemptKind,
        });

        outcome.FailureCode.Should().Be("invalid_tool_execution_attempt");
        outcome.TerminalInvoked.Should().BeFalse();
        ledger.Facts.Should().BeEmpty();
        tool.ExecutionCalls.Should().Be(0);
        appender.Records.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AgentToolReplayPolicy.Unspecified, "invalid_tool_replay_policy")]
    [InlineData((AgentToolReplayPolicy)999, "invalid_tool_replay_policy")]
    [InlineData(AgentToolReplayPolicy.ReadOnlyRetryable, "invalid_read_only_replay_policy")]
    [InlineData(AgentToolReplayPolicy.Reconcilable, "missing_tool_operation_reconciler")]
    public async Task ExecuteAsync_WhenToolOwnedReplayPolicyIsInvalid_ShouldFailBeforeAdmission(
        AgentToolReplayPolicy replayPolicy,
        string failureCode)
    {
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Started);
        var tool = new RecordingTool(
            new AgentToolCallSafety(false, false, true),
            replayPolicy: replayPolicy);
        var executor = CreateExecutor(appender, ledger);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.FailureCode.Should().Be(failureCode);
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.Classification);
        outcome.TerminalInvoked.Should().BeFalse();
        ledger.Facts.Should().BeEmpty();
        tool.ExecutionCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenIdempotentPolicyDoesNotUseOperationIdAsKey_ShouldFailBeforeAdmission()
    {
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Started);
        var tool = new RecordingTool(
            new AgentToolCallSafety(false, false, false),
            replayPolicy: AgentToolReplayPolicy.IdempotentRetryable);
        var executor = CreateExecutor(appender, ledger);

        var outcome = await executor.ExecuteAsync(CreateRecoveryRequest(
            tool,
            operationId: "operation-1",
            idempotencyKey: "different-key"));

        outcome.FailureCode.Should().Be("invalid_idempotent_replay_key");
        outcome.TerminalInvoked.Should().BeFalse();
        ledger.Facts.Should().BeEmpty();
        tool.ExecutionCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenInitialReadOnlyCallIsDuplicated_ShouldNotTreatItAsRecovery()
    {
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Duplicate);
        var tool = new RecordingTool(new AgentToolCallSafety(false, true, false));
        var executor = CreateExecutor(appender, ledger);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.FailureCode.Should().Be("tool_execution_already_started");
        outcome.TerminalInvoked.Should().BeFalse();
        tool.ExecutionCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenActorRecoversExactReadOnlyAdmission_ShouldRetryWithStableOperationId()
    {
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Duplicate);
        var tool = new RecordingTool(new AgentToolCallSafety(false, true, false));
        var executor = CreateExecutor(appender, ledger);

        var outcome = await executor.ExecuteAsync(CreateRecoveryRequest(tool, "operation-read"));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.TerminalInvoked.Should().BeTrue();
        tool.ExecutionCalls.Should().Be(1);
        tool.ExecutionContexts.Should().ContainSingle()
            .Which.Request.OperationId.Should().Be("operation-read");
        var fact = ledger.Facts.Should().ContainSingle().Subject;
        fact.OperationId.Should().Be("operation-read");
        fact.ReplayPolicy.Should().Be(AgentToolReplayPolicy.ReadOnlyRetryable);
    }

    [Fact]
    public async Task ExecuteAsync_WhenActorRecoversExactIdempotentAdmission_ShouldPassOperationIdAsIdempotencyKey()
    {
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Duplicate);
        var tool = new RecordingTool(
            new AgentToolCallSafety(false, false, false),
            replayPolicy: AgentToolReplayPolicy.IdempotentRetryable);
        var executor = CreateExecutor(appender, ledger);

        var outcome = await executor.ExecuteAsync(CreateRecoveryRequest(
            tool,
            operationId: "operation-idempotent",
            idempotencyKey: "operation-idempotent"));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        tool.ExecutionCalls.Should().Be(1);
        var executionIdentity = tool.ExecutionContexts.Should().ContainSingle().Subject.Request;
        executionIdentity.OperationId.Should().Be("operation-idempotent");
        executionIdentity.IdempotencyKey.Should().Be("operation-idempotent");
    }

    [Fact]
    public async Task ExecuteAsync_WhenActorRecoversNewNonReplayableAdmission_ShouldExecuteOnce()
    {
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Started);
        var tool = new RecordingTool(new AgentToolCallSafety(false, false, true));
        var executor = CreateExecutor(appender, ledger);

        var outcome = await executor.ExecuteAsync(CreateRecoveryRequest(tool, "operation-new"));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.TerminalInvoked.Should().BeTrue();
        tool.ExecutionCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenActorRecoversDuplicateNonReplayableAdmission_ShouldReturnUncertainWithoutCallingTool()
    {
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Duplicate);
        var tool = new RecordingTool(new AgentToolCallSafety(false, false, true));
        var executor = CreateExecutor(appender, ledger);

        var outcome = await executor.ExecuteAsync(CreateRecoveryRequest(tool, "operation-uncertain"));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Failed);
        outcome.FailureCode.Should().Be("outcome_uncertain");
        outcome.Receipt.FailureOutcome.Should().Be(AgentToolFailureOutcome.OutcomeUncertain);
        outcome.SafeMessage.Should().Contain("OUTCOME_UNCERTAIN");
        outcome.TerminalInvoked.Should().BeFalse();
        tool.ExecutionCalls.Should().Be(0);
        appender.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenReconciliationFindsCompletion_ShouldReuseResultWithoutCallingTool()
    {
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Duplicate);
        var tool = new ReconcilableRecordingTool(
            new AgentToolOperationReconciliationResult(
                AgentToolOperationReconciliationDisposition.Completed,
                new AgentToolTerminalOutcome("{\"reused\":true}")));
        var executor = CreateExecutor(appender, ledger);

        var outcome = await executor.ExecuteAsync(CreateRecoveryRequest(tool, "operation-reconciled"));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.ResultJson.Should().Be("{\"reused\":true}");
        outcome.TerminalInvoked.Should().BeFalse();
        tool.ReconciliationCalls.Should().Be(1);
        tool.ExecutionCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenReconciliationProvesOperationAbsent_ShouldExecuteOnce()
    {
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Duplicate);
        var tool = new ReconcilableRecordingTool(
            new AgentToolOperationReconciliationResult(
                AgentToolOperationReconciliationDisposition.NotFound));
        var executor = CreateExecutor(appender, ledger);

        var outcome = await executor.ExecuteAsync(CreateRecoveryRequest(tool, "operation-absent"));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.TerminalInvoked.Should().BeTrue();
        tool.ReconciliationCalls.Should().Be(1);
        tool.ExecutionCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDurableStartIsPending_ShouldReturnPendingWithoutTerminalAudit()
    {
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Started);
        var pending = PendingOperation("operation-pending", "provider-pending");
        var tool = new ReconcilableRecordingTool(
            AgentToolOperationReconciliationResult.Unknown(),
            startResult: AgentToolOperationStartResult.Pending(pending));
        var executor = CreateExecutor(appender, ledger);

        var outcome = await executor.ExecuteAsync(CreateRecoveryRequest(tool, pending.OperationId) with
        {
            ExecutionAttemptKind = AgentToolExecutionAttemptKind.Initial,
        });

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Pending);
        outcome.PendingOperation.Should().Be(pending);
        outcome.CancellationRecoveryIntent.Should().NotBeNull();
        outcome.CancellationRecoveryIntent!.FailureCode.Should()
            .Be("code_execution_cancel_outcome_uncertain");
        outcome.CancellationRecoveryIntent.ArgumentsSha256.Should()
            .Be(AgentToolArgumentsDigest.ComputeSha256("{}"));
        outcome.TerminalInvoked.Should().BeTrue();
        outcome.AuditCompleted.Should().BeTrue();
        tool.ExecutionCalls.Should().Be(1);
        appender.Records.Should().ContainSingle()
            .Which.ToolExecution!.ExecutionPhase.Should().Be(AuditToolExecutionPhase.Running);
    }

    [Fact]
    public async Task ExecuteAsync_WhenReconciliationRemainsPending_ShouldNotRestartOperation()
    {
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Duplicate);
        var pending = PendingOperation("operation-pending", "provider-pending");
        var refreshed = pending with
        {
            Status = AgentToolPendingOperationStatus.Running,
            ETag = "\"version-2\"",
        };
        var tool = new ReconcilableRecordingTool(
            AgentToolOperationReconciliationResult.Pending(refreshed));
        var executor = CreateExecutor(appender, ledger);

        var outcome = await executor.ExecuteAsync(CreateRecoveryRequest(tool, pending.OperationId) with
        {
            PendingOperation = pending,
        });

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Pending);
        outcome.PendingOperation.Should().Be(refreshed);
        outcome.CancellationRecoveryIntent.Should().NotBeNull();
        outcome.TerminalInvoked.Should().BeFalse();
        tool.ReconciliationCalls.Should().Be(1);
        tool.ExecutionCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenKnownProviderOperationIsNotFound_ShouldNotResubmit()
    {
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Duplicate);
        var pending = PendingOperation("operation-known", "provider-known");
        var tool = new ReconcilableRecordingTool(
            AgentToolOperationReconciliationResult.NotFound());
        var executor = CreateExecutor(appender, ledger);

        var outcome = await executor.ExecuteAsync(CreateRecoveryRequest(tool, pending.OperationId) with
        {
            PendingOperation = pending,
        });

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Failed);
        outcome.FailureCode.Should().Be("outcome_uncertain");
        outcome.Receipt.FailureOutcome.Should().Be(AgentToolFailureOutcome.OutcomeUncertain);
        outcome.TerminalInvoked.Should().BeFalse();
        tool.ReconciliationCalls.Should().Be(1);
        tool.ExecutionCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_WhenReconciliationIsUnknown_ShouldReturnUncertainWithoutCallingTool(
        bool throwDuringReconciliation)
    {
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Duplicate);
        var tool = new ReconcilableRecordingTool(
            new AgentToolOperationReconciliationResult(
                AgentToolOperationReconciliationDisposition.Unknown),
            throwDuringReconciliation);
        var executor = CreateExecutor(appender, ledger);

        var outcome = await executor.ExecuteAsync(CreateRecoveryRequest(tool, "operation-unknown"));

        outcome.FailureCode.Should().Be("outcome_uncertain");
        outcome.Receipt.FailureOutcome.Should().Be(AgentToolFailureOutcome.OutcomeUncertain);
        outcome.TerminalInvoked.Should().BeFalse();
        tool.ReconciliationCalls.Should().Be(1);
        tool.ExecutionCalls.Should().Be(0);
    }

    [Fact]
    public async Task CancelAsync_WhenExactAdmissionIsDuplicateAndProviderIsPending_ShouldRemainPending()
    {
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Duplicate);
        var pending = PendingOperation("operation-cancel-pending", "provider-cancel-pending");
        var tool = new ReconcilableRecordingTool(
            AgentToolOperationReconciliationResult.Unknown(),
            cancellationResult: AgentToolOperationCancellationResult.Pending(pending));
        var executor = CreateExecutor(appender, ledger);

        var result = await executor.CancelAsync(CreateCancellationRequest(tool, pending));

        result.Disposition.Should().Be(AgentToolCancellationDisposition.Pending);
        result.PendingOperation.Should().Be(pending);
        tool.CancellationCalls.Should().Be(1);
        ledger.Facts.Should().ContainSingle().Which.OperationId.Should().Be(pending.OperationId);
        appender.Records.Should().ContainSingle()
            .Which.ToolExecution.ExecutionPhase.Should().Be(AuditToolExecutionPhase.Running);
    }

    [Fact]
    public async Task CancelAsync_WhenAdmissionIsNotDuplicate_ShouldNotInvokeProviderCancellation()
    {
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Started);
        var pending = PendingOperation("operation-cancel-new", "provider-cancel-new");
        var tool = new ReconcilableRecordingTool(AgentToolOperationReconciliationResult.Unknown());
        var executor = CreateExecutor(appender, ledger);

        var result = await executor.CancelAsync(CreateCancellationRequest(tool, pending));

        result.Disposition.Should().Be(AgentToolCancellationDisposition.Failed);
        result.FailureCode.Should().Be("tool_cancellation_admission_not_duplicate");
        result.Retryable.Should().BeTrue();
        tool.CancellationCalls.Should().Be(0);
        appender.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelAsync_WhenTerminalAuditIsUnavailable_ShouldRetryFrozenIntentWithoutProvider()
    {
        var appender = new RecordingAuditTrailAppender((record, index) =>
            index == 2
                ? AuditTrailAppendResult.StoreUnavailable(record.AuditId, "offline")
                : AuditTrailAppendResult.Appended(record.AuditId));
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Duplicate);
        var pending = PendingOperation("operation-cancel-audit", "provider-cancel-audit");
        var terminal = new AgentToolTerminalOutcome(
            """{"success":false,"code":"code_execution_cancelled"}""",
            new AgentToolReceipt
            {
                Status = AgentToolReceiptStatus.Error,
                ResultJson = """{"success":false,"code":"code_execution_cancelled"}""",
                ErrorCode = "code_execution_cancelled",
                ErrorMessage = "cancelled",
                SubjectKind = "nyxid.user-service",
                SubjectId = "service-code",
                SubjectVersion = "version-7",
                SubjectHash = "sha256:terminal",
                ProviderResourceId = "provider-operation-7",
                Effect = AgentToolReceiptEffect.Mutating,
                MutationStage = AgentToolReceiptMutationStage.ReadModelObserved,
            });
        var tool = new ReconcilableRecordingTool(
            AgentToolOperationReconciliationResult.Unknown(),
            cancellationResult: AgentToolOperationCancellationResult.Completed(terminal));
        var executor = CreateExecutor(appender, ledger);
        var request = CreateCancellationRequest(tool, pending);

        var first = await executor.CancelAsync(request);

        first.Disposition.Should().Be(AgentToolCancellationDisposition.Pending);
        first.FailureCode.Should().Be("audit_unavailable");
        first.Retryable.Should().BeTrue();
        first.PendingTerminalIntent.Should().NotBeNull();
        first.PendingTerminalIntent!.Receipt.ErrorCode.Should().Be("code_execution_cancelled");
        var frozenReceipt = first.PendingTerminalIntent.Receipt.Clone();
        tool.ThrowDuringClassification = true;
        tool.ThrowDuringReceiptCreation = true;

        var retry = await executor.CancelAsync(request with
        {
            DeadlineUnixMs = 1,
            TerminalIntent = first.PendingTerminalIntent,
        });

        retry.Disposition.Should().Be(AgentToolCancellationDisposition.Completed);
        retry.CompletedOutcome!.ResultJson.Should().Be(terminal.ResultJson);
        retry.CompletedOutcome.Receipt.ErrorCode.Should().Be(terminal.Receipt!.ErrorCode);
        retry.CompletedOutcome.Receipt.Should().BeEquivalentTo(frozenReceipt);
        retry.CompletedOutcome.AuditCompleted.Should().BeTrue();
        ledger.Facts.Should().ContainSingle();
        tool.CancellationCalls.Should().Be(1);
        appender.Records.Select(record => record.ToolExecution.ExecutionPhase).Should().Equal(
            AuditToolExecutionPhase.Running,
            AuditToolExecutionPhase.Terminal,
            AuditToolExecutionPhase.Running,
            AuditToolExecutionPhase.Terminal);
    }

    [Fact]
    public async Task CancelAsync_WhenRunningAuditIsUnavailable_ShouldDeferTerminalAuditUntilRetry()
    {
        var appender = new RecordingAuditTrailAppender((record, index) =>
            index == 1
                ? AuditTrailAppendResult.StoreUnavailable(record.AuditId, "offline")
                : AuditTrailAppendResult.Appended(record.AuditId));
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Duplicate);
        var pending = PendingOperation("operation-cancel-running-audit", "provider-cancel-running-audit");
        var terminal = new AgentToolTerminalOutcome(
            """{"success":false,"code":"code_execution_cancelled"}""",
            new AgentToolReceipt
            {
                Status = AgentToolReceiptStatus.Error,
                ResultJson = """{"success":false,"code":"code_execution_cancelled"}""",
                ErrorCode = "code_execution_cancelled",
                ErrorMessage = "cancelled",
            });
        var tool = new ReconcilableRecordingTool(
            AgentToolOperationReconciliationResult.Unknown(),
            cancellationResult: AgentToolOperationCancellationResult.Completed(terminal));
        var executor = CreateExecutor(appender, ledger);
        var request = CreateCancellationRequest(tool, pending);

        var first = await executor.CancelAsync(request);

        first.Disposition.Should().Be(AgentToolCancellationDisposition.Pending);
        first.FailureCode.Should().Be("audit_unavailable");
        first.Retryable.Should().BeTrue();
        first.PendingTerminalIntent.Should().NotBeNull();
        appender.Records.Should().ContainSingle()
            .Which.ToolExecution.ExecutionPhase.Should().Be(AuditToolExecutionPhase.Running);

        var retry = await executor.CancelAsync(request with
        {
            TerminalIntent = first.PendingTerminalIntent,
        });

        retry.Disposition.Should().Be(AgentToolCancellationDisposition.Completed);
        retry.CompletedOutcome.Should().NotBeNull();
        retry.CompletedOutcome!.ResultJson.Should().Be(terminal.ResultJson);
        retry.CompletedOutcome.Receipt.ErrorCode.Should().Be(terminal.Receipt!.ErrorCode);
        retry.CompletedOutcome.AuditCompleted.Should().BeTrue();
        ledger.Facts.Should().ContainSingle();
        tool.CancellationCalls.Should().Be(1);
        appender.Records.Select(record => record.ToolExecution.ExecutionPhase).Should().Equal(
            AuditToolExecutionPhase.Running,
            AuditToolExecutionPhase.Running,
            AuditToolExecutionPhase.Terminal);
    }

    [Fact]
    public async Task CancelAsync_WhenDeadlineElapsedAndToolStillReturnsPending_ShouldAuditOutcomeUncertain()
    {
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Duplicate);
        var pending = PendingOperation("operation-cancel-deadline", "provider-cancel-deadline");
        var tool = new ReconcilableRecordingTool(
            AgentToolOperationReconciliationResult.Unknown(),
            cancellationResult: AgentToolOperationCancellationResult.Pending(pending));
        var executor = CreateExecutor(appender, ledger);

        var result = await executor.CancelAsync(
            CreateCancellationRequest(tool, pending) with { DeadlineUnixMs = 1 });

        result.Disposition.Should().Be(AgentToolCancellationDisposition.Completed);
        result.CompletedOutcome!.AuditCompleted.Should().BeTrue();
        result.CompletedOutcome.FailureCode.Should().Be("code_execution_cancel_outcome_uncertain");
        result.CompletedOutcome.Receipt.FailureOutcome.Should().Be(AgentToolFailureOutcome.OutcomeUncertain);
        appender.Records.Should().HaveCount(2);
        appender.Records[1].ToolExecution.ExecutionPhase.Should().Be(AuditToolExecutionPhase.Terminal);
    }

    [Fact]
    public async Task CancelAsync_WhenProviderReturnsTerminalAcrossDeadline_ShouldPreserveProviderTruth()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(now);
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Duplicate);
        var pending = PendingOperation("operation-cancel-terminal-race", "provider-cancel-terminal-race");
        var terminal = new AgentToolTerminalOutcome(
            """{"success":false,"code":"code_execution_cancelled"}""",
            new AgentToolReceipt
            {
                Status = AgentToolReceiptStatus.Error,
                ResultJson = """{"success":false,"code":"code_execution_cancelled"}""",
                ErrorCode = "code_execution_cancelled",
                ErrorMessage = "cancelled",
                SubjectKind = "nyxid.user-service",
                SubjectId = "service-code",
            });
        var tool = new ReconcilableRecordingTool(
            AgentToolOperationReconciliationResult.Unknown(),
            cancellationResult: AgentToolOperationCancellationResult.Completed(terminal),
            beforeCancellationResult: () => timeProvider.UtcNow = now.AddMinutes(2));
        var executor = CreateExecutor(appender, ledger, timeProvider);
        var request = CreateCancellationRequest(tool, pending) with
        {
            DeadlineUnixMs = now.AddMinutes(1).ToUnixTimeMilliseconds(),
        };

        var result = await executor.CancelAsync(request);

        result.Disposition.Should().Be(AgentToolCancellationDisposition.Completed);
        result.CompletedOutcome!.ResultJson.Should().Be(terminal.ResultJson);
        result.CompletedOutcome.Receipt.ErrorCode.Should().Be("code_execution_cancelled");
        result.CompletedOutcome.FailureCode.Should().NotBe("code_execution_cancel_outcome_uncertain");
        tool.CancellationCalls.Should().Be(1);
        appender.Records.Select(record => record.ToolExecution.ExecutionPhase).Should().Equal(
            AuditToolExecutionPhase.Running,
            AuditToolExecutionPhase.Terminal);
    }

    [Fact]
    public async Task CancelAsync_WhenDeadlineAlreadyElapsed_ShouldFinalizeWithoutLedgerOrProvider()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(now);
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.StoreUnavailable);
        var pending = PendingOperation("operation-cancel-expired", "provider-cancel-expired");
        var tool = new ReconcilableRecordingTool(AgentToolOperationReconciliationResult.Unknown());
        var executor = CreateExecutor(appender, ledger, timeProvider);
        var request = CreateCancellationRequest(tool, pending) with
        {
            DeadlineUnixMs = now.ToUnixTimeMilliseconds(),
        };

        var result = await executor.CancelAsync(request);

        result.Disposition.Should().Be(AgentToolCancellationDisposition.Completed);
        result.CompletedOutcome!.FailureCode.Should().Be("code_execution_cancel_outcome_uncertain");
        ledger.Facts.Should().BeEmpty();
        tool.CancellationCalls.Should().Be(0);
        appender.Records.Select(record => record.ToolExecution.ExecutionPhase).Should().Equal(
            AuditToolExecutionPhase.Running,
            AuditToolExecutionPhase.Terminal);
    }

    [Fact]
    public async Task CancelAsync_WhenDeadlineCrossesDuringLedger_ShouldFinalizeWithoutProvider()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(now);
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(
            AgentToolAdmissionStatus.StoreUnavailable,
            () => timeProvider.UtcNow = now.AddMinutes(2));
        var pending = PendingOperation("operation-cancel-ledger", "provider-cancel-ledger");
        var tool = new ReconcilableRecordingTool(AgentToolOperationReconciliationResult.Unknown());
        var executor = CreateExecutor(appender, ledger, timeProvider);
        var request = CreateCancellationRequest(tool, pending) with
        {
            DeadlineUnixMs = now.AddMinutes(1).ToUnixTimeMilliseconds(),
        };

        var result = await executor.CancelAsync(request);

        result.Disposition.Should().Be(AgentToolCancellationDisposition.Completed);
        result.CompletedOutcome!.FailureCode.Should().Be("code_execution_cancel_outcome_uncertain");
        ledger.Facts.Should().ContainSingle();
        tool.CancellationCalls.Should().Be(0);
        appender.Records.Should().HaveCount(2);
    }

    [Fact]
    public async Task CancelAsync_WhenDeadlineCrossesDuringRunningAudit_ShouldFinalizeWithoutProvider()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(now);
        var appender = new RecordingAuditTrailAppender((record, index) =>
        {
            if (index == 1)
                timeProvider.UtcNow = now.AddMinutes(2);
            return AuditTrailAppendResult.Appended(record.AuditId);
        });
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Duplicate);
        var pending = PendingOperation("operation-cancel-audit-deadline", "provider-cancel-audit-deadline");
        var tool = new ReconcilableRecordingTool(AgentToolOperationReconciliationResult.Unknown());
        var executor = CreateExecutor(appender, ledger, timeProvider);
        var request = CreateCancellationRequest(tool, pending) with
        {
            DeadlineUnixMs = now.AddMinutes(1).ToUnixTimeMilliseconds(),
        };

        var result = await executor.CancelAsync(request);

        result.Disposition.Should().Be(AgentToolCancellationDisposition.Completed);
        result.CompletedOutcome!.FailureCode.Should().Be("code_execution_cancel_outcome_uncertain");
        tool.CancellationCalls.Should().Be(0);
        appender.Records.Select(record => record.ToolExecution.ExecutionPhase).Should().Equal(
            AuditToolExecutionPhase.Running,
            AuditToolExecutionPhase.Terminal);
    }

    [Fact]
    public async Task CancelAsync_WhenDeadlineCrossesDuringProviderCancellation_ShouldAuditOutcomeUncertain()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(now);
        var appender = SuccessfulAuditAppender();
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Duplicate);
        var pending = PendingOperation("operation-cancel-provider-deadline", "provider-cancel-provider-deadline");
        var tool = new ReconcilableRecordingTool(
            AgentToolOperationReconciliationResult.Unknown(),
            cancellationResult: AgentToolOperationCancellationResult.Pending(pending),
            beforeCancellationResult: () => timeProvider.UtcNow = now.AddMinutes(2));
        var executor = CreateExecutor(appender, ledger, timeProvider);
        var request = CreateCancellationRequest(tool, pending) with
        {
            DeadlineUnixMs = now.AddMinutes(1).ToUnixTimeMilliseconds(),
        };

        var result = await executor.CancelAsync(request);

        result.Disposition.Should().Be(AgentToolCancellationDisposition.Completed);
        result.CompletedOutcome!.FailureCode.Should().Be("code_execution_cancel_outcome_uncertain");
        tool.CancellationCalls.Should().Be(1);
        appender.Records.Should().HaveCount(2);
    }

    [Fact]
    public async Task CancelAsync_WhenExpiredAuditRecoveryRetries_ShouldNotRepeatLedgerOrProvider()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(now);
        var appender = new RecordingAuditTrailAppender((record, index) =>
            index == 1
                ? AuditTrailAppendResult.StoreUnavailable(record.AuditId, "offline")
                : AuditTrailAppendResult.Appended(record.AuditId));
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Duplicate);
        var pending = PendingOperation("operation-cancel-audit-retry", "provider-cancel-audit-retry");
        var tool = new ReconcilableRecordingTool(AgentToolOperationReconciliationResult.Unknown());
        var executor = CreateExecutor(appender, ledger, timeProvider);
        var request = CreateCancellationRequest(tool, pending) with
        {
            DeadlineUnixMs = now.ToUnixTimeMilliseconds(),
        };

        var first = await executor.CancelAsync(request);
        var retry = await executor.CancelAsync(request);

        first.Disposition.Should().Be(AgentToolCancellationDisposition.Pending);
        first.FailureCode.Should().Be("audit_unavailable");
        retry.Disposition.Should().Be(AgentToolCancellationDisposition.Completed);
        retry.CompletedOutcome!.FailureCode.Should().Be("code_execution_cancel_outcome_uncertain");
        ledger.Facts.Should().BeEmpty();
        tool.CancellationCalls.Should().Be(0);
        appender.Records.Select(record => record.ToolExecution.ExecutionPhase).Should().Equal(
            AuditToolExecutionPhase.Running,
            AuditToolExecutionPhase.Running,
            AuditToolExecutionPhase.Terminal);
    }

    private static AdmittedAgentToolExecutor CreateExecutor(
        IAuditTrailAppender appender,
        IAgentToolAdmissionLedger? admissionLedger = null,
        TimeProvider? timeProvider = null) =>
        new(
            admissionLedger ?? new RecordingAdmissionLedger(AgentToolAdmissionStatus.Started),
            appender,
            new StableIdentityHasher(),
            timeProvider);

    private static async Task AssertClassificationFailureAsync(
        Func<string, AgentToolCallSafety> classify,
        string prohibitedText)
    {
        var appender = new RecordingAuditTrailAppender((record, _) =>
            AuditTrailAppendResult.Appended(record.AuditId));
        var ledger = new RecordingAdmissionLedger(AgentToolAdmissionStatus.Started);
        var tool = new RecordingTool(
            new AgentToolCallSafety(false, true, false),
            classify: classify);
        var executor = CreateExecutor(appender, ledger);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Failed);
        outcome.FailureCode.Should().Be("tool_classification_failed");
        outcome.SafeMessage.Should().Be(nameof(InvalidOperationException));
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.Classification);
        outcome.IsMutation.Should().BeTrue();
        outcome.TerminalInvoked.Should().BeFalse();
        outcome.Retryable.Should().BeFalse();
        outcome.AuditCompleted.Should().BeTrue();
        outcome.ResultJson.Should().NotContain(prohibitedText);
        outcome.Receipt.ErrorCode.Should().Be("tool_classification_failed");
        outcome.Receipt.ErrorMessage.Should().Be(nameof(InvalidOperationException));
        outcome.Receipt.ResultJson.Should().NotContain(prohibitedText);
        tool.SafetyCalls.Should().Be(1);
        tool.ExecutionCalls.Should().Be(0);
        ledger.Facts.Should().BeEmpty();
        var record = appender.Records.Should().ContainSingle().Subject;
        record.ToolExecution.ExecutionPhase.Should().Be(AuditToolExecutionPhase.Terminal);
        record.Failure.Code.Should().Be("tool_classification_failed");
        record.Failure.SanitizedMessage.Should().Be("tool_classification_failed");
        record.ToString().Should().NotContain(prohibitedText);
    }

    private static AuditTrailAppendResult CreateAppendResult(
        AuditTrailAppendStatus status,
        string auditId) => status switch
        {
            AuditTrailAppendStatus.Conflict => AuditTrailAppendResult.Conflict(auditId, "conflict"),
            AuditTrailAppendStatus.StoreUnavailable => AuditTrailAppendResult.StoreUnavailable(auditId, "offline"),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };

    private static RecordingAuditTrailAppender SuccessfulAuditAppender() =>
        new((record, _) => AuditTrailAppendResult.Appended(record.AuditId));

    private static AgentToolExecutionRequest CreateRequest(IAgentTool tool) =>
        new(
            tool,
            "{}",
            CreateTestExecutionContext() with
            {
                Request = new AgentToolRequestIdentity("request-1", "call-1"),
                ExecutionOwner = AgentToolExecutionOwners.Actor("actor-test"),
            },
            AgentToolApprovalContinuationMode.None,
            null);

    private static AgentToolExecutionRequest CreateRecoveryRequest(
        IAgentTool tool,
        string operationId,
        string? idempotencyKey = null) =>
        CreateRequest(tool) with
        {
            ExecutionContext = CreateTestExecutionContext() with
            {
                Request = new AgentToolRequestIdentity(
                    "request-1",
                    "call-1",
                    idempotencyKey,
                    TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds(),
                    operationId),
                ExecutionOwner = AgentToolExecutionOwners.Actor("actor-test"),
            },
            ExecutionAttemptKind = AgentToolExecutionAttemptKind.ActorRecovery,
        };

    private static AgentToolCancellationRequest CreateCancellationRequest(
        IAgentTool tool,
        AgentToolPendingOperation pending) =>
        new(
            tool,
            "{}",
            CreateTestExecutionContext() with
            {
                Request = new AgentToolRequestIdentity(
                    "request-1",
                    "call-1",
                    null,
                    TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds(),
                    pending.OperationId),
                ExecutionOwner = AgentToolExecutionOwners.Actor("actor-test"),
            },
            AgentToolApprovalContinuationMode.ActorOwned,
            AgentToolExecutionAttemptKind.ActorRecovery,
            pending,
            AgentToolOperationCancellationReason.WorkflowStopped,
            DeadlineUnixMs: TimeProvider.System.GetUtcNow().AddMinutes(5).ToUnixTimeMilliseconds());

    private static AgentToolPendingOperation PendingOperation(
        string operationId,
        string providerOperationId) =>
        new(
            operationId,
            providerOperationId,
            $"/executions/{providerOperationId}",
            $"/executions/{providerOperationId}/result",
            $"/executions/{providerOperationId}/cancel",
            AgentToolPendingOperationStatus.Queued,
            null,
            1_000,
            TimeProvider.System.GetUtcNow().AddMinutes(10).ToUnixTimeMilliseconds(),
            "chrono-sandbox",
            "service-code",
            CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission);

    private static AgentToolExecutionContext CreateTestExecutionContext() =>
        AgentToolExecutionContext.Empty with
        {
            ExecutionOwner = AgentToolExecutionOwners.Actor("actor-test"),
        };

    private static AgentToolOperationAdmission ExactUnattendedAdmission() => new(
        "service-lark",
        "api-lark-bot",
        new AgentToolOperationIdentity.AuthoredRequest("sha256:request"),
        AgentToolOperationAuthorizationBasis.ExplicitRequest,
        "POST",
        "/open-apis/approval/v4/instances",
        "sha256:contract",
        [],
        new AgentToolOperationRequestBody(
            true,
            "application/json",
            new AgentToolOperationValueSchema(
                AgentToolOperationValueKind.Object,
                [],
                new HashSet<string>(StringComparer.Ordinal),
                null,
                [],
                true)),
        AgentToolOperationResponsePolicy.TextOnly,
        new AgentToolOperationExecutionPolicy(
            AgentToolOperationRisk.Write,
            AgentToolOperationApproval.Required,
            AgentToolOperationEnforcementOwner.Aevatar,
            [AgentToolOperationExecutionMode.Interactive, AgentToolOperationExecutionMode.Durable]));

    private sealed class RecordingTool(
        AgentToolCallSafety safety,
        Func<string, string>? execute = null,
        bool throwOnReceipt = false,
        Func<string, AgentToolReceipt?>? createReceipt = null,
        string name = "test_tool",
        Func<string, AgentToolCallSafety>? classify = null,
        AgentToolReplayPolicy? replayPolicy = null) : IAgentTool
    {
        private readonly Func<string, string> _execute = execute ?? (_ => "{}");

        public string Name => name;
        public string Description => "test";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode { get; init; } = ToolApprovalMode.NeverRequire;
        public int SafetyCalls { get; private set; }
        public int ExecutionCalls { get; private set; }
        public List<string> SafetyArguments { get; } = [];
        public List<string> ExecutionArguments { get; } = [];
        public List<AgentToolExecutionContext> ExecutionContexts { get; } = [];

        public AgentToolReplayPolicy ResolveReplayPolicy(string argumentsJson) =>
            replayPolicy ?? (safety.IsReadOnly && !safety.IsDestructive
                ? AgentToolReplayPolicy.ReadOnlyRetryable
                : AgentToolReplayPolicy.NonReplayable);

        public AgentToolCallSafety GetCallSafety(string argumentsJson)
        {
            SafetyCalls++;
            SafetyArguments.Add(argumentsJson);
            return classify is null ? safety : classify(argumentsJson);
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
            ExecutionContexts.Add(AgentToolRequestContext.Current
                                  ?? throw new InvalidOperationException("Tool execution context is required."));
            return Task.FromResult(_execute(argumentsJson));
        }
    }

    private sealed class ReconcilableRecordingTool(
        AgentToolOperationReconciliationResult reconciliationResult,
        bool throwDuringReconciliation = false,
        AgentToolOperationStartResult? startResult = null,
        AgentToolOperationCancellationResult? cancellationResult = null,
        Action? beforeCancellationResult = null) : IAgentTool, IAgentToolDurableOperation
    {
        public string Name => "reconcilable_tool";
        public string Description => "test";
        public string ParametersSchema => "{}";
        public int ReconciliationCalls { get; private set; }
        public int ExecutionCalls { get; private set; }
        public int CancellationCalls { get; private set; }
        public List<AgentToolOperationStartRequest> StartRequests { get; } = [];
        public bool ThrowDuringClassification { get; set; }
        public bool ThrowDuringReceiptCreation { get; set; }

        public AgentToolCallSafety GetCallSafety(string argumentsJson) =>
            ThrowDuringClassification
                ? throw new InvalidOperationException("classification changed")
                : new(false, false, false);

        public AgentToolReplayPolicy ResolveReplayPolicy(string argumentsJson) =>
            AgentToolReplayPolicy.Reconcilable;

        public Task<AgentToolOperationReconciliationResult> ReconcileOperationAsync(
            AgentToolOperationReconciliationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ReconciliationCalls++;
            if (throwDuringReconciliation)
                throw new InvalidOperationException("reconciliation failed");
            return Task.FromResult(reconciliationResult);
        }

        public Task<AgentToolOperationStartResult> StartOperationAsync(
            AgentToolOperationStartRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecutionCalls++;
            StartRequests.Add(request);
            return Task.FromResult(startResult ?? AgentToolOperationStartResult.Completed(
                new AgentToolTerminalOutcome("{}")));
        }

        public Task<AgentToolOperationCancellationResult> CancelOperationAsync(
            AgentToolOperationCancellationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CancellationCalls++;
            beforeCancellationResult?.Invoke();
            return Task.FromResult(
                cancellationResult ?? AgentToolOperationCancellationResult.Pending(request.PendingOperation));
        }

        public AgentToolReceipt CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson)
        {
            if (ThrowDuringReceiptCreation)
                throw new InvalidOperationException("receipt mapping changed");

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
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("{}");
        }
    }

    private sealed class TerminalOutcomeTool : IAgentTool
    {
        public string Name => "terminal_outcome";
        public string Description => "typed terminal outcome";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;
        public bool IsDestructive => true;
        public string SideEffectKind => "Example.Publish";

        public AgentToolCallSafety GetCallSafety(string argumentsJson) =>
            new(false, false, true);

        public Task<AgentToolTerminalOutcome> ExecuteWithOutcomeAsync(
            string callId,
            string toolName,
            string argumentsJson,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentToolTerminalOutcome(
                """{"error":true,"status":503,"body":"domain payload"}""",
                new AgentToolReceipt
                {
                    Status = AgentToolReceiptStatus.Success,
                    SubjectKind = "nyxid.user-service",
                    SubjectId = "usvc-outcome",
                }));

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            throw new InvalidOperationException("The typed terminal outcome path must be used.");
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

    private sealed class RecordingAdmissionLedger(
        AgentToolAdmissionStatus status,
        Action? beforeResult = null) : IAgentToolAdmissionLedger
    {
        public List<AgentToolAdmissionFact> Facts { get; } = [];

        public Task<AgentToolAdmissionResult> TryStartAsync(
            AgentToolAdmissionFact fact,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Facts.Add(fact.Clone());
            beforeResult?.Invoke();
            return Task.FromResult(new AgentToolAdmissionResult(status));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class DeduplicatingAdmissionLedger : IAgentToolAdmissionLedger
    {
        private readonly HashSet<string> _admissionIds = new(StringComparer.Ordinal);

        public List<AgentToolAdmissionFact> Facts { get; } = [];
        public List<AgentToolAdmissionStatus> Decisions { get; } = [];

        public Task<AgentToolAdmissionResult> TryStartAsync(
            AgentToolAdmissionFact fact,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Facts.Add(fact.Clone());
            var status = _admissionIds.Add(fact.AdmissionId)
                ? AgentToolAdmissionStatus.Started
                : AgentToolAdmissionStatus.Duplicate;
            Decisions.Add(status);
            return Task.FromResult(new AgentToolAdmissionResult(status));
        }
    }

    private sealed class ThrowingAdmissionLedger(Exception exception) : IAgentToolAdmissionLedger
    {
        public Task<AgentToolAdmissionResult> TryStartAsync(
            AgentToolAdmissionFact fact,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromException<AgentToolAdmissionResult>(exception);
        }
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }
}
