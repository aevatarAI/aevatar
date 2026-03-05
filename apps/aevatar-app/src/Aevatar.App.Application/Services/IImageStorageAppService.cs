namespace Aevatar.App.Application.Services;

public interface IImageStorageAppService
{
    Task<UploadResult> UploadAsync(string userId, string manifestationId, string stage,
        string base64ImageData, CancellationToken ct = default);

    Task DeleteByPrefixAsync(string prefix, CancellationToken ct = default);

    bool IsConfigured();
}
