using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Core.Runtime;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptAgentLifecycleCapabilitiesTests
{
    [Fact]
    public async Task CreateAgentAsync_ShouldCreateAgentThroughRuntime_AndReturnActorId()
    {
        var runtime = new RecordingRuntime();
        var capabilities = new ScriptAgentLifecycleCapabilities(runtime);

        var actorId = await capabilities.CreateAgentAsync(
            typeof(FakeTestAgent).AssemblyQualifiedName!,
            "agent-x",
            CancellationToken.None);

        actorId.Should().Be("agent-x");
        runtime.CreatedType.Should().Be(typeof(FakeTestAgent));
        runtime.CreatedActorId.Should().Be("agent-x");
    }

    [Fact]
    public async Task CreateAgentAsync_ShouldThrow_WhenAgentTypeNotFound()
    {
        var capabilities = new ScriptAgentLifecycleCapabilities(
            new RecordingRuntime());

        Func<Task> act = async () =>
            _ = await capabilities.CreateAgentAsync("Missing.Type, Missing.Assembly", "agent-y", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Unable to resolve GAgent type*");
    }

    [Fact]
    public async Task DestroyLinkAndUnlink_ShouldDelegateToRuntime()
    {
        var runtime = new RecordingRuntime();
        var capabilities = new ScriptAgentLifecycleCapabilities(runtime);

        await capabilities.LinkAgentsAsync("parent-1", "child-1", CancellationToken.None);
        await capabilities.UnlinkAgentAsync("child-1", CancellationToken.None);
        await capabilities.DestroyAgentAsync("child-1", CancellationToken.None);

        runtime.LinkedParentId.Should().Be("parent-1");
        runtime.LinkedChildId.Should().Be("child-1");
        runtime.UnlinkedChildId.Should().Be("child-1");
        runtime.DestroyedActorId.Should().Be("child-1");
    }

    private sealed class RecordingRuntime : IActorRuntime
    {
        public global::System.Type? CreatedType { get; set; }
        public string? CreatedActorId { get; set; }
        public string? DestroyedActorId { get; set; }
        public string? LinkedParentId { get; set; }
        public string? LinkedChildId { get; set; }
        public string? UnlinkedChildId { get; set; }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(global::System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            CreatedType = agentType;
            CreatedActorId = id ?? string.Empty;
            return Task.FromResult<IActor>(new FakeActor(id ?? string.Empty, new FakeTestAgent(id ?? string.Empty)));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            DestroyedActorId = id;
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            LinkedParentId = parentId;
            LinkedChildId = childId;
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            UnlinkedChildId = childId;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeActor(string id, IAgent agent) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = agent;

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeTestAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("fake");
        public Task<IReadOnlyList<global::System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<global::System.Type>>([]);
    }
}
