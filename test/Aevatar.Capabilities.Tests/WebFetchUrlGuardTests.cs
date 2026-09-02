using Aevatar.AI.ToolProviders.Web;
using Aevatar.AI.ToolProviders.Web.Tools;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using FluentAssertions;
using System.Net;
using System.Net.Http.Headers;

namespace Aevatar.Capabilities.Tests;

public sealed class WebFetchUrlGuardTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_ShouldReject_EmptyOrWhitespace(string? candidate)
    {
        var result = WebFetchUrlGuard.Validate(candidate);

        result.IsAllowed.Should().BeFalse();
        result.RejectionCode.Should().Be("empty_url");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("://missing-scheme")]
    [InlineData("just-text-no-scheme")]
    public void Validate_ShouldReject_NonAbsoluteOrUnparseable(string url)
    {
        var result = WebFetchUrlGuard.Validate(url);

        result.IsAllowed.Should().BeFalse();
        result.RejectionCode.Should().Be("invalid_url");
    }

    [Fact]
    public void Validate_ShouldReject_NonHttpScheme()
    {
        var result = WebFetchUrlGuard.Validate("file:///etc/passwd");

        result.IsAllowed.Should().BeFalse();
        result.RejectionCode.Should().Be("unsupported_scheme");
    }

    [Fact]
    public void Validate_ShouldReject_FtpScheme()
    {
        var result = WebFetchUrlGuard.Validate("ftp://example.com/file");

        result.IsAllowed.Should().BeFalse();
        result.RejectionCode.Should().Be("unsupported_scheme");
    }

    [Theory]
    [InlineData("http://localhost/api")]
    [InlineData("http://LOCALHOST/api")]
    [InlineData("http://ip6-localhost/api")]
    [InlineData("https://app.localhost/api")]
    public void Validate_ShouldReject_LoopbackHostnames(string url)
    {
        var result = WebFetchUrlGuard.Validate(url);

        result.IsAllowed.Should().BeFalse();
        result.RejectionCode.Should().Be("blocked_loopback_hostname");
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.5.5.5:8080/path")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://10.255.255.255/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://172.31.255.254/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://169.254.169.254/")]  // AWS instance metadata
    [InlineData("http://0.0.0.0/")]
    public void Validate_ShouldReject_PrivateIpv4Addresses(string url)
    {
        var result = WebFetchUrlGuard.Validate(url);

        result.IsAllowed.Should().BeFalse();
        result.RejectionCode.Should().Be("blocked_private_address");
    }

    [Theory]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://100.127.255.254/")]
    [InlineData("http://192.0.0.8/")]
    [InlineData("http://198.18.0.1/")]
    [InlineData("http://198.19.255.254/")]
    [InlineData("http://224.0.0.1/")]
    [InlineData("http://239.255.255.250/")]
    public void Validate_ShouldReject_AdditionalNonPublicIpv4Ranges(string url)
    {
        var result = WebFetchUrlGuard.Validate(url);

        result.IsAllowed.Should().BeFalse();
        result.RejectionCode.Should().Be("blocked_private_address");
    }

    [Theory]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fc00::1]/")]
    public void Validate_ShouldReject_PrivateIpv6Addresses(string url)
    {
        var result = WebFetchUrlGuard.Validate(url);

        result.IsAllowed.Should().BeFalse();
        result.RejectionCode.Should().Be("blocked_private_address");
    }

    [Fact]
    public void Validate_ShouldReject_Ipv4MappedIpv6_PrivateAddress()
    {
        // 127.0.0.1 mapped: ::ffff:127.0.0.1
        var result = WebFetchUrlGuard.Validate("http://[::ffff:7f00:1]/");

        result.IsAllowed.Should().BeFalse();
        result.RejectionCode.Should().Be("blocked_private_address");
    }

    [Theory]
    [InlineData("http://172.15.0.1/")]   // just outside 172.16/12
    [InlineData("http://172.32.0.1/")]   // just outside 172.16/12
    [InlineData("http://11.0.0.1/")]     // just outside 10/8
    public void Validate_ShouldAccept_AdjacentNonPrivateRanges(string url)
    {
        var result = WebFetchUrlGuard.Validate(url);

        result.IsAllowed.Should().BeTrue();
        result.RejectionCode.Should().BeNull();
        result.NormalizedUrl.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("http://example.com/", "http://example.com/")]
    [InlineData("https://example.com/path?q=1", "https://example.com/path?q=1")]
    [InlineData("  https://example.com  ", "https://example.com/")]
    public void Validate_ShouldAccept_PublicHosts_NormalizingTrim(string input, string expected)
    {
        var result = WebFetchUrlGuard.Validate(input);

        result.IsAllowed.Should().BeTrue();
        result.NormalizedUrl.Should().Be(expected);
        result.RejectionCode.Should().BeNull();
    }

    [Fact]
    public void Validate_ShouldAccept_PublicIpv4()
    {
        var result = WebFetchUrlGuard.Validate("http://8.8.8.8/");

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task WebFetchTool_ShouldNotForwardNyxIdBearerToFetchTarget()
    {
        var handler = new RecordingHandler();
        var client = new WebApiClient(new WebToolOptions(), new HttpClient(handler));
        var tool = new WebFetchTool(client);
        var previous = AgentToolRequestContext.Current;
        try
        {
            AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
            {
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "secret-token",
            });

            var result = await tool.ExecuteAsync("""{"url":"http://8.8.8.8/"}""");

            result.Should().Contain("\"status_code\":200");
            handler.LastAuthorization.Should().BeNull();
            handler.RequestUrls.Should().ContainSingle("https://8.8.8.8/");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task WebFetchTool_ShouldRejectPrivateUrl_BeforeCallingFetchClient()
    {
        var handler = new RecordingHandler();
        var client = new WebApiClient(new WebToolOptions(), new HttpClient(handler));
        var tool = new WebFetchTool(client);

        var result = await tool.ExecuteAsync("""{"url":"http://127.0.0.1/"}""");

        result.Should().Contain("\"error\":\"blocked_private_address\"");
        handler.RequestUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchUrlAsync_ShouldSendFetchHeadersAndBearer_WhenTokenProvided()
    {
        var handler = new RecordingHandler();
        var client = new WebApiClient(new WebToolOptions(), new HttpClient(handler));

        var result = await client.FetchUrlAsync("secret-token", "http://8.8.8.8/page", CancellationToken.None);

        result.StatusCode.Should().Be(200);
        result.Body.Should().Be("ok");
        handler.LastAuthorization.Should().NotBeNull();
        handler.LastAuthorization!.Scheme.Should().Be("Bearer");
        handler.LastAuthorization.Parameter.Should().Be("secret-token");
        handler.LastAcceptMediaTypes.Should().Contain("text/html");
        handler.LastAcceptMediaTypes.Should().Contain("text/plain");
        handler.LastAcceptMediaTypes.Should().Contain("application/json");
        handler.LastUserAgent.Should().Be("AevatarAgent/1.0");
    }

    [Fact]
    public async Task FetchUrlAsync_ShouldFollowSameHostRelativeRedirect()
    {
        var handler = new RecordingHandler
        {
            ResponseFactory = request => request.RequestUri!.AbsolutePath == "/start"
                ? Redirect("/final")
                : Ok("done"),
        };
        var client = new WebApiClient(new WebToolOptions(), new HttpClient(handler));

        var result = await client.FetchUrlAsync(string.Empty, "http://8.8.8.8/start", CancellationToken.None);

        result.StatusCode.Should().Be(200);
        result.Body.Should().Be("done");
        result.RedirectUrl.Should().BeNull();
        handler.RequestUrls.Should().Equal("http://8.8.8.8/start", "http://8.8.8.8/final");
    }

    [Fact]
    public async Task FetchUrlAsync_ShouldReturnRedirectUrl_WhenRedirectTargetChangesHost()
    {
        var handler = new RecordingHandler
        {
            ResponseFactory = _ => Redirect("http://8.8.4.4/final"),
        };
        var client = new WebApiClient(new WebToolOptions(), new HttpClient(handler));

        var result = await client.FetchUrlAsync(string.Empty, "http://8.8.8.8/start", CancellationToken.None);

        result.StatusCode.Should().Be(302);
        result.Body.Should().BeNull();
        result.OriginalUrl.Should().Be("http://8.8.8.8/start");
        result.RedirectUrl.Should().Be("http://8.8.4.4/final");
        handler.RequestUrls.Should().ContainSingle("http://8.8.8.8/start");
    }

    [Fact]
    public async Task FetchUrlAsync_ShouldRejectRedirectTarget_WhenItResolvesToPrivateAddress()
    {
        var handler = new RecordingHandler
        {
            ResponseFactory = _ => Redirect("http://127.0.0.1/private"),
        };
        var client = new WebApiClient(new WebToolOptions(), new HttpClient(handler));

        var result = await client.FetchUrlAsync(string.Empty, "http://8.8.8.8/start", CancellationToken.None);

        result.StatusCode.Should().Be(302);
        result.Body.Should().BeNull();
        result.RedirectUrl.Should().BeNull();
        result.OriginalUrl.Should().Be("http://8.8.8.8/start");
        result.Error.Should().Be(new WebToolError(
            "WEB_FETCH_URL_REJECTED",
            "The web URL was rejected."));
    }

    [Fact]
    public async Task FetchUrlAsync_ShouldStopAfterRedirectLimit()
    {
        var handler = new RecordingHandler
        {
            ResponseFactory = _ => Redirect("/again"),
        };
        var client = new WebApiClient(new WebToolOptions(), new HttpClient(handler));

        var result = await client.FetchUrlAsync(string.Empty, "http://8.8.8.8/start", CancellationToken.None);

        result.StatusCode.Should().Be(0);
        result.ContentType.Should().Be("error");
        result.Body.Should().BeNull();
        result.Error.Should().Be(new WebToolError(
            "WEB_FETCH_TRANSPORT_FAILURE",
            "The web request failed."));
        handler.RequestUrls.Should().HaveCount(5);
    }

    [Fact]
    public async Task FetchUrlAsync_ShouldReturnTypedError_ForNonSuccessResponse()
    {
        var handler = new RecordingHandler
        {
            ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("upstream failed"),
            },
        };
        var client = new WebApiClient(new WebToolOptions(), new HttpClient(handler));

        var result = await client.FetchUrlAsync(string.Empty, "http://8.8.8.8/fail", CancellationToken.None);

        result.StatusCode.Should().Be(502);
        result.Body.Should().BeNull();
        result.RedirectUrl.Should().BeNull();
        result.Error.Should().Be(new WebToolError(
            "WEB_FETCH_HTTP_502",
            "The web request failed."));
    }

    [Fact]
    public async Task FetchUrlAsync_ShouldRejectInitialPrivateUrl_WithoutSendingRequest()
    {
        var handler = new RecordingHandler();
        var client = new WebApiClient(new WebToolOptions(), new HttpClient(handler));

        var result = await client.FetchUrlAsync(string.Empty, "http://127.0.0.1/private", CancellationToken.None);

        result.StatusCode.Should().Be(0);
        result.ContentType.Should().Be("error");
        result.Body.Should().BeNull();
        result.Error.Should().Be(new WebToolError(
            "WEB_FETCH_URL_REJECTED",
            "The web URL was rejected."));
        handler.RequestUrls.Should().BeEmpty();
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Content = new StringContent(string.Empty),
        };
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }
        public AuthenticationHeaderValue? LastAuthorization { get; private set; }
        public IReadOnlyList<string> LastAcceptMediaTypes { get; private set; } = [];
        public string LastUserAgent { get; private set; } = string.Empty;
        public List<string> RequestUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization;
            LastAcceptMediaTypes = request.Headers.Accept
                .Select(static header => header.MediaType ?? string.Empty)
                .ToArray();
            LastUserAgent = request.Headers.UserAgent.ToString();
            RequestUrls.Add(request.RequestUri?.ToString() ?? string.Empty);
            return Task.FromResult(ResponseFactory?.Invoke(request) ?? Ok("ok"));
        }
    }
}
