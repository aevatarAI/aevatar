using System.Text;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageWorkflowStoragePort : IWorkflowStoragePort
{
    private const string WorkflowDirectory = "workflows";
    private const string ExplicitScopeSource = "workflow-storage:scopeId";

    private readonly ChronoStorageCatalogBlobClient _blobClient;

    public ChronoStorageWorkflowStoragePort(ChronoStorageCatalogBlobClient blobClient)
    {
        _blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));
    }

    public async Task UploadWorkflowYamlAsync(string scopeId, string workflowId, string workflowName, string yaml, CancellationToken ct)
    {
        _ = workflowName;
        var yamlBytes = Encoding.UTF8.GetBytes(yaml);
        var normalizedWorkflowId = NormalizeRequired(workflowId, nameof(workflowId));
        var context = ResolveWorkflowContext(scopeId, $"{WorkflowDirectory}/{normalizedWorkflowId}.yaml");
        if (context == null)
            throw new InvalidOperationException("Scoped workflow draft storage is not enabled.");

        await _blobClient.UploadAsync(context, yamlBytes, "text/yaml", ct);
    }

    public async Task<IReadOnlyList<StoredWorkflowYaml>> ListWorkflowYamlsAsync(string scopeId, CancellationToken ct)
    {
        var directoryContext = ResolveWorkflowDirectoryContext(scopeId);
        if (directoryContext == null)
            return [];

        var objects = await _blobClient.ListObjectsAsync(directoryContext, WorkflowDirectory, ct);
        if (objects.Objects.Count == 0)
            return [];

        var workflows = new List<StoredWorkflowYaml>(objects.Objects.Count);
        foreach (var storageObject in objects.Objects)
        {
            var workflowId = TryResolveWorkflowId(storageObject.Key);
            if (string.IsNullOrWhiteSpace(workflowId))
                continue;

            var stored = await GetWorkflowYamlAsync(scopeId, workflowId, ct);
            if (stored is null)
                continue;

            var updatedAtUtc = TryParseUpdatedAt(storageObject.LastModified) ?? stored.UpdatedAtUtc;
            workflows.Add(stored with { UpdatedAtUtc = updatedAtUtc });
        }

        return workflows;
    }

    public async Task<StoredWorkflowYaml?> GetWorkflowYamlAsync(string scopeId, string workflowId, CancellationToken ct)
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

        var yaml = Encoding.UTF8.GetString(payload);
        return new StoredWorkflowYaml(
            normalizedWorkflowId,
            normalizedWorkflowId,
            yaml,
            UpdatedAtUtc: null);
    }

    public async Task DeleteWorkflowYamlAsync(string scopeId, string workflowId, CancellationToken ct)
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

    private static string NormalizeRequired(string? value, string paramName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"{paramName} is required.");

        return normalized;
    }
}
