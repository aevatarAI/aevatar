using System.Net;
using Aevatar.Tools.Cli.Hosting;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class AppApiClientTests
{
    [Fact]
    public async Task StreamDraftRunAsync_WhenBearerTokenProvided_ShouldAttachAuthorizationHeader()
    {
        var handler = new RecordingHttpMessageHandler((request, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n"),
                RequestMessage = request,
            }));
        using var client = new AppApiClient(
            "https://runtime.example",
            handler,
            "token-123");

        using var response = await client.StreamDraftRunAsync(
            "scope-a",
            "hello",
            ["""
            name: test
            roles: []
            steps: []
            """],
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.Authorization.Should().NotBeNull();
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be("token-123");
    }

    private sealed class RecordingHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = await responseFactory(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }
}
