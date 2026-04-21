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
    public async Task CreateDraftAsync_ShouldRewriteYamlNameFromRequestedWorkflowName()
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

        var response = await service.CreateDraftAsync(new SaveWorkflowDraftRequest(
            DirectoryId: directory.DirectoryId,
            WorkflowName: "renamed-workflow",
            FileName: null,
            Yaml: "name: draft\nsteps: []\n"));

        store.LastSavedWorkflowFile.Should().NotBeNull();
        store.LastSavedWorkflowFile!.Yaml.Should().StartWith("name: renamed-workflow");
        response.Name.Should().Be("renamed-workflow");
        response.Yaml.Should().StartWith("name: renamed-workflow");
    }

    [Fact]
    public async Task UpdateSettingsAsync_ShouldTrimTrailingSlash()
    {
        var store = new InMemoryStudioWorkspaceStore();
        var service = new WorkspaceService(store, new StubWorkflowYamlDocumentService());

        var response = await service.UpdateSettingsAsync(new UpdateWorkspaceSettingsRequest("http://127.0.0.1:5100/"));

        response.RuntimeBaseUrl.Should().Be("http://127.0.0.1:5100");
    }

    [Fact]
    public async Task RemoveDirectoryAsync_ShouldKeepBuiltInDirectories()
    {
        var store = new InMemoryStudioWorkspaceStore();
        await store.SaveSettingsAsync(new StudioWorkspaceSettings(
            RuntimeBaseUrl: "http://127.0.0.1:5100",
            Directories:
            [
                new StudioWorkspaceDirectory("dir-1", "Built-in", Path.Combine(Path.GetTempPath(), $"built-in-{Guid.NewGuid():N}"), IsBuiltIn: true),
                new StudioWorkspaceDirectory("dir-2", "Extra", Path.Combine(Path.GetTempPath(), $"extra-{Guid.NewGuid():N}"), IsBuiltIn: false),
            ],
            AppearanceTheme: "blue",
            ColorMode: "light"));
        var service = new WorkspaceService(store, new StubWorkflowYamlDocumentService());

        var response = await service.RemoveDirectoryAsync("dir-2");

        response.Directories.Should().ContainSingle(directory => directory.DirectoryId == "dir-1");
    }

    [Fact]
    public async Task CreateDraftAsync_WhenTargetPathConflictsWithAnotherDraft_ShouldThrowWorkflowDraftPathConflictException()
    {
        var store = new InMemoryStudioWorkspaceStore();
        var directory = CreateDirectory();
        await store.SaveSettingsAsync(new StudioWorkspaceSettings(
            RuntimeBaseUrl: "http://127.0.0.1:5100",
            Directories: [directory],
            AppearanceTheme: "blue",
            ColorMode: "light"));
        store.WorkflowFiles.Add(new StoredWorkflowFile(
            WorkflowId: "existing-workflow",
            Name: "existing-workflow",
            FileName: "shared.yaml",
            FilePath: Path.Combine(directory.Path, "shared.yaml"),
            DirectoryId: directory.DirectoryId,
            DirectoryLabel: directory.Label,
            Yaml: "name: existing-workflow\nsteps: []\n",
            Layout: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow));
        var service = new WorkspaceService(store, new StubWorkflowYamlDocumentService());

        var act = () => service.CreateDraftAsync(new SaveWorkflowDraftRequest(
            DirectoryId: directory.DirectoryId,
            WorkflowName: "new-workflow",
            FileName: "shared.yaml",
            Yaml: "name: new-workflow\nsteps: []\n"));

        var exception = await act.Should().ThrowAsync<WorkflowDraftPathConflictException>();
        exception.Which.WorkflowId.Should().Be("new-workflow");
        exception.Which.ConflictingWorkflowId.Should().Be("existing-workflow");
    }

    [Fact]
    public async Task UpdateDraftAsync_WhenWorkflowIsMissing_ShouldThrowWorkflowDraftNotFoundException()
    {
        var store = new InMemoryStudioWorkspaceStore();
        var directory = CreateDirectory();
        await store.SaveSettingsAsync(new StudioWorkspaceSettings(
            RuntimeBaseUrl: "http://127.0.0.1:5100",
            Directories: [directory],
            AppearanceTheme: "blue",
            ColorMode: "light"));
        var service = new WorkspaceService(store, new StubWorkflowYamlDocumentService());

        var act = () => service.UpdateDraftAsync(
            "missing-workflow",
            new SaveWorkflowDraftRequest(
                DirectoryId: directory.DirectoryId,
                WorkflowName: "missing-workflow",
                FileName: null,
                Yaml: "name: missing-workflow\nsteps: []\n"));

        var exception = await act.Should().ThrowAsync<WorkflowDraftNotFoundException>();
        exception.Which.WorkflowId.Should().Be("missing-workflow");
    }

    [Fact]
    public async Task SaveWorkflowAsync_WhenWorkflowIdIsProvided_ShouldReuseExistingWorkflowId()
    {
        var store = new InMemoryStudioWorkspaceStore();
        var directory = CreateDirectory();
        await store.SaveSettingsAsync(new StudioWorkspaceSettings(
            RuntimeBaseUrl: "http://127.0.0.1:5100",
            Directories: [directory],
            AppearanceTheme: "blue",
            ColorMode: "light"));
        store.WorkflowFiles.Add(new StoredWorkflowFile(
            WorkflowId: "workflow-1",
            Name: "workflow-1",
            FileName: "workflow-1.yaml",
            FilePath: Path.Combine(directory.Path, "workflow-1.yaml"),
            DirectoryId: directory.DirectoryId,
            DirectoryLabel: directory.Label,
            Yaml: "name: workflow-1\nsteps: []\n",
            Layout: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow));
        var service = new WorkspaceService(store, new StubWorkflowYamlDocumentService());

        var response = await service.SaveWorkflowAsync(new SaveWorkflowFileRequest(
            WorkflowId: "workflow-1",
            DirectoryId: directory.DirectoryId,
            WorkflowName: "workflow-1",
            FileName: "workflow-1-renamed.yaml",
            Yaml: "name: workflow-1\nsteps: []\n"));

        response.WorkflowId.Should().Be("workflow-1");
        response.FileName.Should().Be("workflow-1-renamed.yaml");
        store.LastSavedWorkflowFile.Should().NotBeNull();
        store.LastSavedWorkflowFile!.WorkflowId.Should().Be("workflow-1");
    }

    [Fact]
    public async Task GetWorkflowAsync_ShouldReturnParsedWorkflowDocument()
    {
        var store = new InMemoryStudioWorkspaceStore();
        var directory = CreateDirectory();
        await store.SaveSettingsAsync(new StudioWorkspaceSettings(
            RuntimeBaseUrl: "http://127.0.0.1:5100",
            Directories: [directory],
            AppearanceTheme: "blue",
            ColorMode: "light"));
        store.WorkflowFiles.Add(new StoredWorkflowFile(
            WorkflowId: "workflow-1",
            Name: "workflow-1",
            FileName: "workflow-1.yaml",
            FilePath: Path.Combine(directory.Path, "workflow-1.yaml"),
            DirectoryId: directory.DirectoryId,
            DirectoryLabel: directory.Label,
            Yaml: "name: workflow-1\nsteps: []\n",
            Layout: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow));
        var service = new WorkspaceService(store, new StubWorkflowYamlDocumentService());

        var response = await service.GetWorkflowAsync("workflow-1");

        response.Should().NotBeNull();
        response!.Document.Should().NotBeNull();
        response.Document!.Name.Should().Be("workflow-1");
        response.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task ListWorkflowsAsync_ShouldReturnLegacyWorkflowSummaries()
    {
        var store = new InMemoryStudioWorkspaceStore();
        var directory = CreateDirectory();
        await store.SaveSettingsAsync(new StudioWorkspaceSettings(
            RuntimeBaseUrl: "http://127.0.0.1:5100",
            Directories: [directory],
            AppearanceTheme: "blue",
            ColorMode: "light"));
        store.WorkflowFiles.Add(new StoredWorkflowFile(
            WorkflowId: "workflow-1",
            Name: "workflow-1",
            FileName: "workflow-1.yaml",
            FilePath: Path.Combine(directory.Path, "workflow-1.yaml"),
            DirectoryId: directory.DirectoryId,
            DirectoryLabel: directory.Label,
            Yaml: "name: workflow-1\nsteps: []\n",
            Layout: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow));
        var service = new WorkspaceService(store, new StubWorkflowYamlDocumentService());

        var response = await service.ListWorkflowsAsync();

        response.Should().ContainSingle();
        response[0].WorkflowId.Should().Be("workflow-1");
        response[0].Name.Should().Be("workflow-1");
    }

    private static StudioWorkspaceDirectory CreateDirectory() =>
        new(
            DirectoryId: "dir-1",
            Label: "Test Root",
            Path: Path.Combine(Path.GetTempPath(), $"studio-workflows-{Guid.NewGuid():N}"),
            IsBuiltIn: false);

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

        public List<StoredWorkflowFile> WorkflowFiles { get; } = [];

        public Task<StudioWorkspaceSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_settings);

        public Task SaveSettingsAsync(StudioWorkspaceSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredWorkflowFile>> ListWorkflowFilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredWorkflowFile>>(WorkflowFiles.ToList());

        public Task<StoredWorkflowFile?> GetWorkflowFileAsync(string workflowId, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoredWorkflowFile?>(WorkflowFiles.FirstOrDefault(file =>
                string.Equals(file.WorkflowId, workflowId, StringComparison.Ordinal)));

        public Task<StoredWorkflowFile> SaveWorkflowFileAsync(StoredWorkflowFile workflowFile, CancellationToken cancellationToken = default)
        {
            LastSavedWorkflowFile = workflowFile;
            var existingIndex = WorkflowFiles.FindIndex(file =>
                string.Equals(file.WorkflowId, workflowFile.WorkflowId, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                WorkflowFiles[existingIndex] = workflowFile;
            }
            else
            {
                WorkflowFiles.Add(workflowFile);
            }

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
