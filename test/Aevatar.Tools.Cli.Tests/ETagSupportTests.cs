using Aevatar.Studio.Hosting.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Tools.Cli.Tests;

/// <summary>
/// Locks in If-Match parsing semantics. Distinguishing absent from invalid is essential — a
/// malformed precondition silently falling back to last-writer-wins would defeat the
/// optimistic concurrency guarantee that the caller is requesting.
/// </summary>
public sealed class ETagSupportTests
{
    [Fact]
    public void ParseIfMatch_WhenHeaderMissing_ReturnsAbsent()
    {
        var status = ETagSupport.ParseIfMatch(MakeRequest(headerValue: null), out var version);

        status.Should().Be(IfMatchStatus.Absent);
        version.Should().Be(0);
    }

    [Fact]
    public void ParseIfMatch_WhenHeaderEmpty_ReturnsAbsent()
    {
        var status = ETagSupport.ParseIfMatch(MakeRequest("   "), out _);
        status.Should().Be(IfMatchStatus.Absent);
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("\"5\"", 5)]
    [InlineData("0", 0)]
    [InlineData("\"42\"", 42)]
    public void ParseIfMatch_WhenStrongSingleVersion_ReturnsValid(string header, long expected)
    {
        var status = ETagSupport.ParseIfMatch(MakeRequest(header), out var version);

        status.Should().Be(IfMatchStatus.Valid);
        version.Should().Be(expected);
    }

    [Theory]
    [InlineData("W/\"5\"")]                // weak validator
    [InlineData("\"5\", \"6\"")]           // multi-value
    [InlineData("\"5\",\"6\"")]            // multi-value (no space)
    [InlineData("*")]                      // wildcard
    [InlineData("not-a-number")]           // non-numeric
    [InlineData("\"abc\"")]                // non-numeric quoted
    [InlineData("\"\"")]                   // empty quoted
    [InlineData("-1")]                     // negative
    [InlineData("\"-1\"")]                 // negative quoted
    public void ParseIfMatch_WhenMalformed_ReturnsInvalid(string header)
    {
        var status = ETagSupport.ParseIfMatch(MakeRequest(header), out _);

        status.Should().Be(IfMatchStatus.Invalid);
    }

    private static HttpRequest MakeRequest(string? headerValue)
    {
        var ctx = new DefaultHttpContext();
        if (headerValue is not null)
            ctx.Request.Headers["If-Match"] = headerValue;
        return ctx.Request;
    }
}
