using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.WorkflowTemplates;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Hosting.Controllers;
using Aevatar.Studio.Tests.Shared;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowTemplatesControllerTests
{
    [Fact]
    public async Task List_ShouldReturnOnlyPublicTemplates_WithFreshnessAndRequiredConnections()
    {
        var controller = CreateController(
            new RecordingWorkflowCatalogPort(
                Detail("tmpl-alpha", showInLibrary: true, stateVersion: 7),
                Detail("tmpl-hidden", showInLibrary: false, stateVersion: 8)),
            new RecordingStudioWorkspacePorts());

        var result = await controller.List(
            query: null,
            sort: null,
            cursor: null,
            take: null,
            CancellationToken.None);

        var response = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PublicWorkflowTemplateListResponse>().Subject;
        response.Items.Should().ContainSingle();
        var template = response.Items[0];
        template.TemplateId.Should().Be("tmpl-alpha");
        template.DisplayName.Should().Be("tmpl-alpha");
        template.DefaultDraftName.Should().Be("tmpl-alpha");
        template.AuthorityStateVersion.Should().Be(7);
        template.StepCount.Should().Be(2);
        template.RequiredConnections.Should().Equal("nyxid-calendar");
        template.RequiresLlmProvider.Should().BeTrue();
        template.Freshness.ProjectionWatermark.Should().Be(ProjectionWatermark);
        response.Freshness.VersionSemantics.Should().Contain("max=7");
    }

    [Fact]
    public async Task List_ShouldExposeFreshnessAcrossFilteredResult_WhenPageIsTruncated()
    {
        var controller = CreateController(
            new RecordingWorkflowCatalogPort(
                Detail("tmpl-alpha", showInLibrary: true, stateVersion: 7),
                Detail(
                    "tmpl-beta",
                    showInLibrary: true,
                    stateVersion: 9,
                    projectionWatermark: ProjectionWatermark.AddMinutes(5))),
            new RecordingStudioWorkspacePorts());

        var result = await controller.List(
            query: null,
            sort: null,
            cursor: null,
            take: 1,
            CancellationToken.None);

        var response = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PublicWorkflowTemplateListResponse>().Subject;
        response.Items.Should().ContainSingle(item => item.TemplateId == "tmpl-beta");
        response.NextCursor.Should().Be("1");
        response.Freshness.VersionSemantics.Should().Contain("max=9");
        response.Freshness.LastEventId.Should().Be("event-9");
    }

    [Fact]
    public async Task Get_WhenTemplateIsHidden_ShouldReturnNotFoundAndNotCreateDraft()
    {
        var workspacePorts = new RecordingStudioWorkspacePorts();
        var controller = CreateController(
            new RecordingWorkflowCatalogPort(Detail("tmpl-hidden", showInLibrary: false, stateVersion: 3)),
            workspacePorts);

        var result = await controller.Get("tmpl-hidden", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        workspacePorts.SavedDrafts.Should().BeEmpty();
        workspacePorts.QueriedScopes.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_WhenTemplateIsPublic_ShouldReturnYamlDefinitionEdgesAndNotCreateDraft()
    {
        var workspacePorts = new RecordingStudioWorkspacePorts();
        var controller = CreateController(
            new RecordingWorkflowCatalogPort(Detail("tmpl-alpha", showInLibrary: true, stateVersion: 7)),
            workspacePorts);

        var result = await controller.Get("tmpl-alpha", CancellationToken.None);

        var detail = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PublicWorkflowTemplateDetailResponse>().Subject;
        detail.Template.TemplateId.Should().Be("tmpl-alpha");
        detail.Yaml.Should().Contain("name: tmpl-alpha");
        detail.Definition.Name.Should().Be("tmpl-alpha");
        detail.Definition.Steps.Should().HaveCount(2);
        detail.Edges.Should().ContainSingle(edge => edge.From == "collect" && edge.To == "summarize");
        detail.AuthorityStateVersion.Should().Be(7);
        detail.Freshness.LastEventId.Should().Be("event-7");
        workspacePorts.SavedDrafts.Should().BeEmpty();
        workspacePorts.QueriedScopes.Should().BeEmpty();
    }

    [Fact]
    public async Task Instantiate_WhenExpectedAuthorityStateVersionIsStale_ShouldReturnConflictAndNotCreateDraft()
    {
        var workspacePorts = new RecordingStudioWorkspacePorts();
        var controller = CreateController(
            new RecordingWorkflowCatalogPort(Detail("tmpl-alpha", showInLibrary: true, stateVersion: 7)),
            workspacePorts);

        var result = await controller.Instantiate(
            "scope-alpha",
            "tmpl-alpha",
            new WorkflowTemplateInstantiateRequest(ExpectedAuthorityStateVersion: 6),
            CancellationToken.None);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value.Should().NotBeNull();
        conflict.Value!.GetType().GetProperty("code")!.GetValue(conflict.Value).Should()
            .Be("WORKFLOW_TEMPLATE_VERSION_CONFLICT");
        workspacePorts.SavedDrafts.Should().BeEmpty();
    }

    [Fact]
    public async Task Instantiate_ShouldCreateScopedDraftThroughExistingDraftPath_WithDistinctWorkflowId()
    {
        var workspacePorts = new RecordingStudioWorkspacePorts();
        var controller = CreateController(
            new RecordingWorkflowCatalogPort(Detail("tmpl-alpha", showInLibrary: true, stateVersion: 7)),
            workspacePorts);

        var result = await controller.Instantiate(
            "scope-alpha",
            "tmpl-alpha",
            new WorkflowTemplateInstantiateRequest(ExpectedAuthorityStateVersion: 7),
            CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        var receipt = accepted.Value.Should().BeOfType<WorkflowDraftCreateAcceptedResponse>().Subject;
        receipt.Accepted.Should().BeTrue();
        receipt.WorkflowId.Should().NotBeNullOrWhiteSpace();
        receipt.WorkflowId.Should().NotBe("tmpl-alpha");
        receipt.WorkspaceId.Should().Be("studio-workspace:scope-alpha");
        workspacePorts.SavedDrafts.Should().ContainSingle();
        var saved = workspacePorts.SavedDrafts[0];
        saved.ScopeId.Should().Be("scope-alpha");
        saved.WorkflowId.Should().Be(receipt.WorkflowId);
        saved.WorkflowId.Should().NotBe("tmpl-alpha");
        saved.WorkflowName.Should().Be("tmpl-alpha");
        saved.Yaml.Should().Contain("name: tmpl-alpha");
    }

    private static WorkflowTemplatesController CreateController(
        IWorkflowCatalogPort catalogPort,
        RecordingStudioWorkspacePorts workspacePorts)
    {
        var yamlDocumentService = new StubWorkflowYamlDocumentService();
        var scopedWorkflowService = new AppScopedWorkflowService(
            yamlDocumentService,
            new StubWorkflowDefinitionParser(),
            workspacePorts,
            workspacePorts);
        var controller = new WorkflowTemplatesController(
            new PublicWorkflowTemplateService(catalogPort, scopedWorkflowService),
            new StubAppScopeResolver(new AppScopeContext("scope-alpha", "test")));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        return controller;
    }

    private static WorkflowCatalogItemDetail Detail(
        string templateId,
        bool showInLibrary,
        long stateVersion,
        DateTimeOffset? projectionWatermark = null) =>
        new()
        {
            Catalog = new WorkflowCatalogItem
            {
                Name = templateId,
                Description = "Collect and summarize the day.",
                ShowInLibrary = showInLibrary,
                RequiresLlmProvider = true,
                RequiredConnectors = ["nyxid-calendar"],
                StepCount = 2,
                AuthorityStateVersion = stateVersion,
                ProjectionWatermark = projectionWatermark ?? ProjectionWatermark,
                LastEventId = $"event-{stateVersion}",
            },
            Yaml = $"name: {templateId}\ndescription: Collect and summarize the day.\nsteps:\n  - id: collect\n    type: connector_call\n  - id: summarize\n    type: llm_call\n",
            Definition = new WorkflowCatalogDefinition
            {
                Name = templateId,
                Description = "Collect and summarize the day.",
                Steps =
                [
                    new WorkflowCatalogStep
                    {
                        Id = "collect",
                        Type = "connector_call",
                        Next = "summarize",
                    },
                    new WorkflowCatalogStep
                    {
                        Id = "summarize",
                        Type = "llm_call",
                    },
                ],
            },
            Edges =
            [
                new WorkflowCatalogEdge
                {
                    From = "collect",
                    To = "summarize",
                    Label = "next",
                },
            ],
        };

    private static readonly DateTimeOffset ProjectionWatermark =
        new(2026, 8, 17, 9, 30, 0, TimeSpan.Zero);

    private sealed class RecordingWorkflowCatalogPort : IWorkflowCatalogPort
    {
        private readonly IReadOnlyList<WorkflowCatalogItemDetail> _details;

        public RecordingWorkflowCatalogPort(params WorkflowCatalogItemDetail[] details)
        {
            _details = details;
        }

        public Task<IReadOnlyList<WorkflowCatalogItem>> ListWorkflowCatalogAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowCatalogItem>>(_details.Select(static detail => detail.Catalog).ToList());

        public Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(string workflowName, CancellationToken ct = default) =>
            Task.FromResult(_details.FirstOrDefault(detail =>
                string.Equals(detail.Catalog.Name, workflowName.Trim(), StringComparison.Ordinal)));

        public Task<IReadOnlyList<WorkflowCatalogItem>> ListPublicWorkflowCatalogAsync(CancellationToken ct = default)
        {
            IReadOnlyList<WorkflowCatalogItem> publicCatalog = _details
                .Select(static detail => detail.Catalog)
                .Where(static item => item.ShowInLibrary)
                .ToList();
            return Task.FromResult(publicCatalog);
        }

        public async Task<WorkflowCatalogItemDetail?> GetPublicWorkflowDetailAsync(
            string templateId,
            CancellationToken ct = default)
        {
            var detail = await GetWorkflowDetailAsync(templateId, ct);
            return detail?.Catalog.ShowInLibrary == true ? detail : null;
        }
    }

    private sealed class StubAppScopeResolver(AppScopeContext? scopeContext) : IAppScopeResolver
    {
        public AppScopeContext? Resolve(HttpContext? httpContext = null) => scopeContext;

        public bool HasAuthenticatedRequestWithoutScope(HttpContext? httpContext = null) => false;

        public bool HasHttpRequestContext(HttpContext? httpContext = null) => false;
    }

    private sealed class StubWorkflowYamlDocumentService : IWorkflowYamlDocumentService
    {
        public WorkflowParseResult Parse(string yaml) =>
            new(new WorkflowDocument
            {
                Name = ReadScalar(yaml, "name") ?? "workflow",
                Description = ReadScalar(yaml, "description") ?? string.Empty,
            }, []);

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
