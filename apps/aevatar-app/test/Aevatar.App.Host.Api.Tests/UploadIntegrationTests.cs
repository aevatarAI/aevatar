using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Aevatar.App.Host.Api.Tests;

public sealed class UploadIntegrationTests : IClassFixture<AppTestFixture>
{
    private readonly AppTestFixture _fx;

    public UploadIntegrationTests(AppTestFixture fx) => _fx = fx;

    [Fact]
    public async Task Upload_ValidBase64_ReturnsUrl()
    {
        _fx.S3Stub.ShouldFail = false;

        using var client = _fx.CreateAuthenticatedClient("upload-ok", "upload@test.com");
        var base64 = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var res = await client.PostAsJsonAsync("/api/upload/plant-image", new
        {
            manifestationId = "m1",
            stage = "seed",
            imageData = base64,
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("imageUrl").GetString().Should().Contain("cdn.test");
    }

    [Fact]
    public async Task Upload_InvalidBase64_ReturnsBadRequest()
    {
        using var client = _fx.CreateAuthenticatedClient("upload-bad64", "bad64@test.com");

        var res = await client.PostAsJsonAsync("/api/upload/plant-image", new
        {
            manifestationId = "m2",
            stage = "seed",
            imageData = "not valid base64!!!",
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_InvalidStage_ReturnsBadRequest()
    {
        using var client = _fx.CreateAuthenticatedClient("upload-stage", "stage@test.com");

        var res = await client.PostAsJsonAsync("/api/upload/plant-image", new
        {
            manifestationId = "m3",
            stage = "invalid",
            imageData = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_StorageFails_Returns500()
    {
        _fx.S3Stub.ShouldFail = true;

        using var client = _fx.CreateAuthenticatedClient("upload-fail", "fail@test.com");
        var base64 = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var res = await client.PostAsJsonAsync("/api/upload/plant-image", new
        {
            manifestationId = "m4",
            stage = "growing",
            imageData = base64,
        });

        res.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        _fx.S3Stub.ShouldFail = false;
    }
}
