using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed partial class NyxIdChatTurnGAgentTests
{
    [Fact]
    public async Task CanaryEffectFault_ShouldCommitAmbiguousWaterlineWithoutCallingEffectExecutor()
    {
        var executor = new RecordingOperationExecutor(command =>
            command.InputCase == NyxIdChatOperationDispatchCommand.InputOneofCase.Llm
                ? new NyxIdChatOperationResultSignal
                {
                    Key = command.Key.Clone(),
                    Llm = new NyxIdChatLLMOperationResult { Content = "Plan ready." },
                }
                : throw new InvalidOperationException("The canary must not invoke the effect provider."));
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

        executor.Commands.Should().ContainSingle(command =>
            command.InputCase == NyxIdChatOperationDispatchCommand.InputOneofCase.Llm);
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
                new NyxIdChatTurnOperationCompletedEvent()).TypeUrl,
            Google.Protobuf.WellKnownTypes.Any.Pack(
                new NyxIdChatTurnOperationDeliveredEvent()).TypeUrl);
        committed[1].EventData.Unpack<NyxIdChatTurnEffectDispatchStartedEvent>()
            .ConsumedCanaryEffectFaultArmId.Should().Be("arm-alpha");
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
        executor.Commands.Should().ContainSingle(command =>
            command.InputCase == NyxIdChatOperationDispatchCommand.InputOneofCase.Llm);
    }
}
