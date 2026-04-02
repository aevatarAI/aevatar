using System.Text;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageScriptStoragePort : IScriptStoragePort
{
    private readonly ChronoStorageCatalogBlobClient _blobClient;

    public ChronoStorageScriptStoragePort(ChronoStorageCatalogBlobClient blobClient)
    {
        _blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));
    }

    public async Task UploadScriptAsync(string scriptId, string sourceText, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(sourceText);
        var key = $"scripts/{scriptId}.cs";
        var context = _blobClient.TryResolveContext(string.Empty, key);
        if (context == null) return;

        await _blobClient.UploadAsync(context, bytes, "text/plain", ct);
    }
}
