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

        var saved = await environment.Service.SaveWorkflowAsync(new SaveWorkflowFileRequest(
            WorkflowId: null,
            DirectoryId: directoryId,
            WorkflowName: "hello-chat",
            FileName: null,
            Yaml: "name: hello-chat\ndescription: saved draft\nsteps: []\n"));

        (await environment.Service.ListWorkflowsAsync())
            .Should()
            .Contain(summary => summary.WorkflowId == saved.WorkflowId);

        await environment.Service.DeleteDraftAsync(saved.WorkflowId);

        (await environment.Service.ListWorkflowsAsync())
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

        var saved = await environment.Service.SaveWorkflowAsync(new SaveWorkflowFileRequest(
            WorkflowId: null,
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
    public async Task DeleteDraftAsync_WhenWorkflowIsMissing_ShouldSucceedIdempotently()
    {
        using var environment = new WorkspaceEnvironment();
        var missingPath = Path.Combine(environment.HomeDirectory, "workflows", "missing.yaml");
        var missingWorkflowId = WorkspaceService.CreateStableId(missingPath);

        var act = () => environment.Service.DeleteDraftAsync(missingWorkflowId);

        await act.Should().NotThrowAsync();
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
    public async Task DeleteDraftAsync_WhenPathIsOutsideRegisteredDirectory_ShouldRejectAndNotDelete()
    {
        using var environment = new WorkspaceEnvironment();
        var outsidePath = Path.Combine(Path.GetTempPath(), $"studio-delete-draft-outside-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(outsidePath, "name: outside\nsteps: []\n");
        var outsideWorkflowId = WorkspaceService.CreateStableId(outsidePath);

        try
        {
            var act = () => environment.Service.DeleteDraftAsync(outsideWorkflowId);

            await act.Should().ThrowAsync<InvalidOperationException>();
            File.Exists(outsidePath).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(outsidePath))
                File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenPathTraversesOutsideRegisteredDirectory_ShouldRejectAndNotDelete()
    {
        using var environment = new WorkspaceEnvironment();
        var settings = await environment.Store.GetSettingsAsync();
        var registeredDirectory = settings.Directories.Single().Path;
        var escapePath = Path.GetFullPath(Path.Combine(registeredDirectory, "..", $"studio-delete-draft-escape-{Guid.NewGuid():N}.yaml"));
        await File.WriteAllTextAsync(escapePath, "name: escape\nsteps: []\n");
        var traversalWorkflowId = WorkspaceService.CreateStableId(Path.Combine(registeredDirectory, "..", Path.GetFileName(escapePath)));

        try
        {
            var act = () => environment.Service.DeleteDraftAsync(traversalWorkflowId);

            await act.Should().ThrowAsync<InvalidOperationException>();
            File.Exists(escapePath).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(escapePath))
                File.Delete(escapePath);
        }
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
