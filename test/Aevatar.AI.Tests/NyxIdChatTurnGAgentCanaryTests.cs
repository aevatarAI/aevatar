using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed partial class NyxIdChatTurnGAgentTests
{
    [Fact]
    public async Task CanaryEffectFault_ShouldConsumeDeniedPerRequestResultAndCommitAmbiguousWaterline()
    {
        var executor = new RecordingOperationExecutor(command =>
            command.InputCase == NyxIdChatOperationDispatchCommand.InputOneofCase.Llm
                ? new NyxIdChatOperationResultSignal
                {
                    Key = command.Key.Clone(),
                    Llm = new NyxIdChatLLMOperationResult { Content = "Plan ready." },
                }
                : DeniedPerRequestResult(command.Key));
        var eventStore = new InMemoryEventStoreForTests();
        var operationDispatch = new RecordingOperationDispatchPort(executor);
        var failFirstConsumedAcknowledgement = true;
        var actorDispatch = new RecordingDispatchPort((_, envelope) =>
        {
            if (failFirstConsumedAcknowledgement &&
                envelope.Payload.Is(NyxIdChatCanaryEffectFaultConsumedSignal.Descriptor))
            {
                failFirstConsumedAcknowledgement = false;
                throw new InvalidOperationException("simulated consumed acknowledgement outage");
            }

            return Task.CompletedTask;
        });
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateAgent(services, operationDispatch, actorDispatch);
        await agent.ActivateAsync();
        var initial = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey(),
            Llm = new NyxIdChatLLMOperationInput
            {
                Request = new ChatRequestEvent
                {
                    Prompt = "prepare the exact canary plan",
                    SessionId = "turn-alpha",
                },
            },
        };
        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", initial));
        await operationDispatch.DeliverPendingSignalsAsync(agent);

        var admission = ExactWriteAdmission();
        admission.ReadBack = new AgentToolOperationReadBackPayload
        {
            ReadOperation = ExactReadAdmission(),
            Arguments = new Struct(),
            CheckName = "resource-visible",
            Assertion = new AgentToolReadBackAssertionPayload
            {
                Match = AgentToolReadBackMatchPayload.Exists,
                JsonPointer = "/data",
            },
        };
        admission.ReadBack.ReadOperation.CatalogDigest = admission.CatalogDigest;
        var continuation = PlanGateContinuation(
            NyxIdChatPlanGateDecisions.HashArguments("{\"value\":1}"));
        continuation.PlanGateContinuation.OperationAdmission = admission.Clone();
        continuation.PlanGateContinuation.ToolContext.Caller =
            new AgentToolCallerContextPayload
            {
                ScopeId = "scope-alpha",
                OwnerSubject = "owner-alpha",
                ResponseId = "plan-gate-alpha",
            };
        continuation.PlanGateContinuation.CanaryEffectFault = new NyxIdChatCanaryEffectFaultDirective
        {
            ArmId = "arm-alpha",
            ClientRequestId = "client-arm-alpha",
            Key = continuation.Key.Clone(),
            ServiceInstanceId = admission.ServiceInstanceId,
            CatalogDigest = admission.CatalogDigest,
            OwnerSubject = "owner-alpha",
            ExpiresAt = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 7, 24, 8, 5, 0, TimeSpan.Zero)),
        };
        var gateAdmission = CreatePlanGateAdmission(initial.Key, continuation);
        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", gateAdmission));
        var eventsBeforeCanary = (await eventStore.GetEventsAsync("turn-actor-alpha")).Count;

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", continuation));

        executor.Commands.Should().HaveCount(2);
        executor.Commands.Last().InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.PlanGateContinuation);
        agent.State.CanaryEffectFaultConsumed.Should().BeFalse(
            "the directive is consumed only after the typed denial result reaches the actor");
        agent.State.EffectDispatchWaterline.Should().Be(NyxIdChatEffectEvidence.MayHaveChanged);

        await operationDispatch.DeliverPendingSignalsAsync(agent);

        agent.State.CanaryEffectFaultConsumed.Should().BeTrue();
        agent.State.CanaryEffectFault.ArmId.Should().Be("arm-alpha");
        agent.State.EffectDispatchWaterline.Should().Be(NyxIdChatEffectEvidence.MayHaveChanged);
        agent.State.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.MayHaveChanged);
        var committed = (await eventStore.GetEventsAsync("turn-actor-alpha"))
            .Skip(eventsBeforeCanary)
            .ToArray();
        committed.Select(static item => item.EventData.TypeUrl).Should().Equal(
            Google.Protobuf.WellKnownTypes.Any.Pack(
                new NyxIdChatTurnOperationAdmittedEvent()).TypeUrl,
            Google.Protobuf.WellKnownTypes.Any.Pack(
                new NyxIdChatTurnEffectDispatchStartedEvent()).TypeUrl,
            Google.Protobuf.WellKnownTypes.Any.Pack(
                new NyxIdChatTurnCanaryEffectFaultTriggeredEvent()).TypeUrl,
            Google.Protobuf.WellKnownTypes.Any.Pack(
                new NyxIdChatTurnOperationCompletedEvent()).TypeUrl,
            Google.Protobuf.WellKnownTypes.Any.Pack(
                new NyxIdChatTurnOperationDeliveredEvent()).TypeUrl);
        var triggered = committed[2].EventData
            .Unpack<NyxIdChatTurnCanaryEffectFaultTriggeredEvent>();
        triggered.ArmId.Should().Be("arm-alpha");
        triggered.ApprovalRequestId.Should().Be("approval-real-alpha");
        triggered.ApprovalCallId.Should().Be("call-alpha");
        triggered.ApprovalToolName.Should().Be("tool-alpha");
        triggered.ApprovalTerminalOutcome.Should().Be(NyxIdApprovalTerminalOutcome.Rejected);
        var failure = actorDispatch.Calls
            .Where(call => call.Envelope.Payload.Is(NyxIdChatOperationResultSignal.Descriptor))
            .Select(call => call.Envelope.Payload.Unpack<NyxIdChatOperationResultSignal>())
            .Last();
        failure.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        failure.Failure.FailureCode.Should().Be(NyxIdChatTurnGAgent.CanaryEffectFaultCode);
        failure.Failure.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.MayHaveChanged);
        var consumed = actorDispatch.Calls
            .Where(call => call.Envelope.Payload.Is(
                NyxIdChatCanaryEffectFaultConsumedSignal.Descriptor))
            .Select(call => call.Envelope.Payload.Unpack<NyxIdChatCanaryEffectFaultConsumedSignal>())
            .Should().ContainSingle().Which;
        consumed.ArmId.Should().Be("arm-alpha");
        consumed.Key.Should().BeEquivalentTo(continuation.Key);
        consumed.TurnActorId.Should().Be("turn-actor-alpha");
        consumed.ConsumedAt.Should().Be(agent.State.CanaryEffectFaultConsumedAt);
        consumed.ApprovalCallId.Should().Be("call-alpha");
        consumed.ApprovalToolName.Should().Be("tool-alpha");
        consumed.ApprovalSubjectKind.Should().Be("nyxid.user-service");
        consumed.ApprovalSubjectId.Should().Be("connected-service-alpha");

        var recoveredDispatch = new RecordingDispatchPort();
        var recovered = CreateAgent(services, operationDispatch, recoveredDispatch);
        await recovered.ActivateAsync();

        recovered.State.CanaryEffectFaultConsumed.Should().BeTrue();
        recovered.State.ResultDelivered.Should().BeTrue();
        recoveredDispatch.Calls
            .Where(call => call.Envelope.Payload.Is(
                NyxIdChatCanaryEffectFaultConsumedSignal.Descriptor))
            .Select(call => call.Envelope.Payload.Unpack<NyxIdChatCanaryEffectFaultConsumedSignal>())
            .Should().ContainSingle().Which.Should().BeEquivalentTo(consumed);
        executor.Commands.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchPort_ShouldFallbackToNormalDeniedCompletionWhenCanarySignalFails(
        bool fallbackDispatchAlsoFails)
    {
        var command = EligibleCanaryCommand();
        var fallbackAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var actorDispatch = new RecordingDispatchPort((_, envelope) =>
        {
            if (envelope.Payload.Is(NyxIdChatCanaryEffectFaultTriggeredSignal.Descriptor))
                throw new InvalidOperationException("simulated canary signal outage");

            if (envelope.Payload.Is(NyxIdChatTurnOperationExecutionCompletedSignal.Descriptor))
            {
                fallbackAttempted.TrySetResult();
                if (fallbackDispatchAlsoFails)
                    throw new InvalidOperationException("simulated persistent actor dispatch outage");
            }

            return Task.CompletedTask;
        });
        var port = new NyxIdChatTurnOperationDispatchPort(
            new RecordingOperationExecutor(current => DeniedPerRequestResult(current.Key)),
            new UnavailableNyxIdChatTurnOperationReconciliationPort(),
            actorDispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                NyxIdChatTurnOperationDispatchPort>.Instance);

        await port.OpenSession().DispatchExecutionAsync(
            "turn-actor-alpha",
            command,
            "correlation-alpha",
            CancellationToken.None);
        await fallbackAttempted.Task;

        actorDispatch.Calls.Select(call => call.Envelope.Payload.TypeUrl).Should().Equal(
            Any.Pack(new NyxIdChatCanaryEffectFaultTriggeredSignal()).TypeUrl,
            Any.Pack(new NyxIdChatTurnOperationExecutionCompletedSignal()).TypeUrl);
        var fallback = actorDispatch.Calls[1].Envelope.Payload
            .Unpack<NyxIdChatTurnOperationExecutionCompletedSignal>();
        fallback.Source.Should().Be(NyxIdChatTurnOperationCompletionSource.Execution);
        fallback.Result.Should().BeEquivalentTo(DeniedPerRequestResult(command.Key));
    }

    [Fact]
    public async Task DispatchPort_ShouldSuppressNormalCompletionOnlyForExactCanaryDenialBoundary()
    {
        var command = EligibleCanaryCommand();
        var executor = new RecordingOperationExecutor(current =>
            DeniedPerRequestResult(current.Key));
        var dispatched = new TaskCompletionSource<EventEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var actorDispatch = new RecordingDispatchPort((actorId, envelope) =>
        {
            if (actorId == "turn-actor-alpha")
                dispatched.TrySetResult(envelope.Clone());
            return Task.CompletedTask;
        });
        var port = new NyxIdChatTurnOperationDispatchPort(
            executor,
            new UnavailableNyxIdChatTurnOperationReconciliationPort(),
            actorDispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                NyxIdChatTurnOperationDispatchPort>.Instance);

        await port.OpenSession().DispatchExecutionAsync(
            "turn-actor-alpha",
            command,
            "correlation-alpha",
            CancellationToken.None);

        var envelope = await dispatched.Task;
        envelope.Payload.Is(NyxIdChatCanaryEffectFaultTriggeredSignal.Descriptor).Should().BeTrue();
        var fault = envelope.Payload.Unpack<NyxIdChatCanaryEffectFaultTriggeredSignal>();
        fault.ArmId.Should().Be("arm-alpha");
        fault.DeniedResult.Should().BeEquivalentTo(DeniedPerRequestResult(command.Key));
        actorDispatch.Calls.Should().NotContain(call =>
            call.Envelope.Payload.Is(NyxIdChatTurnOperationExecutionCompletedSignal.Descriptor));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("mode")]
    [InlineData("request_id")]
    [InlineData("error_code")]
    [InlineData("effect")]
    [InlineData("outcome")]
    [InlineData("subject")]
    [InlineData("call")]
    [InlineData("tool")]
    [InlineData("key")]
    [InlineData("directive_service")]
    public void CanaryDenialBoundary_ShouldFailClosedForIncompleteTypedProof(string mismatch)
    {
        var command = EligibleCanaryCommand();
        var result = DeniedPerRequestResult(command.Key);
        switch (mismatch)
        {
            case "status":
                result.Tool.Receipt.Status = AgentToolReceiptStatus.Error;
                break;
            case "mode":
                result.Tool.Receipt.NyxIdApprovalDecisionMode = NyxIdApprovalDecisionMode.Grant;
                break;
            case "request_id":
                result.Tool.Receipt.ApprovalRequestId = string.Empty;
                break;
            case "error_code":
                result.Tool.Receipt.ErrorCode = "NYXID_APPROVAL_REQUIRED";
                break;
            case "effect":
                result.Tool.ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged;
                break;
            case "outcome":
                result.Tool.Receipt.NyxIdApprovalTerminalOutcome =
                    NyxIdApprovalTerminalOutcome.Expired;
                break;
            case "subject":
                result.Tool.Receipt.SubjectId = "connected-service-beta";
                break;
            case "call":
                result.Tool.Receipt.CallId = "call-beta";
                break;
            case "tool":
                result.Tool.Receipt.ToolName = "tool-beta";
                break;
            case "key":
                result.Key.OperationId = "operation-beta";
                break;
            case "directive_service":
                command.PlanGateContinuation.CanaryEffectFault.ServiceInstanceId =
                    "connected-service-beta";
                break;
        }

        NyxIdChatTurnOperationDispatchPort.IsCanaryEffectFaultBoundaryResult(command, result)
            .Should().BeFalse();
    }

    private static NyxIdChatOperationResultSignal DeniedPerRequestResult(
        NyxIdChatOperationKey key) => new()
    {
        Key = key.Clone(),
        Tool = new NyxIdChatToolOperationResult
        {
            ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            Receipt = new AgentToolReceipt
            {
                CallId = "call-alpha",
                ToolName = "tool-alpha",
                Status = AgentToolReceiptStatus.Denied,
                Effect = AgentToolReceiptEffect.Mutating,
                NyxIdApprovalDecisionMode = NyxIdApprovalDecisionMode.PerRequest,
                NyxIdApprovalTerminalOutcome = NyxIdApprovalTerminalOutcome.Rejected,
                ApprovalRequestId = "approval-real-alpha",
                ErrorCode = "NYXID_APPROVAL_FAILED",
                SubjectKind = "nyxid.user-service",
                SubjectId = "connected-service-alpha",
            },
        },
    };

    private static NyxIdChatOperationDispatchCommand EligibleCanaryCommand()
    {
        var command = PlanGateContinuation(
            NyxIdChatPlanGateDecisions.HashArguments("{\"value\":1}"));
        var admission = ExactWriteAdmission();
        admission.ReadBack = new AgentToolOperationReadBackPayload
        {
            ReadOperation = ExactReadAdmission(),
            Arguments = new Struct(),
            CheckName = "resource-visible",
            Assertion = new AgentToolReadBackAssertionPayload
            {
                Match = AgentToolReadBackMatchPayload.Exists,
                JsonPointer = "/data",
            },
        };
        admission.ReadBack.ReadOperation.CatalogDigest = admission.CatalogDigest;
        command.PlanGateContinuation.OperationAdmission = admission;
        command.PlanGateContinuation.ToolContext.Caller = new AgentToolCallerContextPayload
        {
            ScopeId = "scope-alpha",
            OwnerSubject = "owner-alpha",
            ResponseId = "plan-gate-alpha",
        };
        command.PlanGateContinuation.CanaryEffectFault = new NyxIdChatCanaryEffectFaultDirective
        {
            ArmId = "arm-alpha",
            Key = command.Key.Clone(),
            ServiceInstanceId = admission.ServiceInstanceId,
            CatalogDigest = admission.CatalogDigest,
            OwnerSubject = "owner-alpha",
            ExpiresAt = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 7, 24, 8, 5, 0, TimeSpan.Zero)),
        };
        return command;
    }
}
