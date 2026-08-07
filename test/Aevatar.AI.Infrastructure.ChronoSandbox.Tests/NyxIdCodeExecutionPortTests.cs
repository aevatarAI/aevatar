using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Infrastructure.ChronoSandbox.Tests;

public sealed class NyxIdCodeExecutionPortTests
{
    private const string BearerToken = "caller-bearer";
    private const string CodeServiceId = "us-code-alpha";

    [Fact]
    public async Task ExecuteAsync_ResolvesExactCodeServiceAndUsesBearerProxyExecution()
    {
        var handler = new SequenceHandler(
            JsonResponse(Inventory(
                Service("us-code-no-forward", "chrono-sandbox", false, true, "proxy:*"),
                Service("us-code-no-delegation", "chrono-sandbox", true, false, "proxy:*"),
                Service("us-code-wrong-scope", "chrono-sandbox", true, true, "sandbox:execute"),
                Service(CodeServiceId, "chrono-sandbox", true, true, "proxy:*"))),
            JsonResponse(
                """
                {
                  "success": true,
                  "output": {
                    "stdout": "42\n",
                    "stderr": "",
                    "exit_code": 0,
                    "execution_time_ms": 17
                  },
                  "diagnostic_id": "diag-code-1"
                }
                """));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(userServiceId: null));

