using Aevatar.Configuration;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Hosting;
using Aevatar.Studio.Hosting.Controllers;
using Aevatar.Studio.Tests.Shared;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Tests;

public sealed class WorkspaceControllerWorkflowDraftCreateTests
{
    [Fact]
    public async Task CreateDraft_WithScopedWorkspace_ShouldReturnAcceptedReadinessReceipt()
    {
        var workspacePort = new RecordingStudioWorkspacePorts();
        var controller = CreateController(
            new AppScopeContext("scope-1", "test"),
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: workspacePort);

        var result = await controller.CreateDraft(
            NewDraftRequest("workflow-1"),
            scopeId: null,
            CancellationToken.None);

        var acceptedResult = result.Should().BeOfType<AcceptedResult>().Subject;
        var receipt = acceptedResult.Value.Should().BeOfType<WorkflowDraftCreateAcceptedResponse>().Subject;
        receipt.Accepted.Should().BeTrue();
        receipt.AckStage.Should().Be("accepted");
        receipt.WorkspaceId.Should().Be("studio-workspace:scope-1");
        receipt.ActorId.Should().Be("studio-workspace:scope-1");
        receipt.CommandId.Should().NotBeNullOrWhiteSpace();
        receipt.ExpectedVersion.Should().BeNull();
        receipt.Readiness.Readable.Should().BeFalse();
        receipt.Readiness.Stage.Should().Be("projection_pending");
        receipt.Readiness.Message.Should().Contain("Poll the workflow draft by id");
        workspacePort.SavedDrafts.Should().ContainSingle()
            .Which.WorkflowId.Should().Be(receipt.WorkflowId);
    }

    [Fact]
    public async Task CreateDraft_WithUnscopedWorkspace_ShouldKeepReturningWorkflowDraftResponse()
    {
        var workspacePort = new RecordingStudioWorkspacePorts();
        var controller = CreateController(
            scopeContext: null,
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: workspacePort);

        var result = await controller.CreateDraft(
            NewDraftRequest("workflow-1"),
            scopeId: null,
            CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var draft = okResult.Value.Should().BeOfType<WorkflowDraftResponse>().Subject;
        draft.WorkflowId.Should().NotBeNullOrWhiteSpace();
        draft.Name.Should().Be("workflow-1");
        draft.FileName.Should().Be("workflow-1.yaml");
        draft.Yaml.Should().Be("name: workflow-1\nsteps: []\n");
        workspacePort.SavedDrafts.Should().ContainSingle()
            .Which.WorkflowId.Should().Be(draft.WorkflowId);
    }

    private static WorkspaceController CreateController(
        AppScopeContext? scopeContext,
        IStudioWorkspaceQueryPort workspaceQueryPort,
        IStudioWorkspaceCommandPort workspaceCommandPort)
    {
        var yamlDocumentService = new StubWorkflowYamlDocumentService();
        var controller = new WorkspaceController(
            new WorkspaceService(workspaceQueryPort, workspaceCommandPort, yamlDocumentService),
            new AppScopedWorkflowService(
                yamlDocumentService,
                new StubWorkflowDefinitionParser(),
                workspaceQueryPort,
                workspaceCommandPort),
            new StubAppScopeResolver(scopeContext),
            Options.Create(new StudioHostingOptions()));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection().BuildServiceProvider(),
            },
        };
        return controller;
    }

    private static SaveWorkflowDraftRequest NewDraftRequest(string workflowName) =>
        new(
            DirectoryId: "scope:scope-1",
            WorkflowName: workflowName,
            FileName: null,
            Yaml: $"name: {workflowName}\nsteps: []\n");

    private sealed class StubAppScopeResolver : IAppScopeResolver
    {
        private readonly AppScopeContext? _scopeContext;

        public StubAppScopeResolver(AppScopeContext? scopeContext)
        {
            _scopeContext = scopeContext;
        }

        public AppScopeContext? Resolve(HttpContext? httpContext = null) =>
            _scopeContext;

        public bool HasAuthenticatedRequestWithoutScope(HttpContext? httpContext = null) =>
            false;

        public bool HasHttpRequestContext(HttpContext? httpContext = null) => false;
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

    private sealed class StubWorkflowDefinitionParser : IWorkflowDefinitionParser
    {
        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default) =>
            Task.FromResult(WorkflowYamlParseResult.Success(
                workflowYaml.Split('\n')[0][5..].Trim()));

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

}
