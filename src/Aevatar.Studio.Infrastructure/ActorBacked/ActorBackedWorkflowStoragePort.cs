using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.WorkflowStorage;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IWorkflowStoragePort"/>.
/// Writes go through <see cref="WorkflowStorageGAgent"/> event handlers.
/// Per-scope isolation: each scope gets its own <c>workflow-storage-{scopeId}</c> actor.
/// </summary>
internal sealed class ActorBackedWorkflowStoragePort : IWorkflowStoragePort
{
    private const string StorageActorIdPrefix = "workflow-storage-";

    private readonly IActorRuntime _runtime;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly ILogger<ActorBackedWorkflowStoragePort> _logger;

    public ActorBackedWorkflowStoragePort(
        IActorRuntime runtime,
        IAppScopeResolver scopeResolver,
        ILogger<ActorBackedWorkflowStoragePort> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
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

    private string ResolveScopeId()
    {
        var scope = _scopeResolver.Resolve();
        return scope?.ScopeId ?? "default";
    }

    private string ResolveStorageActorId() => StorageActorIdPrefix + ResolveScopeId();

    private async Task<IActor> EnsureActorAsync(CancellationToken ct)
    {
        var actorId = ResolveStorageActorId();
        var actor = await _runtime.GetAsync(actorId);
        if (actor is not null)
            return actor;

        return await _runtime.CreateAsync<WorkflowStorageGAgent>(actorId, ct);
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
