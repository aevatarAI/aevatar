using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class TextDiffServiceTests
{
    private readonly TextDiffService _service = new();

    [Fact]
    public void BuildLineDiff_ShouldReturnSingleEqual_WhenBothNull()
    {
        var diff = _service.BuildLineDiff(null, null);
        diff.Should().ContainSingle().Which.Operation.Should().Be("equal");
    }

    [Fact]
    public void BuildLineDiff_ShouldReturnSingleEqual_WhenBothEmpty()
    {
        var diff = _service.BuildLineDiff("", "");
        diff.Should().ContainSingle().Which.Operation.Should().Be("equal");
    }

    [Fact]
    public void BuildLineDiff_ShouldReturnAdded_WhenBeforeIsNull()
    {
        var diff = _service.BuildLineDiff(null, "line1\nline2");
        diff.Should().Contain(d => d.Operation == "added");
    }

    [Fact]
    public void BuildLineDiff_ShouldReturnRemoved_WhenAfterIsNull()
    {
        var diff = _service.BuildLineDiff("line1\nline2", null);
        diff.Should().Contain(d => d.Operation == "removed");
    }

    [Fact]
    public void BuildLineDiff_ShouldReturnAllEqual_WhenIdentical()
    {
        var text = "line1\nline2\nline3";
        var diff = _service.BuildLineDiff(text, text);
        diff.Should().HaveCount(3);
        diff.Should().OnlyContain(d => d.Operation == "equal");
    }

    [Fact]
    public void BuildLineDiff_ShouldDetectSingleLineChange()
    {
        var before = "line1\nline2\nline3";
        var after = "line1\nchanged\nline3";
        var diff = _service.BuildLineDiff(before, after);
        diff.Should().Contain(d => d.Operation == "removed" && d.Text == "line2");
        diff.Should().Contain(d => d.Operation == "added" && d.Text == "changed");
        diff.Where(d => d.Operation == "equal").Should().HaveCount(2);
    }

    [Fact]
    public void BuildLineDiff_ShouldDetectAddedLines()
    {
        var before = "a\nc";
        var after = "a\nb\nc";
        var diff = _service.BuildLineDiff(before, after);
        diff.Should().Contain(d => d.Operation == "added" && d.Text == "b");
        diff.Where(d => d.Operation == "equal").Should().HaveCount(2);
    }

    [Fact]
    public void BuildLineDiff_ShouldDetectRemovedLines()
    {
        var before = "a\nb\nc";
        var after = "a\nc";
        var diff = _service.BuildLineDiff(before, after);
        diff.Should().Contain(d => d.Operation == "removed" && d.Text == "b");
        diff.Where(d => d.Operation == "equal").Should().HaveCount(2);
    }

    [Fact]
    public void BuildLineDiff_ShouldHandleCrlf()
    {
        var diff = _service.BuildLineDiff("a\r\nb", "a\nb");
        diff.Should().HaveCount(2);
        diff.Should().OnlyContain(d => d.Operation == "equal");
    }

    [Fact]
    public void BuildLineDiff_ShouldAssignLineNumbers()
    {
        var before = "a\nb";
        var after = "a\nc";
        var diff = _service.BuildLineDiff(before, after);
        diff.Where(d => d.LeftLineNumber.HasValue).Should().NotBeEmpty();
        diff.Where(d => d.RightLineNumber.HasValue).Should().NotBeEmpty();
    }
}
