using System.Security.Cryptography;
using System.Text;
using Aevatar.Configuration;
using Aevatar.GAgents.WorkflowDelivery;
using Aevatar.Studio.Application.Delivery;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Infrastructure.Workflows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowDeliveryPackageCatalogTests
{
    private static readonly string[] AllowedWorkflowNames =
    [
        "hr_onboarding_email_approval",
        "hr_monthly_attendance_approval",
        "hr_attendance_fill_reminder",
        "fin_invoice_precheck_approval",
        "fin_budget_variance_monitor",
    ];

    [Fact]
    public async Task ListAsync_ShouldExposeExactlyTheFiveAllowlistedParseableWorkflows()
    {
        var catalog = CreateCatalog(AllowedWorkflowNames);

        var packages = await catalog.ListAsync("admin-alpha", CancellationToken.None);

        packages.Select(static package => package.WorkflowName)
            .Should().Equal(AllowedWorkflowNames);
        packages.Should().OnlyContain(static package =>
            package.PackageId == package.WorkflowName &&
            package.SourceYaml.Length > 0);

        var parser = new WorkflowParser();
        foreach (var package in packages)
            parser.Parse(package.SourceYaml).Name.Should().Be(package.WorkflowName);
    }

    [Theory]
    [InlineData("hr_onboarding_email_approval")]
    [InlineData("hr_monthly_attendance_approval")]
    [InlineData("hr_attendance_fill_reminder")]
    [InlineData("fin_invoice_precheck_approval")]
    [InlineData("fin_budget_variance_monitor")]
    public async Task GetAsync_ShouldDeriveStableSourceAndPackageHashes(string workflowName)
    {
        var catalog = CreateCatalog(AllowedWorkflowNames);

        var first = await catalog.GetAsync(
            workflowName,
            "admin-alpha",
            CancellationToken.None);
        var second = await catalog.GetAsync(
            workflowName,
            "admin-beta",
            CancellationToken.None);
        var expectedHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(first.SourceYaml)));
        var expectedPackageHash = WorkflowDeliveryConventions.ComputePackageHash(first);

        first.SourceHash.Should().Be(expectedHash);
        first.PackageHash.Should().Be(expectedPackageHash);
        first.Version.Should().Be(expectedPackageHash[..16]);
        first.PackageVersionId.Should().Be($"{workflowName}@{expectedPackageHash[..16]}");
        second.SourceYaml.Should().Be(first.SourceYaml);
        second.SourceHash.Should().Be(first.SourceHash);
        second.PackageHash.Should().Be(first.PackageHash);
        second.Version.Should().Be(first.Version);
        second.PackageVersionId.Should().Be(first.PackageVersionId);
    }

    [Fact]
    public async Task GetAsync_PackageIdentity_ShouldCoverAllImmutableDeliverySemantics()
    {
        var package = await CreateCatalog(AllowedWorkflowNames).GetAsync(
            "fin_invoice_precheck_approval",
            "admin-alpha",
            CancellationToken.None);
        package.AcceptancePolicy.Mode.Should().Be(WorkflowDeliveryAcceptanceMode.Manual);
        package.AcceptancePolicy.Limitation.Should().Contain("input_file_refs");

        var variants = new[]
        {
            Mutate(package, value => value.VariableSchema[0].Label += " changed"),
            Mutate(package, value => value.ConnectionSlots[0].ServiceSlug += "-changed"),
            Mutate(package, value => value.Capabilities.Add("new.capability")),
            Mutate(package, value => value.RiskSummary += " changed"),
            Mutate(package, value => value.ParserDiagnostics.Add("new diagnostic")),
            Mutate(package, value => value.AcceptancePolicy.Mode = WorkflowDeliveryAcceptanceMode.AutomaticPreview),
            Mutate(package, value => value.AcceptancePolicy.Limitation += " changed"),
        };

        variants.Should().OnlyContain(value =>
            WorkflowDeliveryConventions.ComputePackageHash(value) != package.PackageHash);
    }

    [Fact]
    public async Task GetAsync_WhenSourceExistsButWorkflowIsNotAllowlisted_ShouldFailClosed()
    {
        var catalog = CreateCatalog(AllowedWorkflowNames);

        var act = () => catalog.GetAsync(
            "delivery_catalog_fixture_outside_allowlist",
            "admin-alpha",
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<WorkflowDeliveryPackageNotAllowedException>();
        exception.Which.WorkflowName.Should().Be("delivery_catalog_fixture_outside_allowlist");
    }

    [Fact]
    public async Task ListAsync_WhenConfigurationContainsUnsupportedWorkflow_ShouldFailClosed()
    {
        var configuredNames = AllowedWorkflowNames
            .Append("delivery_catalog_fixture_outside_allowlist")
            .ToArray();
        var catalog = CreateCatalog(configuredNames);

        var act = () => catalog.ListAsync(
            "admin-alpha",
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unsupported workflow package*");
    }

    private static WorkflowDeliveryPackageCatalog CreateCatalog(IReadOnlyList<string> allowedWorkflowNames)
    {
        var definitions = new WorkflowDefinitionCatalog();
        var loader = new WorkflowDefinitionFileLoader();
        loader.LoadInto(
            definitions,
            [AevatarPaths.RepoRootWorkflows],
            NullLogger.Instance);
        definitions.Register(
            "delivery_catalog_fixture_outside_allowlist",
            """
            name: delivery_catalog_fixture_outside_allowlist
            steps:
              - id: complete
                type: assign
                parameters:
                  target: result
                  value: done
            """,
            ExternalCapabilityExecutionMode.Interactive);

        return new WorkflowDeliveryPackageCatalog(
            definitions,
            new RealWorkflowDefinitionParser(),
            Options.Create(new WorkflowDeliveryOptions
            {
                AllowedWorkflowNames = [.. allowedWorkflowNames],
            }),
            TimeProvider.System);
    }

    private static WorkflowPackageVersionSnapshot Mutate(
        WorkflowPackageVersionSnapshot source,
        Action<WorkflowPackageVersionSnapshot> mutation)
    {
        var clone = source.Clone();
        mutation(clone);
        return clone;
    }

    private sealed class RealWorkflowDefinitionParser : IWorkflowDefinitionParser
    {
        private readonly WorkflowParser _parser = new();

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var workflow = _parser.Parse(workflowYaml);
                return Task.FromResult(WorkflowYamlParseResult.Success(workflow.Name));
            }
            catch (Exception exception)
            {
                return Task.FromResult(WorkflowYamlParseResult.Invalid(exception.Message));
            }
        }

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
