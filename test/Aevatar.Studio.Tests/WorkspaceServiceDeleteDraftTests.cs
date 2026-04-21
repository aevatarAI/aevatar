using System.Text.RegularExpressions;
using Aevatar.Configuration;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Tests;

public sealed partial class WorkspaceServiceDeleteDraftTests
{
    [Fact]
    public async Task DeleteDraftAsync_DeleteThenList_ShouldRemoveStoredWorkflow()
    {
        using var environment = new WorkspaceEnvironment();
        var settings = await environment.Store.GetSettingsAsync();
        var directoryId = settings.Directories.Single().DirectoryId;

        var saved = await environment.Service.CreateDraftAsync(new SaveWorkflowDraftRequest(
            DirectoryId: directoryId,
            WorkflowName: "hello-chat",
            FileName: null,
            Yaml: "name: hello-chat\ndescription: saved draft\nsteps: []\n"));

        (await environment.Service.ListDraftsAsync())
            .Should()
            .Contain(summary => summary.WorkflowId == saved.WorkflowId);

        await environment.Service.DeleteDraftAsync(saved.WorkflowId);

        (await environment.Service.ListDraftsAsync())
            .Should()
            .NotContain(summary => summary.WorkflowId == saved.WorkflowId);
    }

    [Fact]
    public async Task DeleteDraftAsync_ShouldRemoveLayoutAlongWithYaml()
    {
        using var environment = new WorkspaceEnvironment();
        var settings = await environment.Store.GetSettingsAsync();
        var directoryId = settings.Directories.Single().DirectoryId;
        var layout = new WorkflowLayoutDocument
        {
            NodePositions = new Dictionary<string, WorkflowNodeLayout>(StringComparer.Ordinal)
            {
                ["step-1"] = new(12, 34),
            },
        };

        var saved = await environment.Service.CreateDraftAsync(new SaveWorkflowDraftRequest(
            DirectoryId: directoryId,
            WorkflowName: "hello-layout",
            FileName: null,
            Yaml: "name: hello-layout\nsteps: []\n",
            Layout: layout));
        var layoutPath = $"{saved.FilePath}.layout.json";

        File.Exists(saved.FilePath).Should().BeTrue();
        File.Exists(layoutPath).Should().BeTrue();

        await environment.Service.DeleteDraftAsync(saved.WorkflowId);

        File.Exists(saved.FilePath).Should().BeFalse();
        File.Exists(layoutPath).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenWorkflowIsMissing_ShouldThrowWorkflowDraftNotFoundException()
    {
        using var environment = new WorkspaceEnvironment();
        var missingWorkflowId = "missing-workflow";

        var act = () => environment.Service.DeleteDraftAsync(missingWorkflowId);

        await act.Should().ThrowAsync<WorkflowDraftNotFoundException>()
            .Where(exception => exception.WorkflowId == missingWorkflowId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DeleteDraftAsync_WhenWorkflowIdIsEmpty_ShouldThrowInvalidOperationException(string workflowId)
    {
        using var environment = new WorkspaceEnvironment();

        var act = () => environment.Service.DeleteDraftAsync(workflowId);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenDraftIsDeletedTwice_ShouldThrowWorkflowDraftNotFoundException()
    {
        using var environment = new WorkspaceEnvironment();
        var settings = await environment.Store.GetSettingsAsync();
        var directoryId = settings.Directories.Single().DirectoryId;
        var draft = await environment.Service.CreateDraftAsync(new SaveWorkflowDraftRequest(
            DirectoryId: directoryId,
            WorkflowName: "delete-twice",
            FileName: null,
            Yaml: "name: delete-twice\nsteps: []\n"));

        await environment.Service.DeleteDraftAsync(draft.WorkflowId);

        var act = () => environment.Service.DeleteDraftAsync(draft.WorkflowId);

        await act.Should().ThrowAsync<WorkflowDraftNotFoundException>()
            .Where(exception => exception.WorkflowId == draft.WorkflowId);
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenDraftIsRenamed_ShouldDeleteUsingStableWorkflowId()
    {
        using var environment = new WorkspaceEnvironment();
        var settings = await environment.Store.GetSettingsAsync();
        var directory = settings.Directories.Single();
        var created = await environment.Service.CreateDraftAsync(new SaveWorkflowDraftRequest(
            DirectoryId: directory.DirectoryId,
            WorkflowName: "rename-before-delete",
            FileName: null,
            Yaml: "name: rename-before-delete\nsteps: []\n"));
        var updated = await environment.Service.UpdateDraftAsync(
            created.WorkflowId,
            new SaveWorkflowDraftRequest(
                DirectoryId: directory.DirectoryId,
                WorkflowName: "rename-before-delete",
                FileName: "renamed-draft.yaml",
                Yaml: "name: rename-before-delete\nsteps: []\n"));

        updated.WorkflowId.Should().Be(created.WorkflowId);
        File.Exists(updated.FilePath).Should().BeTrue();

        await environment.Service.DeleteDraftAsync(created.WorkflowId);

        File.Exists(updated.FilePath).Should().BeFalse();
    }

    [Fact]
    public async Task UpdateDraftAsync_WhenRenameDiffersOnlyByCase_ShouldKeepRenamedDraftFile()
    {
        using var environment = new WorkspaceEnvironment();
        var settings = await environment.Store.GetSettingsAsync();
        var directory = settings.Directories.Single();
        var created = await environment.Service.CreateDraftAsync(new SaveWorkflowDraftRequest(
            DirectoryId: directory.DirectoryId,
            WorkflowName: "case-only-rename",
            FileName: "Foo.yaml",
            Yaml: "name: case-only-rename\nsteps: []\n"));

        var updated = await environment.Service.UpdateDraftAsync(
            created.WorkflowId,
            new SaveWorkflowDraftRequest(
                DirectoryId: directory.DirectoryId,
                WorkflowName: "case-only-rename",
                FileName: "foo.yaml",
                Yaml: "name: case-only-rename\nsteps: []\n"));

        updated.WorkflowId.Should().Be(created.WorkflowId);
        updated.FilePath.Should().EndWith("foo.yaml");
        File.Exists(updated.FilePath).Should().BeTrue();
    }

    private sealed class WorkspaceEnvironment : IDisposable
    {
        private readonly string? _previousHome;
        private readonly string _rootDirectory;

        public WorkspaceEnvironment()
        {
            HomeDirectory = Path.Combine(Path.GetTempPath(), $"studio-delete-draft-home-{Guid.NewGuid():N}");
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"studio-delete-draft-root-{Guid.NewGuid():N}");
            _previousHome = Environment.GetEnvironmentVariable(AevatarPaths.HomeEnv);
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, HomeDirectory);

            Store = new FileStudioWorkspaceStore(Options.Create(new StudioStorageOptions
            {
                RootDirectory = _rootDirectory,
            }));
            Service = new WorkspaceService(Store, new StubWorkflowYamlDocumentService());
        }

        public string HomeDirectory { get; }

        public FileStudioWorkspaceStore Store { get; }

        public WorkspaceService Service { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, _previousHome);
            if (Directory.Exists(HomeDirectory))
            {
                Directory.Delete(HomeDirectory, recursive: true);
            }

            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
        }
    }

    private sealed class StubWorkflowYamlDocumentService : IWorkflowYamlDocumentService
    {
        private static readonly Regex NameRegex = new(@"(?m)^name:\s*(.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex DescriptionRegex = new(@"(?m)^description:\s*(.+?)\s*$", RegexOptions.Compiled);

        public WorkflowParseResult Parse(string yaml)
        {
            if (string.IsNullOrWhiteSpace(yaml))
            {
                return new WorkflowParseResult(null, []);
            }

            var input = yaml ?? string.Empty;
            var nameMatch = NameRegex.Match(input);
            var descriptionMatch = DescriptionRegex.Match(input);
            return new WorkflowParseResult(
                new WorkflowDocument
                {
                    Name = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : string.Empty,
                    Description = descriptionMatch.Success ? descriptionMatch.Groups[1].Value.Trim() : string.Empty,
                },
                []);
        }

        public string Serialize(WorkflowDocument document) =>
            $"name: {document.Name}\nsteps: []\n";
    }
}
