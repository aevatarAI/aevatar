namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Port for uploading script source artifacts to external storage (e.g., chrono-storage).
/// </summary>
public interface IScriptStoragePort
{
    Task UploadScriptAsync(string scriptId, string sourceText, CancellationToken ct);
}
