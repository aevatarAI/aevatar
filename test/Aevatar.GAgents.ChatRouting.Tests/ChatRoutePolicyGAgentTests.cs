using System.Reflection;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChatRouting.Tests;

/// <summary>
/// Unit tests for <see cref="ChatRoutePolicyGAgent"/>. The agent is exercised
/// in-process (no Orleans): the actor id is reflection-set, an in-memory event
/// store backs event sourcing, and command handlers are invoked directly.
/// </summary>
public sealed class ChatRoutePolicyGAgentTests : IAsyncLifetime
{
    private const string ActorId = "chat-route-policy:scope-1";

    private ChatRoutePolicyGAgent _agent = null!;
    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore, InMemoryEventStore>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));
        _serviceProvider = services.BuildServiceProvider();

        _agent = new ChatRoutePolicyGAgent
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<ChatRoutePolicyState>>(),
        };
        // GAgentBase.Id has a private setter and SetId is internal to
        // Aevatar.Foundation.Core; the test assembly is not a friend, so the
        // per-scope actor id is reflection-set.
        typeof(GAgentBase).GetProperty(nameof(GAgentBase.Id))!.SetValue(_agent, ActorId);

        await _agent.ActivateAsync();
    }

    public Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HandleUpsertAsync_CreatesPolicy_WithDefaultTargetAndRules()
    {
        await _agent.HandleUpsertAsync(new UpsertChatRoutePolicyRequested
        {
            OwnerScope = new OwnerScope { RegistrationScopeId = "scope-1" },
            DefaultTarget = ForwardToModelAction("chrono-llm/gpt-5.5"),
            Rules = { Rule("summary", priority: 10) },
        });

        _agent.State.PolicyId.Should().Be(ActorId);
        _agent.State.Version.Should().Be(1);
        _agent.State.DefaultTarget.ForwardToModel.ModelName.Should().Be("chrono-llm/gpt-5.5");
        _agent.State.OwnerScope.RegistrationScopeId.Should().Be("scope-1");
        _agent.State.Rules.Should().ContainSingle().Which.RuleId.Should().Be("summary");
        _agent.State.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleRemoveRuleAsync_RemovesNamedRule()
    {
        await _agent.HandleUpsertAsync(new UpsertChatRoutePolicyRequested
        {
            DefaultTarget = ForwardToModelAction("chrono-llm/gpt-5.5"),
            Rules = { Rule("keep", priority: 10), Rule("drop", priority: 5) },
        });

        await _agent.HandleRemoveRuleAsync(new RemoveChatRouteRuleRequested { RuleId = "drop" });

        _agent.State.Version.Should().Be(2, "the second committed command bumps the monotonic version");
        _agent.State.Rules.Select(rule => rule.RuleId).Should().Equal("keep");
    }

    [Fact]
    public async Task HandleUpsertRuleAsync_MergesRuleAgainstAuthoritativeState()
    {
        await _agent.HandleUpsertAsync(new UpsertChatRoutePolicyRequested
        {
            OwnerScope = new OwnerScope { RegistrationScopeId = "scope-1" },
            DefaultTarget = ForwardToModelAction("existing-default"),
            Rules =
            {
                Rule("keep", priority: 10),
                Rule("voice-demo", priority: 5),
            },
        });

        await _agent.HandleUpsertRuleAsync(new UpsertChatRouteRuleRequested
        {
            OwnerScope = new OwnerScope { RegistrationScopeId = "ignored-new-scope" },
            DefaultTargetIfUninitialized = ForwardToModelAction("ignored-default"),
            Rule = Rule("voice-demo", priority: 1000, modelName: "voice-model"),
        });

        _agent.State.Version.Should().Be(2);
        _agent.State.OwnerScope.RegistrationScopeId.Should().Be("scope-1");
        _agent.State.DefaultTarget.ForwardToModel.ModelName.Should().Be("existing-default");
        _agent.State.Rules.Select(rule => rule.RuleId).Should().Equal("voice-demo", "keep");
        _agent.State.Rules.Single(rule => rule.RuleId == "keep")
            .Action.ForwardToModel.ModelName.Should().Be("chrono-llm/gpt-5.5");
        _agent.State.Rules.Single(rule => rule.RuleId == "voice-demo")
            .Action.ForwardToModel.ModelName.Should().Be("voice-model");
    }

    [Fact]
    public async Task HandleUpsertRuleAsync_InitializesPolicyWhenMissing()
    {
        await _agent.HandleUpsertRuleAsync(new UpsertChatRouteRuleRequested
        {
            OwnerScope = new OwnerScope { RegistrationScopeId = "scope-1" },
            DefaultTargetIfUninitialized = ForwardToModelAction("initial-default"),
            Rule = Rule("voice-demo", priority: 1000, modelName: "voice-model"),
        });

        _agent.State.PolicyId.Should().Be(ActorId);
        _agent.State.Version.Should().Be(1);
        _agent.State.OwnerScope.RegistrationScopeId.Should().Be("scope-1");
        _agent.State.DefaultTarget.ForwardToModel.ModelName.Should().Be("initial-default");
        _agent.State.Rules.Should().ContainSingle()
            .Which.Action.ForwardToModel.ModelName.Should().Be("voice-model");
    }

    [Fact]
    public async Task HandleUpsertRuleAsync_MissingRuleId_RejectsWithoutPersistingEvent()
    {
        var act = () => _agent.HandleUpsertRuleAsync(new UpsertChatRouteRuleRequested
        {
            OwnerScope = new OwnerScope { RegistrationScopeId = "scope-1" },
            DefaultTargetIfUninitialized = ForwardToModelAction("initial-default"),
            Rule = Rule(" ", priority: 1000, modelName: "voice-model"),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rule.rule_id is required*");
        _agent.State.Version.Should().Be(0, "a rejected command persists no event");
    }

    [Fact]
    public async Task HandleUpsertRuleAsync_UninitializedPolicyWithoutDefaultTarget_RejectsWithoutPersistingEvent()
    {
        var act = () => _agent.HandleUpsertRuleAsync(new UpsertChatRouteRuleRequested
        {
            OwnerScope = new OwnerScope { RegistrationScopeId = "scope-1" },
            Rule = Rule("voice-demo", priority: 1000, modelName: "voice-model"),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*default_target_if_uninitialized is required*");
        _agent.State.Version.Should().Be(0, "a rejected command persists no event");
    }

    [Fact]
    public async Task HandleUpsertAsync_MissingDefaultTarget_RejectsCommand()
    {
        var act = () => _agent.HandleUpsertAsync(new UpsertChatRoutePolicyRequested
        {
            OwnerScope = new OwnerScope { RegistrationScopeId = "scope-1" },
            // default_target intentionally unset.
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*default_target is required*");
        _agent.State.Version.Should().Be(0, "a rejected command persists no event");
    }

    [Fact]
    public async Task HandleUpsertAsync_PersistsRulesInPriorityOrder()
    {
        await _agent.HandleUpsertAsync(new UpsertChatRoutePolicyRequested
        {
            DefaultTarget = ForwardToModelAction("chrono-llm/gpt-5.5"),
            Rules =
            {
                Rule("low", priority: 1),
                Rule("high", priority: 100),
                Rule("mid-b", priority: 50),
                Rule("mid-a", priority: 50),
            },
        });

        _agent.State.Rules.Select(rule => rule.RuleId)
            .Should().Equal(
                ["high", "mid-a", "mid-b", "low"],
                "rules persist highest-priority-first, ties broken by rule_id lexical order");
    }

    [Fact]
    public async Task HandleRemoveRuleAsync_UnknownRule_RejectsCommand()
    {
        await _agent.HandleUpsertAsync(new UpsertChatRoutePolicyRequested
        {
            DefaultTarget = ForwardToModelAction("chrono-llm/gpt-5.5"),
            Rules = { Rule("keep", priority: 10) },
        });

        var act = () => _agent.HandleRemoveRuleAsync(new RemoveChatRouteRuleRequested { RuleId = "ghost" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rule 'ghost' not found*");
        _agent.State.Version.Should().Be(1, "a rejected command persists no event");
        _agent.State.Rules.Should().ContainSingle();
    }

    [Fact]
    public void ChatRoutePolicyGAgent_HandlesOnlyConfigCommands()
    {
        // Guard: the policy actor is config-only — it must never grow a query
        // handler or a turn-message handler (CLAUDE.md 读写分离; config actor
        // 不接 turn dispatch). Every [EventHandler] takes a known command type.
        var handlerParameterTypes = typeof(ChatRoutePolicyGAgent)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes(typeof(EventHandlerAttribute), inherit: false).Length > 0)
            .Select(method => method.GetParameters().Single().ParameterType)
            .ToList();

        handlerParameterTypes.Should().BeEquivalentTo(
            [
                typeof(UpsertChatRoutePolicyRequested),
                typeof(UpsertChatRouteRuleRequested),
                typeof(RemoveChatRouteRuleRequested),
            ]);
    }

    private static ChatRouteAction ForwardToModelAction(string modelName) =>
        new() { ForwardToModel = new ForwardToModel { ModelName = modelName } };

    private static ChatRouteRule Rule(
        string ruleId,
        int priority,
        string modelName = "chrono-llm/gpt-5.5") =>
        new()
        {
            RuleId = ruleId,
            Priority = priority,
            Action = ForwardToModelAction(modelName),
        };

    /// <summary>Minimal in-process <see cref="IEventStore"/> for unit tests.</summary>
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
            {
                throw new InvalidOperationException(
                    $"Optimistic concurrency conflict: expected {expectedVersion}, actual {currentVersion}");
            }

            var appended = events.Select(stateEvent => stateEvent.Clone()).ToList();
            stream.AddRange(appended);
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream[^1].Version,
                CommittedEvents = { appended.Select(stateEvent => stateEvent.Clone()) },
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
                ? stream.Where(stateEvent => stateEvent.Version > fromVersion.Value)
                    .Select(stateEvent => stateEvent.Clone()).ToList()
                : stream.Select(stateEvent => stateEvent.Clone()).ToList();
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
            stream.RemoveAll(stateEvent => stateEvent.Version <= toVersion);
            return Task.FromResult((long)(before - stream.Count));
        }
    }
}
