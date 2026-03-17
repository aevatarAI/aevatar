using System.Text.Json;
using Aevatar.Tools.Cli.Bridge;
using Aevatar.Tools.Cli.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Tools.Cli.Tests;

public sealed class AppBridgeEndpointsTests
{
    [Fact]
    public async Task TryCreateProxyErrorResult_WhenBackendRedirects_ShouldReturnUnauthorizedJson()
    {
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.Found)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://backend.example/api/scopes/scope-1/workflows"),
        };
        response.Headers.Location = new Uri("https://login.example/sign-in", UriKind.Absolute);

        var created = AppBridgeEndpoints.TryCreateProxyErrorResult(response, out var result);

        created.Should().BeTrue();

        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.Body.Position = 0;
        var payload = await JsonSerializer.DeserializeAsync<AppApiErrorResponse>(
            context.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        payload.Should().NotBeNull();
        payload!.Code.Should().Be(AppApiErrors.BackendAuthRequiredCode);
        payload.LoginUrl.Should().Be("https://login.example/sign-in");
    }
}
