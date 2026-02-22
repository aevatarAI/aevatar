using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Infrastructure.Runs;

/// <summary>
/// Infrastructure adapter for workflow actor lifecycle and binding operations.
/// </summary>
internal sealed class WorkflowRunActorPort : IWorkflowRunActorPort
{
    private const string WorkflowRunActorPortPublisherId = "workflow.run.actor.port";
    private readonly IActorRuntime _runtime;
    private readonly IAgentManifestStore _manifestStore;

    public WorkflowRunActorPort(IActorRuntime runtime, IAgentManifestStore manifestStore)
    {
        _runtime = runtime;
        _manifestStore = manifestStore;
    }

    public Task<IActor?> GetAsync(string actorId, CancellationToken ct = default)
    {
        _ = ct;
        return _runtime.GetAsync(actorId);
    }

    public Task<IActor> CreateAsync(CancellationToken ct = default) =>
        _runtime.CreateAsync<WorkflowGAgent>(ct: ct);

    public Task DestroyAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("Actor id is required.", nameof(actorId));

        return _runtime.DestroyAsync(actorId, ct);
    }

    public async Task<bool> IsWorkflowActorAsync(IActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var manifest = await _manifestStore.LoadAsync(actor.Id, ct);
        return IsWorkflowAgentType(manifest?.AgentTypeName);
    }

    public async Task<string?> GetBoundWorkflowNameAsync(IActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var manifest = await _manifestStore.LoadAsync(actor.Id, ct);
        if (manifest?.Metadata == null)
            return null;

        return manifest.Metadata.TryGetValue(WorkflowManifestMetadataKeys.WorkflowName, out var workflowName)
            ? workflowName
            : null;
    }

    public Task ConfigureWorkflowAsync(
        IActor actor,
        string workflowYaml,
        string workflowName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var envelope = CreateConfigureWorkflowEnvelope(workflowYaml, workflowName);
        return actor.HandleEventAsync(envelope, ct);
    }

    private static bool IsWorkflowAgentType(string? agentTypeName)
    {
        if (string.IsNullOrWhiteSpace(agentTypeName))
            return false;

        var resolved = System.Type.GetType(agentTypeName, throwOnError: false);
        if (resolved != null)
            return typeof(WorkflowGAgent).IsAssignableFrom(resolved);

        var workflowTypeName = typeof(WorkflowGAgent).FullName ?? nameof(WorkflowGAgent);
        return agentTypeName.Contains(workflowTypeName, StringComparison.Ordinal);
    }

    private static EventEnvelope CreateConfigureWorkflowEnvelope(string workflowYaml, string workflowName) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ConfigureWorkflowEvent
            {
                WorkflowYaml = workflowYaml ?? string.Empty,
                WorkflowName = workflowName ?? string.Empty,
            }),
            PublisherId = WorkflowRunActorPortPublisherId,
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
}
