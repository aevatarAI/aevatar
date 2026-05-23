using System.Text.RegularExpressions;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Tests.Shared;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class AppScopedWorkflowServiceTests
{
    [Fact]
    public void PublicSurface_ShouldNotExposeObsoleteCompatWrappers()
    {
        var publicInstanceMethods = typeof(AppScopedWorkflowService)
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Where(method => method.DeclaringType == typeof(AppScopedWorkflowService))
            .Select(static method => method.Name)
            .ToList();

        publicInstanceMethods.Should().NotContain("ListAsync");
        publicInstanceMethods.Should().NotContain("GetAsync");
        publicInstanceMethods.Should().NotContain("SaveDraftAsync");
    }

    [Fact]
    public async Task CreateDraftAsync_ShouldRewriteYamlNameFromRequestedWorkflowName()
    {
        var workspacePort = new RecordingStudioWorkspacePorts();
        var service = new AppScopedWorkflowService(
            new StubWorkflowYamlDocumentService(),
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: workspacePort);

        var response = await service.CreateDraftAsync(
            "scope-1",
            new SaveWorkflowDraftRequest(
                DirectoryId: "scope:scope-1",
                WorkflowName: "renamed-workflow",
                FileName: null,
                Yaml: "name: draft\nsteps: []\n"));

        workspacePort.LastUpload.Should().NotBeNull();
        workspacePort.LastUpload!.ScopeId.Should().Be("scope-1");
        workspacePort.LastUpload!.WorkflowId.Should().Be("renamed-workflow");
        workspacePort.LastUpload.WorkflowName.Should().Be("renamed-workflow");
        workspacePort.LastUpload.Yaml.Should().StartWith("name: renamed-workflow");
        response.Name.Should().Be("renamed-workflow");
        response.Yaml.Should().StartWith("name: renamed-workflow");
    }

    [Fact]
    public async Task UpdateDraftAsync_ShouldRewriteYamlNameFromRequestedWorkflowName()
    {
        var originalCreatedAt = new DateTimeOffset(2026, 4, 9, 8, 0, 0, TimeSpan.Zero);
        var workspacePort = new RecordingStudioWorkspacePorts(
            NewDraft(
                "renamed-workflow",
                "old-name",
                "name: old-name\nsteps: []\n",
                originalCreatedAt));
        var service = new AppScopedWorkflowService(
            new StubWorkflowYamlDocumentService(),
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: workspacePort);

        var response = await service.UpdateDraftAsync(
            "scope-1",
            "renamed-workflow",
            new SaveWorkflowDraftRequest(
                DirectoryId: "scope:scope-1",
                WorkflowName: "renamed-workflow",
                FileName: null,
                Yaml: "name: draft\nsteps: []\n"));

        workspacePort.LastUpload.Should().NotBeNull();
        workspacePort.LastUpload!.ScopeId.Should().Be("scope-1");
        workspacePort.LastUpload.WorkflowId.Should().Be("renamed-workflow");
        workspacePort.LastUpload.WorkflowName.Should().Be("renamed-workflow");
        workspacePort.LastUpload.Yaml.Should().StartWith("name: renamed-workflow");
        response.WorkflowId.Should().Be("renamed-workflow");
        response.Name.Should().Be("renamed-workflow");
        response.Yaml.Should().StartWith("name: renamed-workflow");
    }

    [Fact]
    public async Task ListDraftsAsync_WhenStoredDraftExistsUnderDifferentScope_ShouldNotLeakAcrossScopes()
    {
        var service = new AppScopedWorkflowService(
            new StubWorkflowYamlDocumentService(),
            workspaceQueryPort: new RecordingStudioWorkspacePorts(new[]
            {
                new ScopedDraft(
                    "scope-2",
                    NewDraft(
                        "hello-chat",
                        "hello-chat",
                        "name: hello-chat\ndescription: stored workflow\nsteps: []\n",
                        new DateTimeOffset(2026, 4, 10, 9, 0, 0, TimeSpan.Zero))),
            }));

        var workflows = await service.ListDraftsAsync("scope-1");

        workflows.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDraftAsync_WhenStoredDraftExistsUnderDifferentScope_ShouldNotLeakAcrossScopes()
    {
        var service = new AppScopedWorkflowService(
            new StubWorkflowYamlDocumentService(),
            workspaceQueryPort: new RecordingStudioWorkspacePorts(new[]
            {
                new ScopedDraft(
                    "scope-2",
                    NewDraft(
                        "hello-chat",
                        "hello-chat",
                        "name: hello-chat\ndescription: restored from storage\nsteps: []\n",
                        new DateTimeOffset(2026, 4, 10, 9, 0, 0, TimeSpan.Zero))),
            }));

        var workflow = await service.GetDraftAsync("scope-1", "hello-chat");

        workflow.Should().BeNull();
    }

    [Fact]
    public async Task ListDraftsAsync_WhenWorkspaceContainsDraft_ShouldReturnDraftSummary()
    {
        var service = new AppScopedWorkflowService(
            new StubWorkflowYamlDocumentService(),
            workspaceQueryPort: new RecordingStudioWorkspacePorts(
                NewDraft(
                    "hello-chat",
                    "hello-chat",
                    "name: hello-chat\ndescription: stored workflow\nsteps: []\n",
                    new DateTimeOffset(2026, 4, 10, 9, 0, 0, TimeSpan.Zero))));

        var workflows = await service.ListDraftsAsync("scope-1");

        workflows.Should().ContainSingle();
        workflows[0].WorkflowId.Should().Be("hello-chat");
        workflows[0].Name.Should().Be("hello-chat");
        workflows[0].Description.Should().Be("stored workflow");
    }

    [Fact]
    public async Task ListDraftsAsync_ShouldUseStoredYamlToPopulateStepCount()
    {
        var storedUpdatedAt = new DateTimeOffset(2026, 4, 16, 10, 53, 48, TimeSpan.Zero);
        var service = new AppScopedWorkflowService(
            new StubWorkflowYamlDocumentService(),
            workspaceQueryPort: new RecordingStudioWorkspacePorts(
                NewDraft(
                    "test03",
                    "test03",
                    "name: test03\ndescription: restored from storage\nsteps:\n  - id: llm_call\n",
                    storedUpdatedAt)));

        var workflows = await service.ListDraftsAsync("scope-1");

        workflows.Should().ContainSingle();
        workflows[0].WorkflowId.Should().Be("test03");
        workflows[0].StepCount.Should().Be(1);
        workflows[0].Description.Should().Be("restored from storage");
        workflows[0].UpdatedAtUtc.Should().Be(storedUpdatedAt);
    }

    [Fact]
    public async Task GetDraftAsync_WhenWorkspaceContainsDraft_ShouldReturnDraft()
    {
        var service = new AppScopedWorkflowService(
            new StubWorkflowYamlDocumentService(),
            workspaceQueryPort: new RecordingStudioWorkspacePorts(
                NewDraft(
                    "hello-chat",
                    "hello-chat",
                    "name: hello-chat\ndescription: restored from storage\nsteps: []\n",
                    new DateTimeOffset(2026, 4, 10, 9, 0, 0, TimeSpan.Zero))));

        var workflow = await service.GetDraftAsync("scope-1", "hello-chat");

        workflow.Should().NotBeNull();
        workflow!.WorkflowId.Should().Be("hello-chat");
        workflow.Name.Should().Be("hello-chat");
        workflow.Yaml.Should().Contain("restored from storage");
    }

    [Fact]
    public async Task GetDraftAsync_WhenStoredDraftExists_ShouldPreferWorkflowDraftData()
    {
        var service = new AppScopedWorkflowService(
            new StubWorkflowYamlDocumentService(),
            workspaceQueryPort: new RecordingStudioWorkspacePorts(
                NewDraft(
                    "test03",
                    "test03",
                    "name: draft-version\ndescription: prefer stored draft\nsteps:\n  - id: llm_call\n",
                    new DateTimeOffset(2026, 4, 16, 10, 53, 48, TimeSpan.Zero))));

        var result = await service.GetDraftAsync("scope-1", "test03");

        result.Should().NotBeNull();
        result!.Name.Should().Be("draft-version");
        result.Yaml.Should().Contain("draft-version");
        result.Yaml.Should().Contain("llm_call");
    }

    private sealed class StubWorkflowYamlDocumentService : IWorkflowYamlDocumentService
    {
        private static readonly Regex NameRegex = new(@"(?m)^name:\s*(.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex DescriptionRegex = new(@"(?m)^description:\s*(.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex StepsBlockRegex = new(@"(?ms)^steps:\s*\n(?<items>(?:\s*-\s.*\n?)*)", RegexOptions.Compiled);
        private static readonly Regex StepItemRegex = new(@"(?m)^\s*-\s+", RegexOptions.Compiled);

        public WorkflowParseResult Parse(string yaml)
        {
            if (string.IsNullOrWhiteSpace(yaml))
                return new(null, []);

            var input = yaml ?? string.Empty;
            var nameMatch = NameRegex.Match(input);
            var descriptionMatch = DescriptionRegex.Match(input);
            var steps = new List<StepModel>();
            var stepsMatch = StepsBlockRegex.Match(input);
            if (stepsMatch.Success)
            {
                var stepItems = StepItemRegex.Matches(stepsMatch.Groups["items"].Value).Count;
                for (var index = 0; index < stepItems; index++)
                {
                    steps.Add(new StepModel());
                }
            }

            return new(new WorkflowDocument
            {
                Name = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : string.Empty,
                Description = descriptionMatch.Success ? descriptionMatch.Groups[1].Value.Trim() : string.Empty,
                Steps = steps,
            }, []);
        }

        public string Serialize(WorkflowDocument document) => $"name: {document.Name}\nsteps: []\n";
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
