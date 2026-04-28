using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

internal sealed class WorkflowAgentCommandPort : IWorkflowAgentCommandPort
{
    private const string PublisherActorId = "scheduled.workflow-agent";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly UserAgentCatalogProjectionPort _catalogProjectionPort;

    public WorkflowAgentCommandPort(
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
        InitializeWorkflowAgentCommand command,
        bool runImmediately,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(command);

        await EnsureWorkflowAgentActorAsync(agentId, ct);
        await _catalogProjectionPort.EnsureProjectionForActorAsync(UserAgentCatalogGAgent.WellKnownId, ct);

        await DispatchAsync(agentId, command, ct);

        if (runImmediately)
        {
            await DispatchAsync(
                agentId,
                new TriggerWorkflowAgentExecutionCommand { Reason = "create_agent" },
                ct);
        }
    }

    public async Task TriggerAsync(
        string agentId,
        string reason,
        string? revisionFeedback,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        await EnsureWorkflowAgentActorAsync(agentId, ct);
        await DispatchAsync(
            agentId,
            new TriggerWorkflowAgentExecutionCommand
            {
                Reason = reason ?? string.Empty,
                RevisionFeedback = revisionFeedback ?? string.Empty,
            },
            ct);
    }

    public async Task DisableAsync(string agentId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        await EnsureWorkflowAgentActorAsync(agentId, ct);
        await DispatchAsync(agentId, new DisableWorkflowAgentCommand { Reason = reason ?? string.Empty }, ct);
    }

    public async Task EnableAsync(string agentId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        await EnsureWorkflowAgentActorAsync(agentId, ct);
        await DispatchAsync(agentId, new EnableWorkflowAgentCommand { Reason = reason ?? string.Empty }, ct);
    }

    private async Task EnsureWorkflowAgentActorAsync(string agentId, CancellationToken ct)
    {
        _ = await _actorRuntime.GetAsync(agentId)
            ?? await _actorRuntime.CreateAsync<WorkflowAgentGAgent>(agentId, ct);
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
