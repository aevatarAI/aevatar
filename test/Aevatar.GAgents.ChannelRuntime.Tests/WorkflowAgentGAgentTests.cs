using System.Reflection;
using Aevatar.AI.Abstractions.LLMProviders;
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
    public async Task HandleInitializeAsync_ShouldAwaitUpsertDispatchBeforeFiringExecutionUpdate()
    {
        // Issue #440 regression — symmetric with SkillRunnerGAgentTests'
        // HandleInitializeAsync_ShouldAwaitUpsertDispatchBeforeFiringExecutionUpdate.
        // WorkflowAgent's UpsertRegistryAsync follows the same await-then-await pattern
        // against the catalog and is vulnerable to the same race if dispatch ever
        // regresses to fire-and-forget. Gate the Upsert dispatch on a
        // TaskCompletionSource and assert ExecutionUpdate is not even dispatched until
        // the gate releases.

        var upsertGate = new TaskCompletionSource();
        var upsertDispatchStarted = new TaskCompletionSource();
        var executionDispatchStarted = new TaskCompletionSource();

        var scheduler = Substitute.For<Foundation.Abstractions.Runtime.Callbacks.IActorRuntimeCallbackScheduler>();
        scheduler
            .ScheduleTimeoutAsync(
                Arg.Any<Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackTimeoutRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var req = call.Arg<Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackTimeoutRequest>();
                return Task.FromResult(new Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease(
                    req.ActorId, req.CallbackId, 1L,
                    Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackBackend.InMemory));
            });
        scheduler.CancelAsync(
                Arg.Any<Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var catalogProxy = Substitute.For<IActor>();
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(catalogProxy));

        UserAgentCatalogGAgent? catalog = null;
        var dispatch = Substitute.For<IActorDispatchPort>();
        dispatch.DispatchAsync(
                UserAgentCatalogGAgent.WellKnownId,
                Arg.Any<EventEnvelope>(),
                Arg.Any<CancellationToken>())
            .Returns(call => DispatchGated(
                call.Arg<EventEnvelope>(),
                call.Arg<CancellationToken>(),
                catalog!,
                upsertGate,
                upsertDispatchStarted,
                executionDispatchStarted));

        using var provider = BuildServiceProvider(
            new CapturingWorkflowDispatchService(),
            services =>
            {
                services.AddSingleton(runtime);
                services.AddSingleton(dispatch);
                services.AddSingleton(scheduler);
            });

        catalog = new UserAgentCatalogGAgent
        {
            Services = provider,
            EventSourcingBehaviorFactory =
                provider.GetRequiredService<IEventSourcingBehaviorFactory<UserAgentCatalogState>>(),
        };
        AssignActorId(catalog, UserAgentCatalogGAgent.WellKnownId);
        await catalog.ActivateAsync();

        var agent = new WorkflowAgentGAgent
        {
            Services = provider,
            EventSourcingBehaviorFactory =
                provider.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowAgentState>>(),
        };
        AssignActorId(agent, "workflow-agent-440-regression");
        await agent.ActivateAsync();

        var initTask = agent.HandleInitializeAsync(new InitializeWorkflowAgentCommand
        {
            WorkflowId = "social-media-agent-1",
            WorkflowName = "social_media_agent_1",
            WorkflowActorId = "workflow-actor-1",
            ExecutionPrompt = "Generate the scheduled social media draft for review.",
            ScheduleCron = "0 9 * * *",
            ScheduleTimezone = "UTC",
            ConversationId = "oc_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key-1",
            Enabled = true,
            ScopeId = "scope-1",
        });

        await upsertDispatchStarted.Task;

        executionDispatchStarted.Task.IsCompleted.Should().BeFalse(
            "the WorkflowAgent must await Upsert's dispatch task before firing ExecutionUpdate; "
            + "regressing to fire-and-forget would let ExecutionUpdate race ahead of Upsert "
            + "and be dropped by the missing-entry guard in HandleExecutionUpdateAsync");

        upsertGate.SetResult();
        await initTask;

        executionDispatchStarted.Task.IsCompleted.Should().BeTrue(
            "ExecutionUpdate must dispatch after Upsert completes so /agent-status shows Next run");
        catalog.State.Entries.Should().ContainSingle();
        var entry = catalog.State.Entries[0];
        entry.AgentId.Should().Be("workflow-agent-440-regression");
        entry.Status.Should().Be(WorkflowAgentDefaults.StatusRunning);
        entry.ScheduleCron.Should().Be("0 9 * * *");
        entry.NextRunAt.Should().NotBeNull(
            "init's post-Upsert ExecutionUpdate must land at the catalog so /agent-status shows Next run");
    }

    private static async Task DispatchGated(
        EventEnvelope envelope,
        CancellationToken ct,
        UserAgentCatalogGAgent catalog,
        TaskCompletionSource upsertGate,
        TaskCompletionSource upsertDispatchStarted,
        TaskCompletionSource executionDispatchStarted)
    {
        if (envelope.Payload.Is(UserAgentCatalogUpsertCommand.Descriptor))
        {
            upsertDispatchStarted.TrySetResult();
            await upsertGate.Task;
        }
        else if (envelope.Payload.Is(UserAgentCatalogExecutionUpdateCommand.Descriptor))
        {
            executionDispatchStarted.TrySetResult();
        }

        await catalog.HandleEventAsync(envelope, ct);
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

    [Fact]
    public async Task HandleTriggerAsync_ShouldPinOwnerLlmConfigOverridesOnDispatchedMetadata()
    {
        // Symmetric with SkillRunnerGAgentTests'
        // BuildExecutionMetadata_ShouldPinOwnerLlmConfigOverrides_WhenSourceReturnsConfig:
        // workflow-backed agents (e.g. social_media) honor the bot owner's pre-configured
        // model + NyxID route + tool cap exactly the same way. Without this, the workflow's
        // LLM steps fall through to NyxIdLLMProvider's compile-time `gpt-5.4` + gateway
        // default and 400 when the bot owner pre-configured `chrono-llm` instead of OpenAI.
        var source = new SkillRunnerGAgentTests.StubOwnerLlmConfigSource(new OwnerLlmConfig(
            DefaultModel: "gpt-5.5",
            PreferredLlmRoute: "/api/v1/proxy/s/chrono-llm",
            MaxToolRounds: 7));

        var dispatchService = new CapturingWorkflowDispatchService();
        using var provider = BuildServiceProvider(dispatchService);
        var agent = new WorkflowAgentGAgent(ownerLlmConfigSource: source)
        {
            Services = provider,
            EventSourcingBehaviorFactory =
                provider.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowAgentState>>(),
        };
        AssignActorId(agent, "workflow-agent-userconfig");
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

        await agent.HandleTriggerAsync(new TriggerWorkflowAgentExecutionCommand { Reason = "schedule" });

        dispatchService.LastCommand.Should().NotBeNull();
        var metadata = dispatchService.LastCommand!.Metadata;
        metadata.Should().NotBeNull();
        metadata![Aevatar.AI.Abstractions.LLMProviders.LLMRequestMetadataKeys.ModelOverride]
            .Should().Be("gpt-5.5");
        metadata[Aevatar.AI.Abstractions.LLMProviders.LLMRequestMetadataKeys.NyxIdRoutePreference]
            .Should().Be("/api/v1/proxy/s/chrono-llm");
        metadata[Aevatar.AI.Abstractions.LLMProviders.LLMRequestMetadataKeys.MaxToolRoundsOverride]
            .Should().Be("7");
        source.RequestedScopeIds.Should().ContainSingle().Which.Should().Be("scope-1");
    }

    [Fact]
    public async Task HandleTriggerAsync_ShouldOmitOwnerLlmOverrides_WhenSourceIsAbsent()
    {
        // Hosts that don't wire IOwnerLlmConfigSource (e.g. the existing test suite, or a
        // host without Studio.Application composed in) must still produce a valid dispatched
        // metadata bag with no override keys leaking — provider defaults take over.
        var dispatchService = new CapturingWorkflowDispatchService();
        using var provider = BuildServiceProvider(dispatchService);
        var agent = new WorkflowAgentGAgent
        {
            Services = provider,
            EventSourcingBehaviorFactory =
                provider.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowAgentState>>(),
        };
        AssignActorId(agent, "workflow-agent-no-source");
        await agent.ActivateAsync();

        await agent.HandleInitializeAsync(new InitializeWorkflowAgentCommand
        {
            WorkflowId = "social-media-agent-2",
            WorkflowName = "social_media_agent_2",
            WorkflowActorId = "workflow-actor-2",
            ExecutionPrompt = "Generate the scheduled social media draft for review.",
            ConversationId = "oc_chat_2",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key-2",
            Enabled = true,
            ScopeId = "scope-2",
        });

        await agent.HandleTriggerAsync(new TriggerWorkflowAgentExecutionCommand { Reason = "schedule" });

        dispatchService.LastCommand.Should().NotBeNull();
        var metadata = dispatchService.LastCommand!.Metadata;
        metadata.Should().NotBeNull();
        metadata!.Should().NotContainKey(Aevatar.AI.Abstractions.LLMProviders.LLMRequestMetadataKeys.ModelOverride);
        metadata.Should().NotContainKey(Aevatar.AI.Abstractions.LLMProviders.LLMRequestMetadataKeys.NyxIdRoutePreference);
        metadata.Should().NotContainKey(Aevatar.AI.Abstractions.LLMProviders.LLMRequestMetadataKeys.MaxToolRoundsOverride);
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
