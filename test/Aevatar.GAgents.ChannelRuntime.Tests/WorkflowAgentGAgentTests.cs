using System.Reflection;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class WorkflowAgentGAgentTests : IAsyncLifetime
{
    private WorkflowAgentGAgent _agent = null!;
    private CapturingWorkflowDispatchService _dispatchService = null!;
    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        _dispatchService = new CapturingWorkflowDispatchService();
        _serviceProvider = BuildServiceProvider(_dispatchService);
        _agent = new WorkflowAgentGAgent
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowAgentState>>(),
        };

        await _agent.ActivateAsync();
    }

    public async Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task HandleTriggerAsync_ShouldIncludeRevisionFeedbackInWorkflowPrompt()
    {
        await _agent.HandleInitializeAsync(new InitializeWorkflowAgentCommand
        {
            WorkflowId = "social-media-agent-1",
            WorkflowName = "social_media_agent_1",
            WorkflowActorId = "workflow-actor-1",
            ExecutionPrompt = "Generate the scheduled social media draft for review.",
            ConversationId = "oc_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key-1",
            Enabled = true,
            ScopeId = "scope-1",
        });

        await _agent.HandleTriggerAsync(new TriggerWorkflowAgentExecutionCommand
        {
            Reason = "run_agent",
            RevisionFeedback = "Need a stronger hook and clearer CTA.",
        });

        _dispatchService.LastCommand.Should().NotBeNull();
        _dispatchService.LastCommand!.Prompt.Should().Contain("Trigger reason: run_agent");
        _dispatchService.LastCommand.Prompt.Should().Contain("Revision feedback: Need a stronger hook and clearer CTA.");
        _dispatchService.LastCommand.Metadata.Should().Contain(new KeyValuePair<string, string>(ChannelMetadataKeys.ConversationId, "oc_chat_1"));
        _dispatchService.LastCommand.Metadata.Should().Contain(new KeyValuePair<string, string>("scope_id", "scope-1"));
    }

    [Fact]
    public async Task HandleInitializeAsync_ShouldDispatchCatalogCommandsThroughDispatchPort()
    {
        var catalogActor = Substitute.For<IActor>();
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(catalogActor));

        var dispatch = Substitute.For<IActorDispatchPort>();
        var captured = new List<EventEnvelope>();
        dispatch.DispatchAsync(
                UserAgentCatalogGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(captured.Add),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var provider = BuildServiceProvider(
            new CapturingWorkflowDispatchService(),
            services =>
            {
                services.AddSingleton(runtime);
                services.AddSingleton(dispatch);
            });
        var agent = new WorkflowAgentGAgent
        {
            Services = provider,
            EventSourcingBehaviorFactory =
                provider.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowAgentState>>(),
        };
        AssignActorId(agent, "workflow-agent-dispatch-test");
        await agent.ActivateAsync();

        await agent.HandleInitializeAsync(new InitializeWorkflowAgentCommand
        {
            WorkflowId = "social-media-agent-1",
            WorkflowName = "social_media_agent_1",
            WorkflowActorId = "workflow-actor-1",
            ExecutionPrompt = "Generate the scheduled social media draft for review.",
            ConversationId = "oc_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key-1",
            Enabled = true,
            ScopeId = "scope-1",
        });

        captured.Should().HaveCount(2);
        captured[0].Payload.Is(UserAgentCatalogUpsertCommand.Descriptor).Should().BeTrue();
        captured[1].Payload.Is(UserAgentCatalogExecutionUpdateCommand.Descriptor).Should().BeTrue();
        captured.Should().OnlyContain(envelope =>
            envelope.Route.PublisherActorId == "workflow-agent-dispatch-test" &&
            envelope.Route.Direct.TargetActorId == UserAgentCatalogGAgent.WellKnownId);
        await catalogActor.DidNotReceive()
            .HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>());
    }

    private sealed class CapturingWorkflowDispatchService
        : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public WorkflowChatRunRequest? LastCommand { get; private set; }

        public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            return Task.FromResult(CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                new WorkflowChatRunAcceptedReceipt(
                    ActorId: "workflow-run-actor-1",
                    WorkflowName: command.WorkflowName ?? "unknown",
                    CommandId: "cmd-1",
                    CorrelationId: "corr-1")));
        }
    }

    private static ServiceProvider BuildServiceProvider(
        CapturingWorkflowDispatchService dispatchService,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore, InMemoryEventStore>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));
        services.AddSingleton<ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>(dispatchService);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static void AssignActorId(GAgentBase agent, string actorId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [actorId]);
    }

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _events[agentId] = stream;
            }

            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            if (currentVersion != expectedVersion)
                throw new InvalidOperationException(
                    $"Optimistic concurrency conflict: expected {expectedVersion}, actual {currentVersion}");

            var appended = events.Select(x => x.Clone()).ToList();
            stream.AddRange(appended);
            var latest = stream.Count == 0 ? 0 : stream[^1].Version;
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = latest,
                CommittedEvents = { appended.Select(x => x.Clone()) },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
                return Task.FromResult<IReadOnlyList<StateEvent>>([]);

            IReadOnlyList<StateEvent> result = fromVersion.HasValue
                ? stream.Where(x => x.Version > fromVersion.Value).Select(x => x.Clone()).ToList()
                : stream.Select(x => x.Clone()).ToList();
            return Task.FromResult(result);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream) || stream.Count == 0)
                return Task.FromResult(0L);
            return Task.FromResult(stream[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (toVersion <= 0 || !_events.TryGetValue(agentId, out var stream))
                return Task.FromResult(0L);

            var before = stream.Count;
            stream.RemoveAll(x => x.Version <= toVersion);
            return Task.FromResult((long)(before - stream.Count));
        }
    }
}
