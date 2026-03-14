using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class ProjectionWorkflowActorBindingReader : IWorkflowActorBindingReader
{
    private readonly Func<string, CancellationToken, Task> _ensureProjectionAsync;
    private readonly Func<string, CancellationToken, Task<WorkflowActorBindingDocument?>> _getDocumentAsync;
    private readonly Func<string, Task<bool>> _existsAsync;
    private readonly Func<string, Type, CancellationToken, Task<bool>> _isExpectedAsync;

    public ProjectionWorkflowActorBindingReader(
        WorkflowBindingProjectionPortService projectionPort,
        IProjectionDocumentStore<WorkflowActorBindingDocument, string> documentStore,
        IActorRuntime runtime,
        IAgentTypeVerifier agentTypeVerifier)
    {
        ArgumentNullException.ThrowIfNull(projectionPort);
        ArgumentNullException.ThrowIfNull(documentStore);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(agentTypeVerifier);

        _ensureProjectionAsync = async (actorId, ct) =>
        {
            _ = await projectionPort.EnsureActorProjectionAsync(actorId, ct);
        };
        _getDocumentAsync = (actorId, ct) => documentStore.GetAsync(actorId, ct);
        _existsAsync = runtime.ExistsAsync;
        _isExpectedAsync = agentTypeVerifier.IsExpectedAsync;
    }

    internal ProjectionWorkflowActorBindingReader(
        Func<string, CancellationToken, Task> ensureProjectionAsync,
        Func<string, CancellationToken, Task<WorkflowActorBindingDocument?>> getDocumentAsync,
        Func<string, Task<bool>> existsAsync,
        Func<string, Type, CancellationToken, Task<bool>> isExpectedAsync)
    {
        _ensureProjectionAsync = ensureProjectionAsync ?? throw new ArgumentNullException(nameof(ensureProjectionAsync));
        _getDocumentAsync = getDocumentAsync ?? throw new ArgumentNullException(nameof(getDocumentAsync));
        _existsAsync = existsAsync ?? throw new ArgumentNullException(nameof(existsAsync));
        _isExpectedAsync = isExpectedAsync ?? throw new ArgumentNullException(nameof(isExpectedAsync));
    }

    public async Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        if (!await _existsAsync(actorId))
            return null;

        var actorKind = await ResolveActorKindAsync(actorId, ct);
        if (actorKind == WorkflowActorKind.Unsupported)
            return WorkflowActorBinding.Unsupported(actorId);

        await _ensureProjectionAsync(actorId, ct);
        var document = await _getDocumentAsync(actorId, ct);
        return document == null
            ? CreateUnboundBinding(actorId, actorKind)
            : MapDocument(document, actorId, actorKind);
    }

    private async Task<WorkflowActorKind> ResolveActorKindAsync(string actorId, CancellationToken ct)
    {
        if (await _isExpectedAsync(actorId, typeof(WorkflowGAgent), ct))
            return WorkflowActorKind.Definition;
        if (await _isExpectedAsync(actorId, typeof(WorkflowRunGAgent), ct))
            return WorkflowActorKind.Run;

        return WorkflowActorKind.Unsupported;
    }

    private static WorkflowActorBinding CreateUnboundBinding(string actorId, WorkflowActorKind actorKind) =>
        new(
            actorKind,
            actorId,
            actorKind == WorkflowActorKind.Definition ? actorId : string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static WorkflowActorBinding MapDocument(
        WorkflowActorBindingDocument document,
        string fallbackActorId,
        WorkflowActorKind fallbackActorKind)
    {
        ArgumentNullException.ThrowIfNull(document);

        var actorId = string.IsNullOrWhiteSpace(document.ActorId)
            ? fallbackActorId
            : document.ActorId;
        var actorKind = document.ActorKind == WorkflowActorKind.Unsupported
            ? fallbackActorKind
            : document.ActorKind;
        var definitionActorId = string.IsNullOrWhiteSpace(document.DefinitionActorId) && actorKind == WorkflowActorKind.Definition
            ? actorId
            : document.DefinitionActorId ?? string.Empty;

        return new WorkflowActorBinding(
            actorKind,
            actorId,
            definitionActorId,
            document.RunId ?? string.Empty,
            document.WorkflowName ?? string.Empty,
            document.WorkflowYaml ?? string.Empty,
            new Dictionary<string, string>(document.InlineWorkflowYamls, StringComparer.OrdinalIgnoreCase));
    }
}
