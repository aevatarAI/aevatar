using Aevatar.Configuration;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Tests.Shared;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class AppScopedWorkflowServiceDeleteDraftTests
{
    [Fact]
    public async Task DeleteDraftAsync_ShouldCallWorkspaceCommandPortWithExplicitScope()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var workspacePort = new RecordingStudioWorkspacePorts(new[]
        {
            new ScopedDraft(
                "scope-1",
                NewDraft(
                    "workflow-1",
                    "workflow-1",
                    "name: workflow-1\nsteps: []\n",
                    DateTimeOffset.UtcNow)),
        });
        var service = environment.CreateService(
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: workspacePort);

        await service.DeleteDraftAsync("scope-1", "workflow-1");

        var deleted = workspacePort.DeletedDrafts.Should().ContainSingle().Subject;
        deleted.ScopeId.Should().Be("scope-1");
        deleted.WorkflowId.Should().Be("workflow-1");
        deleted.ExpectedVersion.Should().Be(11);
    }

    [Fact]
    public async Task DeleteDraftAsync_ShouldNotCallRuntimePorts()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var service = environment.CreateService(
            workspaceQueryPort: new RecordingStudioWorkspacePorts(new[]
            {
                new ScopedDraft(
                    "scope-1",
                    NewDraft(
                        "workflow-1",
                        "workflow-1",
                        "name: workflow-1\nsteps: []\n",
                        DateTimeOffset.UtcNow)),
            }),
            workspaceCommandPort: new RecordingStudioWorkspacePorts());

        await service.DeleteDraftAsync("scope-1", "workflow-1");

        // Runtime ports are not part of AppScopedWorkflowService anymore; draft deletion stays on workspace ports.
    }

    [Fact]
    public async Task CreateDraftAsync_ShouldPersistScopedDraftWithoutCreatingMemberAuthority()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var workspacePort = new RecordingStudioWorkspacePorts();
        var service = environment.CreateService(
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: workspacePort);

        var accepted = await service.CreateDraftAsync(
            "scope-1",
            new SaveWorkflowDraftRequest(
                DirectoryId: "scope:scope-1",
                WorkflowName: "workflow-1",
                FileName: null,
                Yaml: "name: workflow-1\nsteps: []\n"));

        var savedDraft = workspacePort.SavedDrafts.Should().ContainSingle().Subject;
        savedDraft.ScopeId.Should().Be("scope-1");
        savedDraft.ExpectedVersion.Should().Be(11);
        savedDraft.WorkflowId.Should().Be(accepted.WorkflowId);
        accepted.Accepted.Should().BeTrue();
        accepted.AckStage.Should().Be("accepted");
        accepted.WorkspaceId.Should().Be("studio-workspace:scope-1");
        accepted.ActorId.Should().Be("studio-workspace:scope-1");
        accepted.CommandId.Should().NotBeNullOrWhiteSpace();
        accepted.ExpectedVersion.Should().Be(11);
        accepted.Readiness.Readable.Should().BeFalse();
        accepted.Readiness.Stage.Should().Be("projection_pending");
        accepted.Readiness.Message.Should().Contain("Poll the workflow draft by id");
        Guid.TryParse(accepted.WorkflowId, out _).Should().BeTrue();
        accepted.WorkflowId.Should().NotBe("workflow-1");
    }

    [Fact]
    public async Task CreateDraftAsync_WithDistinctWorkflowName_ShouldNotDeriveMemberIdFromWorkflowId()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var workspacePort = new RecordingStudioWorkspacePorts();
        var service = environment.CreateService(
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: workspacePort);

        var accepted = await service.CreateDraftAsync(
            "scope-1",
            new SaveWorkflowDraftRequest(
                DirectoryId: "scope:scope-1",
                WorkflowName: "wf-alpha",
                FileName: null,
                Yaml: "name: wf-alpha\ndescription: member description\nsteps: []\n"));

        Guid.TryParse(accepted.WorkflowId, out _).Should().BeTrue();
        accepted.WorkflowId.Should().NotBe("wf-alpha");
        accepted.Readiness.Readable.Should().BeFalse();
        workspacePort.SavedDrafts.Should().ContainSingle()
            .Which.WorkflowId.Should().Be(accepted.WorkflowId);
    }

    [Fact]
    public async Task CreateDraftAsync_WhenFilePathAlreadyExists_ShouldRejectNewOpaqueWorkflowId()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var workspacePort = new RecordingStudioWorkspacePorts(new[]
        {
            new ScopedDraft(
                "scope-1",
                new StudioWorkflowDraftRecord(
                    "existing-workflow",
                    "workflow-1",
                    "workflow-1.yaml",
                    "scope://scope-1/workflow-1.yaml",
                    "scope:scope-1",
                    "scope-1",
                    "name: workflow-1\nsteps: []\n",
                    Layout: null,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    1)),
        });
        var service = environment.CreateService(
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: workspacePort);

        var act = () => service.CreateDraftAsync(
            "scope-1",
            new SaveWorkflowDraftRequest(
                DirectoryId: "scope:scope-1",
                WorkflowName: "workflow-1",
                FileName: null,
                Yaml: "name: workflow-1\nsteps: []\n"));

        var exception = (await act.Should().ThrowAsync<WorkflowDraftPathConflictException>()).Subject.Single();
        Guid.TryParse(exception.WorkflowId, out _).Should().BeTrue();
        exception.ConflictingWorkflowId.Should().Be("existing-workflow");
        exception.TargetPath.Should().Be("scope-1/workflow-1.yaml");
        workspacePort.SavedDrafts.Should().BeEmpty();
    }

    [Fact]
    public void AppScopedWorkflowService_ShouldNotDependOnStudioMemberCommandPort()
    {
        typeof(AppScopedWorkflowService)
            .GetConstructors()
            .SelectMany(static constructor => constructor.GetParameters())
            .Select(static parameter => parameter.ParameterType)
            .Should()
            .NotContain(typeof(IStudioMemberCommandPort));
    }

    [Fact]
    public async Task UpdateDraftAsync_ShouldOnlyUpdateExistingDraft()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var workspacePort = new RecordingStudioWorkspacePorts(new[]
        {
            new ScopedDraft(
                "scope-1",
                NewDraft(
                    "workflow-1",
                    "workflow-1",
                    "name: workflow-1\nsteps: []\n",
                    DateTimeOffset.UtcNow)),
        });
        var service = environment.CreateService(
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: workspacePort);

        await service.UpdateDraftAsync(
            "scope-1",
            "workflow-1",
            new SaveWorkflowDraftRequest(
                DirectoryId: "scope:scope-1",
                WorkflowName: "workflow-renamed",
                FileName: null,
                Yaml: "name: workflow-renamed\nsteps: []\n"));

    }

    [Fact]
    public async Task CreateDraftAsync_WhenReadModelStaysStale_ShouldReturnAcceptedButImmediateReadsMissIt()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var staleWorkspaceQueryPort = new RecordingStudioWorkspacePorts();
        var acceptingWorkspaceCommandPort = new AcceptingWorkspaceCommandPort();
        var service = environment.CreateService(
            workspaceQueryPort: staleWorkspaceQueryPort,
            workspaceCommandPort: acceptingWorkspaceCommandPort);

        var accepted = await service.CreateDraftAsync(
            "scope-1",
            new SaveWorkflowDraftRequest(
                DirectoryId: "scope:scope-1",
                WorkflowName: "workflow-1",
                FileName: null,
                Yaml: "name: workflow-1\nsteps: []\n"));

        acceptingWorkspaceCommandPort.SavedDrafts.Should().ContainSingle()
            .Which.WorkflowId.Should().Be(accepted.WorkflowId);
        accepted.Accepted.Should().BeTrue();
        accepted.Readiness.Readable.Should().BeFalse();
        accepted.Readiness.Stage.Should().Be("projection_pending");
        (await service.GetDraftAsync("scope-1", accepted.WorkflowId)).Should().BeNull();
        var act = () => service.UpdateDraftAsync(
            "scope-1",
            accepted.WorkflowId,
            new SaveWorkflowDraftRequest(
                DirectoryId: "scope:scope-1",
                WorkflowName: "workflow-1",
                FileName: null,
                Yaml: "name: workflow-1\nsteps: []\n"));
        await act.Should().ThrowAsync<WorkflowDraftNotFoundException>();
    }

    [Fact]
    public async Task CreateDraftAsync_WhenScopedDraftSaveFails_ShouldNotCreateMemberAuthority()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var workspaceQueryPort = new RecordingStudioWorkspacePorts();
        var workspaceCommandPort = new ThrowingWorkspaceCommandPort(
            new InvalidOperationException("workspace command port is unavailable"));
        var service = environment.CreateService(
            workspaceQueryPort: workspaceQueryPort,
            workspaceCommandPort: workspaceCommandPort);

        var act = () => service.CreateDraftAsync(
            "scope-1",
            new SaveWorkflowDraftRequest(
                DirectoryId: "scope:scope-1",
                WorkflowName: "workflow-1",
                FileName: null,
                Yaml: "name: workflow-1\nsteps: []\n"));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("workspace command port is unavailable");
    }

    [Fact]
    public async Task ListDraftsAsync_WhenDraftHasTypedLayout_ShouldDeriveDraftSummaryWithoutLayoutBadge()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var service = environment.CreateService(
            workspaceQueryPort: new RecordingStudioWorkspacePorts(new[]
            {
                new ScopedDraft(
                    "scope-1",
                    NewDraft(
                        "workflow-1",
                        "workflow-1",
                        "name: workflow-1\nsteps: []\n",
                        DateTimeOffset.UtcNow,
                        new WorkflowLayoutDocument
                        {
                            NodePositions =
                            {
                                ["start"] = new WorkflowNodeLayout(10, 20),
                            },
                        })),
            }));

        var summaries = await service.ListDraftsAsync("scope-1");

        summaries.Should().ContainSingle();
        summaries[0].WorkflowId.Should().Be("workflow-1");
        summaries[0].HasLayout.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteDraftAsync_ShouldDeleteTypedDraftWithoutTouchingLayoutSidecar()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var workspacePort = new RecordingStudioWorkspacePorts(new[]
        {
            new ScopedDraft(
                "scope-1",
                NewDraft(
                    "workflow-1",
                    "workflow-1",
                    "name: workflow-1\nsteps: []\n",
                    DateTimeOffset.UtcNow,
                    new WorkflowLayoutDocument
                    {
                        NodePositions =
                        {
                            ["start"] = new WorkflowNodeLayout(10, 20),
                        },
                    })),
        });
        var service = environment.CreateService(
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: workspacePort);

        await service.DeleteDraftAsync("scope-1", "workflow-1");

        var deleted = workspacePort.DeletedDrafts.Should().ContainSingle().Subject;
        deleted.ScopeId.Should().Be("scope-1");
        deleted.WorkflowId.Should().Be("workflow-1");
        deleted.ExpectedVersion.Should().Be(11);
        (await workspacePort.GetAsync("scope-1", CancellationToken.None)).Drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenDraftIsMissing_ShouldThrowWorkflowDraftNotFoundException()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var workspacePort = new RecordingStudioWorkspacePorts();
        var service = environment.CreateService(
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: workspacePort);

        var act = () => service.DeleteDraftAsync("scope-1", "missing-workflow");

        await act.Should().ThrowAsync<WorkflowDraftNotFoundException>();
        workspacePort.DeletedDrafts.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenStoragePortThrows_ShouldPropagateAndLeaveLayoutIntact()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var workspaceQueryPort = new RecordingStudioWorkspacePorts([
            new ScopedDraft(
                "scope-1",
                NewDraft(
                    "workflow-1",
                    "workflow-1",
                    "name: workflow-1\nsteps: []\n",
                    DateTimeOffset.UtcNow)),
        ]);
        var workspaceCommandPort = new ThrowingWorkspaceCommandPort(
            new InvalidOperationException("workspace command port is unavailable"));
        var service = environment.CreateService(
            workspaceQueryPort: workspaceQueryPort,
            workspaceCommandPort: workspaceCommandPort);
        var layoutPath = environment.BuildLayoutPath("scope-1", "workflow-1");
        Directory.CreateDirectory(Path.GetDirectoryName(layoutPath)!);
        await File.WriteAllTextAsync(layoutPath, "{}");

        var act = () => service.DeleteDraftAsync("scope-1", "workflow-1");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("workspace command port is unavailable");
        File.Exists(layoutPath).Should().BeTrue();
    }

    [Fact]
    public async Task CreateDraftAsync_WhenStoragePortThrows_ShouldPropagateAndNotWriteLayoutSidecar()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var workspacePort = new ThrowingWorkspaceQueryPort(
            new InvalidOperationException("workspace query port is unavailable"));
        var service = environment.CreateService(
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: new RecordingStudioWorkspacePorts());
        var layoutPath = environment.BuildLayoutPath("scope-1", "workflow-1");

        var act = () => service.CreateDraftAsync(
            "scope-1",
            new SaveWorkflowDraftRequest(
                DirectoryId: "scope:scope-1",
                WorkflowName: "workflow-1",
                FileName: null,
                Yaml: "name: workflow-1\nsteps: []\n"));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("workspace query port is unavailable");
        File.Exists(layoutPath).Should().BeFalse();
    }

    [Fact]
    public async Task CreateDraftAsync_WhenWorkflowYamlInvalid_ShouldRejectBeforeWorkspaceSave()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var workflowDefinitionParser = new RecordingWorkflowDefinitionParser
        {
            Result = WorkflowYamlParseResult.Invalid("invalid yaml"),
        };
        var workspacePort = new RecordingStudioWorkspacePorts();
        var service = environment.CreateService(
            workflowDefinitionParser: workflowDefinitionParser,
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: workspacePort);

        var act = () => service.CreateDraftAsync(
            "scope-1",
            new SaveWorkflowDraftRequest(
                DirectoryId: "scope:scope-1",
                WorkflowName: "workflow-1",
                FileName: null,
                Yaml: "name: workflow-1\nsteps: []\n"));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("invalid yaml");
        workflowDefinitionParser.ParseCalls.Should().ContainSingle("name: workflow-1\nsteps: []\n");
        workspacePort.QueriedScopes.Should().BeEmpty();
        workspacePort.SavedDrafts.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenCancelled_ShouldPropagateOperationCanceledException()
    {
        using var environment = new ScopedWorkflowEnvironment();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var workspacePort = new ThrowingWorkspaceQueryPort(new OperationCanceledException(cts.Token));
        var service = environment.CreateService(
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: new RecordingStudioWorkspacePorts());

        var act = () => service.DeleteDraftAsync("scope-1", "workflow-1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class ScopedWorkflowEnvironment : IDisposable
    {
        private readonly string? _previousHome;

        public ScopedWorkflowEnvironment()
        {
            HomeDirectory = Path.Combine(Path.GetTempPath(), $"studio-scoped-delete-home-{Guid.NewGuid():N}");
            _previousHome = Environment.GetEnvironmentVariable(AevatarPaths.HomeEnv);
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, HomeDirectory);
        }

        public string HomeDirectory { get; }

        public AppScopedWorkflowService CreateService(
            IWorkflowDefinitionParser? workflowDefinitionParser = null,
            IStudioWorkspaceQueryPort? workspaceQueryPort = null,
            IStudioWorkspaceCommandPort? workspaceCommandPort = null)
        {
            return new AppScopedWorkflowService(
                new StubWorkflowYamlDocumentService(),
                workflowDefinitionParser ?? new RecordingWorkflowDefinitionParser(),
                workspaceQueryPort,
                workspaceCommandPort);
        }

        public string BuildLayoutPath(string scopeId, string workflowId) =>
            Path.Combine(
                AevatarPaths.Root,
                "app",
                "scope-workflow-layouts",
                $"{StudioDocumentIdNormalizer.Normalize(scopeId, "scope")}--{StudioDocumentIdNormalizer.Normalize(workflowId, "workflow")}.json");

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, _previousHome);
            if (Directory.Exists(HomeDirectory))
            {
                Directory.Delete(HomeDirectory, recursive: true);
            }
        }
    }

    private sealed class StubWorkflowYamlDocumentService : IWorkflowYamlDocumentService
    {
        public WorkflowParseResult Parse(string yaml)
        {
            var document = new WorkflowDocument
            {
                Name = ReadScalar(yaml, "name") ?? "workflow",
                Description = ReadScalar(yaml, "description") ?? string.Empty,
            };
            return new WorkflowParseResult(document, []);
        }

        public string Serialize(WorkflowDocument document) =>
            $"name: {document.Name}\nsteps: []\n";

        private static string? ReadScalar(string yaml, string key)
        {
            foreach (var line in yaml.Split('\n'))
            {
                var trimmed = line.Trim();
                var prefix = $"{key}:";
                if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                    return trimmed[prefix.Length..].Trim();
            }

            return null;
        }
    }

    private sealed class RecordingWorkflowDefinitionParser : IWorkflowDefinitionParser
    {
        public WorkflowYamlParseResult Result { get; init; } = WorkflowYamlParseResult.Success("workflow");

        public List<string> ParseCalls { get; } = [];

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            ParseCalls.Add(workflowYaml);
            return Task.FromResult(Result);
        }
    }

    private sealed class ThrowingWorkspaceQueryPort : IStudioWorkspaceQueryPort
    {
        private readonly Exception _exception;

        public ThrowingWorkspaceQueryPort(Exception exception)
        {
            _exception = exception;
        }

        public Task<StudioWorkspaceSnapshot> GetAsync(CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceSnapshot>(_exception);

        public Task<StudioWorkspaceSnapshot> GetAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceSnapshot>(_exception);
    }

    private sealed class AcceptingWorkspaceCommandPort : IStudioWorkspaceCommandPort
    {
        public List<ScopedWorkflowUpload> SavedDrafts { get; } = [];

        public Task<StudioWorkspaceCommandReceipt> UpdateSettingsAsync(StudioWorkspaceSettings settings, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromResult(Receipt("scope-1", expectedVersion));

        public Task<StudioWorkspaceCommandReceipt> AddDirectoryAsync(StudioWorkspaceDirectory directory, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromResult(Receipt("scope-1", expectedVersion));

        public Task<StudioWorkspaceCommandReceipt> RemoveDirectoryAsync(string directoryId, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromResult(Receipt("scope-1", expectedVersion));

        public Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(StudioWorkflowDraftRecord draft, long? expectedVersion = null, CancellationToken ct = default) =>
            SaveDraftAsync("scope-1", draft, expectedVersion, ct);

        public Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(string scopeId, StudioWorkflowDraftRecord draft, long? expectedVersion = null, CancellationToken ct = default)
        {
            SavedDrafts.Add(new ScopedWorkflowUpload(
                scopeId,
                draft.WorkflowId,
                draft.Name,
                draft.Yaml,
                draft.UpdatedAtUtc,
                expectedVersion));
            return Task.FromResult(Receipt(scopeId, expectedVersion));
        }

        public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(string workflowId, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromResult(Receipt("scope-1", expectedVersion));

        public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(string scopeId, string workflowId, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromResult(Receipt(scopeId, expectedVersion));

        private static StudioWorkspaceCommandReceipt Receipt(string scopeId, long? expectedVersion) =>
            new($"studio-workspace:{scopeId}", $"studio-workspace:{scopeId}", Guid.NewGuid().ToString("N"), expectedVersion);
    }

    private sealed class ThrowingWorkspaceCommandPort : IStudioWorkspaceCommandPort
    {
        private readonly Exception _exception;

        public ThrowingWorkspaceCommandPort(Exception exception)
        {
            _exception = exception;
        }

        public Task<StudioWorkspaceCommandReceipt> UpdateSettingsAsync(StudioWorkspaceSettings settings, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);

        public Task<StudioWorkspaceCommandReceipt> AddDirectoryAsync(StudioWorkspaceDirectory directory, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);

        public Task<StudioWorkspaceCommandReceipt> RemoveDirectoryAsync(string directoryId, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);

        public Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(StudioWorkflowDraftRecord draft, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);

        public Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(string scopeId, StudioWorkflowDraftRecord draft, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);

        public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(string workflowId, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);

        public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(string scopeId, string workflowId, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);
    }


    private static StudioWorkflowDraftRecord NewDraft(
        string workflowId,
        string name,
        string yaml,
        DateTimeOffset updatedAtUtc,
        WorkflowLayoutDocument? layout = null) =>
        new(
            workflowId,
            name,
            $"{workflowId}.yaml",
            $"scope://scope-1/{workflowId}.yaml",
            "scope:scope-1",
            "scope-1",
            yaml,
            layout,
            updatedAtUtc,
            updatedAtUtc,
            1);

}

