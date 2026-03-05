using System.Text.Json;
using Aevatar.App.Application.Errors;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.App.Application.Tests;

public sealed class AppErrorMiddlewareTests
{
    private static AppErrorMiddleware CreateMiddleware(RequestDelegate next)
        => new(next, NullLogger<AppErrorMiddleware>.Instance);

    private static DefaultHttpContext CreateContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<JsonElement> ReadResponseBody(HttpResponse response)
    {
        response.Body.Position = 0;
        return await JsonSerializer.DeserializeAsync<JsonElement>(response.Body);
    }

    [Fact]
    public async Task NoException_PassesThrough()
    {
        var nextCalled = false;
        var mw = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = CreateContext();

        await mw.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task AppException_ReturnsMappedStatusCodeAndJson()
    {
        var mw = CreateMiddleware(_ => throw new AppException("CUSTOM", "something failed", 422, new[] { "field1" }));
        var ctx = CreateContext();

        await mw.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(422);
        ctx.Response.ContentType.Should().StartWith("application/json");
        var body = await ReadResponseBody(ctx.Response);
        var err = body.GetProperty("error");
        err.GetProperty("code").GetString().Should().Be("CUSTOM");
        err.GetProperty("message").GetString().Should().Be("something failed");
        err.GetProperty("issues").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task ValidationException_Returns400()
    {
        var mw = CreateMiddleware(_ => throw new ValidationException("bad input"));
        var ctx = CreateContext();

        await mw.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(400);
        var body = await ReadResponseBody(ctx.Response);
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task NotFoundException_Returns404()
    {
        var mw = CreateMiddleware(_ => throw new NotFoundException("Widget"));
        var ctx = CreateContext();

        await mw.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(404);
        var body = await ReadResponseBody(ctx.Response);
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("NOT_FOUND");
        body.GetProperty("error").GetProperty("message").GetString().Should().Contain("Widget");
    }

    [Fact]
    public async Task LimitReachedException_Returns429()
    {
        var mw = CreateMiddleware(_ => throw new LimitReachedException("RATE_LIMIT", "slow down", "60"));
        var ctx = CreateContext();

        await mw.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(429);
        var body = await ReadResponseBody(ctx.Response);
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("RATE_LIMIT");
    }

    [Fact]
    public async Task UnauthorizedAccessException_Returns401()
    {
        var mw = CreateMiddleware(_ => throw new UnauthorizedAccessException());
        var ctx = CreateContext();

        await mw.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(401);
        var body = await ReadResponseBody(ctx.Response);
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("UNAUTHORIZED");
    }

    [Fact]
    public async Task GenericException_Returns500()
    {
        var mw = CreateMiddleware(_ => throw new InvalidOperationException("boom"));
        var ctx = CreateContext();

        await mw.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(500);
        var body = await ReadResponseBody(ctx.Response);
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("INTERNAL_ERROR");
    }
}