        outcome.Failure.Should().BeNull();
        outcome.Result.Should().Be(new CodeExecutionResult("42\n", "", 0, "diag-code-1", 17));
        outcome.ResolvedRoute.Should().Be(new CodeExecutionRouteIdentity(
            "chrono-sandbox",
            CodeServiceId,
            CodeExecutionRouteIdentitySource.NyxIdUserServiceCatalog));
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Method.Should().Be(HttpMethod.Get.Method);
        handler.Requests[0].PathAndQuery.Should().Be("/api/v1/user-services");
        handler.Requests[1].Method.Should().Be(HttpMethod.Post.Method);
        handler.Requests[1].PathAndQuery.Should().Be(
            "/api/v1/proxy/s/chrono-sandbox/execute?_nyxid_via=us-code-alpha");
        handler.Requests.Should().OnlyContain(request => request.Authorization == $"Bearer {BearerToken}");
        handler.Requests.Should().OnlyContain(request => request.ApiKeys.Count == 0);
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        body.RootElement.EnumerateObject().Select(static property => property.Name)
            .Should().Equal("language", "script");
        body.RootElement.GetProperty("language").GetString().Should().Be("python");
        body.RootElement.GetProperty("script").GetString().Should().Be("print(42)");
    }

    [Fact]
    public async Task ExecuteAsync_WhenRouteSlugIsNotCanonical_FailsBeforeNyxIdCall()
    {
        var handler = new SequenceHandler();
        var port = CreatePort(handler);
        var request = Request(CodeServiceId) with
        {
            Route = new CodeExecutionRouteIdentity(
                "chrono-managed-codex",
                CodeServiceId,
                CodeExecutionRouteIdentitySource.CodeExecutionContract),
        };

        var outcome = await port.ExecuteAsync(request);

        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.AdmissionDenied,
            Code = "code_execution_request_invalid",
        });
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(CodeExecutionLanguage.Unspecified)]
    [InlineData((CodeExecutionLanguage)999)]
    public async Task ExecuteAsync_WhenLanguageIsOutsideContract_FailsBeforeNyxIdCall(
        CodeExecutionLanguage language)
    {
        var handler = new SequenceHandler();
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId) with { Language = language });

        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.AdmissionDenied,
            Code = "code_execution_request_invalid",
        });
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, true, "proxy:*")]
    [InlineData(true, false, "proxy:*")]
    [InlineData(true, true, "sandbox:execute")]
    [InlineData(true, true, "proxy:* admin")]
    [InlineData(true, true, "proxy:* sandbox:execute admin")]
    public async Task ExecuteAsync_WhenSharedServiceViolatesExecutionPolicy_FailsClosedBeforeProxy(
        bool forwardAccessToken,
        bool injectDelegationToken,
        string delegationTokenScope)
    {
        var handler = new SequenceHandler(JsonResponse(Inventory(
            Service(
                CodeServiceId,
                "chrono-sandbox",
                forwardAccessToken,
                injectDelegationToken,
                delegationTokenScope))));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Result.Should().BeNull();
        outcome.ResolvedRoute.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.TargetNotConfigured,
            Code = "code_execution_route_unavailable",
        });
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].PathAndQuery.Should().Be("/api/v1/user-services");
    }

    [Theory]
    [InlineData(true, "proxy:*")]
    [InlineData(true, "proxy:* sandbox:execute")]
    [InlineData(true, "sandbox:execute proxy:*")]
    [InlineData(false, "proxy:* sandbox:execute")]
    [InlineData(false, "sandbox:execute proxy:*")]
    public async Task ExecuteAsync_WhenSharedServiceHasAllowedTransitionPolicy_UsesExactRoute(
        bool forwardAccessToken,
        string delegationTokenScope)
    {
        var handler = new SequenceHandler(
            JsonResponse(Inventory(Service(
                CodeServiceId,
                "chrono-sandbox",
                forwardAccessToken,
                true,
                delegationTokenScope))),
            JsonResponse(
                """
                {
                  "success": true,
                  "output": {
                    "stdout": "ok",
                    "stderr": "",
                    "exit_code": 0
                  }
                }
                """));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Failure.Should().BeNull();
        outcome.ResolvedRoute!.UserServiceId.Should().Be(CodeServiceId);
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].PathAndQuery.Should().Be(
            "/api/v1/proxy/s/chrono-sandbox/execute?_nyxid_via=us-code-alpha");
    }

    [Fact]
    public async Task ExecuteAsync_WhenSharedServiceIsInactive_FailsClosedBeforeProxy()
    {
        var handler = new SequenceHandler(JsonResponse(Inventory(Service(
            CodeServiceId,
            "chrono-sandbox",
            true,
            true,
            "proxy:*",
            isActive: false))));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        AssertRouteUnavailable(outcome, handler);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSharedServiceIsNotPersonal_FailsClosedBeforeProxy()
    {
        var handler = new SequenceHandler(JsonResponse(Inventory(Service(
            CodeServiceId,
            "chrono-sandbox",
            true,
            true,
            "proxy:*",
            credentialSourceType: "platform"))));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        AssertRouteUnavailable(outcome, handler);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSharedServiceOmitsForwardingPolicy_FailsClosedBeforeProxy()
    {
        var handler = new SequenceHandler(JsonResponse(
            """
            {
              "services": [{
                "id": "us-code-alpha",
                "slug": "chrono-sandbox",
                "is_active": true,
                "inject_delegation_token": true,
                "delegation_token_scope": "proxy:* sandbox:execute",
                "credential_source": { "type": "personal" }
              }]
            }
            """));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        AssertRouteUnavailable(outcome, handler);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSharedServiceOmitsCredentialSource_FailsClosedBeforeProxy()
    {
        var handler = new SequenceHandler(JsonResponse(
            """
            {
              "services": [{
                "id": "us-code-alpha",
                "slug": "chrono-sandbox",
                "is_active": true,
                "forward_access_token": true,
                "inject_delegation_token": true,
                "delegation_token_scope": "proxy:*"
              }]
            }
            """));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        AssertRouteUnavailable(outcome, handler);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMultipleSharedServicesMatch_FailsClosedBeforeProxy()
    {
        var handler = new SequenceHandler(JsonResponse(Inventory(
            Service("us-code-alpha", "chrono-sandbox", true, true, "proxy:*"),
            Service("us-code-beta", "chrono-sandbox", true, true, "proxy:* sandbox:execute"))));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(userServiceId: null));

        AssertRouteUnavailable(outcome, handler);
    }

    [Fact]
    public async Task ExecuteAsync_WhenProcessExitsNonZero_PreservesResultAndTypedFailure()
    {
        var handler = HandlerWithProxyResponse(JsonResponse(
            """
            {
              "success": false,
              "output": {
                "stdout": "partial output",
                "stderr": "traceback",
                "exit_code": 7,
                "execution_time_ms": 31
              },
              "error": {
                "code": "EXECUTION_FAILED",
                "message": "untrusted upstream detail"
              },
              "diagnostic_id": "diag-code-2"
            }
            """));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Result.Should().Be(new CodeExecutionResult(
            "partial output",
            "traceback",
            7,
            "diag-code-2",
            31));
        outcome.Failure.Should().Be(new CodeExecutionFailure(
            CodeExecutionFailureKind.ExecutionFailed,
            "EXECUTION_FAILED",
            "Code execution exited unsuccessfully.",
            "diag-code-2"));
        outcome.ResolvedRoute!.UserServiceId.Should().Be(CodeServiceId);
    }

    [Fact]
    public async Task ExecuteAsync_WhenProcessFailureCodeIsNotOwned_UsesStableInternalCode()
    {
        var port = CreatePort(HandlerWithProxyResponse(JsonResponse(
            """
            {
              "success": false,
              "output": {
                "stdout": "partial output",
                "stderr": "provider detail",
                "exit_code": 9
              },
              "error": {
                "code": "UNTRUSTED_PROVIDER_CODE",
                "message": "untrusted upstream detail"
              }
            }
            """)));

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Result.Should().NotBeNull();
        outcome.Failure.Should().Be(new CodeExecutionFailure(
            CodeExecutionFailureKind.ExecutionFailed,
            "code_execution_failed",
            "Code execution failed."));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"success\":true,\"output\":{\"stdout\":\"ok\",\"stderr\":\"\"}}")]
    [InlineData("{\"success\":true,\"output\":{\"stdout\":\"ok\",\"stderr\":\"\",\"exit_code\":1}}")]
    public async Task ExecuteAsync_WhenProxyOutputIsMalformed_ReturnsTypedFailure(string responseBody)
    {
        var port = CreatePort(HandlerWithProxyResponse(JsonResponse(responseBody)));

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Result.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.MalformedOutput,
            Code = "code_execution_response_invalid",
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CodeExecutionFailureKind.AdmissionDenied, "NYXID_PROXY_UNAUTHORIZED")]
    [InlineData(HttpStatusCode.Forbidden, CodeExecutionFailureKind.AdmissionDenied, "NYXID_PROXY_FORBIDDEN")]
    [InlineData(HttpStatusCode.NotFound, CodeExecutionFailureKind.TargetNotConfigured, "NYXID_PROXY_HTTP_404")]
    [InlineData(HttpStatusCode.TooManyRequests, CodeExecutionFailureKind.TransportUnavailable, "NYXID_PROXY_HTTP_429")]
    [InlineData(HttpStatusCode.BadGateway, CodeExecutionFailureKind.TransportUnavailable, "NYXID_PROXY_HTTP_502")]
    public async Task ExecuteAsync_WhenProxyRejectsRequest_ClassifiesHttpBoundary(
        HttpStatusCode statusCode,
        CodeExecutionFailureKind expectedKind,
        string expectedCode)
    {
        var port = CreatePort(HandlerWithProxyResponse(JsonResponse(
            """{"detail":"untrusted"}""",
            statusCode)));

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Result.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = expectedKind,
            Code = expectedCode,
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenChronoReturnsTypedInfrastructureFailure_PreservesSafeEvidence()
    {
        var port = CreatePort(HandlerWithProxyResponse(JsonResponse(
            """
            {
              "success": false,
              "error": {
                "code": "SANDBOX_CREATION_FAILED",
                "message": "untrusted upstream detail"
              },
              "diagnostic_id": "diag-sandbox-1"
            }
            """,
            HttpStatusCode.BadGateway)));

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Result.Should().BeNull();
        outcome.Failure.Should().Be(new CodeExecutionFailure(
            CodeExecutionFailureKind.TransportUnavailable,
            "SANDBOX_CREATION_FAILED",
            "The code execution sandbox is unavailable upstream.",
            "diag-sandbox-1"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenNyxIdWrapsTypedTimeout_PreservesNestedSafeEvidence()
    {
        var nestedBody = JsonSerializer.Serialize(new
        {
            success = false,
            error = new { code = "SANDBOX_TIMEOUT", message = "untrusted" },
            diagnostic_id = "diag-timeout-1",
        });
        var envelope = JsonSerializer.Serialize(new
        {
            error = true,
            status = 504,
            body = nestedBody,
        });
        var port = CreatePort(HandlerWithProxyResponse(JsonResponse(
            envelope,
            HttpStatusCode.GatewayTimeout)));

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Failure.Should().Be(new CodeExecutionFailure(
            CodeExecutionFailureKind.TimedOut,
            "SANDBOX_TIMEOUT",
            "Code execution timed out upstream.",
            "diag-timeout-1"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenChronoReturnsUnknownInfrastructureCode_UsesHttpClassification()
    {
        var port = CreatePort(HandlerWithProxyResponse(JsonResponse(
            """
            {
              "error": {
                "code": "UNTRUSTED_PROVIDER_CODE",
                "message": "untrusted upstream detail"
              },
              "diagnostic_id": "diag-generic-1"
            }
            """,
            HttpStatusCode.BadGateway)));

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Failure.Should().Be(new CodeExecutionFailure(
            CodeExecutionFailureKind.TransportUnavailable,
            "NYXID_PROXY_HTTP_502",
            "The code execution proxy request failed.",
            "diag-generic-1"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenProxyTimesOut_ReturnsTypedTimeout()
    {
        var handler = new SequenceHandler(
            JsonResponse(Inventory(Service(
                CodeServiceId,
                "chrono-sandbox",
                true,
                true,
                "proxy:*"))),
            static _ => throw new OperationCanceledException("transport timeout"));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Result.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.TimedOut,
            Code = "code_execution_timed_out",
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenProxyResponseExceedsLimit_ReturnsTypedFailureWithoutReadingBody()
    {
        var oversizedContent = new ThrowOnReadContent(1_048_577);
        var handler = HandlerWithProxyResponse(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = oversizedContent,
        }));
        var port = CreatePort(handler);

        var outcome = await port.ExecuteAsync(Request(CodeServiceId));

        outcome.Result.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.ResponseTooLarge,
            Code = "code_execution_response_too_large",
        });
        oversizedContent.ReadAttempted.Should().BeFalse();
    }

    private static NyxIdCodeExecutionPort CreatePort(HttpMessageHandler handler)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));
        return new NyxIdCodeExecutionPort(
            new TestNyxIdApiClientFactory(client),
            NullLogger<NyxIdCodeExecutionPort>.Instance);
    }

    private static CodeExecutionRequest Request(string? userServiceId) =>
        new(
            CodeExecutionLanguage.Python,
            "print(42)",
            new CodeExecutionRouteIdentity(
                "chrono-sandbox",
                userServiceId,
                CodeExecutionRouteIdentitySource.CodeExecutionContract),
            new CodeExecutionCallerContext(BearerToken));

    private static SequenceHandler HandlerWithProxyResponse(
        Func<CancellationToken, Task<HttpResponseMessage>> proxyResponse) =>
        new(
            JsonResponse(Inventory(Service(
                CodeServiceId,
                "chrono-sandbox",
                true,
                true,
                "proxy:*"))),
            proxyResponse);

    private static SequenceHandler HandlerWithProxyResponse(HttpResponseMessage proxyResponse) =>
        HandlerWithProxyResponse(_ => Task.FromResult(proxyResponse));

    private static Func<CancellationToken, Task<HttpResponseMessage>> JsonResponse(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        _ => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    private static string Inventory(params object[] services) =>
        JsonSerializer.Serialize(new { services });

    private static object Service(
        string id,
        string slug,
        bool forwardAccessToken,
        bool injectDelegationToken,
        string delegationTokenScope,
        bool isActive = true,
        string credentialSourceType = "personal") =>
        new
        {
            id,
            slug,
            is_active = isActive,
            forward_access_token = forwardAccessToken,
            inject_delegation_token = injectDelegationToken,
            delegation_token_scope = delegationTokenScope,
            credential_source = new { type = credentialSourceType },
        };

    private static void AssertRouteUnavailable(
        CodeExecutionOutcome outcome,
        SequenceHandler handler)
    {
        outcome.Result.Should().BeNull();
        outcome.ResolvedRoute.Should().BeNull();
        outcome.Failure.Should().BeEquivalentTo(new
        {
            Kind = CodeExecutionFailureKind.TargetNotConfigured,
            Code = "code_execution_route_unavailable",
        });
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].PathAndQuery.Should().Be("/api/v1/user-services");
    }

    private sealed class TestNyxIdApiClientFactory(NyxIdApiClient client) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => client;
    }

    private sealed class SequenceHandler(
        params Func<CancellationToken, Task<HttpResponseMessage>>[] responses) : HttpMessageHandler
    {
        private int _responseIndex;

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-API-Key", out var apiKeys)
                    ? apiKeys.ToArray()
                    : [],
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken)));

            if (_responseIndex >= responses.Length)
                throw new InvalidOperationException("An unexpected HTTP request was sent.");
            return await responses[_responseIndex++](cancellationToken);
        }
    }

    private sealed record RecordedRequest(
        string Method,
        string PathAndQuery,
        string? Authorization,
        IReadOnlyList<string> ApiKeys,
        string? Body);

    private sealed class ThrowOnReadContent : HttpContent
    {
        public ThrowOnReadContent(long contentLength)
        {
            Headers.ContentLength = contentLength;
        }

        public bool ReadAttempted { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("The oversized body must not be read.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Headers.ContentLength!.Value;
            return true;
        }
    }
}
