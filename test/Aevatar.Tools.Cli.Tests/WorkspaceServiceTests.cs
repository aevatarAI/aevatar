using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Models;
using FluentAssertions;
using System.Text.RegularExpressions;

namespace Aevatar.Tools.Cli.Tests;

public class WorkspaceServiceTests
{
    [Fact]
    public async Task AddDirectoryAsync_ShouldExpandTildePath()
    {
        var store = new InMemoryStudioWorkspaceStore();
        var service = new WorkspaceService(store, new StubWorkflowYamlDocumentService());
        var relativePath = $".aevatar-workflow-studio-tests/{Guid.NewGuid():N}";
        var rawPath = $"~/{relativePath}";
        var expectedPath = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            relativePath));

        try
        {
            var response = await service.AddDirectoryAsync(new AddWorkflowDirectoryRequest(rawPath, "Test Root"));

            response.Directories.Should().ContainSingle(directory =>
                directory.Path == expectedPath &&
                directory.Label == "Test Root" &&
                directory.IsBuiltIn == false);
            Directory.Exists(expectedPath).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(expectedPath))
            {
                Directory.Delete(expectedPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveWorkflowAsync_ShouldRewriteYamlNameFromRequestedWorkflowName()
    {
        var store = new InMemoryStudioWorkspaceStore();
        var directory = new StudioWorkspaceDirectory(
            DirectoryId: "dir-1",
            Label: "Test Root",
            Path: Path.Combine(Path.GetTempPath(), $"studio-workflows-{Guid.NewGuid():N}"),
            IsBuiltIn: false);
        await store.SaveSettingsAsync(new StudioWorkspaceSettings(
            RuntimeBaseUrl: "http://127.0.0.1:5100",
            Directories: [directory],
            AppearanceTheme: "blue",
            ColorMode: "light"));

        var service = new WorkspaceService(store, new StubWorkflowYamlDocumentService());

        var response = await service.SaveWorkflowAsync(new SaveWorkflowFileRequest(
            WorkflowId: null,
            DirectoryId: directory.DirectoryId,
            WorkflowName: "renamed-workflow",
            FileName: null,
            Yaml: "name: draft\nsteps: []\n"));

        store.LastSavedWorkflowFile.Should().NotBeNull();
        store.LastSavedWorkflowFile!.Yaml.Should().StartWith("name: renamed-workflow");
        response.Name.Should().Be("renamed-workflow");
        response.Yaml.Should().StartWith("name: renamed-workflow");
    }

    private sealed class StubWorkflowYamlDocumentService : IWorkflowYamlDocumentService
    {
        private static readonly Regex NameRegex = new(@"(?m)^name:\s*(.+?)\s*$", RegexOptions.Compiled);

        public WorkflowParseResult Parse(string yaml)
        {
            var match = NameRegex.Match(yaml ?? string.Empty);
            return new(new WorkflowDocument
            {
                Name = match.Success ? match.Groups[1].Value.Trim() : string.Empty,
            }, []);
        }

        public string Serialize(WorkflowDocument document) => $"name: {document.Name}\nsteps: []\n";
    }

    private sealed class InMemoryStudioWorkspaceStore : IStudioWorkspaceStore
    {
        private StudioWorkspaceSettings _settings = new(
            RuntimeBaseUrl: "http://127.0.0.1:5100",
            Directories: [],
            AppearanceTheme: "blue",
            ColorMode: "light");
        public StoredWorkflowFile? LastSavedWorkflowFile { get; private set; }

        public Task<StudioWorkspaceSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_settings);

        public Task SaveSettingsAsync(StudioWorkspaceSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredWorkflowFile>> ListWorkflowFilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredWorkflowFile>>([]);

        public Task<StoredWorkflowFile?> GetWorkflowFileAsync(string workflowId, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoredWorkflowFile?>(null);

        public Task<StoredWorkflowFile> SaveWorkflowFileAsync(StoredWorkflowFile workflowFile, CancellationToken cancellationToken = default)
        {
            LastSavedWorkflowFile = workflowFile;
            return Task.FromResult(workflowFile);
        }

        public Task DeleteWorkflowFileAsync(string workflowId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<StoredExecutionRecord>> ListExecutionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredExecutionRecord>>([]);

        public Task<StoredExecutionRecord?> GetExecutionAsync(string executionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoredExecutionRecord?>(null);

        public Task<StoredExecutionRecord> SaveExecutionAsync(StoredExecutionRecord execution, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorCatalog> GetConnectorCatalogAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(StoredConnectorCatalog catalog, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorDraft> GetConnectorDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorDraft> SaveConnectorDraftAsync(StoredConnectorDraft draft, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteConnectorDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleCatalog> SaveRoleCatalogAsync(StoredRoleCatalog catalog, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleDraft> GetRoleDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleDraft> SaveRoleDraftAsync(StoredRoleDraft draft, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteRoleDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
