using System.IO.Compression;
using Aevatar.BackendConsole.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Capabilities.Tests;

[Collection(ProcessEnvSerialCollection.Name)]
public sealed class BackendConsoleAssetServeTests
{
    private static readonly BackendConsoleAsset FixtureAsset = new(
        "fixture",
        typeof(BackendConsoleAssetServeTests).Assembly,
        "BackendConsoleAssetServiceTests.fixture.html",
        "text/html",
        InjectHostConfiguration: true);

    [Fact]
    public async Task Serve_ShouldReturnIdentityBodyWithConditionalCacheHeaders()
    {
        var assets = BuildAssetService();

        var http = await ExecuteAsync(assets, configureRequest: null);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        http.Response.ContentType.Should().Be("text/html; charset=utf-8");
        http.Response.Headers.ETag.ToString().Should().MatchRegex("^\"[0-9a-f]{64}\"$");
        http.Response.Headers.CacheControl.ToString().Should().Be("no-cache");
        http.Response.Headers.Vary.ToString().Should().Be("Accept-Encoding");
        http.Response.Headers.ContentEncoding.ToString().Should().BeEmpty();
        ReadBody(http).Should().Contain("const cfg = {").And.NotContain("__BACKEND_CONSOLE_CONFIG__");
    }

    [Fact]
    public async Task Serve_ShouldReturn304WhenIfNoneMatchMatchesTheServedETag()
    {
        var assets = BuildAssetService();
        var first = await ExecuteAsync(assets, configureRequest: null);
        var etag = first.Response.Headers.ETag.ToString();

        var revalidated = await ExecuteAsync(assets, request => request.Headers.IfNoneMatch = etag);

        revalidated.Response.StatusCode.Should().Be(StatusCodes.Status304NotModified);
        revalidated.Response.Body.Length.Should().Be(0);
        revalidated.Response.Headers.ETag.ToString().Should().Be(etag);
        revalidated.Response.Headers.CacheControl.ToString().Should().Be("no-cache");
    }

    [Fact]
    public async Task Serve_ShouldStayFullWhenIfNoneMatchDiffers()
    {
        var assets = BuildAssetService();

        var http = await ExecuteAsync(assets, request => request.Headers.IfNoneMatch = "\"different\"");

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        ReadBody(http).Should().Contain("const cfg = {");
    }

    [Fact]
    public async Task Serve_ShouldPreferBrotliWhenAcceptEncodingAllowsBoth()
    {
        var assets = BuildAssetService();

        var http = await ExecuteAsync(assets, request => request.Headers.AcceptEncoding = "gzip, br");

        http.Response.Headers.ContentEncoding.ToString().Should().Be("br");
        Decompress(http, static stream => new BrotliStream(stream, CompressionMode.Decompress))
            .Should().Be(RenderedHtml(assets));
    }

    [Fact]
    public async Task Serve_ShouldFallBackToGzipWhenBrotliIsDeclined()
    {
        var assets = BuildAssetService();

        var http = await ExecuteAsync(assets, request => request.Headers.AcceptEncoding = "br;q=0, gzip");

        http.Response.Headers.ContentEncoding.ToString().Should().Be("gzip");
        Decompress(http, static stream => new GZipStream(stream, CompressionMode.Decompress))
            .Should().Be(RenderedHtml(assets));
    }

    [Fact]
    public async Task Serve_ShouldTreatWildcardAcceptEncodingAsCompressible()
    {
        var assets = BuildAssetService();

        var http = await ExecuteAsync(assets, request => request.Headers.AcceptEncoding = "*");

        http.Response.Headers.ContentEncoding.ToString().Should().Be("br");
    }

    [Fact]
    public async Task Serve_ShouldReturnTheSameETagAcrossRequests()
    {
        var assets = BuildAssetService();

        var first = await ExecuteAsync(assets, configureRequest: null);
        var second = await ExecuteAsync(assets, configureRequest: null);

        second.Response.Headers.ETag.ToString().Should().Be(first.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task Serve_ShouldOmitTheBodyForHeadRequests()
    {
        var assets = BuildAssetService();

        var http = await ExecuteAsync(assets, request => request.Method = HttpMethods.Head);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        http.Response.ContentLength.Should().BeGreaterThan(0);
        http.Response.Body.Length.Should().Be(0);
    }

    private static IBackendConsoleAssetService BuildAssetService()
    {
        var services = new ServiceCollection();
        services.AddBackendConsoleStaticAssets(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider().GetRequiredService<IBackendConsoleAssetService>();
    }

    private static async Task<DefaultHttpContext> ExecuteAsync(
        IBackendConsoleAssetService assets,
        Action<HttpRequest>? configureRequest)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        configureRequest?.Invoke(http.Request);
        http.Response.Body = new MemoryStream();

        await assets.Serve(FixtureAsset).ExecuteAsync(http);
        return http;
    }

    private static string RenderedHtml(IBackendConsoleAssetService assets) => assets.Render(FixtureAsset);

    private static string ReadBody(DefaultHttpContext http)
    {
        http.Response.Body.Position = 0;
        using var reader = new StreamReader(http.Response.Body, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static string Decompress(DefaultHttpContext http, Func<Stream, Stream> decompressorFactory)
    {
        http.Response.Body.Position = 0;
        using var decompressor = decompressorFactory(http.Response.Body);
        using var reader = new StreamReader(decompressor);
        return reader.ReadToEnd();
    }
}
