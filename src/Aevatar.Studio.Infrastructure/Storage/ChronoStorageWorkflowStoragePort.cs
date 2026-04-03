using System.Text;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageWorkflowStoragePort : IWorkflowStoragePort
{
    private readonly ChronoStorageCatalogBlobClient _blobClient;

    public ChronoStorageWorkflowStoragePort(ChronoStorageCatalogBlobClient blobClient)
    {
        _blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));
    }

    public async Task UploadWorkflowYamlAsync(string workflowId, string workflowName, string yaml, CancellationToken ct)
    {
        var yamlBytes = Encoding.UTF8.GetBytes(yaml);
        var key = $"workflows/{workflowId}.yaml";
        var context = _blobClient.TryResolveContext(string.Empty, key);
        if (context == null) return;

        await _blobClient.UploadAsync(context, yamlBytes, "text/yaml", ct);
    }
}
