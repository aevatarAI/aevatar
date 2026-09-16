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
    public async Task CanaryEffectFault_ShouldConsumeDeniedDirectToolResultAndCommitAmbiguousWaterline()
    {
        var executor = new RecordingOperationExecutor(command =>
            DeniedPerRequestResult(command.Key));
        var eventStore = new InMemoryEventStoreForTests();
        var command = EligibleCanaryCommand();
        var operationDispatch = new RecordingOperationDispatchPort(executor)
        {
            CapturedToolContext = command.Tool.ToolContext.Clone(),
        };
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
        var callbackScheduler = new RecordingRuntimeCallbackScheduler();
        using var services = BuildEventSourcingServices(eventStore, callbackScheduler);
        var agent = CreateAgent(services, operationDispatch, actorDispatch);
        await agent.ActivateAsync();
        var eventsBeforeCanary = (await eventStore.GetEventsAsync("turn-actor-alpha")).Count;

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", command));

        executor.Commands.Should().ContainSingle();
        executor.Commands.Single().InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.Tool);
        executor.Commands.Single().Tool.CanaryEffectFault.Should().BeEquivalentTo(
            command.Tool.CanaryEffectFault);
        agent.State.CanaryEffectFaultConsumed.Should().BeFalse(
            "the directive is consumed only after the typed denial result reaches the actor");
        agent.State.CanaryEffectFault.Should().BeEquivalentTo(command.Tool.CanaryEffectFault);
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
            Any.Pack(new NyxIdChatTurnOperationAdmittedEvent()).TypeUrl,
            Any.Pack(new NyxIdChatTurnEffectDispatchStartedEvent()).TypeUrl,
            Any.Pack(new NyxIdChatTurnCanaryEffectFaultTriggeredEvent()).TypeUrl,
            Any.Pack(new NyxIdChatTurnOperationCompletedEvent()).TypeUrl,
            Any.Pack(new NyxIdChatTurnOperationDeliveredEvent()).TypeUrl);
        var triggered = committed[2].EventData
            .Unpack<NyxIdChatTurnCanaryEffectFaultTriggeredEvent>();
        triggered.ArmId.Should().Be("arm-alpha");
        triggered.Key.Should().BeEquivalentTo(command.Key);
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
        var firstConsumedAttempt = actorDispatch.Calls
            .Where(call => call.Envelope.Payload.Is(
                NyxIdChatCanaryEffectFaultConsumedSignal.Descriptor))
            .Select(call => call.Envelope.Payload.Unpack<NyxIdChatCanaryEffectFaultConsumedSignal>())
            .Should().ContainSingle().Which;
        var retry = callbackScheduler.TimeoutRequests
            .Where(request => request.TriggerEnvelope.Payload.Is(
                NyxIdChatCanaryEffectFaultConsumedRetryRequested.Descriptor))
            .Should().ContainSingle().Which;
        retry.CallbackId.Should().Be("arm-alpha:canary-effect-fault-consumed-retry");
        retry.DueTime.Should().Be(NyxIdChatTurnGAgent.CanaryEffectFaultConsumedRetryDelay);
        retry.TriggerEnvelope.Payload.Is(
            NyxIdChatCanaryEffectFaultConsumedRetryRequested.Descriptor).Should().BeTrue();
        var retrySignal = retry.TriggerEnvelope.Payload
            .Unpack<NyxIdChatCanaryEffectFaultConsumedRetryRequested>();
        retrySignal.ArmId.Should().Be("arm-alpha");
        retrySignal.Key.Should().BeEquivalentTo(command.Key);

        await agent.HandleEventAsync(retry.TriggerEnvelope);

        var consumed = actorDispatch.Calls
            .Where(call => call.Envelope.Payload.Is(
                NyxIdChatCanaryEffectFaultConsumedSignal.Descriptor))
            .Select(call => call.Envelope.Payload.Unpack<NyxIdChatCanaryEffectFaultConsumedSignal>())
            .Should().HaveCount(2).And.Subject.Last();
        consumed.Should().BeEquivalentTo(firstConsumedAttempt);
        consumed.ArmId.Should().Be("arm-alpha");
        consumed.Key.Should().BeEquivalentTo(command.Key);
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
        recovered.State.CanaryEffectFault.Should().BeEquivalentTo(command.Tool.CanaryEffectFault);
        recovered.State.ResultDelivered.Should().BeTrue();
        recoveredDispatch.Calls
            .Where(call => call.Envelope.Payload.Is(
                NyxIdChatCanaryEffectFaultConsumedSignal.Descriptor))
            .Select(call => call.Envelope.Payload.Unpack<NyxIdChatCanaryEffectFaultConsumedSignal>())
            .Should().ContainSingle().Which.Should().BeEquivalentTo(consumed);

        await recovered.HandleEventAsync(CreateEnvelope("turn-actor-alpha", command.Clone()));

        executor.Commands.Should().ContainSingle(
            "the exact direct Tool replay must not execute the external effect twice");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchPort_ShouldFallbackToNormalDeniedCompletionWhenCanarySignalFails(
        bool fallbackDispatchAlsoFails)
    {
        var command = EligibleCanaryCommand();
        var sourceLlmCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSourceLlmCompletionDispatch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fallbackAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var actorDispatch = new RecordingDispatchPort(async (_, envelope) =>
        {
            if (envelope.Payload.Is(NyxIdChatCanaryEffectFaultTriggeredSignal.Descriptor))
                throw new InvalidOperationException("simulated canary signal outage");

            if (envelope.Payload.Is(NyxIdChatTurnOperationExecutionCompletedSignal.Descriptor))
            {
                var completion = envelope.Payload
                    .Unpack<NyxIdChatTurnOperationExecutionCompletedSignal>();
                if (string.Equals(
                        completion.Result.Key.OperationId,
                        "operation-llm-source-alpha",
                        StringComparison.Ordinal))
                {
                    sourceLlmCompleted.TrySetResult();
                    await allowSourceLlmCompletionDispatch.Task;
                    return;
                }

                fallbackAttempted.TrySetResult();
                if (fallbackDispatchAlsoFails)
                    throw new InvalidOperationException("simulated persistent actor dispatch outage");
            }
        });
        var port = new NyxIdChatTurnOperationDispatchPort(
            new CanarySessionOperationExecutor(),
            new UnavailableNyxIdChatTurnOperationReconciliationPort(),
            actorDispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                NyxIdChatTurnOperationDispatchPort>.Instance);
        var session = port.OpenSession();

        await session.DispatchExecutionAsync(
            "turn-actor-alpha",
            SourceLlmCommand(),
            "correlation-source-alpha",
            CancellationToken.None);
        await sourceLlmCompleted.Task;
        session.CaptureToolContext().Should().BeEquivalentTo(command.Tool.ToolContext);
        actorDispatch.Calls.Clear();

        await session.DispatchExecutionAsync(
            "turn-actor-alpha",
            command,
            "correlation-alpha",
            CancellationToken.None);
        await fallbackAttempted.Task;
        allowSourceLlmCompletionDispatch.TrySetResult();

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
        var executor = new CanarySessionOperationExecutor();
        var sourceLlmCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSourceLlmCompletionDispatch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched = new TaskCompletionSource<EventEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var actorDispatch = new RecordingDispatchPort(async (actorId, envelope) =>
        {
            if (envelope.Payload.Is(NyxIdChatTurnOperationExecutionCompletedSignal.Descriptor))
            {
                var completion = envelope.Payload
                    .Unpack<NyxIdChatTurnOperationExecutionCompletedSignal>();
                if (string.Equals(
                        completion.Result.Key.OperationId,
                        "operation-llm-source-alpha",
                        StringComparison.Ordinal))
                {
                    sourceLlmCompleted.TrySetResult();
                    await allowSourceLlmCompletionDispatch.Task;
                    return;
                }
            }

            if (actorId == "turn-actor-alpha")
                dispatched.TrySetResult(envelope.Clone());
        });
        var port = new NyxIdChatTurnOperationDispatchPort(
            executor,
            new UnavailableNyxIdChatTurnOperationReconciliationPort(),
            actorDispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                NyxIdChatTurnOperationDispatchPort>.Instance);
        var session = port.OpenSession();

        await session.DispatchExecutionAsync(
            "turn-actor-alpha",
            SourceLlmCommand(),
            "correlation-source-alpha",
            CancellationToken.None);
        await sourceLlmCompleted.Task;
        session.CaptureToolContext().Should().BeEquivalentTo(command.Tool.ToolContext);
        actorDispatch.Calls.Clear();

        await session.DispatchExecutionAsync(
            "turn-actor-alpha",
            command,
            "correlation-alpha",
            CancellationToken.None);

        var envelope = await dispatched.Task;
        allowSourceLlmCompletionDispatch.TrySetResult();
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
                result.Key.OperationId = "operation-tool-beta";
                break;
            case "directive_service":
                command.Tool.CanaryEffectFault.ServiceInstanceId =
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
        var key = CreateKey(
            stepId: "step-tool-alpha",
            operationId: "operation-tool-alpha");
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
        return new NyxIdChatOperationDispatchCommand
        {
            Key = key,
            Tool = new NyxIdChatToolOperationInput
            {
                CallId = "call-alpha",
                ToolName = "tool-alpha",
                ArgumentsJson = "{\"value\":1}",
                MayChangeExternalState = true,
                Idempotent = false,
                IdempotencyKey = key.OperationId,
                OperationAdmission = admission,
                ToolContext = new AgentToolExecutionContextPayload
                {
                    Caller = new AgentToolCallerContextPayload
                    {
                        ScopeId = "scope-alpha",
                        OwnerSubject = "owner-alpha",
                        ResponseId = "response-alpha",
                    },
                },
                CanaryEffectFault = new NyxIdChatCanaryEffectFaultDirective
                {
                    ArmId = "arm-alpha",
                    ClientRequestId = "client-arm-alpha",
                    Key = key.Clone(),
                    ServiceInstanceId = admission.ServiceInstanceId,
                    CatalogDigest = admission.CatalogDigest,
                    OwnerSubject = "owner-alpha",
                    ExpiresAt = Timestamp.FromDateTimeOffset(
                        new DateTimeOffset(2026, 7, 24, 8, 5, 0, TimeSpan.Zero)),
                },
            },
        };
    }

    private static NyxIdChatOperationDispatchCommand SourceLlmCommand() => new()
    {
        Key = CreateKey(
            stepId: "step-llm-source-alpha",
            operationId: "operation-llm-source-alpha"),
        Llm = new NyxIdChatLLMOperationInput
        {
            Request = new ChatRequestEvent
            {
                Prompt = "materialize one direct effect Tool command",
                SessionId = "turn-alpha",
                ToolContext = new AgentToolExecutionContextPayload
                {
                    Caller = new AgentToolCallerContextPayload
                    {
                        ScopeId = "scope-alpha",
                        OwnerSubject = "owner-alpha",
                        ResponseId = "response-alpha",
                    },
                },
            },
        },
    };

    private sealed class CanarySessionOperationExecutor : INyxIdChatTurnOperationExecutor
    {
        public Task<NyxIdChatTurnOperationExecution> ExecuteAsync(
            NyxIdChatOperationDispatchCommand command,
            NyxIdChatTransientExecutionSession session,
            Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
            CancellationToken ct)
        {
            _ = reportProgressAsync;
            ct.ThrowIfCancellationRequested();
            if (command.Llm?.Request?.ToolContext is { } sourceToolContext)
            {
                session.StepState = new AgentRunReplyStepState
                {
                    ToolContext = sourceToolContext.Clone(),
                };
                return Task.FromResult(new NyxIdChatTurnOperationExecution(
                    new NyxIdChatOperationResultSignal
                    {
                        Key = command.Key.Clone(),
                        Llm = new NyxIdChatLLMOperationResult
                        {
                            Content = "Direct Tool ready.",
                        },
                    }));
            }

            return Task.FromResult(new NyxIdChatTurnOperationExecution(
                DeniedPerRequestResult(command.Key)));
        }
    }
}
