using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.App.Application.Services;

public sealed class ImageStorageOptions
{
    public string Region { get; set; } = "";
    public string BucketName { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string CdnUrl { get; set; } = "";
    public long MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024;
}

public interface IS3StorageClient
{
    Task PutObjectAsync(string bucket, string key, Stream data, string contentType, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListKeysAsync(string bucket, string prefix, CancellationToken ct = default);
    Task DeleteObjectsAsync(string bucket, IReadOnlyList<string> keys, CancellationToken ct = default);
}

public sealed class ImageStorageAppService : IImageStorageAppService
{
    private readonly IS3StorageClient _s3;
    private readonly ImageStorageOptions _options;
    private readonly ILogger<ImageStorageAppService> _logger;

    public ImageStorageAppService(IS3StorageClient s3, IOptions<ImageStorageOptions> options,
        ILogger<ImageStorageAppService> logger)
    {
        _s3 = s3;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<UploadResult> UploadAsync(string userId, string manifestationId, string stage,
        string base64ImageData, CancellationToken ct = default)
    {
        var bytes = Convert.FromBase64String(base64ImageData);

        if (bytes.Length > _options.MaxFileSizeBytes)
            throw new InvalidOperationException(
                $"File size exceeds limit (max {_options.MaxFileSizeBytes / 1024 / 1024}MB)");

        var key = GenerateKey(userId, manifestationId, stage);

        using var stream = new MemoryStream(bytes);
        await _s3.PutObjectAsync(_options.BucketName, key, stream, "image/png", ct);

        var url = BuildUrl(key);
        _logger.LogInformation("Uploaded image to S3: {Key} -> {Url}", key, url);
        return new UploadResult(url, key);
    }

    public async Task DeleteByPrefixAsync(string prefix, CancellationToken ct = default)
    {
        var keys = await _s3.ListKeysAsync(_options.BucketName, prefix, ct);
        if (keys.Count == 0)
        {
            _logger.LogInformation("No objects found with prefix: {Prefix}", prefix);
            return;
        }

        await _s3.DeleteObjectsAsync(_options.BucketName, keys, ct);
        _logger.LogInformation("Deleted {Count} files with prefix: {Prefix}", keys.Count, prefix);
    }

    public bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(_options.BucketName);

    public static string GenerateKey(string userId, string manifestationId, string stage) =>
        $"{userId}/{manifestationId}_{stage}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png";

    private string BuildUrl(string key) =>
        !string.IsNullOrWhiteSpace(_options.CdnUrl)
            ? $"{_options.CdnUrl.TrimEnd('/')}/{key}"
            : $"https://{_options.BucketName}.s3.{_options.Region}.amazonaws.com/{key}";
}

public sealed record UploadResult(string Url, string Key);
