using System.Text;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageWorkflowStoragePort : IWorkflowStoragePort
{
    private const string WorkflowDirectory = "workflows";

    private readonly ChronoStorageCatalogBlobClient _blobClient;

    public ChronoStorageWorkflowStoragePort(ChronoStorageCatalogBlobClient blobClient)
    {
        _blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));
    }

    public async Task UploadWorkflowYamlAsync(string workflowId, string workflowName, string yaml, CancellationToken ct)
    {
        var yamlBytes = Encoding.UTF8.GetBytes(yaml);
        var key = $"{WorkflowDirectory}/{workflowId}.yaml";
        var context = _blobClient.TryResolveContext(string.Empty, key);
        if (context == null) return;

        await _blobClient.UploadAsync(context, yamlBytes, "text/yaml", ct);
    }

    public async Task<IReadOnlyList<StoredWorkflowYaml>> ListWorkflowYamlsAsync(CancellationToken ct)
    {
        var directoryContext = ResolveWorkflowDirectoryContext();
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

            var stored = await GetWorkflowYamlAsync(workflowId, ct);
            if (stored != null)
            {
                var updatedAtUtc = TryParseUpdatedAt(storageObject.LastModified) ?? stored.UpdatedAtUtc;
                workflows.Add(stored with { UpdatedAtUtc = updatedAtUtc });
            }
        }

        return workflows;
    }

    public async Task<StoredWorkflowYaml?> GetWorkflowYamlAsync(string workflowId, CancellationToken ct)
    {
        var normalizedWorkflowId = workflowId?.Trim() ?? string.Empty;
        if (normalizedWorkflowId.Length == 0)
            return null;

        var context = _blobClient.TryResolveContext(string.Empty, $"{WorkflowDirectory}/{normalizedWorkflowId}.yaml");
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

    private ChronoStorageCatalogBlobClient.RemoteScopeContext? ResolveWorkflowDirectoryContext() =>
        _blobClient.TryResolveContext(string.Empty, $"{WorkflowDirectory}/.index");

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
}
