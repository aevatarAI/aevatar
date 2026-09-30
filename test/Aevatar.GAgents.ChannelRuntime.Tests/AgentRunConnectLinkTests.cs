using System.Reflection;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed partial class AgentRunGAgentTests
{
    [Fact]
    public async Task ConnectLinkPendingBatch_ShouldSuspendWithoutAdvancingOrExposingProvisionalResults()
    {
        var (agent, executor, publisher, command) = CreateConnectLinkRun();

        await agent.HandleNextToolStepAsync(command);

        agent.State.GenerationStep.NextStepIndex.Should().Be(2);
        agent.State.GenerationStep.Round.Should().Be(0);
        agent.State.GenerationStep.PendingToolCalls.Should().HaveCount(3);
        agent.State.GenerationStep.Messages.Should().BeEmpty();
        agent.State.GenerationStep.ToolReceipts.Should().BeEmpty();
        executor.LlmStepExecutions.Should().BeEmpty();
        executor.ToolStepExecutions.Should().BeEmpty();
        publisher.Sent.Select(item => item.Message).OfType<ConnectLinkToolResultRequested>()
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task ConnectLinkResults_ShouldResumeExactBatchOnce_AndKeepRealUrlOutOfHistoryAndReceipts()
    {
        var (agent, executor, publisher, command) = CreateConnectLinkRun();
        await agent.HandleNextToolStepAsync(command);
        var last = ConnectLinkResult("call-last");
        await DeliverConnectLinkResultAsync(agent, last);
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());
        agent.State.GenerationStep.NextStepIndex.Should().Be(2);
        executor.LlmStepExecutions.Should().BeEmpty();

        var first = ConnectLinkResult("call-first");
        await DeliverConnectLinkResultAsync(agent, first);
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());
        await DeliverConnectLinkResultAsync(agent, first);
        await DeliverConnectLinkResultAsync(agent, last);
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());

        var resumed = executor.LlmStepExecutions.Should().ContainSingle().Subject;
        resumed.StepIndex.Should().Be(3);
        resumed.StepState.Round.Should().Be(1);
        resumed.StepState.PendingToolCalls.Should().BeEmpty();
        resumed.StepState.Messages.Select(message => message.ToolCallId)
            .Should().Equal("call-first", "call-ordinary", "call-last");
        resumed.StepState.Messages[1].Content.Should().Be("inventory-completed");
        foreach (var result in new[] { first, last })
        {
            var message = resumed.StepState.Messages.Single(item => item.ToolCallId == result.Target.CallId);
            using var json = JsonDocument.Parse(message.Content);
            json.RootElement.GetProperty("id").GetString().Should().Be(result.Result.ExternalRequestId);
            json.RootElement.GetProperty("connect_url").GetString().Should().Be(result.Result.ConnectUrl);
            var receipt = resumed.StepState.ToolReceipts.Single(item => item.CallId == result.Target.CallId);
            receipt.ChannelConnectLinkPending.Should().BeNull();
            receipt.ResultJson.Should().Contain("[redacted]").And.NotContain(result.Result.ConnectUrl);
            resumed.StepState.AppendedHistory.Single(item => item.ToolCallId == result.Target.CallId)
                .Content.Should().Contain("[redacted]").And.NotContain(result.Result.ConnectUrl);
            resumed.StepState.PendingHistoryMessages.Single(item => item.ToolCallId == result.Target.CallId)
                .Content.Should().Contain("[redacted]").And.NotContain(result.Result.ConnectUrl);
        }
        executor.ToolStepExecutions.Should().BeEmpty("completed tools must never be re-executed during result recovery");
        publisher.Sent.Select(item => item.Message).OfType<ConnectLinkToolResultConsumed>().Should().HaveCount(5);
        publisher.Published.OfType<LlmReplyReadyEvent>().Should().BeEmpty();
        publisher.Sent.Select(item => item.Message).OfType<ExternalCallbackLinkPresented>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("publisher")]
    [InlineData("actor")]
    [InlineData("run")]
    [InlineData("attempt")]
    [InlineData("step")]
    [InlineData("call")]
    [InlineData("callback")]
    [InlineData("operation")]
    [InlineData("arguments")]
    public async Task ConnectLinkResults_ShouldRejectMismatchedIdentity(string mismatch)
    {
        var (agent, executor, publisher, command) = CreateConnectLinkRun();
        await agent.HandleNextToolStepAsync(command);
        var result = ConnectLinkResult("call-first");
        var source = result.OperationActorId;
        switch (mismatch)
        {
            case "publisher": source = "actor-other"; break;
            case "actor": result.Target.ActorId = "actor-other"; break;
            case "run": result.Target.RunId = "run-other"; break;
            case "attempt": result.Target.Attempt++; break;
            case "step": result.Target.StepIndex++; break;
            case "call": result.Target.CallId = "call-other"; break;
            case "callback": result.CallbackId = "callback-other"; break;
            case "operation": result.OperationActorId = "operation-other"; source = result.OperationActorId; break;
            case "arguments":
                var changed = agent.State.Clone();
                changed.GenerationStep.PendingToolCalls[0].ArgumentsJson = "{\"service_name\":\"other\"}";
                SetState(agent, changed);
                break;
        }
        await DeliverConnectLinkResultAsync(agent, result, source);

        agent.State.ConnectLinkBatches.Single().Calls.Should().OnlyContain(call => call.Result == null);
        agent.State.GenerationStep.NextStepIndex.Should().Be(2);
        publisher.Sent.Select(item => item.Message).OfType<ConnectLinkToolResultConsumed>().Should().BeEmpty();
        executor.LlmStepExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task ConnectLinkSuspension_ShouldPersistOnlyCredentialHandles_AndRestoreOriginalRunCredentials()
    {
        var secrets = Substitute.For<IRuntimeSecretStore>();
        secrets.ResolveAsync(Arg.Any<ResolveRuntimeSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new ResolveRuntimeSecretResult(null,
                call.Arg<ResolveRuntimeSecretRequest>().Purpose == ChannelRelayRuntimeSecretPurposes.ReplyToken
                    ? "resolved-reply-token" : "resolved-user-token"));
        var (agent, executor, _, command) = CreateConnectLinkRun(secrets);
        command.Request.ToolContext.ExternalMetadata["nyxid.access_token"] = "secret-external-metadata";
        await agent.HandleNextToolStepAsync(command);
        var saved = RoundTrip(agent.State);
        var savedText = Encoding.UTF8.GetString(saved.ToByteArray());
        foreach (var rawSecret in new[]
                 { "secret-reply", "secret-user", "secret-tool", "secret-llm", "secret-metadata", "secret-external-metadata" })
            savedText.Should().NotContain(rawSecret);
        saved.ConnectLinkBatches.Single().Request.RelayReplyTokenRef.Ref.Should().Be("secret-ref-reply");
        saved.ConnectLinkBatches.Single().Request.RelayUserAccessTokenRef.Ref.Should().Be("secret-ref-user");
        await DeliverConnectLinkResultAsync(agent, ConnectLinkResult("call-first"));
        await DeliverConnectLinkResultAsync(agent, ConnectLinkResult("call-last"));
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());

        var resumed = executor.LlmStepExecutions.Should().ContainSingle().Subject;
        resumed.Request.ReplyToken.Should().Be("resolved-reply-token");
        resumed.Request.Activity.TransportExtras.NyxUserAccessToken.Should().Be("resolved-user-token");
        resumed.Request.ExternalCallbackId.Should().BeEmpty("creation resumes the original tool, not the terminal business callback");
        await secrets.Received(1).ResolveAsync(Arg.Is<ResolveRuntimeSecretRequest>(request =>
            request.Ref == "secret-ref-reply" && request.Purpose == ChannelRelayRuntimeSecretPurposes.ReplyToken &&
            request.OwnerRunId == "run-link" && request.OwnerStepId == "correlation-link"), Arg.Any<CancellationToken>());
        await secrets.Received(1).ResolveAsync(Arg.Is<ResolveRuntimeSecretRequest>(request =>
            request.Ref == "secret-ref-user" && request.Purpose == ChannelRelayRuntimeSecretPurposes.UserAccessToken &&
            request.OwnerRunId == "run-link" && request.OwnerStepId == "correlation-link"), Arg.Any<CancellationToken>());
        Encoding.UTF8.GetString(agent.State.ToByteArray()).Should()
            .NotContain("resolved-reply-token").And.NotContain("resolved-user-token");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectLinkAppliedBatch_ShouldRecoverModelDispatchAfterRestart_AtExactResumeStep(bool finalRound)
    {
        var (agent, executor, _, command) = CreateConnectLinkRun();
        if (finalRound)
        {
            var state = agent.State.Clone();
            state.GenerationStep.MaxToolRounds = 1;
            SetState(agent, state);
        }
        await agent.HandleNextToolStepAsync(command);
        await DeliverConnectLinkResultAsync(agent, ConnectLinkResult("call-first"));
        await DeliverConnectLinkResultAsync(agent, ConnectLinkResult("call-last"));
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());
        executor.LlmStepExecutions.Should().BeEmpty("batch application commits before the model dispatch self message");
        agent.State.ConnectLinkBatches.Single().ResumeStepIndex.Should().Be(finalRound ? 4 : 3);
        agent.State.GenerationStep.NextStepIndex.Should().Be(finalRound ? 4 : 3);
        agent.State.GenerationStep.FinalNoToolsStep.Should().Be(finalRound);
        var (restarted, resumedExecutor, publisher, _) = CreateConnectLinkRun();
        SetState(restarted, RoundTrip(agent.State));
        await InvokeConnectLinkRecoveryHookAsync(restarted, "OnActivateAsync");
        publisher.Published.OfType<AgentRunConnectLinkRecoveryRequested>().Should().ContainSingle();
        await restarted.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());
        await restarted.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());

        var resumed = resumedExecutor.LlmStepExecutions.Should().ContainSingle().Subject;
        resumed.StepIndex.Should().Be(finalRound ? 4 : 3);
        resumed.StepState.FinalNoToolsStep.Should().Be(finalRound);
        resumed.StepState.Round.Should().Be(1);
        resumedExecutor.ToolStepExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task ConnectLinkDuplicateResult_ShouldRescheduleAfterAcceptanceSignalWasLost()
    {
        var (agent, _, publisher, command) = CreateConnectLinkRun();
        await agent.HandleNextToolStepAsync(command);
        var result = ConnectLinkResult("call-first");
        await DeliverConnectLinkResultAsync(agent, result);
        publisher.Published.Clear();
        await DeliverConnectLinkResultAsync(agent, result);
        publisher.Published.OfType<AgentRunConnectLinkRecoveryRequested>().Should().ContainSingle();
    }

    [Theory]
    [InlineData("OnActivateAsync")]
    [InlineData("OnCommittedStatePublicationRecoveredAsync")]
    public async Task ConnectLinkRecovery_ShouldRestoreSubscriptionAfterCommitBeforeInitialSend(string hook)
    {
        var (agent, _, _, command) = CreateConnectLinkRun();
        await agent.HandleNextToolStepAsync(command);
        var (restarted, executor, publisher, _) = CreateConnectLinkRun();
        SetState(restarted, RoundTrip(agent.State));
        await InvokeConnectLinkRecoveryHookAsync(restarted, hook);
        publisher.Published.OfType<AgentRunConnectLinkRecoveryRequested>().Should().ContainSingle();
        await restarted.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());

        publisher.Sent.Select(item => item.Message).OfType<ConnectLinkToolResultRequested>().Should().HaveCount(2);
        executor.ToolStepExecutions.Should().BeEmpty();
        executor.LlmStepExecutions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("unconfirmed")]
    [InlineData("expired")]
    [InlineData("timeout")]
    public async Task ConnectLinkCreationFailure_ShouldFinishOnlyOriginalTool_WithHonestError(string reason)
    {
        var (agent, executor, publisher, command) = CreateConnectLinkRun();
        await agent.HandleNextToolStepAsync(command);
        var first = ConnectLinkResult("call-first");
        if (reason == "provider")
            first.Result = new ConnectLinkCreationResult
            {
                FailureCode = "sender_authorization_unavailable",
                FailureOutcome = ConnectLinkCreationFailureOutcome.NotCreated,
            };
        if (reason == "unconfirmed")
            first.Result = new ConnectLinkCreationResult { FailureCode = "connect_link_creation_unconfirmed" };
        if (reason == "expired")
            first.Result.ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
        if (reason != "timeout")
            await DeliverConnectLinkResultAsync(agent, first);
        await DeliverConnectLinkResultAsync(agent, ConnectLinkResult("call-last"));
        if (reason == "timeout")
        {
            var state = agent.State.Clone();
            state.ConnectLinkBatches.Single().DeadlineUnixMs = 1;
            SetState(agent, state);
        }
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());

        var resumed = executor.LlmStepExecutions.Should().ContainSingle().Subject;
        var receipt = resumed.StepState.ToolReceipts.Single(item => item.CallId == "call-first");
        receipt.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be(reason switch
        {
            "provider" => "sender_authorization_unavailable",
            "unconfirmed" => "connect_link_creation_unconfirmed",
            "expired" => "connect_link_creation_result_invalid",
            _ => "connect_link_creation_timed_out",
        });
        receipt.FailureOutcome.Should().Be(reason == "provider"
            ? AgentToolFailureOutcome.CalleeConfirmed : AgentToolFailureOutcome.OutcomeUncertain);
        resumed.StepState.Messages.Single(item => item.ToolCallId == "call-first").Content.Should().Contain(receipt.ErrorCode);
        publisher.Published.OfType<LlmReplyReadyEvent>().Should().BeEmpty();
        publisher.Sent.Select(item => item.Message).OfType<CallbackCompleted>().Should().BeEmpty();
    }

    [Fact]
    public async Task ConnectLinkLocalTimeout_ShouldRejectLateProviderResult_WithoutChangingFailureOrReexecuting()
    {
        var (agent, executor, publisher, command) = CreateConnectLinkRun();
        await agent.HandleNextToolStepAsync(command);
        await DeliverConnectLinkResultAsync(agent, ConnectLinkResult("call-last"));
        var expired = agent.State.Clone();
        expired.ConnectLinkBatches.Single().DeadlineUnixMs = 1;
        SetState(agent, expired);
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());
        var beforeLateResult = agent.State.Clone();
        publisher.Sent.Clear();

        await DeliverConnectLinkResultAsync(agent, ConnectLinkResult("call-first"));
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());

        agent.State.Should().Be(beforeLateResult);
        var rejected = publisher.Sent.Select(item => item.Message).OfType<ConnectLinkToolResultRejected>()
            .Should().ContainSingle().Subject;
        rejected.FailureCode.Should().Be("connect_link_creation_timed_out");
        rejected.Target.CallId.Should().Be("call-first");
        publisher.Sent.Select(item => item.Message).OfType<ConnectLinkToolResultConsumed>().Should().BeEmpty();
        executor.LlmStepExecutions.Should().ContainSingle();
        executor.ToolStepExecutions.Should().BeEmpty();
        var failedCall = agent.State.ConnectLinkBatches.Single().Calls.Single(call => call.CallId == "call-first");
        failedCall.Result.Should().BeNull("a local deadline is not a provider failure fact");
        failedCall.LocalFailureCode.Should().Be("connect_link_creation_timed_out");
    }

    [Fact]
    public async Task ConnectLinkResume_ShouldRetryAfterContinuationAndFailurePublishingThrow()
    {
        var (agent, executor, publisher, command) = CreateConnectLinkRun();
        await agent.HandleNextToolStepAsync(command);
        await DeliverConnectLinkResultAsync(agent, ConnectLinkResult("call-first"));
        await DeliverConnectLinkResultAsync(agent, ConnectLinkResult("call-last"));
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());
        publisher.FailSelfStepMessages = true;
        var act = () => agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());
        await act.Should().ThrowAsync<InvalidOperationException>();
        publisher.FailSelfStepMessages = false;
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());

        executor.LlmStepExecutions.Should().HaveCount(2);
        executor.ToolStepExecutions.Should().BeEmpty();
        agent.State.GenerationStep.Round.Should().Be(1);
        publisher.Published.OfType<AgentRunNextLlmStepRequestedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task ConnectLinkResume_ShouldReachFixedDeadline_WhenContinuationDeliveryIsLost()
    {
        var (agent, executor, publisher, command) = CreateConnectLinkRun();
        await agent.HandleNextToolStepAsync(command);
        await agent.HandleReplyGenerationTimedOutAsync(new AgentRunReplyGenerationTimedOut
        { RunId = "run-link", CorrelationId = "correlation-link", Attempt = 1 });
        await DeliverConnectLinkResultAsync(agent, ConnectLinkResult("call-first"));
        await DeliverConnectLinkResultAsync(agent, ConnectLinkResult("call-last"));
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());
        publisher.DropSelfStepMessages = true;
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());
        var savedDeadline = agent.State.ConnectLinkBatches.Single().ResumeDeadlineUnixMs;
        savedDeadline.Should().BeGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());
        agent.State.ConnectLinkBatches.Single().ResumeDeadlineUnixMs.Should().Be(savedDeadline);
        var expired = agent.State.Clone();
        expired.ConnectLinkBatches.Single().ResumeDeadlineUnixMs = 1;
        SetState(agent, expired);
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());

        agent.State.Status.Should().Be(AgentRunStatus.Failed);
        executor.LlmStepExecutions.Should().ContainSingle();
        publisher.Sent.Select(item => item.Message).OfType<DeferredLlmReplyDroppedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task ConnectLinkResumeDeadline_ShouldCoverFollowingSteps_AndRejectOriginalStaleTimeout()
    {
        var (agent, _, publisher, command) = CreateConnectLinkRun();
        await agent.HandleNextToolStepAsync(command);
        await DeliverConnectLinkResultAsync(agent, ConnectLinkResult("call-first"));
        await DeliverConnectLinkResultAsync(agent, ConnectLinkResult("call-last"));
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());
        await agent.HandleConnectLinkRecoveryAsync(ConnectLinkRecovery());
        var deadline = agent.State.ConnectLinkBatches.Single().ResumeDeadlineUnixMs;
        var scheduler = (RecordingCallbackScheduler)agent.Services.GetRequiredService<IActorRuntimeCallbackScheduler>();
        var timeout = scheduler.Timeouts.Select(item => item.TriggerEnvelope.Payload)
            .Where(payload => payload.Is(AgentRunReplyGenerationTimedOut.Descriptor))
            .Select(payload => payload.Unpack<AgentRunReplyGenerationTimedOut>()).Should().ContainSingle().Subject;
        timeout.TimedOutAtUnixMs.Should().Be(deadline);
        var progressed = agent.State.Clone();
        progressed.GenerationStep.NextStepIndex += 2;
        SetState(agent, progressed);
        var stale = timeout.Clone();
        stale.TimedOutAtUnixMs--;
        await agent.HandleReplyGenerationTimedOutAsync(stale);
        agent.State.Status.Should().Be(AgentRunStatus.ReplyGenerationRequested);

        await agent.HandleReplyGenerationTimedOutAsync(timeout);

        agent.State.Status.Should().Be(AgentRunStatus.Failed);
        publisher.Sent.Select(item => item.Message).OfType<DeferredLlmReplyDroppedEvent>().Should().ContainSingle();
    }

    private static ConnectLinkToolResultProduced ConnectLinkResult(string callId) => new()
    {
        CallbackId = "callback-" + callId, OperationActorId = "operation-" + callId,
        Target = new ConnectLinkToolContinuationTarget
        {
            ActorId = "run-actor-link", RunId = "run-link", Attempt = 1, StepIndex = 3, CallId = callId,
        },
        Result = new ConnectLinkCreationResult
        {
            ExternalRequestId = "link-" + callId, ConnectUrl = "https://nyxid.test/connect/secret-" + callId,
            ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
        },
    };

    private static AgentRunConnectLinkRecoveryRequested ConnectLinkRecovery() => new()
    { RunId = "run-link", Attempt = 1, StepIndex = 3 };

    private static Task DeliverConnectLinkResultAsync(AgentRunGAgent agent, ConnectLinkToolResultProduced result,
        string? publisher = null) => agent.HandleEventAsync(new EventEnvelope
    {
        Id = Guid.NewGuid().ToString("N"), Payload = Any.Pack(result),
        Route = new EnvelopeRoute
        { PublisherActorId = publisher ?? result.OperationActorId, Direct = new DirectRoute { TargetActorId = agent.Id } },
    });

    private static Task InvokeConnectLinkRecoveryHookAsync(AgentRunGAgent agent, string method)
    {
        var hook = typeof(AgentRunGAgent).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)hook.Invoke(agent, method == "OnActivateAsync"
            ? [CancellationToken.None] : [new EventEnvelope(), CancellationToken.None])!;
    }

    private static (AgentRunGAgent Agent, PausedReplyGenerationExecutor Executor,
        ConnectLinkPublisher Publisher, AgentRunNextToolStepRequestedEvent Command) CreateConnectLinkRun(
            IRuntimeSecretStore? secrets = null)
    {
        var publisher = new ConnectLinkPublisher();
        var executor = new PausedReplyGenerationExecutor();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateRunAgentWithExecutor(new DispatchingActorRuntime(), executor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { ResponseTimeoutSeconds = 60 }, publisher, scheduler);
        SetId(agent, "run-actor-link");
        var services = new ServiceCollection().AddSingleton<IActorRuntimeCallbackScheduler>(scheduler);
        if (secrets is not null)
            services.AddSingleton(secrets);
        agent.Services = services.BuildServiceProvider();
        var state = new AgentRunGAgentState
        {
            RunId = "run-link",
            CorrelationId = "correlation-link",
            TargetActorId = "conversation-link",
            Status = AgentRunStatus.ReplyGenerationRequested,
            GenerationAttempt = 1,
            GenerationStep = new AgentRunReplyStepState
            {
                RunId = "run-link", CorrelationId = "correlation-link", TargetActorId = "conversation-link",
                Attempt = 1, NextStepIndex = 2, MaxToolRounds = 8, PendingToolAuthorizationConsumed = true,
                ToolContext = new AgentToolExecutionContextPayload
                {
                    Channel = new AgentToolChannelContextPayload
                    {
                        Continuation = new AgentToolChannelContinuationContext
                        {
                            ConversationActorId = "conversation-link",
                        },
                    },
                },
            },
        };
        var request = new NeedsLlmReplyEvent
        {
            RunId = state.RunId, CorrelationId = state.CorrelationId, TargetActorId = state.TargetActorId,
            RegistrationId = "registration-link", Activity = BuildRelayActivity(),
            ReplyToken = "secret-reply", ReplyTokenExpiresAtUnixMs = long.MaxValue,
            RelayReplyTokenRef = new RuntimeSecretReference { Ref = "secret-ref-reply", ExpiresAtUnixMs = long.MaxValue },
            RelayUserAccessTokenRef = new RuntimeSecretReference { Ref = "secret-ref-user", ExpiresAtUnixMs = long.MaxValue },
            ToolContext = new AgentToolExecutionContextPayload
            {
                Credentials = new AgentToolCredentialsPayload { NyxIdAccessToken = "secret-tool" },
            },
            LlmControl = new LLMControlContextPayload { NyxIdAccessToken = "secret-llm" },
        };
        request.Activity.TransportExtras ??= new TransportExtras();
        request.Activity.TransportExtras.NyxUserAccessToken = "secret-user";
        request.Metadata.Add("nyxid.access_token", "secret-metadata");
        var result = new AgentRunToolStepResult { AdvanceRound = true };
        foreach (var callId in new[] { "call-first", "call-ordinary", "call-last" })
        {
            var name = callId == "call-ordinary" ? "read_inventory" : "nyxid_connect_links.create";
            var call = new AgentRunToolCall { Id = callId, Name = name, ArgumentsJson = "{}" };
            state.GenerationStep.PendingToolCalls.Add(call);
            state.GenerationStep.PendingToolAuthorizations.Add(new AgentRunPendingToolAuthorization { Call = call.Clone() });
            result.ResultMessages.Add(new AgentRunChatMessage
            {
                Role = "tool", ToolCallId = callId, Content = callId == "call-ordinary" ? "inventory-completed" : "accepted",
            });
            var receipt = new AgentToolReceipt
            {
                CallId = callId, ToolName = name, Status = AgentToolReceiptStatus.Success,
                MutationStage = AgentToolReceiptMutationStage.Accepted,
                ResultJson = callId == "call-ordinary" ? "inventory-completed" : "accepted",
            };
            if (callId != "call-ordinary")
                receipt.ChannelConnectLinkPending = new ChannelConnectLinkPendingReceipt
                {
                    CallbackId = "callback-" + callId, OperationActorId = "operation-" + callId,
                };
            result.ToolReceipts.Add(receipt);
        }
        SetState(agent, state);
        return (agent, executor, publisher, new AgentRunNextToolStepRequestedEvent
        {
            RunId = state.RunId, CorrelationId = state.CorrelationId, TargetActorId = state.TargetActorId,
            Attempt = 1, StepIndex = 3, Request = request, ToolStepResult = result,
        });
    }

    private sealed class ConnectLinkPublisher : IEventPublisher
    {
        public List<IMessage> Published { get; } = [];
        public List<(string Target, IMessage Message)> Sent { get; } = [];
        public bool FailSelfStepMessages { get; set; }
        public bool DropSelfStepMessages { get; set; }

        public Task PublishAsync<T>(T message, TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default, EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null) where T : IMessage
        {
            if (message is AgentRunNextLlmStepRequestedEvent or AgentRunReplyGenerationFailed)
            {
                if (FailSelfStepMessages)
                    throw new InvalidOperationException("Injected self-message delivery failure.");
                if (DropSelfStepMessages)
                    return Task.CompletedTask;
            }
            Published.Add(message);
            return Task.CompletedTask;
        }

        public Task SendToAsync<T>(string targetActorId, T message, CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null, EventEnvelopePublishOptions? options = null) where T : IMessage
        {
            Sent.Add((targetActorId, message));
            return Task.CompletedTask;
        }
    }
}
