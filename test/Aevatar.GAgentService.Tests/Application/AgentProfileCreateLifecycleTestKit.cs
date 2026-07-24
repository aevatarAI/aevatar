using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Infrastructure.AgentProfiles;
using Aevatar.GAgentService.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Tests.Application;

internal sealed class MissingProofAgentProfileActorPortHarness
{
    public MissingProofAgentProfileActorPortHarness()
    {
        Runtime = new RecordingRuntime();
        Dispatch = new RecordingDispatchPort();
        Port = new AgentProfileActorPort(
            Runtime,
            Dispatch,
            new AgentProfileIngressProofService(
                Options.Create(new AgentProfileIngressProofOptions())));
    }

    public RecordingRuntime Runtime { get; }

    public RecordingDispatchPort Dispatch { get; }

    public AgentProfileActorPort Port { get; }

    internal sealed class RecordingRuntime : IActorRuntime
    {
        public List<string> GetCalls { get; } = [];

        public List<(Type ActorType, string ActorId)> CreateCalls { get; } = [];

        public List<(Type ActorType, string ActorId)> MaterializedCalls { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(
            string? id = null,
            CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(
            Type agentType,
            string? id = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? $"created:{agentType.Name}";
            CreateCalls.Add((agentType, actorId));
            return Task.FromResult<IActor>(new RecordingActor(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IActor?> GetAsync(string id)
        {
            GetCalls.Add(id);
            return Task.FromResult<IActor?>(null);
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(
            string parentId,
            string childId,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    internal sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent { get; } = new TestStaticServiceAgent();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
