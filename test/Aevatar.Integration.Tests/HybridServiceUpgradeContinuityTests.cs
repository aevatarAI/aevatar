using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Core.Agents;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Integration.Tests.TestDoubles.Protocols;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public sealed class HybridServiceUpgradeContinuityTests
{
    [Fact]
    public async Task StaticServiceState_ShouldContinueAfterReplacingScriptingImplementation()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddAevatarWorkflow();
        services.AddScriptCapability();
        services.AddSingleton<IRoleAgentTypeResolver, RoleGAgentTypeResolver>();

        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var eventStore = provider.GetRequiredService<Aevatar.Foundation.Abstractions.Persistence.IEventStore>();

        var workflowActor = await runtime.CreateAsync<TextNormalizationWorkflowProtocolGAgent>(
            "hybrid-workflow-" + Guid.NewGuid().ToString("N")[..8],
            CancellationToken.None);
        var scriptingActorV1 = await runtime.CreateAsync<TextNormalizationScriptingProtocolGAgent>(
            "hybrid-script-v1-" + Guid.NewGuid().ToString("N")[..8],
            CancellationToken.None);
        var scriptingActorV2 = await runtime.CreateAsync<TextNormalizationScriptingProtocolGAgent>(
            "hybrid-script-v2-" + Guid.NewGuid().ToString("N")[..8],
            CancellationToken.None);
        var serviceActor = await runtime.CreateAsync<HybridServiceStateGAgent>(
            "hybrid-service-" + Guid.NewGuid().ToString("N")[..8],
            CancellationToken.None);

        await serviceActor.HandleEventAsync(CreateEnvelope(new ConfigureHybridServiceRequested
        {
            WorkflowActorId = workflowActor.Id,
            ScriptingActorId = scriptingActorV1.Id,
        }), CancellationToken.None);

        await serviceActor.HandleEventAsync(CreateEnvelope(new HybridServiceRequested
        {
            RequestId = "req-1",
            InputText = "  Mixed Case  ",
        }), CancellationToken.None);

        var firstSnapshot = await QueryServiceAsync(provider, serviceActor.Id, CancellationToken.None);
        firstSnapshot.ProcessedCount.Should().Be(1);
        firstSnapshot.WorkflowActorId.Should().Be(workflowActor.Id);
        firstSnapshot.ScriptingActorId.Should().Be(scriptingActorV1.Id);
        firstSnapshot.Traces.Should().HaveCount(1);
        firstSnapshot.Traces[0].RequestId.Should().Be("req-1");
        firstSnapshot.Traces[0].WorkflowOutput.Should().Be("MIXED CASE");
        firstSnapshot.Traces[0].ScriptingOutput.Should().Be("MIXED CASE");
        firstSnapshot.Traces[0].ActiveScriptingActorId.Should().Be(scriptingActorV1.Id);
        firstSnapshot.Traces[0].FinalOutput.Should().Be($"MIXED CASE|MIXED CASE|{scriptingActorV1.Id}");

        await serviceActor.HandleEventAsync(CreateEnvelope(new ReplaceHybridScriptingImplementationRequested
        {
            ScriptingActorId = scriptingActorV2.Id,
        }), CancellationToken.None);
        await runtime.DestroyAsync(scriptingActorV1.Id, CancellationToken.None);

        await serviceActor.HandleEventAsync(CreateEnvelope(new HybridServiceRequested
        {
            RequestId = "req-2",
            InputText = " next-value ",
        }), CancellationToken.None);

        var secondSnapshot = await QueryServiceAsync(provider, serviceActor.Id, CancellationToken.None);
        secondSnapshot.ProcessedCount.Should().Be(2);
        secondSnapshot.WorkflowActorId.Should().Be(workflowActor.Id);
        secondSnapshot.ScriptingActorId.Should().Be(scriptingActorV2.Id);
        secondSnapshot.Traces.Should().HaveCount(2);
        secondSnapshot.Traces[0].ActiveScriptingActorId.Should().Be(scriptingActorV1.Id);
        secondSnapshot.Traces[1].RequestId.Should().Be("req-2");
        secondSnapshot.Traces[1].WorkflowOutput.Should().Be("NEXT-VALUE");
        secondSnapshot.Traces[1].ScriptingOutput.Should().Be("NEXT-VALUE");
        secondSnapshot.Traces[1].ActiveScriptingActorId.Should().Be(scriptingActorV2.Id);
        secondSnapshot.Traces[1].FinalOutput.Should().Be($"NEXT-VALUE|NEXT-VALUE|{scriptingActorV2.Id}");
        var persistedEvents = await eventStore.GetEventsAsync(serviceActor.Id, ct: CancellationToken.None);
        persistedEvents.Should().HaveCount(4);
        persistedEvents.Any(x => x.EventData != null && x.EventData.Is(HybridServiceConfigured.Descriptor)).Should().BeTrue();
        persistedEvents.Any(x => x.EventData != null && x.EventData.Is(HybridServiceScriptingImplementationReplaced.Descriptor)).Should().BeTrue();
        persistedEvents.Count(x => x.EventData?.Is(HybridServiceProcessed.Descriptor) == true).Should().Be(2);
    }

    private static Task<HybridServiceSnapshot> QueryServiceAsync(
        IServiceProvider provider,
        string actorId,
        CancellationToken ct)
    {
        var streams = provider.GetRequiredService<IStreamProvider>();
        var requestReplyClient = provider.GetRequiredService<IStreamRequestReplyClient>();
        var dispatchPort = provider.GetRequiredService<IActorDispatchPort>();

        return QueryAsync<HybridServiceQueryResponded, HybridServiceSnapshot>(
            requestReplyClient,
            streams,
            actorId,
            dispatchPort,
            "hybrid-service-query",
            static (requestId, replyStreamId) => CreateEnvelope(new HybridServiceQueryRequested
            {
                RequestId = requestId,
                ReplyStreamId = replyStreamId,
            }),
            static (response, requestId) => string.Equals(response.RequestId, requestId, StringComparison.Ordinal),
            static response => response.Current?.Clone() ?? new HybridServiceSnapshot(),
            static requestId => $"Hybrid service query timed out. request_id={requestId}",
            ct);
    }

    private static async Task<TProjection> QueryAsync<TResponse, TProjection>(
        IStreamRequestReplyClient requestReplyClient,
        IStreamProvider streams,
        string actorId,
        IActorDispatchPort dispatchPort,
        string replyStreamPrefix,
        Func<string, string, EventEnvelope> envelopeFactory,
        Func<TResponse, string, bool> isMatch,
        Func<TResponse, TProjection> map,
        Func<string, string> timeoutMessageFactory,
        CancellationToken ct)
        where TResponse : IMessage, new()
    {
        var response = await requestReplyClient.QueryActorAsync(
            streams,
            actorId,
            dispatchPort,
            replyStreamPrefix,
            TimeSpan.FromSeconds(5),
            envelopeFactory,
            isMatch,
            timeoutMessageFactory,
            ct);

        return map(response);
    }

    private static EventEnvelope CreateEnvelope(IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("hybrid-test", TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        };

    private sealed class HybridServiceStateGAgent(
        IStreamProvider streams,
        IStreamRequestReplyClient requestReplyClient,
        IActorDispatchPort dispatchPort)
        : GAgentBase<HybridServiceSnapshot>
    {
        private readonly IStreamProvider _streams = streams ?? throw new ArgumentNullException(nameof(streams));
        private readonly IStreamRequestReplyClient _requestReplyClient = requestReplyClient ?? throw new ArgumentNullException(nameof(requestReplyClient));
        private readonly IActorDispatchPort _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));

        [EventHandler]
        public Task HandleConfigureRequested(ConfigureHybridServiceRequested evt) =>
            PersistDomainEventAsync(new HybridServiceConfigured
            {
                WorkflowActorId = evt.WorkflowActorId ?? string.Empty,
                ScriptingActorId = evt.ScriptingActorId ?? string.Empty,
            }, CancellationToken.None);

        [EventHandler]
        public Task HandleReplaceScriptingImplementationRequested(ReplaceHybridScriptingImplementationRequested evt) =>
            PersistDomainEventAsync(new HybridServiceScriptingImplementationReplaced
            {
                ScriptingActorId = evt.ScriptingActorId ?? string.Empty,
            }, CancellationToken.None);

        [EventHandler]
        public async Task HandleRequested(HybridServiceRequested evt)
        {
            var current = State.Clone();
            EnsureConfigured(current);

            var workflowCurrent = await QueryTextNormalizationAsync(
                current.WorkflowActorId,
                "workflow-" + (evt.RequestId ?? string.Empty),
                evt.InputText,
                CancellationToken.None);
            var scriptingCurrent = await QueryTextNormalizationAsync(
                current.ScriptingActorId,
                "scripting-" + (evt.RequestId ?? string.Empty),
                evt.InputText,
                CancellationToken.None);

            var next = current.Clone();
            next.ProcessedCount += 1;
            next.Traces.Add(new HybridServiceTrace
            {
                RequestId = evt.RequestId ?? string.Empty,
                InputText = evt.InputText ?? string.Empty,
                WorkflowOutput = workflowCurrent.NormalizedText ?? string.Empty,
                ScriptingOutput = scriptingCurrent.NormalizedText ?? string.Empty,
                FinalOutput =
                    $"{workflowCurrent.NormalizedText}|{scriptingCurrent.NormalizedText}|{current.ScriptingActorId}",
                ActiveScriptingActorId = current.ScriptingActorId ?? string.Empty,
            });

            await PersistDomainEventAsync(new HybridServiceProcessed
            {
                RequestId = evt.RequestId ?? string.Empty,
                Current = next,
            }, CancellationToken.None);
        }

        [EventHandler]
        public Task HandleQueryRequested(HybridServiceQueryRequested evt)
        {
            if (string.IsNullOrWhiteSpace(evt.RequestId) || string.IsNullOrWhiteSpace(evt.ReplyStreamId))
                return Task.CompletedTask;

            return EventPublisher.SendToAsync(
                evt.ReplyStreamId,
                new HybridServiceQueryResponded
                {
                    RequestId = evt.RequestId,
                    Current = State.Clone(),
                },
                CancellationToken.None,
                sourceEnvelope: null);
        }

        protected override HybridServiceSnapshot TransitionState(HybridServiceSnapshot current, IMessage evt)
        {
            var next = current?.Clone() ?? new HybridServiceSnapshot();

            switch (evt)
            {
                case HybridServiceConfigured configured:
                    next.WorkflowActorId = configured.WorkflowActorId ?? string.Empty;
                    next.ScriptingActorId = configured.ScriptingActorId ?? string.Empty;
                    return next;
                case HybridServiceScriptingImplementationReplaced replaced:
                    next.ScriptingActorId = replaced.ScriptingActorId ?? string.Empty;
                    return next;
                case HybridServiceProcessed processed:
                    return processed.Current?.Clone() ?? new HybridServiceSnapshot();
                default:
                    return next;
            }
        }

        private async Task<TextNormalizationReadModel> QueryTextNormalizationAsync(
            string actorId,
            string commandId,
            string? inputText,
            CancellationToken ct)
        {
            await _dispatchPort.DispatchAsync(
                actorId,
                CreateEnvelope(new TextNormalizationRequested
                {
                    CommandId = commandId,
                    InputText = inputText ?? string.Empty,
                }),
                ct);

            return await QueryAsync<TextNormalizationQueryResponded, TextNormalizationReadModel>(
                _requestReplyClient,
                _streams,
                actorId,
                _dispatchPort,
                "hybrid-normalization-query",
                static (requestId, replyStreamId) => CreateEnvelope(new TextNormalizationQueryRequested
                {
                    RequestId = requestId,
                    ReplyStreamId = replyStreamId,
                }),
                static (response, requestId) => string.Equals(response.RequestId, requestId, StringComparison.Ordinal),
                static response => response.Current?.Clone() ?? new TextNormalizationReadModel(),
                static requestId => $"Hybrid normalization query timed out. request_id={requestId}",
                ct);
        }

        private static void EnsureConfigured(HybridServiceSnapshot state)
        {
            if (string.IsNullOrWhiteSpace(state.WorkflowActorId) ||
                string.IsNullOrWhiteSpace(state.ScriptingActorId))
            {
                throw new InvalidOperationException("Hybrid service state is not configured.");
            }
        }
    }
}
