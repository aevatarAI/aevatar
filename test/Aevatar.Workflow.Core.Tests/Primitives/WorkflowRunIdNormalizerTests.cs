using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public class WorkflowRunIdNormalizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public void Normalize_WhenRunIdMissing_ShouldReturnDefault(string? runId)
    {
        var normalized = WorkflowRunIdNormalizer.Normalize(runId);

        normalized.Should().Be("default");
    }

    [Fact]
    public void Normalize_WhenRunIdHasPadding_ShouldTrim()
    {
        var normalized = WorkflowRunIdNormalizer.Normalize("  run-123  ");

        normalized.Should().Be("run-123");
    }

    [Fact]
    public void Normalize_WhenRunIdValid_ShouldKeepOriginal()
    {
        var normalized = WorkflowRunIdNormalizer.Normalize("run-xyz");

        normalized.Should().Be("run-xyz");
    }
}
