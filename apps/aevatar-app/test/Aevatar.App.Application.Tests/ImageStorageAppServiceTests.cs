using FluentAssertions;
using Aevatar.App.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.App.Application.Tests;

public sealed class ImageStorageAppServiceTests
{
    private static readonly ImageStorageOptions DefaultOpts = new()
    {
        Region = "us-east-1",
        BucketName = "test-bucket",
        CdnUrl = "https://cdn.test.local",
        MaxFileSizeBytes = 1024,
    };

    private static (ImageStorageAppService Svc, StubS3StorageClient Stub) Create(
        ImageStorageOptions? opts = null, IS3StorageClient? s3 = null)
    {
        opts ??= DefaultOpts;
        var stub = s3 as StubS3StorageClient ?? new StubS3StorageClient();
        var svc = new ImageStorageAppService(stub, Options.Create(opts),
            NullLogger<ImageStorageAppService>.Instance);
        return (svc, stub);
    }

    [Fact]
    public void GenerateKey_FormatsCorrectly()
    {
        var key = ImageStorageAppService.GenerateKey("user123", "manifest456", "seed");
        key.Should().StartWith("user123/manifest456_seed_");
        key.Should().EndWith(".png");
    }

    [Fact]
    public void GenerateKey_DifferentStages()
    {
        var seed = ImageStorageAppService.GenerateKey("u", "m", "seed");
        var bloom = ImageStorageAppService.GenerateKey("u", "m", "blooming");
        seed.Should().Contain("_seed_");
        bloom.Should().Contain("_blooming_");
    }

    [Fact]
    public void GenerateKey_IncludesTimestamp()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var key = ImageStorageAppService.GenerateKey("u", "m", "seed");
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var parts = key.Split('_');
        var timestampStr = parts[^1].Replace(".png", "");
        var ts = long.Parse(timestampStr);
        ts.Should().BeInRange(before, after);
    }

    [Fact]
    public void GenerateKey_PrefixMatchesDeletePattern()
    {
        var key = ImageStorageAppService.GenerateKey("user1", "manifest1", "growing");
        key.Should().StartWith("user1/", "delete by prefix should match all user's files");
    }

    [Theory]
    [InlineData("test-bucket", true)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    public void IsConfigured_ReturnsExpected(string bucket, bool expected)
    {
        var opts = new ImageStorageOptions { BucketName = bucket };
        var (svc, _) = Create(opts);
        svc.IsConfigured().Should().Be(expected);
    }

    [Fact]
    public async Task UploadAsync_Success_ReturnsUrl()
    {
        var (svc, stub) = Create();
        var base64 = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        var result = await svc.UploadAsync("u1", "m1", "seed", base64);
        result.Url.Should().StartWith("https://cdn.test.local/u1/m1_seed_");
        result.Key.Should().StartWith("u1/m1_seed_");
        stub.PutCallCount.Should().Be(1);
        stub.LastPutBucket.Should().Be("test-bucket");
    }

    [Fact]
    public async Task UploadAsync_NoCdnUrl_FallsBackToS3Url()
    {
        var opts = new ImageStorageOptions
        {
            Region = "ap-southeast-1",
            BucketName = "my-bucket",
            MaxFileSizeBytes = 1024,
        };
        var (svc, _) = Create(opts);
        var base64 = Convert.ToBase64String(new byte[] { 1 });
        var result = await svc.UploadAsync("u", "m", "seed", base64);
        result.Url.Should().StartWith("https://my-bucket.s3.ap-southeast-1.amazonaws.com/u/m_seed_");
    }

    [Fact]
    public async Task UploadAsync_ExceedsMaxSize_Throws()
    {
        var opts = new ImageStorageOptions
        {
            BucketName = "b",
            MaxFileSizeBytes = 4,
        };
        var (svc, _) = Create(opts);
        var base64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 });
        var act = () => svc.UploadAsync("u", "m", "s", base64);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exceeds*");
    }

    [Fact]
    public async Task DeleteByPrefixAsync_WithObjects_DeletesThem()
    {
        var listStub = new StubS3StorageClient();
        listStub.ListKeysResult.Add("u1/file1.png");
        listStub.ListKeysResult.Add("u1/file2.png");
        var (svc, stub) = Create(s3: listStub);
        await svc.DeleteByPrefixAsync("u1/");
        stub.DeleteCallCount.Should().Be(1);
        stub.LastDeleteKeys.Should().Contain("u1/file1.png").And.Contain("u1/file2.png");
    }

    [Fact]
    public async Task DeleteByPrefixAsync_NoObjects_SkipsDelete()
    {
        var (svc, stub) = Create();
        await svc.DeleteByPrefixAsync("nonexistent/");
        stub.DeleteCallCount.Should().Be(0);
    }
}

internal sealed class StubS3StorageClient : IS3StorageClient
{
    public int PutCallCount { get; private set; }
    public string? LastPutBucket { get; private set; }
    public int DeleteCallCount { get; private set; }
    public List<string> LastDeleteKeys { get; private set; } = [];
    public List<string> ListKeysResult { get; } = [];

    public Task PutObjectAsync(string bucket, string key, Stream data, string contentType, CancellationToken ct = default)
    {
        PutCallCount++;
        LastPutBucket = bucket;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(string bucket, string prefix, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(ListKeysResult);

    public Task DeleteObjectsAsync(string bucket, IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        DeleteCallCount++;
        LastDeleteKeys = keys.ToList();
        return Task.CompletedTask;
    }
}
