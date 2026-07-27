using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Services;
using Aevatar.Studio.Infrastructure.Serialization;
using Aevatar.Studio.Infrastructure.WorkflowTemplates;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class EmbeddedWorkflowTemplateCatalogQueryPortTests
{
    [Fact]
    public async Task ListAsync_ShouldFilterAndPageInStableTemplateOrder()
    {
        var catalog = NewCatalog(
            Registration("zeta-report", "1", "Zeta Report", "Reports", "summarize"),
            Registration("alpha-report", "2", "Alpha Report", "Reports", "summarize"),
            Registration("alpha-alert", "1", "Alpha Alert", "Operations", "notify"));

        var first = await catalog.ListAsync(new WorkflowTemplateCatalogQuery(
            Query: "report",
            Category: "reports",
            Cursor: null,
            PageSize: 1));
        var second = await catalog.ListAsync(new WorkflowTemplateCatalogQuery(
            Query: "report",
            Category: "reports",
            Cursor: first.NextCursor,
            PageSize: 1));

        first.Items.Should().ContainSingle().Which.TemplateId.Should().Be("alpha-report");
        first.Items[0].Should().NotBeOfType<WorkflowTemplateDetail>();
        first.NextCursor.Should().NotBeNullOrWhiteSpace();
        second.Items.Should().ContainSingle().Which.TemplateId.Should().Be("zeta-report");
        second.NextCursor.Should().BeNull();
        first.ETag.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnOnlyTheExactRegisteredRevision()
    {
        var catalog = NewCatalog(
            Registration("alpha-report", "1", "Alpha Report", "Reports", "summarize"),
            Registration("alpha-report", "2", "Alpha Report", "Reports", "summarize-v2"));

        var found = await catalog.GetAsync("alpha-report", "1");
        var missing = await catalog.GetAsync("alpha-report", "3");

        found.Status.Should().Be(WorkflowTemplateLookupStatus.Found);
        found.Detail!.Revision.Should().Be("1");
        found.Detail.WorkflowYaml.Should().Contain("name: summarize\n");
        missing.Status.Should().Be(WorkflowTemplateLookupStatus.NotFound);
        missing.Detail.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ShouldDistinguishDisabledAndIncompatibleRevisions()
    {
        var incompatible = new WorkflowTemplateCompatibility(
            WorkflowTemplateCompatibilityStatus.Incompatible,
            WorkflowTemplateCompatibilityReason.RequiredPrimitiveUnavailable);
        var catalog = NewCatalog(
            Registration("disabled-template", "1", "Disabled", "Operations", "disabled", isEnabled: false),
            Registration("future-template", "1", "Future", "Operations", "future", compatibility: incompatible));

        var disabled = await catalog.GetAsync("disabled-template", "1");
        var future = await catalog.GetAsync("future-template", "1");

        disabled.Status.Should().Be(WorkflowTemplateLookupStatus.Disabled);
        disabled.Detail.Should().BeNull();
        future.Status.Should().Be(WorkflowTemplateLookupStatus.Incompatible);
        future.Detail!.Compatibility.Should().Be(incompatible);
    }

    [Fact]
    public async Task Queries_ShouldAdmitEveryReturnedRegistrationThroughTheCanonicalParser()
    {
        var parser = new RecordingWorkflowDefinitionParser();
        var catalog = NewCatalog(parser,
            Registration("alpha-report", "1", "Alpha Report", "Reports", "summarize"));

        await catalog.ListAsync(new WorkflowTemplateCatalogQuery(PageSize: 20));
        await catalog.GetAsync("alpha-report", "1");

        parser.ParsedYamls.Should().HaveCount(2).And.OnlyContain(yaml => yaml.Contains("name: summarize\n"));
    }

    [Fact]
    public async Task Query_ShouldRejectInvalidPageAndOpaqueCursorInputs()
    {
        var catalog = NewCatalog(Registration("alpha-report", "1", "Alpha", "Reports", "alpha"));

        var invalidSize = () => catalog.ListAsync(new WorkflowTemplateCatalogQuery(PageSize: 0));
        var invalidCursor = () => catalog.ListAsync(new WorkflowTemplateCatalogQuery(Cursor: "../catalog", PageSize: 20));

        await invalidSize.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await invalidCursor.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetAsync_ShouldRejectPathLikeIdentityWithoutInvokingTheParser()
    {
        var parser = new RecordingWorkflowDefinitionParser();
        var catalog = NewCatalog(parser,
            Registration("alpha-report", "1", "Alpha", "Reports", "alpha"));

        var query = () => catalog.GetAsync("../secrets", "1");

        await query.Should().ThrowAsync<ArgumentException>();
        parser.ParsedYamls.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_ShouldRejectARegistrationWhenCanonicalWorkflowValidationFails()
    {
        var parser = new RecordingWorkflowDefinitionParser { Error = "yaml contains private prompt text" };
        var catalog = NewCatalog(parser,
            Registration("alpha-report", "1", "Alpha", "Reports", "alpha"));

        var query = () => catalog.ListAsync(new WorkflowTemplateCatalogQuery(PageSize: 20));

        var exception = await query.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("alpha-report").And.Contain("revision '1'");
        exception.Which.Message.Should().NotContain(parser.Error);
    }

    [Fact]
    public async Task Constructor_ShouldSnapshotMutableRegistrationRequirements()
    {
        var requiredPrimitives = new[] { "transform" };
        var registration = Registration("alpha-report", "1", "Alpha", "Reports", "alpha") with
        {
            Requirements = new WorkflowTemplateRequirements(requiredPrimitives, "1.0"),
        };
        var catalog = NewCatalog(registration);
        requiredPrimitives[0] = "mutated";

        var page = await catalog.ListAsync(new WorkflowTemplateCatalogQuery(PageSize: 20));

        page.Items[0].Requirements.RequiredPrimitives.Should().Equal("transform");
        page.Items[0].Requirements.RequiredPrimitives.Should().NotBeAssignableTo<string[]>();
    }

    private static EmbeddedWorkflowTemplateCatalogQueryPort NewCatalog(
        params EmbeddedWorkflowTemplateRegistration[] registrations) =>
        NewCatalog(new RecordingWorkflowDefinitionParser(), registrations);

    private static EmbeddedWorkflowTemplateCatalogQueryPort NewCatalog(
        IWorkflowDefinitionParser parser,
        params EmbeddedWorkflowTemplateRegistration[] registrations) =>
        new(
            parser,
            new YamlWorkflowDocumentService(WorkflowCompatibilityProfile.AevatarV1),
            new WorkflowValidator(WorkflowCompatibilityProfile.AevatarV1),
            new WorkflowGraphMapper(WorkflowCompatibilityProfile.AevatarV1),
            registrations);

    private static EmbeddedWorkflowTemplateRegistration Registration(
        string templateId,
        string revision,
        string title,
        string category,
        string workflowName,
        bool isEnabled = true,
        WorkflowTemplateCompatibility? compatibility = null) =>
        new(
            templateId,
            revision,
            0,
            Text(title),
            Text($"{title} summary"),
            Text($"{title} description"),
            category,
            ["test"],
            new WorkflowTemplateExpectedIO(Text("input"), Text("output")),
            $"name: {workflowName}\nsteps:\n  - id: start\n    type: transform\n",
            new WorkflowTemplateRequirements(["transform"], "1.0"),
            compatibility ?? WorkflowTemplateCompatibility.Compatible,
            isEnabled);

    private static WorkflowTemplateLocalizedText Text(string value) =>
        new(value, value);

    private sealed class RecordingWorkflowDefinitionParser : IWorkflowDefinitionParser
    {
        public List<string> ParsedYamls { get; } = [];
        public string? Error { get; init; }

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            ParsedYamls.Add(workflowYaml);
            return Task.FromResult(Error == null
                ? WorkflowYamlParseResult.Success("validated")
                : WorkflowYamlParseResult.Invalid(Error));
        }
    }
}
