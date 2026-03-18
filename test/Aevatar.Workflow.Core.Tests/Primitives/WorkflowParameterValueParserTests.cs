using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowParameterValueParserTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("y", true)]
    [InlineData("on", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("n", false)]
    [InlineData("off", false)]
    public void GetBool_ShouldParseCommonBooleanTokens(string raw, bool expected)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enabled"] = raw,
        };

        var value = WorkflowParameterValueParser.GetBool(parameters, fallback: !expected, "enabled");

        value.Should().Be(expected);
    }

    [Fact]
    public void GetBool_WhenValueIsMissingOrInvalid_ShouldReturnFallback()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enabled"] = "maybe",
        };

        WorkflowParameterValueParser.GetBool(parameters, fallback: true, "missing", "enabled").Should().BeTrue();
        WorkflowParameterValueParser.GetBool(parameters, fallback: false, "enabled").Should().BeFalse();
    }

    [Fact]
    public void ParseStringList_ShouldHandleJsonArrays_DelimitedText_AndInvalidJson()
    {
        var fromJson = WorkflowParameterValueParser.ParseStringList("""["alpha", 2, true, null, {"nested":1}]""");
        var fromDelimited = WorkflowParameterValueParser.ParseStringList(" \"alpha\", 'beta' ; [gamma]\n delta ");
        var fromInvalidJson = WorkflowParameterValueParser.ParseStringList("[alpha, beta]");

        fromJson.Should().Equal("alpha", "2", "true", """{"nested":1}""");
        fromDelimited.Should().Equal("alpha", "beta", "gamma", "delta");
        fromInvalidJson.Should().Equal("alpha", "beta");
    }

    [Fact]
    public void NormalizeEscapedText_ShouldUnwrapQuotes_ReplaceEscapes_AndFallbackOnEmpty()
    {
        var normalized = WorkflowParameterValueParser.NormalizeEscapedText("\"line1\\nline2\\tvalue\"", "fallback");
        var emptyQuoted = WorkflowParameterValueParser.NormalizeEscapedText("''", "fallback");
        var missing = WorkflowParameterValueParser.NormalizeEscapedText(null, "fallback");

        normalized.Should().Be("line1\nline2\tvalue");
        emptyQuoted.Should().Be("fallback");
        missing.Should().Be("fallback");
    }

    [Fact]
    public void SplitInputByDelimiterOrJsonArray_ShouldSupportJsonArrays_Objects_AndCustomDelimiters()
    {
        var fromJson = WorkflowParameterValueParser.SplitInputByDelimiterOrJsonArray("""["alpha","beta"]""", "|");
        var fromDefaultDelimiter = WorkflowParameterValueParser.SplitInputByDelimiterOrJsonArray("a\n---\nb", string.Empty);
        var fromCustomDelimiter = WorkflowParameterValueParser.SplitInputByDelimiterOrJsonArray("x||y||z", "||");
        var fromJsonObject = WorkflowParameterValueParser.SplitInputByDelimiterOrJsonArray("""{"a":1}""", "||");

        fromJson.Should().Equal("alpha", "beta");
        fromDefaultDelimiter.Should().Equal("a", "b");
        fromCustomDelimiter.Should().Equal("x", "y", "z");
        fromJsonObject.Should().Equal("""{"a":1}""");
    }
}
