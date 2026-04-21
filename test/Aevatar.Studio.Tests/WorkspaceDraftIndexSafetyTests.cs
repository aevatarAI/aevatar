using Aevatar.Studio.Application.Studio.Contracts;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed partial class WorkspaceServiceDeleteDraftTests
{
    [Fact]
    public async Task CreateDraftAsync_WhenWorkflowDirectoryIsRemoved_ShouldNotDeleteUnmanagedDraftWithReusedWorkflowId()
    {
        using var environment = new WorkspaceEnvironment();
        var extraDirectoryPath = Path.Combine(environment.HomeDirectory, "external-workflows");
        Directory.CreateDirectory(extraDirectoryPath);

        var updatedSettings = await environment.Service.AddDirectoryAsync(
            new AddWorkflowDirectoryRequest(extraDirectoryPath, "External"));
        var extraDirectory = updatedSettings.Directories.Single(directory => directory.Path == extraDirectoryPath);
        var builtInDirectory = updatedSettings.Directories.Single(directory => directory.IsBuiltIn);

        var originalDraft = await environment.Service.CreateDraftAsync(new SaveWorkflowDraftRequest(
            DirectoryId: extraDirectory.DirectoryId,
            WorkflowName: "shared-workflow",
            FileName: null,
            Yaml: "name: shared-workflow\nsteps: []\n"));

        await environment.Service.RemoveDirectoryAsync(extraDirectory.DirectoryId);

        var recreatedDraft = await environment.Service.CreateDraftAsync(new SaveWorkflowDraftRequest(
            DirectoryId: builtInDirectory.DirectoryId,
            WorkflowName: "shared-workflow",
            FileName: null,
            Yaml: "name: shared-workflow\nsteps: []\n"));

        recreatedDraft.WorkflowId.Should().Be(originalDraft.WorkflowId);
        File.Exists(originalDraft.FilePath).Should().BeTrue();
        File.Exists(recreatedDraft.FilePath).Should().BeTrue();
        recreatedDraft.FilePath.Should().NotBe(originalDraft.FilePath);
    }
}
