using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

// Refactor (iter23/cluster-002):
//   Old pattern: Command ports synchronously activate projection scopes before dispatch and sometimes turn projection lease failure into command admission failure.
//   New principle: Command ports dispatch accepted commands; projection activation is owned by committed-state hooks, explicit observation binders, startup activators, or background materializers.
// Refactor (iter111/cluster-111-handled-dispatch-contract):
//   Old pattern: Public CQRS/runtime surface exposes IActorHandledDispatchPort, lets command paths synchronously wait for one actor turn, then returns DispatchAdmission.
//   New principle: Command skeleton depends only on accepted inbox dispatch; any handled/committed/readmodel stage is modeled as explicit follow-up observation or continuation event, never as dispatch ACK.
internal sealed class SkillRunnerCommandPort : ISkillRunnerCommandPort
{
    private const string PublisherActorId = "scheduled.skill-runner";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;

    public SkillRunnerCommandPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
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
