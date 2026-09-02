using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowYamlParseResultTests
{
    [Fact]
    public void Factories_ShouldClassifySuccessfulAndInvalidResults()
    {
        var success = WorkflowYamlParseResult.Success("workflow-1");
        var invalid = WorkflowYamlParseResult.Invalid(null!);
        var directSuccess = new WorkflowYamlParseResult("workflow-1", " ");
        var inlineSuccess = WorkflowInlineYamlBundleParseResult.Success(
            "workflow-1",
            "name: workflow-1",
            new Dictionary<string, string>());
        var inlineInvalid = WorkflowInlineYamlBundleParseResult.Invalid(null!);

        success.ErrorCode.Should().Be(WorkflowYamlParseErrorCode.None);
        invalid.ErrorCode.Should().Be(WorkflowYamlParseErrorCode.InvalidYaml);
        directSuccess.ErrorCode.Should().Be(WorkflowYamlParseErrorCode.None);
        inlineSuccess.ErrorCode.Should().Be(WorkflowYamlParseErrorCode.None);
        inlineInvalid.ErrorCode.Should().Be(WorkflowYamlParseErrorCode.InvalidYaml);
    }
}
