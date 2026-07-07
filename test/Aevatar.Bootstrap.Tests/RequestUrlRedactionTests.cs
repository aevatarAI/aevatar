using Aevatar.Bootstrap.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aevatar.Bootstrap.Tests;

public sealed class SensitiveQueryRedactorTests
{
    [Fact]
    public void Redact_ReplacesAccessTokenValue_KeepsOtherParams()
    {
        var result = SensitiveQueryRedactor.Redact(
            "?codec=pcm16&mode=half_duplex&access_token=eyJhbGciOi.payload.signature");

        result.Should().Be("?codec=pcm16&mode=half_duplex&access_token=REDACTED");
    }

    [Fact]
    public void Redact_ValueContainingEquals_RedactsWholeValue()
    {
        // JWTs / base64 can contain '=' padding; the whole value must be replaced.
        var result = SensitiveQueryRedactor.Redact("?token=aGVsbG8=world==");

        result.Should().Be("?token=REDACTED");
    }

    [Fact]
    public void Redact_IsCaseInsensitiveOnKey()
    {
        SensitiveQueryRedactor.Redact("?Access_Token=abc").Should().Be("?Access_Token=REDACTED");
        SensitiveQueryRedactor.Redact("?REFRESH_TOKEN=abc").Should().Be("?REFRESH_TOKEN=REDACTED");
    }

    [Fact]
    public void Redact_RedactsEverySensitiveParam()
    {
        var result = SensitiveQueryRedactor.Redact(
            "?access_token=a&safe=keep&api_key=b&code=c&client_secret=d");

        result.Should().Be("?access_token=REDACTED&safe=keep&api_key=REDACTED&code=REDACTED&client_secret=REDACTED");
    }

    [Fact]
    public void Redact_WithoutLeadingQuestionMark_IsPreserved()
    {
        SensitiveQueryRedactor.Redact("access_token=abc&safe=1").Should().Be("access_token=REDACTED&safe=1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("?")]
    [InlineData("?codec=pcm16&mode=half_duplex")]
    public void Redact_NoSensitiveContent_ReturnsInputUnchanged(string? input)
    {
        SensitiveQueryRedactor.Redact(input).Should().Be(input ?? string.Empty);
    }

    [Fact]
    public void Redact_BareSensitiveFlagWithoutValue_IsUnchanged()
    {
        // No '=' means no value to leak.
        SensitiveQueryRedactor.Redact("?access_token").Should().Be("?access_token");
    }
}

public sealed class RedactingRequestLoggingMiddlewareTests
{
    private const string RawToken = "eyJhbGciOiJUNIT.secret-payload.signature-do-not-log";

    [Fact]
    public async Task InvokeAsync_LogsRedactedUrl_AndNeverLogsRawToken()
    {
        var context = new DefaultHttpContext();
        context.Request.Protocol = "HTTP/1.1";
        context.Request.Method = "GET";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("aevatar-console-backend-api.aevatar.ai");
        context.Request.Path = "/ws/voice";
        context.Request.QueryString = new QueryString($"?codec=pcm16&mode=half_duplex&access_token={RawToken}");

        var logger = new RecordingLogger<RedactingRequestLoggingMiddleware>();
        RequestDelegate next = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        };
        var middleware = new RedactingRequestLoggingMiddleware(next, logger);

        await middleware.InvokeAsync(context);

        logger.Messages.Should().HaveCount(2, "one line on request start, one on finish");
        logger.Messages.Should().NotContain(
            m => m.Contains(RawToken),
            "the raw access token must never be written to logs");
        logger.Messages.Should().OnlyContain(m => m.Contains("access_token=REDACTED"));
        logger.Messages.Should().Contain(m => m.Contains("/ws/voice"));
        logger.Messages.Should().Contain(m => m.StartsWith("Request finished") && m.Contains("200"));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
