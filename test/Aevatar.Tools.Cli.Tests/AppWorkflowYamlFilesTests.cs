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

    [Fact]
    public void NormalizeSaveFilename_WhenBothNull_ShouldThrow()
    {
        var act = () => AppWorkflowYamlFiles.NormalizeSaveFilename(
            requestedFilename: null,
            workflowName: "");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*filename is required*");
    }

    [Fact]
    public void NormalizeSaveFilename_WhenSpecialCharsOnly_ShouldThrow()
    {
        var act = () => AppWorkflowYamlFiles.NormalizeSaveFilename(
            requestedFilename: "!!!.yaml",
            workflowName: "ignored");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must contain letters or digits*");
    }

    [Fact]
    public void NormalizeSaveFilename_WithExplicitFilename_ShouldUseIt()
    {
        var filename = AppWorkflowYamlFiles.NormalizeSaveFilename(
            requestedFilename: "my-flow.yaml",
            workflowName: "ignored");

        filename.Should().Be("my-flow.yaml");
    }

    [Fact]
    public void NormalizeSaveFilename_ShouldCollapseConsecutiveUnderscores()
    {
        var filename = AppWorkflowYamlFiles.NormalizeSaveFilename(
            requestedFilename: null,
            workflowName: "hello   world");

        filename.Should().NotContain("__");
        filename.Should().Be("hello_world.yaml");
    }

    [Fact]
    public void NormalizeContentForSave_NullInput_ShouldReturnNewLine()
    {
        var content = AppWorkflowYamlFiles.NormalizeContentForSave(null!);

        content.Should().Be(Environment.NewLine);
    }
}
