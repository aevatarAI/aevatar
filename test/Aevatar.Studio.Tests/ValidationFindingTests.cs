using Aevatar.Studio.Domain.Studio.Models;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class ValidationFindingTests
{
    [Fact]
    public void Error_ShouldCreateErrorLevelFinding()
    {
        var finding = ValidationFinding.Error("/path", "msg", "hint", "code");
        finding.Level.Should().Be(ValidationLevel.Error);
        finding.Path.Should().Be("/path");
        finding.Message.Should().Be("msg");
        finding.Hint.Should().Be("hint");
        finding.Code.Should().Be("code");
    }

    [Fact]
    public void Error_ShouldAllowNullHintAndCode()
    {
        var finding = ValidationFinding.Error("/path", "msg");
        finding.Hint.Should().BeNull();
        finding.Code.Should().BeNull();
    }

    [Fact]
    public void Warning_ShouldCreateWarningLevelFinding()
    {
        var finding = ValidationFinding.Warning("/path", "msg", "hint", "code");
        finding.Level.Should().Be(ValidationLevel.Warning);
        finding.Path.Should().Be("/path");
        finding.Message.Should().Be("msg");
        finding.Hint.Should().Be("hint");
        finding.Code.Should().Be("code");
    }

    [Fact]
    public void Warning_ShouldAllowNullHintAndCode()
    {
        var finding = ValidationFinding.Warning("/path", "msg");
        finding.Hint.Should().BeNull();
        finding.Code.Should().BeNull();
    }
}
