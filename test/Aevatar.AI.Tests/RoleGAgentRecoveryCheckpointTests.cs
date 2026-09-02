using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.AI.Tests;

public sealed class RoleGAgentRecoveryCheckpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PrepareBatch_WhenIntentCommitFails_ShouldNotInvokeTool()
    {
        var tool = new TestTool("mutate", AgentToolReplayPolicy.NonReplayable);
        var tools = new ToolManager();
        tools.Register(tool);
        var executionPort = new RecordingExecutionPort(ExecutedOutcome("unused"));
        var executor = new StreamingToolExecutor(
            tools,
            toolContext: ToolContext("actor-a", "session-a", bearer: "bearer-must-not-persist"),
            toolExecutionPort: executionPort,
            checkpointPort: new ThrowingCheckpointPort());

        var act = () => executor.PrepareBatchAsync(
            "session-a",
            0,
            [ToolCall("call-a", tool.Name, "{\"secret\":\"value\"}")]);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("intent commit failed");
        executionPort.Requests.Should().BeEmpty();
        tool.ExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task Recover_WhenCompletionIsCommitted_ShouldReuseResultWithoutExternalCall()
    {
        var fixture = await CreateFixtureAsync("role-completed-recovery");
        await fixture.StartSessionAsync("session-a");
        var prepared = await fixture.Agent.PrepareBatchAsync(Batch(
            "session-a",
            0,
            Operation("call-a", fixture.Tool, AgentToolReplayPolicy.NonReplayable)));
        await fixture.Agent.CommitCompletionAsync(
            prepared.Single(),
            new ToolExecutionResult("call-a", fixture.Tool.Name, "{\"ok\":true}", false));

        var recovered = await InvokeRecoveredResultsAsync(
            fixture.Agent,
            "session-a",
            fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint,
            ToolContext(fixture.ActorId, "session-a"));

        recovered.Should().NotBeNull();
        fixture.ExecutionPort.Requests.Should().BeEmpty();
        fixture.Tool.ExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task Recover_WhenNonReplayableCompletionIsMissing_ShouldCommitOutcomeUncertain()
    {
        var fixture = await CreateFixtureAsync("role-non-replayable");
        await fixture.StartSessionAsync("session-a", new RoleChatRunContext
        {
            CompletionNotificationActorId = "service-run:session-a",
            CompletionNotificationDeliveryId = "delivery-session-a",
            CompletionNotificationExpiresAtUnixMs = Now.AddMinutes(1).ToUnixTimeMilliseconds(),
        });
        await fixture.Agent.PrepareBatchAsync(Batch(
            "session-a",
            0,
            Operation("call-a", fixture.Tool, AgentToolReplayPolicy.NonReplayable)));
        var checkpoint = fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint.Clone();
        var recovery = new RoleChatRecoveryContinuationRequested
        {
            SessionId = "session-a",
            ExpectedCheckpointGeneration = checkpoint.Generation,
        };

        await fixture.Agent.HandleChatRecoveryContinuationRequestedAsync(recovery);

        var session = fixture.Agent.State.Sessions["session-a"];
        session.Completed.Should().BeTrue();
        session.Outcome.Should().Be(RoleChatSessionOutcome.OutcomeUncertain);
        session.FailureCode.Should().Be("SESSION_OUTCOME_UNCERTAIN");
        session.CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Dispatched);
        fixture.Publisher.Published.OfType<RoleChatSessionCompletedEvent>()
            .Should().ContainSingle(notification =>
                notification.SessionId == "session-a" &&
                notification.Outcome == RoleChatSessionOutcome.OutcomeUncertain);

        await fixture.Agent.HandleChatRecoveryContinuationRequestedAsync(recovery);

        fixture.Publisher.Published.OfType<RoleChatSessionCompletedEvent>()
            .Should().ContainSingle(notification => notification.SessionId == "session-a");
        fixture.ExecutionPort.Requests.Should().BeEmpty();
        fixture.Tool.ExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task Recover_WhenOutcomeUncertainNotificationSendFails_ShouldScheduleRetry()
    {
        var publisher = new RecordingPublisher { FailCompletionNotification = true };
        var fixture = await CreateFixtureAsync(
            "role-non-replayable-notification-retry",
            publisher: publisher);
        await fixture.StartSessionAsync("session-a", new RoleChatRunContext
        {
            CompletionNotificationActorId = "service-run:session-a",
            CompletionNotificationDeliveryId = "delivery-session-a",
            CompletionNotificationExpiresAtUnixMs = Now.AddMinutes(1).ToUnixTimeMilliseconds(),
        });
        await fixture.Agent.PrepareBatchAsync(Batch(
            "session-a",
            0,
            Operation("call-a", fixture.Tool, AgentToolReplayPolicy.NonReplayable)));
        var checkpoint = fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint.Clone();

        await fixture.Agent.HandleChatRecoveryContinuationRequestedAsync(
            new RoleChatRecoveryContinuationRequested
            {
                SessionId = "session-a",
                ExpectedCheckpointGeneration = checkpoint.Generation,
            });

        var session = fixture.Agent.State.Sessions["session-a"];
        session.Completed.Should().BeTrue();
        session.Outcome.Should().Be(RoleChatSessionOutcome.OutcomeUncertain);
        session.CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.RetryScheduled);
        session.CompletionNotificationAttempt.Should().Be(1);
        session.CompletionNotificationRetryCallbackId.Should().NotBeNullOrWhiteSpace();
        fixture.ExecutionPort.Requests.Should().BeEmpty();
        fixture.Tool.ExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task RecoveryGeneration_WhenStale_ShouldBeNoOpBeforeRecoveryWork()
    {
        var fixture = await CreateFixtureAsync("role-stale-generation");
        await fixture.StartSessionAsync("session-a");
        var current = fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint.Clone();
        var staleUpdate = new RoleChatRecoveryCheckpointUpdatedEvent
        {
            SessionId = "session-a",
            ExpectedGeneration = current.Generation - 1,
            Checkpoint = new RoleChatRecoveryCheckpoint
            {
                Generation = current.Generation + 1,
                Stage = RoleChatRecoveryCheckpointStage.ToolBatchPrepared,
                PayloadExpiresAtUnixMs = current.PayloadExpiresAtUnixMs,
            },
        };

        await fixture.Agent.PersistForTestAsync(staleUpdate);
        fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint.Should().BeEquivalentTo(current);

        await fixture.Agent.HandleChatRecoveryContinuationRequestedAsync(new RoleChatRecoveryContinuationRequested
        {
            SessionId = "session-a",
            ExpectedCheckpointGeneration = current.Generation - 1,
        });

        fixture.Agent.State.Sessions["session-a"].Completed.Should().BeFalse();
        fixture.ExecutionPort.Requests.Should().BeEmpty();
        fixture.Publisher.Published.OfType<RoleChatRecoveryContinuationRequested>().Should().BeEmpty();
    }

    [Fact]
    public async Task MultiToolBatch_ShouldRemainPreparedUntilEveryCompletionIsCommitted()
    {
        var fixture = await CreateFixtureAsync("role-multi-tool");
        await fixture.StartSessionAsync("session-a");
        var prepared = await fixture.Agent.PrepareBatchAsync(Batch(
            "session-a",
            3,
            Operation("call-a", fixture.Tool, AgentToolReplayPolicy.ReadOnlyRetryable),
            Operation("call-b", fixture.Tool, AgentToolReplayPolicy.ReadOnlyRetryable)));

        fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint.Stage
            .Should().Be(RoleChatRecoveryCheckpointStage.ToolBatchPrepared);

        await fixture.Agent.CommitCompletionAsync(
            prepared[0],
            new ToolExecutionResult("call-a", fixture.Tool.Name, "{\"value\":1}", false));
        fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint.Stage
            .Should().Be(RoleChatRecoveryCheckpointStage.ToolBatchPrepared);

        await fixture.Agent.CommitCompletionAsync(
            prepared[1],
            new ToolExecutionResult("call-b", fixture.Tool.Name, "{\"value\":2}", false));
        var checkpoint = fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint;
        checkpoint.Stage.Should().Be(RoleChatRecoveryCheckpointStage.ModelReady);
        checkpoint.ToolCompletions.Select(static completion => completion.OperationId)
            .Should().BeEquivalentTo(prepared.Select(static operation => operation.OperationId));
    }

    [Fact]
    public async Task CompletionAppendFailure_WhenRetryProducesDifferentResult_ShouldAdoptFirstSealedResult()
    {
        const string firstResult = "{\"attempt\":1}";
        const string secondResult = "{\"attempt\":2}";
        var store = new FailOnceCompletionCheckpointEventStore();
        var vault = CreateVault();
        var first = await CreateFixtureAsync("role-result-adoption", store, vault);
        await first.StartSessionAsync("session-a");
        var operation = (await first.Agent.PrepareBatchAsync(Batch(
            "session-a",
            0,
            Operation("call-a", first.Tool, AgentToolReplayPolicy.ReadOnlyRetryable)))).Single();

        var firstCommit = () => first.Agent.CommitCompletionAsync(
            operation,
            new ToolExecutionResult("call-a", first.Tool.Name, firstResult, false));
        await firstCommit.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("completion checkpoint append failed");

        var recovered = await CreateFixtureAsync(
            first.ActorId,
            store,
            vault,
            first.Tool,
            first.ExecutionPort);
        await recovered.Agent.CommitCompletionAsync(
            operation,
            new ToolExecutionResult("call-a", first.Tool.Name, secondResult, false));

        var completion = recovered.Agent.State.Sessions["session-a"].RecoveryCheckpoint
            .ToolCompletions.Should().ContainSingle().Which;
        completion.ResultSha256.Should().Be(AgentToolArgumentsDigest.ComputeSha256(firstResult));
        var restored = await InvokeRecoveredResultsAsync(
            recovered.Agent,
            "session-a",
            recovered.Agent.State.Sessions["session-a"].RecoveryCheckpoint,
            ToolContext(recovered.ActorId, "session-a"));
        ExtractRecoveredResult(restored).Should().Be(firstResult);
    }

    [Fact]
    public async Task HandleChatRequest_WhenExternalEffectCompletesButCheckpointAppendFails_ShouldRecoverSealedResult()
    {
        var store = new FailOnceCompletionCheckpointEventStore();
        var tool = new TestTool("side-effect", AgentToolReplayPolicy.NonReplayable);
        var executionPort = new RecordingExecutionPort(ExecutedOutcome("{\"effect\":true}"));
        var provider = new ToolThenTextProvider(tool.Name, "recovered after sealed result");
        var fixture = await CreateFixtureAsync(
            "role-full-turn-checkpoint-failure",
            store,
            tool: tool,
            executionPort: executionPort,
            providerFactory: provider);

        await fixture.Agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-a",
            Prompt = "perform the side effect",
        });

        var interrupted = fixture.Agent.State.Sessions["session-a"];
        interrupted.Completed.Should().BeFalse();
        interrupted.Outcome.Should().NotBe(RoleChatSessionOutcome.Failed);
        interrupted.RecoveryCheckpoint.Should().NotBeNull();
        interrupted.RecoveryCheckpoint.Stage.Should().Be(RoleChatRecoveryCheckpointStage.ToolBatchPrepared);
        interrupted.RecoveryCheckpoint.ToolCompletions.Should().BeEmpty();
        executionPort.Requests.Should().ContainSingle();
        provider.StreamCallCount.Should().Be(1);

        var recovery = fixture.Publisher.Published.OfType<RoleChatRecoveryContinuationRequested>()
            .Should().ContainSingle().Which;
        await fixture.Agent.HandleChatRecoveryContinuationRequestedAsync(recovery);

        var completed = fixture.Agent.State.Sessions["session-a"];
        completed.Completed.Should().BeTrue();
        completed.Outcome.Should().Be(RoleChatSessionOutcome.Completed);
        completed.FinalContent.Should().Be("recovered after sealed result");
        executionPort.Requests.Should().ContainSingle(
            "the recovery must adopt the sealed result instead of invoking the side effect again");
        provider.StreamCallCount.Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_WhenCommittedResultReferenceIsCorruptOrMissing_ShouldFinalizeOutcomeUncertain(
        bool missingReference)
    {
        var fixture = await CreateFixtureAsync($"role-invalid-result-ref-{missingReference}");
        await fixture.StartSessionAsync("session-a");
        var operation = (await fixture.Agent.PrepareBatchAsync(Batch(
            "session-a",
            0,
            Operation("call-a", fixture.Tool, AgentToolReplayPolicy.ReadOnlyRetryable)))).Single();
        await fixture.Agent.CommitCompletionAsync(
            operation,
            new ToolExecutionResult("call-a", fixture.Tool.Name, "{\"ok\":true}", false));
        var current = fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint;
        var invalid = current.Clone();
        invalid.Generation++;
        invalid.Stage = RoleChatRecoveryCheckpointStage.ToolBatchPrepared;
        if (missingReference)
            invalid.ToolCompletions[0].ResultReference.Ref = "missing-result-reference";
        else
            invalid.ToolCompletions[0].ResultReference.Fingerprint = "sha256:corrupt";
        await fixture.Agent.PersistForTestAsync(new RoleChatRecoveryCheckpointUpdatedEvent
        {
            SessionId = "session-a",
            ExpectedGeneration = current.Generation,
            Checkpoint = invalid,
        });

        await fixture.Agent.HandleChatRecoveryContinuationRequestedAsync(new RoleChatRecoveryContinuationRequested
        {
            SessionId = "session-a",
            ExpectedCheckpointGeneration = invalid.Generation,
        });

        var session = fixture.Agent.State.Sessions["session-a"];
        session.Completed.Should().BeTrue();
        session.Outcome.Should().Be(RoleChatSessionOutcome.OutcomeUncertain);
        session.FailureCode.Should().Be("SESSION_OUTCOME_UNCERTAIN");
        fixture.ExecutionPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Recovery_WhenCheckpointPayloadExpired_ShouldReachActorOwnedOutcomeUncertainFinalizer()
    {
        var store = new InMemoryEventStoreForTests();
        var vault = CreateVault();
        var first = await CreateFixtureAsync("role-expired-checkpoint", store, vault);
        await first.StartSessionAsync("session-a");
        await first.Agent.PrepareBatchAsync(Batch(
            "session-a",
            0,
            Operation("call-a", first.Tool, AgentToolReplayPolicy.ReadOnlyRetryable)));

        var publisher = new RecordingPublisher();
        var recovered = await CreateFixtureAsync(
            first.ActorId,
            store,
            vault,
            first.Tool,
            first.ExecutionPort,
            publisher,
            Now.AddHours(25));
        var finalizer = publisher.Published.OfType<RoleChatIncompleteSessionFinalizationRequested>()
            .Should().ContainSingle().Which;

        await recovered.Agent.HandleIncompleteSessionFinalizationRequestedAsync(finalizer);

        var session = recovered.Agent.State.Sessions["session-a"];
        session.Completed.Should().BeTrue();
        session.Outcome.Should().Be(RoleChatSessionOutcome.OutcomeUncertain);
        recovered.ExecutionPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadOnlyIntent_WhenCrashOccursBeforeExternalCall_ShouldExecuteOnceDuringRecovery()
    {
        var executionPort = new RecordingExecutionPort(ExecutedOutcome("{\"recovered\":true}"));
        var fixture = await CreateFixtureAsync(
            "role-readonly-pre-external",
            executionPort: executionPort);
        await fixture.StartSessionAsync("session-a");
        var operation = (await fixture.Agent.PrepareBatchAsync(Batch(
            "session-a",
            0,
            Operation("call-a", fixture.Tool, AgentToolReplayPolicy.ReadOnlyRetryable)))).Single();
        executionPort.Requests.Should().BeEmpty();
        fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint.ToolIntents
            .Should().ContainSingle(intent => intent.OperationId == operation.OperationId);

        var recovered = await InvokeRecoveredResultsAsync(
            fixture.Agent,
            "session-a",
            fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint,
            ToolContext(fixture.ActorId, "session-a"));

        recovered.Should().NotBeNull();
        executionPort.Requests.Should().ContainSingle();
        executionPort.Requests.Single().ExecutionAttemptKind
            .Should().Be(AgentToolExecutionAttemptKind.ActorRecovery);
        executionPort.Requests.Single().ExecutionContext.Request.OperationId
            .Should().Be(operation.OperationId);
        fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint.ToolCompletions
            .Should().ContainSingle(completion => completion.OperationId == operation.OperationId);
    }

    [Fact]
    public async Task NonReplayableEffect_WhenCrashOccursBeforeCompletionCommit_ShouldNotExecuteTwice()
    {
        var executionPort = new RecordingExecutionPort(ExecutedOutcome("{\"effect\":true}"));
        var fixture = await CreateFixtureAsync(
            "role-effect-pre-completion",
            executionPort: executionPort);
        await fixture.StartSessionAsync("session-a");
        var operation = (await fixture.Agent.PrepareBatchAsync(Batch(
            "session-a",
            0,
            Operation("call-a", fixture.Tool, AgentToolReplayPolicy.NonReplayable)))).Single();
        await executionPort.ExecuteAsync(new AgentToolExecutionRequest(
            fixture.Tool,
            operation.ToolCall.ArgumentsJson,
            operation.ExecutionContext,
            AgentToolApprovalContinuationMode.ActorOwned,
            null));

        await fixture.Agent.HandleChatRecoveryContinuationRequestedAsync(new RoleChatRecoveryContinuationRequested
        {
            SessionId = "session-a",
            ExpectedCheckpointGeneration = fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint.Generation,
        });

        executionPort.Requests.Should().ContainSingle();
        fixture.Agent.State.Sessions["session-a"].Outcome
            .Should().Be(RoleChatSessionOutcome.OutcomeUncertain);
    }

    [Fact]
    public async Task IdempotentRecovery_ShouldReuseStableOperationIdAndProduceOneExternalEffect()
    {
        var executionPort = RecordingExecutionPort.Idempotent();
        var fixture = await CreateFixtureAsync(
            "role-idempotent-recovery",
            executionPort: executionPort);
        await fixture.StartSessionAsync("session-a");
        var operation = (await fixture.Agent.PrepareBatchAsync(Batch(
            "session-a",
            0,
            Operation("call-a", fixture.Tool, AgentToolReplayPolicy.IdempotentRetryable)))).Single();
        var firstOutcome = await executionPort.ExecuteAsync(new AgentToolExecutionRequest(
            fixture.Tool,
            operation.ToolCall.ArgumentsJson,
            operation.ExecutionContext,
            AgentToolApprovalContinuationMode.ActorOwned,
            null));
        firstOutcome.TerminalInvoked.Should().BeTrue();

        await InvokeRecoveredResultsAsync(
            fixture.Agent,
            "session-a",
            fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint,
            ToolContext(fixture.ActorId, "session-a"));

        executionPort.Requests.Should().HaveCount(2);
        executionPort.ExternalEffectCount.Should().Be(1);
        executionPort.Requests.Select(request => request.ExecutionContext.Request.OperationId)
            .Should().OnlyContain(operationId => operationId == operation.OperationId);
        executionPort.Requests.Select(request => request.ExecutionContext.Request.IdempotencyKey)
            .Should().OnlyContain(idempotencyKey => idempotencyKey == operation.OperationId);
        fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint.ToolCompletions
            .Should().ContainSingle(completion => completion.OperationId == operation.OperationId);
    }

    [Fact]
    public async Task ModelStreamInterruption_ShouldResumeFromCommittedModelBoundaryWithoutTextEqualityPromise()
    {
        var provider = new CountingProviderFactory("resumed model output");
        var fixture = await CreateFixtureAsync(
            "role-model-stream-recovery",
            providerFactory: provider);
        await fixture.StartSessionAsync("session-a");
        await fixture.Agent.PersistForTestAsync(new RoleChatSessionProgressedEvent
        {
            SessionId = "session-a",
            Sequence = 1,
            TextDelta = new RoleChatTextDeltaProgress { Delta = "partial output before crash" },
        });

        await fixture.Agent.HandleChatRecoveryContinuationRequestedAsync(new RoleChatRecoveryContinuationRequested
        {
            SessionId = "session-a",
            ExpectedCheckpointGeneration = 1,
        });

        var session = fixture.Agent.State.Sessions["session-a"];
        session.Completed.Should().BeTrue();
        session.Outcome.Should().Be(RoleChatSessionOutcome.Completed);
        session.FinalContent.Should().Be("resumed model output");
        session.FinalContent.Should().NotBe("partial output before crash");
        session.LastProgressSequence.Should().BeGreaterThan(1);
        provider.StreamCallCount.Should().Be(1);
    }

    [Fact]
    public async Task StaleGeneration_WhenNormalCheckpointPathValidates_ShouldRejectBeforeAppend()
    {
        var store = new RecordingBatchEventStore();
        var fixture = await CreateFixtureAsync("role-stale-pre-append", store);
        await fixture.StartSessionAsync("session-a");
        var appendCount = store.AppendBatches.Count;
        var current = fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint;
        var staleNext = current.Clone();
        staleNext.Generation++;
        staleNext.Stage = RoleChatRecoveryCheckpointStage.ToolBatchPrepared;

        var act = () => InvokeCheckpointValidation(
            fixture.Agent,
            "session-a",
            current.Generation - 1,
            staleNext);

        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("The chat recovery checkpoint transition is stale or invalid.");
        store.AppendBatches.Should().HaveCount(appendCount);
    }

    [Fact]
    public async Task ApprovalContinuation_WhenSelfPublishFails_ShouldCommitResultAndClearAtomicallyForActivationRecovery()
    {
        var store = new RecordingBatchEventStore();
        var vault = CreateVault();
        var tool = new TestTool("approved-mutation", AgentToolReplayPolicy.NonReplayable);
        var executionPort = new RecordingExecutionPort(ExecutedOutcome("{\"approved\":true}"));
        var failingPublisher = new RecordingPublisher { FailRecoveryContinuation = true };
        var fixture = await CreateFixtureAsync(
            "role-approval-recovery",
            store,
            vault,
            tool,
            executionPort,
            failingPublisher);
        await fixture.StartSessionAsync("session-a");
        var prepared = await fixture.Agent.PrepareBatchAsync(Batch(
            "session-a",
            0,
            Operation("call-a", tool, AgentToolReplayPolicy.NonReplayable)));
        await fixture.Agent.CommitCompletionAsync(
            prepared.Single(),
            ApprovalRequiredResult("call-a", tool.Name));
        var pendingRequestId = fixture.Agent.State.PendingApproval!.RequestId;

        var act = () => fixture.Agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
        {
            RequestId = pendingRequestId,
            ContinuationTurnId = "session-a-continuation",
            Approved = true,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("self continuation publish failed");
        fixture.Agent.State.PendingApproval.Should().BeNull();
        var committedCheckpoint = fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint;
        committedCheckpoint.Stage.Should().Be(RoleChatRecoveryCheckpointStage.ContinuationPrepared);
        committedCheckpoint.ToolCompletions.Should().ContainSingle(completion =>
            completion.OperationId == prepared.Single().OperationId && completion.Success);

        var committed = await store.GetEventsAsync(fixture.ActorId);
        var checkpointEvent = committed
            .Where(stateEvent => stateEvent.EventData.Is(RoleChatRecoveryCheckpointUpdatedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatRecoveryCheckpointUpdatedEvent>())
            .Last();
        var clearEvent = committed
            .Where(stateEvent => stateEvent.EventData.Is(ClearPendingApprovalEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<ClearPendingApprovalEvent>())
            .Last();
        checkpointEvent.Checkpoint.Stage.Should().Be(RoleChatRecoveryCheckpointStage.ContinuationPrepared);
        clearEvent.RequestId.Should().Be(pendingRequestId);
        store.AppendBatches.Should().ContainSingle(batch =>
            batch.Any(stateEvent =>
                stateEvent.EventData.Is(RoleChatRecoveryCheckpointUpdatedEvent.Descriptor) &&
                stateEvent.EventData.Unpack<RoleChatRecoveryCheckpointUpdatedEvent>().Checkpoint.Stage ==
                RoleChatRecoveryCheckpointStage.ContinuationPrepared) &&
            batch.Any(stateEvent =>
                stateEvent.EventData.Is(ClearPendingApprovalEvent.Descriptor) &&
                stateEvent.EventData.Unpack<ClearPendingApprovalEvent>().RequestId == pendingRequestId));

        var recoveredPublisher = new RecordingPublisher();
        var recovered = await CreateFixtureAsync(
            fixture.ActorId,
            store,
            vault,
            tool,
            executionPort,
            recoveredPublisher);

        recovered.Agent.State.PendingApproval.Should().BeNull();
        recoveredPublisher.Published.OfType<RoleChatRecoveryContinuationRequested>()
            .Should().ContainSingle(request =>
                request.SessionId == "session-a" &&
                request.OperationId == committedCheckpoint.PendingOperationId &&
                request.ExpectedCheckpointGeneration == committedCheckpoint.Generation);
    }

    [Fact]
    public async Task ApprovalRecovery_WhenToolOutcomeIsUncertain_ShouldFinalizeSourceSessionAndClearPending()
    {
        var store = new RecordingBatchEventStore();
        var tool = new TestTool("uncertain-approved-mutation", AgentToolReplayPolicy.NonReplayable);
        var executionPort = new RecordingExecutionPort(OutcomeUncertain("call-a", tool.Name));
        var fixture = await CreateFixtureAsync(
            "role-approval-outcome-uncertain",
            store,
            tool: tool,
            executionPort: executionPort);
        await fixture.StartSessionAsync("session-a");
        var operation = (await fixture.Agent.PrepareBatchAsync(Batch(
            "session-a",
            0,
            Operation("call-a", tool, AgentToolReplayPolicy.NonReplayable)))).Single();
        await fixture.Agent.CommitCompletionAsync(
            operation,
            ApprovalRequiredResult("call-a", tool.Name));
        var pendingRequestId = fixture.Agent.State.PendingApproval!.RequestId;

        await fixture.Agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
        {
            RequestId = pendingRequestId,
            ContinuationTurnId = "session-a-continuation",
            Approved = true,
        });

        var source = fixture.Agent.State.Sessions["session-a"];
        source.Completed.Should().BeTrue();
        source.Outcome.Should().Be(RoleChatSessionOutcome.OutcomeUncertain);
        source.FailureCode.Should().Be("SESSION_OUTCOME_UNCERTAIN");
        fixture.Agent.State.PendingApproval.Should().BeNull();
        fixture.Agent.State.Sessions.Should().NotContainKey("session-a-continuation");
        executionPort.Requests.Should().ContainSingle(request =>
            request.ExecutionAttemptKind == AgentToolExecutionAttemptKind.ActorRecovery);

        var committed = await store.GetEventsAsync(fixture.ActorId);
        committed.Should().NotContain(stateEvent =>
            stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor) &&
            stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>().FailureCode ==
            "approval_continuation_failed");
        store.AppendBatches.Should().ContainSingle(batch =>
            batch.Any(stateEvent =>
                stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor) &&
                stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>().SessionId == "session-a" &&
                stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>().Outcome ==
                RoleChatSessionOutcome.OutcomeUncertain) &&
            batch.Any(stateEvent =>
                stateEvent.EventData.Is(ClearPendingApprovalEvent.Descriptor) &&
                stateEvent.EventData.Unpack<ClearPendingApprovalEvent>().RequestId == pendingRequestId));
    }

    [Fact]
    public async Task ActorRecovery_WhenPrimaryCredentialIsSourceReadable_ShouldRestorePrimaryAccessTokenSlot()
    {
        const string token = "source-readable-primary";
        var vault = CreateVault();
        var tool = new TestTool("source-readable-tool", AgentToolReplayPolicy.ReadOnlyRetryable);
        var executionPort = new RecordingExecutionPort(ExecutedOutcome("{\"ok\":true}"));
        var provider = new CountingProviderFactory("credential recovery completed");
        var fixture = await CreateFixtureAsync(
            "role-source-readable-recovery",
            vault: vault,
            tool: tool,
            executionPort: executionPort,
            providerFactory: provider);
        var context = ToolContext(fixture.ActorId, "session-a") with
        {
            Credentials = new AgentToolCredentials(
                token,
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
        };
        var durableReference = await StoreDurableCredentialAsync(
            vault,
            CredentialSecretPurposes.WorkflowCallerSourceReadableUserBearerToken,
            token);
        await StartCredentialSessionAsync(fixture, "session-a", context, durableReference);
        await fixture.Agent.PrepareBatchAsync(new ChatToolBatchIntent(
            "session-a",
            0,
            [new ChatToolOperationIntent(
                ToolCall("call-a", tool.Name, "{}"),
                context,
                AgentToolReplayPolicy.ReadOnlyRetryable,
                ToolPresentationDescriptors.Generic(tool.Name, tool.Description))]));

        await fixture.Agent.HandleChatRecoveryContinuationRequestedAsync(new RoleChatRecoveryContinuationRequested
        {
            SessionId = "session-a",
            ExpectedCheckpointGeneration =
                fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint.Generation,
        });

        var request = executionPort.Requests.Should().ContainSingle().Which;
        request.ExecutionAttemptKind.Should().Be(AgentToolExecutionAttemptKind.ActorRecovery);
        request.ExecutionContext.Credentials.NyxIdCredentialKind.Should()
            .Be(AgentToolNyxIdCredentialKind.SourceReadableUserBearer);
        request.ExecutionContext.Credentials.NyxIdAccessToken.Should().Be(token);
        fixture.Agent.State.Sessions["session-a"].Outcome.Should().Be(RoleChatSessionOutcome.Completed);
    }

    [Fact]
    public async Task ActorRecovery_WhenProxyDelegationSupplementalCredentialHasNoSealedReference_ShouldFailClosed()
    {
        const string delegationToken = "proxy-delegation-primary";
        const string supplementalToken = "source-readable-supplemental";
        var vault = CreateVault();
        var tool = new TestTool("delegated-tool", AgentToolReplayPolicy.ReadOnlyRetryable);
        var executionPort = new RecordingExecutionPort(ExecutedOutcome("{\"must_not_run\":true}"));
        var fixture = await CreateFixtureAsync(
            "role-proxy-supplemental-recovery",
            vault: vault,
            tool: tool,
            executionPort: executionPort);
        var context = ToolContext(fixture.ActorId, "session-a") with
        {
            Credentials = new AgentToolCredentials(
                delegationToken,
                null,
                null,
                AgentToolNyxIdCredentialKind.ProxyDelegation,
                supplementalToken),
        };
        var durableReference = await StoreDurableCredentialAsync(
            vault,
            CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
            delegationToken);
        await StartCredentialSessionAsync(fixture, "session-a", context, durableReference);
        await fixture.Agent.PrepareBatchAsync(new ChatToolBatchIntent(
            "session-a",
            0,
            [new ChatToolOperationIntent(
                ToolCall("call-a", tool.Name, "{}"),
                context,
                AgentToolReplayPolicy.ReadOnlyRetryable,
                ToolPresentationDescriptors.Generic(tool.Name, tool.Description))]));

        await fixture.Agent.HandleChatRecoveryContinuationRequestedAsync(new RoleChatRecoveryContinuationRequested
        {
            SessionId = "session-a",
            ExpectedCheckpointGeneration =
                fixture.Agent.State.Sessions["session-a"].RecoveryCheckpoint.Generation,
        });

        var session = fixture.Agent.State.Sessions["session-a"];
        session.Completed.Should().BeTrue();
        session.Outcome.Should().Be(RoleChatSessionOutcome.OutcomeUncertain);
        session.FailureCode.Should().Be("SESSION_OUTCOME_UNCERTAIN");
        executionPort.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("other-actor", "session-a", "operation-a", false)]
    [InlineData("actor-a", "other-session", "operation-a", false)]
    [InlineData("actor-a", "session-a", "other-operation", false)]
    [InlineData("actor-a", "session-a", "operation-a", true)]
    public async Task RecoveryPayloadReference_WhenScopeOrLifetimeMismatches_ShouldFailClosed(
        string actorId,
        string sessionId,
        string operationId,
        bool expired)
    {
        var store = new SecretVaultChatToolRecoveryPayloadStore(new InMemorySecretVault());
        var reference = await store.StoreAsync(
            "actor-a",
            "session-a",
            "operation-a",
            ChatToolRecoveryPayloadKind.Arguments,
            "{\"secret\":true}",
            DateTimeOffset.UtcNow.AddHours(1));
        var boundary = expired
            ? DateTimeOffset.FromUnixTimeMilliseconds(reference.ExpiresAtUnixMs)
            : DateTimeOffset.UtcNow;

        var act = () => store.ResolveAsync(
            reference,
            actorId,
            sessionId,
            operationId,
            ChatToolRecoveryPayloadKind.Arguments,
            boundary);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void RecoveryCheckpointPayload_ShouldNotPersistBearerOrOpenMetadata()
    {
        const string bearer = "bearer-super-secret";
        const string orgToken = "org-super-secret";
        const string senderToken = "sender-super-secret";
        const string metadataSecret = "metadata-super-secret";
        var context = ToolContext("actor-a", "session-a", bearer) with
        {
            Credentials = new AgentToolCredentials(bearer, orgToken, senderToken),
            ExternalMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["private"] = metadataSecret,
            },
            SkillRecovery = new AgentSkillRecoveryContext(
                true,
                true,
                "command",
                "original-command-with-secret",
                "skill",
                2,
                "arguments-with-secret",
                true),
        };

        var payloadBytes = context.ToRecoveryPayload().ToByteArray();
        var persisted = System.Text.Encoding.UTF8.GetString(payloadBytes);

        persisted.Should().NotContain(bearer)
            .And.NotContain(orgToken)
            .And.NotContain(senderToken)
            .And.NotContain(metadataSecret)
            .And.NotContain("original-command-with-secret")
            .And.NotContain("arguments-with-secret");
    }

    private static ChatToolBatchIntent Batch(
        string sessionId,
        int round,
        params ChatToolOperationIntent[] operations) =>
        new(sessionId, round, operations);

    private static ChatToolOperationIntent Operation(
        string callId,
        IAgentTool tool,
        AgentToolReplayPolicy replayPolicy) =>
        new(
            ToolCall(callId, tool.Name, "{\"input\":\"safe\"}"),
            ToolContext("unused", "session-a"),
            replayPolicy,
            ToolPresentationDescriptors.Generic(tool.Name, tool.Description));

    private static ToolCall ToolCall(string callId, string toolName, string argumentsJson) => new()
    {
        Id = callId,
        Name = toolName,
        ArgumentsJson = argumentsJson,
    };

    private static AgentToolExecutionContext ToolContext(
        string actorId,
        string sessionId,
        string? bearer = null) =>
        AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(sessionId, null, null, Now.ToUnixTimeMilliseconds()),
            Credentials = new AgentToolCredentials(bearer, null, null),
            Caller = new AgentToolCallerContext("scope-a", "subject-a", null),
            ExecutionOwner = AgentToolExecutionOwners.Actor(actorId),
        };

    private static AgentToolExecutionOutcome ExecutedOutcome(string result) => new(
        AgentToolExecutionOutcomeKind.Executed,
        result,
        new AgentToolReceipt
        {
            Status = AgentToolReceiptStatus.Success,
            ResultJson = result,
        },
        IsMutation: true,
        FailureCode: string.Empty,
        SafeMessage: string.Empty,
        AgentToolExecutionFailureStage.None,
        TerminalInvoked: true,
        Retryable: false,
        AuditCompleted: true);

    private static AgentToolExecutionOutcome OutcomeUncertain(string callId, string toolName) => new(
        AgentToolExecutionOutcomeKind.Failed,
        "{\"outcome_uncertain\":true}",
        new AgentToolReceipt
        {
            CallId = callId,
            ToolName = toolName,
            Status = AgentToolReceiptStatus.Error,
            ErrorCode = "outcome_uncertain",
        },
        IsMutation: true,
        FailureCode: "outcome_uncertain",
        SafeMessage: "The external effect may have completed.",
        AgentToolExecutionFailureStage.TerminalExecution,
        TerminalInvoked: true,
        Retryable: false,
        AuditCompleted: false);

    private static InMemorySecretVault CreateVault() =>
        new(new FakeTimeProvider(Now));

    private static ToolExecutionResult ApprovalRequiredResult(string callId, string toolName) => new(
        callId,
        toolName,
        "{\"approval_required\":true}",
        true,
        new AgentToolReceipt
        {
            CallId = callId,
            ToolName = toolName,
            Status = AgentToolReceiptStatus.ApprovalRequired,
            ApprovalRequestId = "approval-a",
            IsDestructive = true,
            ResultJson = "{\"approval_required\":true}",
        });

    private static async Task<object?> InvokeRecoveredResultsAsync(
        RoleGAgent agent,
        string sessionId,
        RoleChatRecoveryCheckpoint checkpoint,
        AgentToolExecutionContext context)
    {
        var method = typeof(RoleGAgent).GetMethod(
            "RecoverCheckpointToolResultsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RecoverCheckpointToolResultsAsync not found.");
        var task = (Task)(method.Invoke(agent, [sessionId, checkpoint, context, CancellationToken.None])
                          ?? throw new InvalidOperationException("Recovery invocation returned null."));
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private static string ExtractRecoveredResult(object? recovered)
    {
        var item = ((System.Collections.IEnumerable)(recovered
                   ?? throw new InvalidOperationException("Recovered results are missing.")))
            .Cast<object>()
            .Single();
        return (string)(item.GetType().GetProperty("Result")!.GetValue(item)
                        ?? throw new InvalidOperationException("Recovered result is missing."));
    }

    private static void InvokeCheckpointValidation(
        RoleGAgent agent,
        string sessionId,
        long expectedGeneration,
        RoleChatRecoveryCheckpoint checkpoint)
    {
        var method = typeof(RoleGAgent).GetMethod(
            "ValidateCheckpointUpdate",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ValidateCheckpointUpdate not found.");
        method.Invoke(agent, [sessionId, expectedGeneration, checkpoint]);
    }

    private static async Task<DurableCallerCredentialRef> StoreDurableCredentialAsync(
        InMemorySecretVault vault,
        string purpose,
        string token)
    {
        const string ownerScopeKey = "credential-owner-a";
        const string subjectId = "credential-subject-a";
        var stored = await vault.PutAsync(new StoreSecretRequest(
            purpose,
            ownerScopeKey,
            subjectId,
            token,
            "role recovery test credential",
            Now.AddHours(24)));
        return new DurableCallerCredentialRef
        {
            Ref = stored.Reference.Ref,
            Purpose = purpose,
            OwnerScopeKey = ownerScopeKey,
            SubjectId = subjectId,
            SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
        };
    }

    private static Task StartCredentialSessionAsync(
        Fixture fixture,
        string sessionId,
        AgentToolExecutionContext context,
        DurableCallerCredentialRef durableReference) =>
        fixture.Agent.PersistForTestAsync(new RoleChatSessionStartedEvent
        {
            SessionId = sessionId,
            Prompt = "credential recovery",
            ScopeId = "scope-a",
            RecoveryCheckpoint = new RoleChatRecoveryCheckpoint
            {
                Generation = 1,
                Stage = RoleChatRecoveryCheckpointStage.ModelReady,
                RecoveryContext = context.ToRecoveryPayload(),
                CallerDurableCredential = durableReference.Clone(),
                RequiresRuntimeCredential = true,
                PayloadExpiresAtUnixMs = Now.AddHours(24).ToUnixTimeMilliseconds(),
            },
        });

    private static async Task<Fixture> CreateFixtureAsync(
        string actorId,
        IEventStore? store = null,
        InMemorySecretVault? vault = null,
        TestTool? tool = null,
        RecordingExecutionPort? executionPort = null,
        RecordingPublisher? publisher = null,
        DateTimeOffset? now = null,
        ILLMProviderFactory? providerFactory = null)
    {
        store ??= new InMemoryEventStoreForTests();
        var timeProvider = new FakeTimeProvider(now ?? Now);
        vault ??= new InMemorySecretVault(timeProvider);
        tool ??= new TestTool("tool-a", AgentToolReplayPolicy.NonReplayable);
        executionPort ??= new RecordingExecutionPort(ExecutedOutcome("{\"ok\":true}"));
        publisher ??= new RecordingPublisher();
        var scheduler = new RecordingScheduler();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler>(scheduler)
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
        var agent = new TestRoleGAgent(
            executionPort,
            [new StaticToolSource([tool])],
            timeProvider,
            vault,
            providerFactory)
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
            EventPublisher = publisher,
        };
        typeof(GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, [actorId]);
        await agent.ActivateAsync();
        return new Fixture(actorId, agent, store, vault, tool, executionPort, publisher, services);
    }

    private sealed record Fixture(
        string ActorId,
        TestRoleGAgent Agent,
        IEventStore Store,
        InMemorySecretVault Vault,
        TestTool Tool,
        RecordingExecutionPort ExecutionPort,
        RecordingPublisher Publisher,
        ServiceProvider Services)
    {
        public Task StartSessionAsync(string sessionId, RoleChatRunContext? runContext = null) =>
            Agent.PersistForTestAsync(
                new RoleChatSessionStartedEvent
                {
                    SessionId = sessionId,
                    Prompt = "prompt",
                    ScopeId = "scope-a",
                    RunContext = runContext?.Clone(),
                    RecoveryCheckpoint = new RoleChatRecoveryCheckpoint
                    {
                        Generation = 1,
                        Stage = RoleChatRecoveryCheckpointStage.ModelReady,
                        RecoveryContext = ToolContext(ActorId, sessionId).ToRecoveryPayload(),
                        PayloadExpiresAtUnixMs = Now.AddHours(24).ToUnixTimeMilliseconds(),
                    },
                });
    }

    private sealed class TestRoleGAgent(
        IAgentToolExecutionPort executionPort,
        IEnumerable<IAgentToolSource> toolSources,
        TimeProvider timeProvider,
        ISecretVault vault,
        ILLMProviderFactory? providerFactory)
        : RoleGAgent(
            executionPort,
            llmProviderFactory: providerFactory,
            toolSources: toolSources,
            timeProvider: timeProvider,
            chatToolRecoverySecretVault: vault)
    {
        public Task PersistForTestAsync(IMessage evt) => PersistDomainEventAsync(evt);
    }

    private sealed class TestTool(string name, AgentToolReplayPolicy replayPolicy) : IAgentTool
    {
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";
        public int ExecutionCount { get; private set; }
        public AgentToolReplayPolicy ResolveReplayPolicy(string argumentsJson) => replayPolicy;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecutionCount++;
            return Task.FromResult("{\"executed\":true}");
        }
    }

    private sealed class StaticToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class RecordingExecutionPort : IAgentToolExecutionPort
    {
        private readonly Func<AgentToolExecutionRequest, AgentToolExecutionOutcome> _execute;
        private readonly Dictionary<string, AgentToolExecutionOutcome> _idempotentOutcomes =
            new(StringComparer.Ordinal);

        public RecordingExecutionPort(AgentToolExecutionOutcome outcome)
            : this(_ => outcome)
        {
        }

        private RecordingExecutionPort(Func<AgentToolExecutionRequest, AgentToolExecutionOutcome> execute)
        {
            _execute = execute;
        }

        public List<AgentToolExecutionRequest> Requests { get; } = [];
        public int ExternalEffectCount { get; private set; }

        public static RecordingExecutionPort Idempotent()
        {
            RecordingExecutionPort? port = null;
            port = new RecordingExecutionPort(request =>
            {
                var key = request.ExecutionContext.Request.IdempotencyKey
                          ?? throw new InvalidOperationException("Idempotency key is required.");
                if (port!._idempotentOutcomes.TryGetValue(key, out var existing))
                    return existing;

                port.ExternalEffectCount++;
                var outcome = ExecutedOutcome($"{{\"effect\":{port.ExternalEffectCount}}}");
                port._idempotentOutcomes[key] = outcome;
                return outcome;
            });
            return port;
        }

        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(_execute(request));
        }
    }

    private sealed class CountingProviderFactory(string response) : ILLMProviderFactory, ILLMProvider
    {
        public int StreamCallCount { get; private set; }
        public string Name => "recovery-test";
        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StreamCallCount++;
            yield return new LLMStreamChunk { DeltaContent = response };
            await Task.CompletedTask;
            yield return new LLMStreamChunk
            {
                IsLast = true,
                Usage = new TokenUsage(1, 1, 2),
            };
        }
    }

    private sealed class ToolThenTextProvider(string toolName, string response)
        : ILLMProviderFactory, ILLMProvider
    {
        public int StreamCallCount { get; private set; }
        public string Name => "tool-then-text";
        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            StreamCallCount++;
            if (StreamCallCount == 1)
            {
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = ToolCall("call-a", toolName, "{\"input\":\"safe\"}"),
                };
            }
            else
            {
                yield return new LLMStreamChunk { DeltaContent = response };
            }

            await Task.CompletedTask;
            yield return new LLMStreamChunk
            {
                IsLast = true,
                Usage = new TokenUsage(1, 1, 2),
            };
        }
    }

    private sealed class ThrowingCheckpointPort : IChatToolCheckpointPort
    {
        public Task<IReadOnlyList<PreparedChatToolOperation>> PrepareBatchAsync(
            ChatToolBatchIntent batch,
            CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<PreparedChatToolOperation>>(
                new InvalidOperationException("intent commit failed"));

        public Task CommitCompletionAsync(
            PreparedChatToolOperation operation,
            ToolExecutionResult result,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public bool FailRecoveryContinuation { get; init; }
        public bool FailCompletionNotification { get; init; }
        public List<IMessage> Published { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            if (FailRecoveryContinuation && evt is RoleChatRecoveryContinuationRequested)
                throw new InvalidOperationException("self continuation publish failed");
            Published.Add(evt);
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            if (FailCompletionNotification && evt is RoleChatSessionCompletedEvent)
                throw new InvalidOperationException("completion notification send failed");
            return PublishAsync(evt, TopologyAudience.Self, ct, sourceEnvelope, options);
        }
    }

    private sealed class RecordingScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingBatchEventStore : IEventStore
    {
        private readonly InMemoryEventStoreForTests _inner = new();

        public List<IReadOnlyList<StateEvent>> AppendBatches { get; } = [];

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var batch = events.Select(static stateEvent => stateEvent.Clone()).ToArray();
            AppendBatches.Add(batch);
            return _inner.AppendAsync(agentId, batch, expectedVersion, ct);
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            _inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            _inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            _inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }

    private sealed class FailOnceCompletionCheckpointEventStore : IEventStore
    {
        private readonly InMemoryEventStoreForTests _inner = new();
        private bool _failed;

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var batch = events.Select(static stateEvent => stateEvent.Clone()).ToArray();
            if (!_failed && batch.Any(stateEvent =>
                    stateEvent.EventData.Is(RoleChatRecoveryCheckpointUpdatedEvent.Descriptor) &&
                    stateEvent.EventData.Unpack<RoleChatRecoveryCheckpointUpdatedEvent>()
                        .Checkpoint.ToolCompletions.Count > 0))
            {
                _failed = true;
                throw new InvalidOperationException("completion checkpoint append failed");
            }

            return _inner.AppendAsync(agentId, batch, expectedVersion, ct);
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            _inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            _inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            _inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }
}
