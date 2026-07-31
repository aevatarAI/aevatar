using System.Diagnostics;
using System.Diagnostics.Metrics;
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
        appender.Records.Single(record => record.ToolExecution.ExecutionPhase == AuditToolExecutionPhase.Terminal)
            .CredentialSource.Should().Be(AuditCredentialSource.ChannelRegistration);
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

    private static AdmittedAgentToolExecutor CreateExecutor(
        IAuditTrailAppender appender,
        IAgentToolAdmissionLedger? admissionLedger = null) =>
        new(
            admissionLedger ?? new RecordingAdmissionLedger(AgentToolAdmissionStatus.Started),
            appender,
            new StableIdentityHasher());

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
            CreateTestExecutionContext() with
            {
                Request = new AgentToolRequestIdentity("request-1", "call-1"),
                ExecutionOwner = AgentToolExecutionOwners.Actor("actor-test"),
            },
            AgentToolApprovalContinuationMode.None,
            null);

    private static AgentToolExecutionContext CreateTestExecutionContext() =>
        AgentToolExecutionContext.Empty with
        {
            ExecutionOwner = AgentToolExecutionOwners.Actor("actor-test"),
        };

    private sealed class RecordingTool(
        AgentToolCallSafety safety,
        Func<string, string>? execute = null,
        bool throwOnReceipt = false,
        Func<string, AgentToolReceipt?>? createReceipt = null,
        string name = "test_tool") : IAgentTool
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
            ExecutionContexts.Add(AgentToolRequestContext.Current
                                  ?? throw new InvalidOperationException("Tool execution context is required."));
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

    private sealed class RecordingAdmissionLedger(AgentToolAdmissionStatus status) : IAgentToolAdmissionLedger
    {
        public List<AgentToolAdmissionFact> Facts { get; } = [];

        public Task<AgentToolAdmissionResult> TryStartAsync(
            AgentToolAdmissionFact fact,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Facts.Add(fact.Clone());
            return Task.FromResult(new AgentToolAdmissionResult(status));
        }
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
