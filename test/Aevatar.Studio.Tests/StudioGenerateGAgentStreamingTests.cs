using System.Reflection;
using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class StudioGenerateGAgentStreamingTests
{
    [Fact]
    public async Task ScriptGenerateAsync_WhenProviderStreamsSplitContent_ShouldReturnConcatenatedContent()
    {
        using var services = BuildServiceProvider();
        var provider = new SplitStreamingProviderFactory(["script ", "draft"]);
        var agent = CreateAgent(new ScriptGenerateGAgent(provider), services, "script-generate-stream");

        await agent.ActivateAsync();

        var result = await agent.GenerateAsync("write script", "request-script", metadata: null);

        result.Should().Be("script draft");
        provider.StreamCallCount.Should().Be(1);
    }

    [Fact]
    public async Task WorkflowGenerateAsync_WhenProviderStreamsSplitContent_ShouldReturnConcatenatedContent()
    {
        using var services = BuildServiceProvider();
        var provider = new SplitStreamingProviderFactory(["workflow ", "yaml"]);
        var agent = CreateAgent(new WorkflowGenerateGAgent(provider), services, "workflow-generate-stream");

        await agent.ActivateAsync();

        var result = await agent.GenerateAsync("write workflow", "request-workflow", metadata: null);

        result.Should().Be("workflow yaml");
        provider.StreamCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GenerateAsync_WhenProviderStreamHasNoContent_ShouldReturnEmptyString()
    {
        using var services = BuildServiceProvider();
        var scriptProvider = new SplitStreamingProviderFactory([]);
        var workflowProvider = new SplitStreamingProviderFactory([]);
        var scriptAgent = CreateAgent(new ScriptGenerateGAgent(scriptProvider), services, "script-empty-stream");
        var workflowAgent = CreateAgent(new WorkflowGenerateGAgent(workflowProvider), services, "workflow-empty-stream");

        await scriptAgent.ActivateAsync();
        await workflowAgent.ActivateAsync();

        var scriptResult = await scriptAgent.GenerateAsync("empty script", "request-script-empty", metadata: null);
        var workflowResult = await workflowAgent.GenerateAsync("empty workflow", "request-workflow-empty", metadata: null);

        scriptResult.Should().BeEmpty();
        workflowResult.Should().BeEmpty();
        scriptProvider.StreamCallCount.Should().BeGreaterThan(0);
        workflowProvider.StreamCallCount.Should().BeGreaterThan(0);
    }

    private static TAgent CreateAgent<TAgent>(TAgent agent, IServiceProvider services, string actorId)
        where TAgent : AIGAgentBase<Empty>
    {
        agent.Services = services;
        agent.EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<Empty>>();
        AssignActorId(agent, actorId);
        return agent;
    }

    private static ServiceProvider BuildServiceProvider()
    {
        return new ServiceCollection()
            .AddSingleton<IEventStore, InMemoryEventStoreForStudioGenerateTests>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
    }

    private static void AssignActorId(object agent, string actorId)
    {
        var setId = typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setId.Invoke(agent, [actorId]);
    }

    private sealed class SplitStreamingProviderFactory(IReadOnlyList<string> chunks)
        : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "studio-test-provider";

        public int StreamCallCount { get; private set; }

        public ILLMProvider GetProvider(string name)
        {
            _ = name;
            return this;
        }

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            request.RequestId.Should().NotBeNullOrWhiteSpace();
            StreamCallCount++;
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                yield return new LLMStreamChunk { DeltaContent = chunk };
            }

            yield return new LLMStreamChunk
            {
                IsLast = true,
                FinishReason = "stop",
            };

            await Task.CompletedTask;
        }
    }

    private sealed class InMemoryEventStoreForStudioGenerateTests : IEventStore
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
                throw new InvalidOperationException($"Optimistic concurrency conflict: expected {expectedVersion}, actual {currentVersion}");

            var appended = events.Select(x => x.Clone()).ToList();
            stream.AddRange(appended);
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream.Count == 0 ? 0 : stream[^1].Version,
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
            return Task.FromResult(
                _events.TryGetValue(agentId, out var stream) && stream.Count > 0
                    ? stream[^1].Version
                    : 0L);
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
