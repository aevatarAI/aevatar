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
        var service = new WorkspaceService(store, store, new StubWorkflowYamlDocumentService());
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

        var service = new WorkspaceService(store, store, new StubWorkflowYamlDocumentService());

        var response = await service.CreateDraftAsync(new SaveWorkflowDraftRequest(
            DirectoryId: directory.DirectoryId,
            WorkflowName: "renamed-workflow",
            FileName: null,
            Yaml: "name: draft\nsteps: []\n"));

        store.LastSavedDraft.Should().NotBeNull();
        store.LastSavedDraft!.Yaml.Should().StartWith("name: renamed-workflow");
        response.Name.Should().Be("renamed-workflow");
        response.Yaml.Should().StartWith("name: renamed-workflow");
    }

    [Fact]
    public async Task Mutations_ShouldDispatchCurrentWorkspaceStateVersionToCommandPort()
    {
        var store = new InMemoryStudioWorkspaceStore();
        var primaryDirectory = CreateDirectory();
        var removableDirectory = new StudioWorkspaceDirectory(
            DirectoryId: "dir-remove",
            Label: "Remove",
            Path: Path.Combine(Path.GetTempPath(), $"studio-workflows-remove-{Guid.NewGuid():N}"),
            IsBuiltIn: false);
        var addedPath = Path.Combine(Path.GetTempPath(), $"studio-workflows-added-{Guid.NewGuid():N}");
        store.SeedStateVersion(17);
        await store.SaveSettingsAsync(new StudioWorkspaceSettings(
            RuntimeBaseUrl: "http://127.0.0.1:5100",
            Directories: [primaryDirectory, removableDirectory],
            AppearanceTheme: "blue",
            ColorMode: "light"));

        var service = new WorkspaceService(store, store, new StubWorkflowYamlDocumentService());

        try
        {
            await service.UpdateSettingsAsync(new UpdateWorkspaceSettingsRequest("http://127.0.0.1:5101"));
            await service.AddDirectoryAsync(new AddWorkflowDirectoryRequest(addedPath, "Added"));
            await service.RemoveDirectoryAsync(removableDirectory.DirectoryId);
            var draft = await service.CreateDraftAsync(new SaveWorkflowDraftRequest(
                DirectoryId: primaryDirectory.DirectoryId,
                WorkflowName: "versioned-workflow",
                FileName: "versioned-workflow.yaml",
                Yaml: "name: versioned-workflow\nsteps: []\n"));
            await service.DeleteDraftAsync(draft.WorkflowId);

            store.CommandPortCalls.Should().Equal(
            [
                new CommandPortCall("UpdateSettings", 17),
                new CommandPortCall("AddDirectory", 18),
                new CommandPortCall("RemoveDirectory", 19),
                new CommandPortCall("SaveDraft", 20),
                new CommandPortCall("DeleteDraft", 21),
            ]);
        }
        finally
        {
            if (Directory.Exists(addedPath))
            {
                Directory.Delete(addedPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UpdateSettingsAsync_ShouldTrimTrailingSlash()
    {
        var store = new InMemoryStudioWorkspaceStore();
        var service = new WorkspaceService(store, store, new StubWorkflowYamlDocumentService());

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
        var service = new WorkspaceService(store, store, new StubWorkflowYamlDocumentService());

        var response = await service.RemoveDirectoryAsync("dir-2");

        response.Directories.Should().ContainSingle(directory => directory.DirectoryId == "dir-1");
    }

    [Fact]
    public async Task AddDirectoryAsync_WhenPathAlreadyExistsWithDifferentCase_ShouldReturnSettingsWithoutDispatch()
    {
        var store = new InMemoryStudioWorkspaceStore();
        var path = Path.Combine(Path.GetTempPath(), $"studio-workflows-existing-{Guid.NewGuid():N}");
        await store.SaveSettingsAsync(new StudioWorkspaceSettings(
            RuntimeBaseUrl: "http://127.0.0.1:5100",
            Directories: [new StudioWorkspaceDirectory("dir-1", "Existing", path, IsBuiltIn: false)],
            AppearanceTheme: "blue",
            ColorMode: "light"));
        var service = new WorkspaceService(store, store, new StubWorkflowYamlDocumentService());
        var duplicatePath = Path.Combine(
            Path.GetDirectoryName(path)!,
            Path.GetFileName(path).ToUpperInvariant());

        try
        {
            var response = await service.AddDirectoryAsync(new AddWorkflowDirectoryRequest(duplicatePath, "Duplicate"));

            response.Directories.Should().ContainSingle(directory =>
                directory.DirectoryId == "dir-1" &&
                directory.Label == "Existing");
            store.CommandPortCalls.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            if (!string.Equals(path, duplicatePath, StringComparison.Ordinal) &&
                Directory.Exists(duplicatePath))
            {
                Directory.Delete(duplicatePath, recursive: true);
            }
        }
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
        store.Drafts.Add(new StudioWorkflowDraftRecord(
            WorkflowId: "existing-workflow",
            Name: "existing-workflow",
            FileName: "shared.yaml",
            FilePath: Path.Combine(directory.Path, "shared.yaml"),
            DirectoryId: directory.DirectoryId,
            DirectoryLabel: directory.Label,
            Yaml: "name: existing-workflow\nsteps: []\n",
            Layout: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Version: 1));
        var service = new WorkspaceService(store, store, new StubWorkflowYamlDocumentService());

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
        var service = new WorkspaceService(store, store, new StubWorkflowYamlDocumentService());

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
        store.Drafts.Add(new StudioWorkflowDraftRecord(
            WorkflowId: "workflow-1",
            Name: "workflow-1",
            FileName: "workflow-1.yaml",
            FilePath: Path.Combine(directory.Path, "workflow-1.yaml"),
            DirectoryId: directory.DirectoryId,
            DirectoryLabel: directory.Label,
            Yaml: "name: workflow-1\nsteps: []\n",
            Layout: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Version: 1));
        var service = new WorkspaceService(store, store, new StubWorkflowYamlDocumentService());

        var response = await service.SaveWorkflowAsync(new SaveWorkflowFileRequest(
            WorkflowId: "workflow-1",
            DirectoryId: directory.DirectoryId,
            WorkflowName: "workflow-1",
            FileName: "workflow-1-renamed.yaml",
            Yaml: "name: workflow-1\nsteps: []\n"));

        response.WorkflowId.Should().Be("workflow-1");
        response.FileName.Should().Be("workflow-1-renamed.yaml");
        store.LastSavedDraft.Should().NotBeNull();
        store.LastSavedDraft!.WorkflowId.Should().Be("workflow-1");
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
        store.Drafts.Add(new StudioWorkflowDraftRecord(
            WorkflowId: "workflow-1",
            Name: "workflow-1",
            FileName: "workflow-1.yaml",
            FilePath: Path.Combine(directory.Path, "workflow-1.yaml"),
            DirectoryId: directory.DirectoryId,
            DirectoryLabel: directory.Label,
            Yaml: "name: workflow-1\nsteps: []\n",
            Layout: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Version: 1));
        var service = new WorkspaceService(store, store, new StubWorkflowYamlDocumentService());

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
        store.Drafts.Add(new StudioWorkflowDraftRecord(
            WorkflowId: "workflow-1",
            Name: "workflow-1",
            FileName: "workflow-1.yaml",
            FilePath: Path.Combine(directory.Path, "workflow-1.yaml"),
            DirectoryId: directory.DirectoryId,
            DirectoryLabel: directory.Label,
            Yaml: "name: workflow-1\nsteps: []\n",
            Layout: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Version: 1));
        var service = new WorkspaceService(store, store, new StubWorkflowYamlDocumentService());

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

    private sealed class InMemoryStudioWorkspaceStore : IStudioWorkspaceQueryPort, IStudioWorkspaceCommandPort
    {
        private StudioWorkspaceSettings _settings = new(
            RuntimeBaseUrl: "http://127.0.0.1:5100",
            Directories: [],
            AppearanceTheme: "blue",
            ColorMode: "light");

        private long _stateVersion;

        public StudioWorkflowDraftRecord? LastSavedDraft { get; private set; }

        public List<StudioWorkflowDraftRecord> Drafts { get; } = [];

        public List<CommandPortCall> CommandPortCalls { get; } = [];

        public void SeedStateVersion(long stateVersion) => _stateVersion = stateVersion;

        public Task<StudioWorkspaceSnapshot> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new StudioWorkspaceSnapshot(
                "workspace-test",
                "scope-test",
                _settings,
                _settings.Directories,
                Drafts.ToList(),
                _stateVersion,
                DateTimeOffset.UtcNow));

        public Task<StudioWorkspaceSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_settings);

        public Task SaveSettingsAsync(StudioWorkspaceSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }

        public Task<StudioWorkspaceCommandReceipt> UpdateSettingsAsync(
            StudioWorkspaceSettings settings,
            long? expectedVersion = null,
            CancellationToken ct = default)
        {
            CommandPortCalls.Add(new CommandPortCall("UpdateSettings", expectedVersion));
            _settings = settings;
            _stateVersion++;
            return Task.FromResult(Receipt(expectedVersion));
        }

        public Task<StudioWorkspaceCommandReceipt> AddDirectoryAsync(
            StudioWorkspaceDirectory directory,
            long? expectedVersion = null,
            CancellationToken ct = default)
        {
            CommandPortCalls.Add(new CommandPortCall("AddDirectory", expectedVersion));
            _settings = _settings with { Directories = _settings.Directories.Append(directory).ToList() };
            _stateVersion++;
            return Task.FromResult(Receipt(expectedVersion));
        }

        public Task<StudioWorkspaceCommandReceipt> RemoveDirectoryAsync(
            string directoryId,
            long? expectedVersion = null,
            CancellationToken ct = default)
        {
            CommandPortCalls.Add(new CommandPortCall("RemoveDirectory", expectedVersion));
            _settings = _settings with
            {
                Directories = _settings.Directories
                    .Where(directory => directory.IsBuiltIn || !string.Equals(directory.DirectoryId, directoryId, StringComparison.Ordinal))
                    .ToList(),
            };
            _stateVersion++;
            return Task.FromResult(Receipt(expectedVersion));
        }

        public Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(
            StudioWorkflowDraftRecord draft,
            long? expectedVersion = null,
            CancellationToken ct = default)
        {
            CommandPortCalls.Add(new CommandPortCall("SaveDraft", expectedVersion));
            LastSavedDraft = draft;
            var existingIndex = Drafts.FindIndex(file =>
                string.Equals(file.WorkflowId, draft.WorkflowId, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                Drafts[existingIndex] = draft;
            }
            else
            {
                Drafts.Add(draft);
            }

            _stateVersion++;
            return Task.FromResult(Receipt(expectedVersion));
        }

        public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(
            string workflowId,
            long? expectedVersion = null,
            CancellationToken ct = default)
        {
            CommandPortCalls.Add(new CommandPortCall("DeleteDraft", expectedVersion));
            Drafts.RemoveAll(file => string.Equals(file.WorkflowId, workflowId, StringComparison.Ordinal));
            _stateVersion++;
            return Task.FromResult(Receipt(expectedVersion));
        }

        private static StudioWorkspaceCommandReceipt Receipt(long? expectedVersion) =>
            new("workspace-test", "workspace-test", Guid.NewGuid().ToString("N"), expectedVersion);
    }

    private sealed record CommandPortCall(string Command, long? ExpectedVersion);
}
