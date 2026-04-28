using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

internal sealed class SkillRunnerCommandPort : ISkillRunnerCommandPort
{
    private const string PublisherActorId = "scheduled.skill-runner";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly UserAgentCatalogProjectionPort _catalogProjectionPort;

    public SkillRunnerCommandPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        UserAgentCatalogProjectionPort catalogProjectionPort)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _catalogProjectionPort = catalogProjectionPort ?? throw new ArgumentNullException(nameof(catalogProjectionPort));
    }

    public async Task InitializeAsync(
        string agentId,
        InitializeSkillRunnerCommand command,
        bool runImmediately,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(command);

        await EnsureSkillRunnerActorAsync(agentId, ct);
        // Prime the catalog projection scope BEFORE dispatch — a late prime
        // can't recover an event the projector already missed when the
        // SkillRunner emits its initialize → catalog upsert chain.
        await _catalogProjectionPort.EnsureProjectionForActorAsync(UserAgentCatalogGAgent.WellKnownId, ct);

        await DispatchAsync(agentId, command, ct);

        if (runImmediately)
        {
            await DispatchAsync(agentId, new TriggerSkillRunnerExecutionCommand { Reason = "create_agent" }, ct);
        }
    }

    public async Task TriggerAsync(string agentId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        await EnsureSkillRunnerActorAsync(agentId, ct);
        await DispatchAsync(agentId, new TriggerSkillRunnerExecutionCommand { Reason = reason ?? string.Empty }, ct);
    }

    public async Task DisableAsync(string agentId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        await EnsureSkillRunnerActorAsync(agentId, ct);
        await DispatchAsync(agentId, new DisableSkillRunnerCommand { Reason = reason ?? string.Empty }, ct);
    }

    public async Task EnableAsync(string agentId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        await EnsureSkillRunnerActorAsync(agentId, ct);
        await DispatchAsync(agentId, new EnableSkillRunnerCommand { Reason = reason ?? string.Empty }, ct);
    }

    private async Task EnsureSkillRunnerActorAsync(string agentId, CancellationToken ct)
    {
        _ = await _actorRuntime.GetAsync(agentId)
            ?? await _actorRuntime.CreateAsync<SkillRunnerGAgent>(agentId, ct);
    }

    private Task DispatchAsync<TCommand>(string agentId, TCommand command, CancellationToken ct)
        where TCommand : class, IMessage
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, agentId),
        };
        return _actorDispatchPort.DispatchAsync(agentId, envelope, ct);
    }
}
