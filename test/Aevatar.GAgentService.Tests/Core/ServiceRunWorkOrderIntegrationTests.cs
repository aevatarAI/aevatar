using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.GAgents.WorkOrder;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Serialization;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ServiceRunWorkOrderIntegrationTests
{
    [Fact]
    public async Task ScriptTerminalFact_ShouldFlowThroughServiceRunOutbox_ToWorkOrderTerminalState()
    {
        const string scopeId = "scope-1";
        const string serviceId = "service-1";
        const string scriptActorId = "script-runtime-1";
        const string dedupKey = "script-service-terminal-chain";
        var workOrderId = WorkOrderConventions.BuildWorkOrderId(scopeId, dedupKey);
        var workOrderActorId = WorkOrderConventions.BuildActorId(scopeId, workOrderId);
        var dispatchCommandId = WorkOrderConventions.BuildDispatchCommandId(workOrderId);
        var requestedRunId = WorkOrderConventions.BuildRequestedRunId(workOrderId);
        var terminalDeliveryId = WorkOrderConventions.BuildTerminalDeliveryId(workOrderId);
        var serviceRunActorId = $"service-run:{scopeId}:{serviceId}:{requestedRunId}";
        var scriptDeliveryId = $"service-run-source:{requestedRunId}:{dispatchCommandId}";
        var requestedAt = DateTimeOffset.UtcNow;

        var router = new RoutingEventPublisher();
        var executionScheduler = new RecordingExecutionScheduler();
        var workOrderStore = new InMemoryEventStore();
        var workOrder = GAgentServiceTestKit.CreateStatefulAgent<WorkOrderGAgent, WorkOrderState>(
            workOrderStore,
            workOrderActorId,
            () => new WorkOrderGAgent(executionScheduler));
        workOrder.EventPublisher = router;

        var serviceRunStore = new InMemoryEventStore();
        var serviceRun = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            serviceRunStore,
            serviceRunActorId,
            static () => new ServiceRunGAgent());
        serviceRun.EventPublisher = router;

        var scriptStore = new InMemoryEventStore();
        var script = GAgentServiceTestKit.CreateStatefulAgent<ScriptBehaviorGAgent, ScriptBehaviorState>(
            scriptStore,
            scriptActorId,
            static () => new ScriptBehaviorGAgent(
                new EmptyScriptDispatcher(),
                new NoOpCapabilityFactory(),
                new UnusedArtifactResolver(),
                new UnusedMessageCodec()));
        script.EventPublisher = router;

        router.RouteAsync = async (targetActorId, message, ct) =>
        {
            if (string.Equals(targetActorId, workOrder.Id, StringComparison.Ordinal) &&
                message is ExecuteWorkOrder execute)
            {
                await workOrder.HandleExecuteAsync(execute);
                return;
            }

            if (string.Equals(targetActorId, workOrder.Id, StringComparison.Ordinal) &&
                message is WorkOrderExecutionAcceptedContinuation accepted)
            {
                await workOrder.HandleExecutionAcceptedAsync(accepted);
                return;
            }

            if (string.Equals(targetActorId, serviceRun.Id, StringComparison.Ordinal) &&
                message is ScriptRunOutcomeRecordedEvent scriptTerminal)
            {
                await serviceRun.HandleScriptRunOutcomeAsync(scriptTerminal);
                return;
            }

            if (string.Equals(targetActorId, workOrder.Id, StringComparison.Ordinal) &&
                message is ServiceRunTerminalNotification serviceTerminal)
            {
                await workOrder.HandleServiceRunTerminalAsync(serviceTerminal);
                return;
            }

            throw new InvalidOperationException(
                $"Unexpected integration message '{message.Descriptor.FullName}' for actor '{targetActorId}'.");
        };

        await workOrder.ActivateAsync();
        await serviceRun.ActivateAsync();
        await script.ActivateAsync();

        await workOrder.HandleCreateAsync(new CreateWorkOrder
        {
            WorkOrderId = workOrderId,
            DedupKey = dedupKey,
            ScopeId = scopeId,
            TeamId = "team-1",
            Requester = new WorkOrderPrincipal
            {
                PrincipalId = "requester-1",
                PrincipalKind = "user",
            },
            MemberId = "member-1",
            PublishedServiceId = serviceId,
            ServiceRevisionId = "revision-1",
            ImplementationKind = "script",
            EndpointId = "run",
            Intent = "run the assigned script service",
            Input = new WorkOrderServiceInput
            {
                Chat = new WorkOrderChatInput { Prompt = "perform the work" },
            },
            ExpectedLifecycleVersion = 0,
            RequestedAtUtc = Timestamp.FromDateTimeOffset(requestedAt),
            TimeoutAtUtc = Timestamp.FromDateTimeOffset(requestedAt.AddHours(1)),
        });
        await workOrder.HandleDispatchAsync(new DispatchWorkOrder
        {
            WorkOrderId = workOrderId,
            ExpectedLifecycleVersion = workOrder.State.LifecycleVersion,
            RequestedBy = workOrder.State.Requester.Clone(),
            DispatchCommandId = dispatchCommandId,
            RequestedRunId = requestedRunId,
            TerminalDeliveryId = terminalDeliveryId,
        });

        var executionRequest = executionScheduler.Requests.Should().ContainSingle().Subject;
        await router.SendToAsync(workOrder.Id, new WorkOrderExecutionAcceptedContinuation
        {
            WorkOrderId = executionRequest.WorkOrderId,
            DispatchCommandId = executionRequest.DispatchCommandId,
            RequestedRunId = executionRequest.RequestedRunId,
            Accepted = new WorkOrderExecutionAccepted
            {
                RunId = executionRequest.RequestedRunId,
                RunActorId = scriptActorId,
                CommandId = executionRequest.DispatchCommandId,
                CorrelationId = executionRequest.DispatchCommandId,
                RevisionId = executionRequest.ServiceRevisionId,
                DeploymentId = "deployment-1",
                AcceptedAtUtc = Timestamp.FromDateTimeOffset(requestedAt.AddSeconds(1)),
            },
        });

        workOrder.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
        workOrder.State.Run.RunId.Should().Be(requestedRunId);

        await serviceRun.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = new ServiceRunRecord
            {
                ScopeId = scopeId,
                ServiceId = serviceId,
                ServiceKey = $"{scopeId}:{serviceId}",
                RunId = requestedRunId,
                CommandId = dispatchCommandId,
                CorrelationId = dispatchCommandId,
                EndpointId = "run",
                ImplementationKind = ServiceImplementationKind.Scripting,
                TargetActorId = scriptActorId,
                RevisionId = "revision-1",
                DeploymentId = "deployment-1",
                CompletionNotificationTarget = new ServiceRunCompletionNotificationTarget
                {
                    ActorId = workOrderActorId,
                    DeliveryId = terminalDeliveryId,
                    ExpiresAtUnixMs = long.MaxValue,
                },
            },
        });

        await script.HandleEnvelopeAsync(BuildEnvelope(
            "bind-script",
            new BindScriptBehaviorRequestedEvent
            {
                DefinitionActorId = "script-definition-1",
                ScriptId = "script-1",
                Revision = "revision-1",
                SourceHash = "source-hash-1",
                ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource(
                    "public sealed class IntegrationScript { }"),
                ScopeId = scopeId,
            },
            correlationId: "bind-script"));
        await script.HandleEnvelopeAsync(BuildEnvelope(
            dispatchCommandId,
            new RunScriptRequestedEvent
            {
                RunId = requestedRunId,
                DefinitionActorId = "script-definition-1",
                ScriptRevision = "revision-1",
                RequestedEventType = "work-order.requested",
                ScopeId = scopeId,
                CommandId = dispatchCommandId,
                CorrelationId = dispatchCommandId,
                CompletionNotificationActorId = serviceRunActorId,
                CompletionNotificationDeliveryId = scriptDeliveryId,
                CompletionNotificationExpiresAtUnixMs = long.MaxValue,
            },
            dispatchCommandId));

        var scriptEvents = await scriptStore.GetEventsAsync(scriptActorId, ct: CancellationToken.None);
        var committedTerminal = scriptEvents
            .Single(persisted => persisted.EventData.Is(ScriptRunOutcomeRecordedEvent.Descriptor))
            .EventData
            .Unpack<ScriptRunOutcomeRecordedEvent>();
        committedTerminal.Status.Should().Be(ScriptRunOutcomeStatus.Succeeded);
        committedTerminal.DeliveryId.Should().Be(scriptDeliveryId);
        committedTerminal.ExpiresAtUnixTimeMs.Should().Be(long.MaxValue);
        script.State.RunOutcomes[requestedRunId].DeliveryId.Should().Be(scriptDeliveryId);
        script.State.RunOutcomes[requestedRunId].Status.Should()
            .Be(ScriptRunOutcomeDeliveryStatus.Dispatched);

        serviceRun.State.Record.Status.Should().Be(ServiceRunStatus.Completed);
        serviceRun.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Dispatched);
        serviceRun.State.PendingTerminalNotification.Should().BeNull();

        workOrder.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Completed);
        workOrder.State.RunOutcome.RunId.Should().Be(requestedRunId);
        workOrder.State.RunOutcome.RunActorId.Should().Be(scriptActorId);
        workOrder.State.RunOutcome.CommandId.Should().Be(dispatchCommandId);
        workOrder.State.RunOutcome.Outcome.Should().Be(WorkOrderTerminalOutcome.Succeeded);
        workOrder.State.RunOutcome.TerminalAtUtc.Should().Be(
            Timestamp.FromDateTimeOffset(
                DateTimeOffset.FromUnixTimeMilliseconds(committedTerminal.OccurredAtUnixTimeMs)));

        var scriptTerminalSend = router.Sends.Should().ContainSingle(sent =>
            sent.Message is ScriptRunOutcomeRecordedEvent).Subject;
        var scriptTerminalOperationId = scriptTerminalSend.Options?.Delivery?.OperationId;
        scriptTerminalOperationId.Should().Be($"script-run-terminal:{scriptDeliveryId}");
        var scriptTerminalEnvelope = new EventEnvelope
        {
            Id = "script-terminal-envelope",
            Payload = Any.Pack(scriptTerminalSend.Message),
            Timestamp = Timestamp.FromDateTimeOffset(requestedAt),
            Route = EnvelopeRouteSemantics.CreateDirect(scriptActorId, serviceRunActorId),
            Propagation = new EnvelopePropagation { CorrelationId = dispatchCommandId },
        };
        scriptTerminalEnvelope.EnsureRuntime().EnsureDeliveryIdentity().OperationId =
            scriptTerminalOperationId;
        scriptTerminalEnvelope.Runtime.DeliveryIdentity.OperationId.Should().Be(scriptTerminalOperationId);
        router.Sends.Should().ContainSingle(sent =>
            sent.Message is ServiceRunTerminalNotification &&
            sent.Options != null &&
            sent.Options.Delivery != null &&
            sent.Options.Delivery.OperationId ==
            $"service-run-terminal-{terminalDeliveryId}");
    }

    private static EventEnvelope BuildEnvelope(
        string id,
        IMessage payload,
        string correlationId) =>
        new()
        {
            Id = id,
            Payload = Any.Pack(payload),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                "service-run-work-order-integration",
                TopologyAudience.Self),
            Propagation = new EnvelopePropagation { CorrelationId = correlationId },
        };

    private sealed class RecordingExecutionScheduler : IWorkOrderExecutionScheduler
    {
        public List<WorkOrderExecutionRequest> Requests { get; } = [];

        public ValueTask ScheduleAsync(
            WorkOrderExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request.Clone());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyScriptDispatcher : IScriptBehaviorDispatcher
    {
        public Task<IReadOnlyList<ScriptDomainFactCommitted>> DispatchAsync(
            ScriptBehaviorDispatchRequest request,
            CancellationToken ct)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ScriptDomainFactCommitted>>([]);
        }
    }

    private sealed class NoOpCapabilityFactory : IScriptBehaviorRuntimeCapabilityFactory
    {
        public IScriptBehaviorRuntimeCapabilities Create(
            ScriptBehaviorRuntimeCapabilityContext context,
            Func<IMessage, TopologyAudience, CancellationToken, Task> publishAsync,
            Func<string, IMessage, CancellationToken, Task> sendToAsync,
            Func<IMessage, CancellationToken, Task> publishToSelfAsync,
            Func<string, TimeSpan, IMessage, CancellationToken, Task<RuntimeCallbackLease>> scheduleSelfSignalAsync,
            Func<RuntimeCallbackLease, CancellationToken, Task> cancelCallbackAsync)
        {
            _ = context;
            _ = publishAsync;
            _ = sendToAsync;
            _ = publishToSelfAsync;
            _ = scheduleSelfSignalAsync;
            _ = cancelCallbackAsync;
            return new NoOpCapabilities();
        }
    }

    private sealed class NoOpCapabilities : IScriptBehaviorRuntimeCapabilities
    {
        public Task<string> AskAIAsync(string prompt, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task PublishAsync(IMessage eventPayload, TopologyAudience direction, CancellationToken ct) =>
            Task.CompletedTask;
        public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct) =>
            Task.CompletedTask;
        public Task PublishToSelfAsync(IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;
        public Task<RuntimeCallbackLease> ScheduleSelfDurableSignalAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage eventPayload,
            CancellationToken ct) =>
            Task.FromResult(new RuntimeCallbackLease(
                "integration-runtime",
                callbackId,
                0,
                RuntimeCallbackBackend.InMemory));
        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<ScriptPromotionDecision> ProposeScriptEvolutionAsync(
            ScriptEvolutionProposal proposal,
            CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ScriptDefinitionUpsertResult> UpsertScriptDefinitionAsync(
            string scriptId,
            string scriptRevision,
            string sourceText,
            string sourceHash,
            string? definitionActorId,
            CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<string> SpawnScriptRuntimeAsync(
            string definitionActorId,
            string scriptRevision,
            string? runtimeActorId,
            ScriptDefinitionBindingSpec definitionSnapshot,
            CancellationToken ct) =>
            throw new NotSupportedException();
        public Task RunScriptInstanceAsync(
            string runtimeActorId,
            string runId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            CancellationToken ct) =>
            throw new NotSupportedException();
        public Task PromoteRevisionAsync(
            string catalogActorId,
            string scriptId,
            string revision,
            string definitionActorId,
            string sourceHash,
            string proposalId,
            CancellationToken ct) =>
            throw new NotSupportedException();
        public Task RollbackRevisionAsync(
            string catalogActorId,
            string scriptId,
            string targetRevision,
            string reason,
            string proposalId,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedArtifactResolver : IScriptBehaviorArtifactResolver
    {
        public ScriptBehaviorArtifact Resolve(ScriptBehaviorArtifactRequest request) =>
            throw new InvalidOperationException("The no-fact integration dispatcher must not resolve an artifact.");
    }

    private sealed class UnusedMessageCodec : IProtobufMessageCodec
    {
        public Any? Pack(IMessage? message) => throw Unexpected();
        public IMessage? Unpack(Any? payload, System.Type messageClrType) => throw Unexpected();
        public IMessage? Unpack(Any? payload, MessageDescriptor descriptor) => throw Unexpected();
        public string GetTypeUrl(System.Type messageClrType) => throw Unexpected();

        private static InvalidOperationException Unexpected() =>
            new("The no-fact integration dispatcher must not use the message codec.");
    }

    private sealed class RoutingEventPublisher : IEventPublisher
    {
        public Func<string, IMessage, CancellationToken, Task>? RouteAsync { get; set; }

        public List<SentMessage> Sends { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
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
            ct.ThrowIfCancellationRequested();
            Sends.Add(new SentMessage(targetActorId, evt, options));
            return RouteAsync?.Invoke(targetActorId, evt, ct)
                ?? throw new InvalidOperationException("Integration message router is not attached.");
        }
    }

    private sealed record SentMessage(
        string TargetActorId,
        IMessage Message,
        EventEnvelopePublishOptions? Options);
}
