using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.WorkflowStorage;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IWorkflowStoragePort"/>.
/// Writes go through <see cref="WorkflowStorageGAgent"/> event handlers.
/// </summary>
internal sealed class ActorBackedWorkflowStoragePort : IWorkflowStoragePort
{
    private const string StorageActorId = "workflow-storage";

    private readonly IActorRuntime _runtime;
    private readonly ILogger<ActorBackedWorkflowStoragePort> _logger;

    public ActorBackedWorkflowStoragePort(
        IActorRuntime runtime,
        ILogger<ActorBackedWorkflowStoragePort> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task UploadWorkflowYamlAsync(
        string workflowId, string workflowName, string yaml, CancellationToken ct)
    {
        var actor = await EnsureActorAsync(ct);
        var evt = new WorkflowYamlUploadedEvent
        {
            WorkflowId = workflowId,
            WorkflowName = workflowName,
            Yaml = yaml,
        };
        await SendCommandAsync(actor, evt, ct);
        _logger.LogDebug("Workflow YAML uploaded via actor: {WorkflowId}", workflowId);
    }

    private async Task<IActor> EnsureActorAsync(CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(StorageActorId);
        if (actor is not null)
            return actor;

        return await _runtime.CreateAsync<WorkflowStorageGAgent>(StorageActorId, ct);
    }

    private static async Task SendCommandAsync(IActor actor, IMessage command, CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };
        await actor.HandleEventAsync(envelope, ct);
    }
}
