using Aevatar.Studio.Application.Protos;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Domain.Studio.Models;
using Google.Protobuf;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageWorkflowDraftStore : IWorkflowDraftStore
{
    private const string WorkflowDirectory = "workflows";
    private const string ExplicitScopeSource = "workflow-draft-store:scopeId";

    private readonly ChronoStorageCatalogBlobClient _blobClient;

    public ChronoStorageWorkflowDraftStore(ChronoStorageCatalogBlobClient blobClient)
    {
        _blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));
    }

    public async Task SaveDraftAsync(
        string scopeId,
        string workflowId,
        string workflowName,
        string yaml,
        WorkflowLayoutDocument? layout,
        CancellationToken ct)
    {
        var normalizedWorkflowId = NormalizeRequired(workflowId, nameof(workflowId));
        var context = ResolveWorkflowContext(scopeId, $"{WorkflowDirectory}/{normalizedWorkflowId}.yaml");
        if (context == null)
            throw new InvalidOperationException("Scoped workflow draft storage is not enabled.");

        var fact = new ScopedWorkflowDraftFact
        {
            WorkflowId = normalizedWorkflowId,
            WorkflowName = workflowName,
            Yaml = yaml,
            Layout = layout is null ? null : ToProtoLayout(layout),
        };
        await _blobClient.UploadAsync(context, fact.ToByteArray(), "application/x-protobuf", ct);
    }

    public async Task<IReadOnlyList<WorkflowDraft>> ListDraftsAsync(string scopeId, CancellationToken ct)
    {
        var directoryContext = ResolveWorkflowDirectoryContext(scopeId);
        if (directoryContext == null)
            return [];

        var objects = await _blobClient.ListObjectsAsync(directoryContext, WorkflowDirectory, ct);
        if (objects.Objects.Count == 0)
            return [];

        var drafts = new List<WorkflowDraft>(objects.Objects.Count);
        foreach (var storageObject in objects.Objects)
        {
            var workflowId = TryResolveWorkflowId(storageObject.Key);
            if (string.IsNullOrWhiteSpace(workflowId))
                continue;

            var draft = await GetDraftAsync(scopeId, workflowId, ct);
            if (draft is null)
                continue;

            var updatedAtUtc = TryParseUpdatedAt(storageObject.LastModified) ?? draft.UpdatedAtUtc;
            drafts.Add(draft with { UpdatedAtUtc = updatedAtUtc });
        }

        return drafts;
    }

    public async Task<WorkflowDraft?> GetDraftAsync(string scopeId, string workflowId, CancellationToken ct)
    {
        var normalizedWorkflowId = workflowId?.Trim() ?? string.Empty;
        if (normalizedWorkflowId.Length == 0)
            return null;

        var context = ResolveWorkflowContext(scopeId, $"{WorkflowDirectory}/{normalizedWorkflowId}.yaml");
        if (context == null)
            return null;

        var payload = await _blobClient.TryDownloadAsync(context, ct);
        if (payload == null || payload.Length == 0)
            return null;

        var fact = ScopedWorkflowDraftFact.Parser.ParseFrom(payload);
        return new WorkflowDraft(
            normalizedWorkflowId,
            string.IsNullOrWhiteSpace(fact.WorkflowName) ? normalizedWorkflowId : fact.WorkflowName,
            fact.Yaml,
            UpdatedAtUtc: null,
            Layout: fact.Layout is null ? null : ToApplicationLayout(fact.Layout));
    }

    public async Task DeleteDraftAsync(string scopeId, string workflowId, CancellationToken ct)
    {
        var normalizedWorkflowId = workflowId?.Trim() ?? string.Empty;
        if (normalizedWorkflowId.Length == 0)
            return;

        var context = ResolveWorkflowContext(scopeId, $"{WorkflowDirectory}/{normalizedWorkflowId}.yaml");
        if (context == null)
            return;

        await _blobClient.DeleteIfExistsAsync(context, ct);
    }

    private ChronoStorageCatalogBlobClient.RemoteScopeContext? ResolveWorkflowDirectoryContext(string scopeId) =>
        ResolveWorkflowContext(scopeId, $"{WorkflowDirectory}/.index");

    private ChronoStorageCatalogBlobClient.RemoteScopeContext? ResolveWorkflowContext(string scopeId, string relativeKey)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        return _blobClient.TryResolveContext(
            new AppScopeContext(normalizedScopeId, ExplicitScopeSource),
            string.Empty,
            relativeKey);
    }

    private static string? TryResolveWorkflowId(string relativeKey)
    {
        if (string.IsNullOrWhiteSpace(relativeKey))
            return null;

        var normalizedKey = relativeKey.Trim();
        if (!normalizedKey.StartsWith($"{WorkflowDirectory}/", StringComparison.Ordinal) ||
            !normalizedKey.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFileNameWithoutExtension(normalizedKey);
    }

    private static DateTimeOffset? TryParseUpdatedAt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return DateTimeOffset.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static ScopedWorkflowLayoutFact ToProtoLayout(WorkflowLayoutDocument layout)
    {
        var fact = new ScopedWorkflowLayoutFact
        {
            Viewport = new ScopedWorkflowViewportFact
            {
                X = layout.Viewport.X,
                Y = layout.Viewport.Y,
                Zoom = layout.Viewport.Zoom,
            },
            EntryWorkflow = layout.EntryWorkflow ?? string.Empty,
        };
        fact.Nodes.AddRange(layout.NodePositions.Select(item => new ScopedWorkflowNodeLayoutFact
        {
            NodeId = item.Key,
            X = item.Value.X,
            Y = item.Value.Y,
        }));
        fact.Groups.AddRange(layout.Groups.Select(item =>
        {
            var group = new ScopedWorkflowLayoutGroupFact { GroupId = item.Key };
            group.NodeIds.AddRange(item.Value);
            return group;
        }));
        fact.Collapsed.AddRange(layout.Collapsed);
        return fact;
    }

    private static WorkflowLayoutDocument ToApplicationLayout(ScopedWorkflowLayoutFact layout)
    {
        return new WorkflowLayoutDocument
        {
            NodePositions = layout.Nodes.ToDictionary(
                node => node.NodeId,
                node => new WorkflowNodeLayout(node.X, node.Y),
                StringComparer.Ordinal),
            Groups = layout.Groups.ToDictionary(
                group => group.GroupId,
                group => group.NodeIds.ToList(),
                StringComparer.Ordinal),
            Collapsed = layout.Collapsed.ToList(),
            Viewport = layout.Viewport is null
                ? new WorkflowViewport()
                : new WorkflowViewport(layout.Viewport.X, layout.Viewport.Y, layout.Viewport.Zoom),
            EntryWorkflow = string.IsNullOrWhiteSpace(layout.EntryWorkflow) ? null : layout.EntryWorkflow,
        };
    }

    private static string NormalizeRequired(string? value, string paramName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"{paramName} is required.");

        return normalized;
    }
}
