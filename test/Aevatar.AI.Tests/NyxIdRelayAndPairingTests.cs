using System.Reflection;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.Tests;

// ─── JWT Subject Extraction Tests ───

public class NyxIdRelayJwtTests
{
    private static readonly MethodInfo TryExtractJwtSubject = typeof(NyxIdChatEndpoints)
        .GetMethod("TryExtractJwtSubject", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static string? CallTryExtractJwtSubject(string token) =>
        (string?)TryExtractJwtSubject.Invoke(null, [token]);

    [Fact]
    public void ValidJwt_ReturnsSubClaim()
    {
        var payload = Base64UrlEncode("""{"sub":"user-123","scope":"proxy read"}""");
        var header = Base64UrlEncode("""{"alg":"RS256","typ":"JWT"}""");
        var token = $"{header}.{payload}.fake-signature";

        CallTryExtractJwtSubject(token).Should().Be("user-123");
    }

    [Fact]
    public void InvalidToken_ReturnsNull()
    {
        CallTryExtractJwtSubject("not-a-jwt").Should().BeNull();
    }

    [Fact]
    public void TokenWithoutSub_ReturnsNull()
    {
        var payload = Base64UrlEncode("""{"scope":"proxy read"}""");
        var header = Base64UrlEncode("""{"alg":"RS256"}""");
        var token = $"{header}.{payload}.sig";

        CallTryExtractJwtSubject(token).Should().BeNull();
    }

    private static string Base64UrlEncode(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

// ─── ToolArgs Tests ───

public class ToolArgsTests
{
    [Fact]
    public void CaseInsensitivePropertyAccess()
    {
        var args = ToolArgs.Parse("""{"Slug":"test","PATH":"/foo"}""");
        args.Str("slug").Should().Be("test");
        args.Str("path").Should().Be("/foo");
        args.Str("SLUG").Should().Be("test");
    }

    [Fact]
    public void Str_ReturnsDefault_WhenMissing()
    {
        var args = ToolArgs.Parse("{}");
        args.Str("missing").Should().BeNull();
        args.Str("missing", "fallback").Should().Be("fallback");
    }

    [Fact]
    public void Bool_Values()
    {
        var args = ToolArgs.Parse("""{"active":true,"disabled":false}""");
        args.Bool("active").Should().BeTrue();
        args.Bool("disabled").Should().BeFalse();
        args.Bool("missing").Should().BeNull();
    }

    [Fact]
    public void RawOrStr_HandlesStringAndObject()
    {
        var args = ToolArgs.Parse("""{"body":"{\"key\":1}","obj":{"nested":true}}""");
        args.RawOrStr("body").Should().Be("{\"key\":1}");
        args.RawOrStr("obj").Should().Contain("nested");
    }

    [Fact]
    public void Headers_ReturnsDictionary()
    {
        var args = ToolArgs.Parse("""{"headers":{"X-Custom":"val1","Accept":"json"}}""");
        var headers = args.Headers();
        headers.Should().NotBeNull();
        headers!["X-Custom"].Should().Be("val1");
        headers["Accept"].Should().Be("json");
    }

    [Fact]
    public void Parse_InvalidJson_HasParseError()
    {
        var args = ToolArgs.Parse("not json at all");
        args.HasParseError.Should().BeTrue();
        args.ParseError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Parse_EmptyString_NoError()
    {
        var args = ToolArgs.Parse("");
        args.HasParseError.Should().BeFalse();
        args.Str("anything").Should().BeNull();
    }
}

// ─── ChannelBotsTool Tests ───

public class NyxIdChannelBotsToolTests
{
    private static (NyxIdChannelBotsTool tool, CaptureHandler handler) CreateTool()
    {
        var captureHandler = new CaptureHandler();
        var httpClient = new HttpClient(captureHandler) { BaseAddress = new Uri("https://test.example.com") };
        var options = new NyxIdToolOptions { BaseUrl = "https://test.example.com" };
        var apiClient = new NyxIdApiClient(options, httpClient);
        return (new NyxIdChannelBotsTool(apiClient), captureHandler);
    }

    [Fact]
    public async Task RegisterAction_MissingParams_ReturnsError()
    {
        var (tool, _) = CreateTool();
        SetFakeToken();
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"register"}""");
            result.Should().Contain("error");
            result.Should().Contain("platform");
        }
        finally { ClearToken(); }
    }

    [Fact]
    public async Task CreateRouteAction_MissingParams_ReturnsError()
    {
        var (tool, _) = CreateTool();
        SetFakeToken();
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"create_route"}""");
            result.Should().Contain("error");
            result.Should().Contain("channel_bot_id");
        }
        finally { ClearToken(); }
    }

    [Fact]
    public async Task NoToken_ReturnsError()
    {
        var (tool, _) = CreateTool();
        var result = await tool.ExecuteAsync("""{"action":"list"}""");
        result.Should().Contain("error");
        result.Should().Contain("access token");
    }

    [Fact]
    public async Task RegisterAction_WithParams_CallsApi()
    {
        var (tool, handler) = CreateTool();
        SetFakeToken();
        try
        {
            var result = await tool.ExecuteAsync(
                """{"action":"register","platform":"telegram","bot_token":"123:ABC","label":"Test Bot"}""");
            handler.LastRequest.Should().NotBeNull();
            handler.LastRequest!.RequestUri!.AbsolutePath.Should().Contain("channel-bots");
            handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        }
        finally { ClearToken(); }
    }

    [Fact]
    public async Task CreateRouteAction_WithParams_CallsApi()
    {
        var (tool, handler) = CreateTool();
        SetFakeToken();
        try
        {
            var result = await tool.ExecuteAsync(
                """{"action":"create_route","channel_bot_id":"bot-1","agent_api_key_id":"key-1","default_agent":true}""");
            handler.LastRequest.Should().NotBeNull();
            handler.LastRequest!.RequestUri!.AbsolutePath.Should().Contain("channel-conversations");
            handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        }
        finally { ClearToken(); }
    }

    [Fact]
    public async Task ListAction_CallsApi()
    {
        var (tool, handler) = CreateTool();
        SetFakeToken();
        try
        {
            await tool.ExecuteAsync("""{"action":"list"}""");
            handler.LastRequest.Should().NotBeNull();
            handler.LastRequest!.RequestUri!.AbsolutePath.Should().Contain("channel-bots");
            handler.LastRequest.Method.Should().Be(HttpMethod.Get);
        }
        finally { ClearToken(); }
    }

    private static void SetFakeToken()
    {
        var key = Aevatar.AI.Abstractions.LLMProviders.LLMRequestMetadataKeys.NyxIdAccessToken;
        Aevatar.AI.Abstractions.ToolProviders.AgentToolRequestContext.CurrentMetadata =
            new Dictionary<string, string> { [key] = "fake-token" };
    }

    private static void ClearToken() =>
        Aevatar.AI.Abstractions.ToolProviders.AgentToolRequestContext.CurrentMetadata = null;

    internal sealed class CaptureHandler : DelegatingHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
