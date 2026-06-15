using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aevatar.Bootstrap.Connectors;
using Aevatar.Foundation.Abstractions.Connectors;
using FluentAssertions;

namespace Aevatar.Bootstrap.Tests;

public sealed class ConnectorIdempotencyTests
{
    [Fact]
    public async Task HttpConnector_ShouldSendIdempotencyKeyHeaderWhenPresent()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
                ReasonPhrase = "OK",
            });
        var connector = new HttpConnector(
            "idem-http",
            "https://example.com",
            allowedMethods: ["POST"],
            allowedPaths: ["/invoke"],
            client: new HttpClient(handler));

        var response = await connector.ExecuteAsync(new ConnectorRequest
        {
            Operation = "/invoke",
            IdempotencyKey = " idem-http-1 ",
            Parameters = new Dictionary<string, string> { ["method"] = "POST" },
        });

        response.Success.Should().BeTrue();
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.GetValues("Idempotency-Key").Should().ContainSingle().Which.Should().Be("idem-http-1");
    }

    [Fact]
    public async Task HostCallbackConnector_ShouldExposeTypedIdempotencyKey()
    {
        var handler = new RecordingHostCallbackHandler();
        var connector = new HostCallbackConnector(
            "host-github",
            "github",
            handler,
            allowedOperations: ["classify_pr"],
            allowedInputKeys: ["issue", "repo"]);

        var response = await connector.ExecuteAsync(new ConnectorRequest
        {
            RunId = "run-1738",
            StepId = "classify",
            IdempotencyKey = "idem-host-1",
            Operation = "classify_pr",
            Payload = """{"issue":"1738","repo":"aevatar"}""",
        });

        response.Success.Should().BeTrue();
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.IdempotencyKey.Should().Be("idem-host-1");
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            LastRequest = request;
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class RecordingHostCallbackHandler : IHostCallbackConnectorHandler
    {
        public string Name => "github";

        public HostCallbackConnectorRequest? LastRequest { get; private set; }

        public Task<HostCallbackConnectorResponse> HandleAsync(
            HostCallbackConnectorRequest request,
            CancellationToken ct = default)
        {
            _ = ct;
            LastRequest = request;
            return Task.FromResult(new HostCallbackConnectorResponse
            {
                Success = true,
                Result = new JsonObject
                {
                    ["ok"] = true,
                },
            });
        }
    }
}
