using Aevatar.Tools.Cli.Hosting;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class AppWorkflowYamlFilesTests
{
    [Fact]
    public void NormalizeSaveFilename_WhenFilenameMissing_ShouldDeriveFromWorkflowName()
    {
        var filename = AppWorkflowYamlFiles.NormalizeSaveFilename(
            requestedFilename: null,
            workflowName: "Review Workflow 2026!");

        filename.Should().Be("Review_Workflow_2026.yaml");
    }

    [Fact]
    public void NormalizeSaveFilename_WhenFilenameContainsDirectoryTraversal_ShouldThrow()
    {
        var act = () => AppWorkflowYamlFiles.NormalizeSaveFilename(
            requestedFilename: "../escape.yaml",
            workflowName: "ignored");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must not include directory segments*");
    }

    [Fact]
    public void NormalizeContentForSave_ShouldTrimAndAppendTrailingNewLine()
    {
        var content = AppWorkflowYamlFiles.NormalizeContentForSave("name: demo\n\n");

        content.Should().Be($"name: demo{Environment.NewLine}");
    }
}
