using Aevatar.GAgentService.Abstractions.Responses;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Abstractions;

public sealed class ResponsesTodoItemParserTests
{
    private static readonly Timestamp Observed =
        Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-12T00:00:00+00:00"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_ShouldReturnEmpty_ForBlankInput(string? input)
    {
        var items = ResponsesTodoItemParser.Parse(input, "resp_1", Observed);

        items.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ShouldReturnEmpty_ForMalformedJson()
    {
        var items = ResponsesTodoItemParser.Parse("not json {", "resp_1", Observed);

        items.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ShouldExtractTodosArray_PreservingIdsAndStatuses()
    {
        var items = ResponsesTodoItemParser.Parse(
            """{"todos":[{"id":"todo-1","content":"Ship","status":"in_progress"},{"content":"Review"}]}""",
            "resp_1",
            Observed);

        items.Should().HaveCount(2);
        items[0].Id.Should().Be("todo-1");
        items[0].Content.Should().Be("Ship");
        items[0].Status.Should().Be("in_progress");
        items[0].SourceResponseId.Should().Be("resp_1");
        items[1].Id.Should().Be("todo_0001");
        items[1].Content.Should().Be("Review");
        items[1].Status.Should().Be("pending");
    }

    [Fact]
    public void Parse_ShouldSkipObjectsWithoutContent()
    {
        var items = ResponsesTodoItemParser.Parse(
            """{"todos":[{"id":"x"},{"content":"keep"}]}""",
            "resp_1",
            Observed);

        items.Should().ContainSingle();
        items[0].Content.Should().Be("keep");
    }

    [Theory]
    [InlineData("""{"todos":[{"task":"alpha"}]}""", "alpha")]
    [InlineData("""{"todos":[{"title":"beta"}]}""", "beta")]
    [InlineData("""{"todos":[{"text":"gamma"}]}""", "gamma")]
    public void Parse_ShouldFallBackToAlternateContentKeys(string json, string expected)
    {
        var items = ResponsesTodoItemParser.Parse(json, "resp_1", Observed);

        items.Should().ContainSingle();
        items[0].Content.Should().Be(expected);
    }

    [Fact]
    public void Parse_ShouldAcceptSingleStringTodo()
    {
        var items = ResponsesTodoItemParser.Parse(""" "do the thing" """, "resp_1", Observed);

        items.Should().ContainSingle();
        items[0].Content.Should().Be("do the thing");
        items[0].Id.Should().Be("todo_0000");
        items[0].Status.Should().Be("pending");
    }

    [Fact]
    public void Parse_ShouldAcceptSingleObjectTodo()
    {
        var items = ResponsesTodoItemParser.Parse(
            """{"id":"only","content":"singleton","status":"done"}""",
            "resp_1",
            Observed);

        items.Should().ContainSingle();
        items[0].Id.Should().Be("only");
        items[0].Status.Should().Be("done");
    }

    [Fact]
    public void Parse_ShouldReturnEmpty_ForRootArrayNotInsideTodos()
    {
        // A bare array is not "object with todos" and not "object" → no todos parsed.
        var items = ResponsesTodoItemParser.Parse("[1,2,3]", "resp_1", Observed);

        items.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ShouldDefaultSourceResponseId_WhenNull()
    {
        var items = ResponsesTodoItemParser.Parse(
            """{"todos":[{"content":"x"}]}""",
            sourceResponseId: null,
            Observed);

        items.Should().ContainSingle();
        items[0].SourceResponseId.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ShouldNormalizeWhitespace_InIdContentAndStatus()
    {
        var items = ResponsesTodoItemParser.Parse(
            """{"todos":[{"id":"  a  ","content":"  ship it  ","status":"  pending  "}]}""",
            "resp_1",
            Observed);

        items.Should().ContainSingle();
        items[0].Id.Should().Be("a");
        items[0].Content.Should().Be("ship it");
        items[0].Status.Should().Be("pending");
    }

    [Fact]
    public void Parse_ShouldAcceptNumericIdViaRawText()
    {
        // ReadString falls through Number → ToString gets raw representation.
        var items = ResponsesTodoItemParser.Parse(
            """{"todos":[{"id":42,"content":"x"}]}""",
            "resp_1",
            Observed);

        items.Should().ContainSingle();
        items[0].Id.Should().Be("42");
    }

    [Fact]
    public void Parse_ShouldUseObservedAt_OnEachItem()
    {
        var observed = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2027-01-01T12:34:56+00:00"));
        var items = ResponsesTodoItemParser.Parse(
            """{"todos":[{"content":"x"}]}""",
            "resp_1",
            observed);

        items[0].CreatedAt.Should().Be(observed);
        items[0].UpdatedAt.Should().Be(observed);
    }
}
