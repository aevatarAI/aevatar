using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.AI.Abstractions.ToolProviders;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;

namespace Aevatar.AI.Tests;

public sealed class NyxIdApiClientAuthorizationTests
{
    [Theory]
    [InlineData(401, "unauthorized", 1001, true)]
    [InlineData(403, "forbidden", 1002, false)]
    [InlineData(401, "forbidden", 1002, false)]
    [InlineData(403, "unauthorized", 1001, false)]
    public void TryParseProxyError_ShouldClassifyOnlyPublishedInvalidCredentialTuple(
        int status,
        string errorKey,
        int errorCode,
        bool authorizationRequired)
    {
        var response = $$"""
            {"error":true,"status":{{status}},"body":"{\"error\":\"{{errorKey}}\",\"error_code\":{{errorCode}},\"message\":\"unstable text must not affect classification\"}"}
            """;

        NyxIdApiClient.TryParseProxyError(response, out var error).Should().BeTrue();
        error.Should().NotBeNull();
        error!.HttpStatus.Should().Be(status);
        error.ErrorKey.Should().Be(errorKey);
        error.ErrorCode.Should().Be(errorCode);
        error.IsAuthorizationRequired.Should().Be(authorizationRequired);
    }

    [Fact]
    public void TryParseProxyError_ShouldRecognizeOrdinaryUpstreamFailureWithoutInspectingBody()
    {
        const string response =
            """{"error":true,"status":403,"body":"{\"message\":\"upstream bearer-secret\",\"documentation_url\":\"https://example.test?token=query-secret\"}"}""";

        NyxIdApiClient.TryParseProxyError(response, out var error).Should().BeTrue();
        error.Should().NotBeNull();
        error!.HttpStatus.Should().Be(403);
        error.ErrorKey.Should().BeEmpty();
        error.ErrorCode.Should().Be(0);
        error.IsAuthorizationRequired.Should().BeFalse();
    }

    [Fact]
    public async Task ProxyRequest_ShouldNotLogQueryOrRawToolArguments()
    {
        var clientLogger = new RecordingLogger<NyxIdApiClient>();
        var toolLogger = new RecordingLogger<NyxIdProxyTool>();
        using var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(new FixedResponseHandler(
                HttpStatusCode.Forbidden,
                """{"error":"forbidden","error_code":1002,"message":"upstream bearer-secret"}""")),
            clientLogger);
        var tool = new NyxIdProxyTool(client, toolLogger);
        using var _scope = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials("request-token-secret", null, null),
        });

        await tool.ExecuteAsync(
            """{"slug":"api-github","path":"/repos/private?access_token=query-secret","headers":{"X-Credential":"header-secret"}}""");

        clientLogger.Output.Should().NotContain("query-secret").And.NotContain("request-token-secret");
        toolLogger.Output.Should()
            .NotContain("query-secret")
            .And.NotContain("header-secret")
            .And.NotContain("request-token-secret");
    }

    [Fact]
    public async Task ProxyTransportFailure_ShouldReturnAndLogOnlySafeDiagnostics()
    {
        var logger = new RecordingLogger<NyxIdApiClient>();
        using var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(new ThrowingHandler(
                new HttpRequestException("transport failed for https://upstream.test?token=query-secret"))),
            logger);

        var result = await client.ProxyRequestAsync(
            "request-token-secret",
            "api-github",
            "/repos/private?access_token=query-secret",
            "GET",
            null,
            null,
            CancellationToken.None);

        result.Should().NotContain("query-secret").And.NotContain("request-token-secret");
        logger.Output.Should().NotContain("query-secret").And.NotContain("request-token-secret");
        NyxIdApiClient.TryParseProxyError(result, out var error).Should().BeTrue();
        error.Should().NotBeNull();
        error!.IsAuthorizationRequired.Should().BeFalse();
    }

    private sealed class FixedResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<string> _entries = [];

        public string Output => string.Join('\n', _entries);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add(formatter(state, exception));
            if (exception is not null)
                _entries.Add(exception.ToString());
        }
    }
}
