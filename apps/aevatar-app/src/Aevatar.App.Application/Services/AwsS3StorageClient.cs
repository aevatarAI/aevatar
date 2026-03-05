using Amazon.S3;
using Amazon.S3.Model;

namespace Aevatar.App.Application.Services;

public sealed class AwsS3StorageClient : IS3StorageClient
{
    private readonly IAmazonS3 _s3;

    public AwsS3StorageClient(IAmazonS3 s3) => _s3 = s3;

    public async Task PutObjectAsync(string bucket, string key, Stream data, string contentType,
        CancellationToken ct = default)
    {
        var request = new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = data,
            ContentType = contentType,
        };
        await _s3.PutObjectAsync(request, ct);
    }

    public async Task<IReadOnlyList<string>> ListKeysAsync(string bucket, string prefix,
        CancellationToken ct = default)
    {
        var request = new ListObjectsV2Request
        {
            BucketName = bucket,
            Prefix = prefix,
        };
        var response = await _s3.ListObjectsV2Async(request, ct);
        return response.S3Objects.Select(o => o.Key).ToList();
    }

    public async Task DeleteObjectsAsync(string bucket, IReadOnlyList<string> keys,
        CancellationToken ct = default)
    {
        if (keys.Count == 0) return;
        var request = new DeleteObjectsRequest
        {
            BucketName = bucket,
            Objects = keys.Select(k => new KeyVersion { Key = k }).ToList(),
        };
        await _s3.DeleteObjectsAsync(request, ct);
    }
}
