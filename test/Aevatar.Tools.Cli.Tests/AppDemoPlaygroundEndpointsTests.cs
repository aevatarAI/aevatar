using Aevatar.Tools.Cli.Hosting;
using Aevatar.Workflow.Infrastructure.Workflows;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Aevatar.Tools.Cli.Tests;

public class AppDemoPlaygroundEndpointsTests
{
    [Fact]
    public void ValidatePlaygroundWorkflow_WhenYamlIsValid_ShouldSucceed()
    {
        var services = new ServiceCollection()
            .BuildServiceProvider();

        var result = AppDemoPlaygroundEndpoints.ValidatePlaygroundWorkflow(
            """
            name: review_summary
            description: Summarize a review.
            steps:
              - id: collect_summary
                type: assign
                parameters:
                  variable: "summary"
                  value: "ready"
            """,
            services);

        result.Valid.Should().BeTrue();
        result.Definition.Should().NotBeNull();
        result.Definition!.Name.Should().Be("review_summary");
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidatePlaygroundWorkflow_WhenStepTypeIsUnknown_ShouldFail()
    {
        var services = new ServiceCollection()
            .BuildServiceProvider();

        var result = AppDemoPlaygroundEndpoints.ValidatePlaygroundWorkflow(
            """
            name: bad_workflow
            description: Contains an unsupported module.
            steps:
              - id: mystery
                type: mystery_step
            """,
            services);

        result.Valid.Should().BeFalse();
        result.Errors.Should().ContainSingle(error => error.Contains("未知原语", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizeWorkflowSaveFilename_WhenFilenameMissing_ShouldDeriveFromWorkflowName()
    {
        var filename = AppDemoPlaygroundEndpoints.NormalizeWorkflowSaveFilename(
            requestedFilename: null,
            workflowName: "Review Workflow 2026!");

        filename.Should().Be("Review_Workflow_2026.yaml");
    }

    [Fact]
    public void NormalizeWorkflowSaveFilename_WhenFilenameContainsDirectoryTraversal_ShouldThrow()
    {
        var act = () => AppDemoPlaygroundEndpoints.NormalizeWorkflowSaveFilename(
            requestedFilename: "../escape.yaml",
            workflowName: "ignored");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must not include directory segments*");
    }

    [Fact]
    public void ClassifyWorkflowForLibrary_WhenWorkflowIsPrimitiveMiniExample_ShouldHideFromLibrary()
    {
        var classification = AppDemoPlaygroundEndpoints.ClassifyWorkflowForLibrary(
            workflowName: "01_transform",
            sourceKind: "demo",
            category: "deterministic");

        classification.ShowInLibrary.Should().BeFalse();
        classification.IsPrimitiveExample.Should().BeTrue();
        classification.Group.Should().Be("primitive-examples");
    }

    [Fact]
    public void ClassifyWorkflowForLibrary_WhenWorkflowComesFromHome_ShouldAppearInYourWorkflows()
    {
        var classification = AppDemoPlaygroundEndpoints.ClassifyWorkflowForLibrary(
            workflowName: "incident_triage",
            sourceKind: "home",
            category: "llm");

        classification.ShowInLibrary.Should().BeTrue();
        classification.Group.Should().Be("your-workflows");
        classification.SourceLabel.Should().Be("Saved");
    }

    [Fact]
    public void ClassifyWorkflowForLibrary_WhenWorkflowComesFromWorkspace_ShouldMergeIntoYourWorkflows()
    {
        var classification = AppDemoPlaygroundEndpoints.ClassifyWorkflowForLibrary(
            workflowName: "workspace_triage",
            sourceKind: "cwd",
            category: "deterministic");

        classification.ShowInLibrary.Should().BeTrue();
        classification.Group.Should().Be("your-workflows");
        classification.GroupLabel.Should().Be("Your Workflows");
        classification.SourceLabel.Should().Be("Workspace");
    }

    [Fact]
    public void ClassifyWorkflowForLibrary_WhenWorkflowIsHumanInteractive_ShouldUseAiAndHumanGroup()
    {
        var classification = AppDemoPlaygroundEndpoints.ClassifyWorkflowForLibrary(
            workflowName: "43_human_input_manual_triage",
            sourceKind: "demo",
            category: "llm");

        classification.ShowInLibrary.Should().BeTrue();
        classification.Group.Should().Be("ai-workflows");
        classification.GroupLabel.Should().Be("AI & Human Workflows");
        classification.SourceLabel.Should().Be("Interactive");
    }

    [Theory]
    [InlineData("01_transform", "demo", "deterministic")]
    [InlineData("43_human_input_manual_triage", "demo", "llm")]
    [InlineData("workspace_triage", "cwd", "deterministic")]
    [InlineData("incident_triage", "home", "llm")]
    [InlineData("repo_install", "repo", "deterministic")]
    public void ClassifyWorkflowForLibrary_ShouldStayConsistentWithSharedClassifier(
        string workflowName,
        string sourceKind,
        string category)
    {
        var appClassification = AppDemoPlaygroundEndpoints.ClassifyWorkflowForLibrary(workflowName, sourceKind, category);
        var sharedClassification = WorkflowLibraryClassifier.Classify(workflowName, sourceKind, category);

        appClassification.Should().BeEquivalentTo(sharedClassification);
    }

    [Fact]
    public void CuratedPrimitiveExamples_ShouldContainTransformStarterExample()
    {
        var field = typeof(AppDemoPlaygroundEndpoints).GetField(
            "CuratedPrimitiveExamples",
            BindingFlags.Static | BindingFlags.NonPublic);

        field.Should().NotBeNull();
        var curated = field!.GetValue(null)
            .Should().BeAssignableTo<IReadOnlyDictionary<string, string[]>>()
            .Subject;

        curated.Should().ContainKey("transform");
        curated["transform"].Should().Contain("01_transform");
    }

    [Fact]
    public void ResolvePrimitiveDescriptor_ShouldExposeDescriptionAliasesAndParameters()
    {
        var descriptor = AppDemoPlaygroundEndpoints.ResolvePrimitiveDescriptor("parallel_fanout");

        descriptor.Name.Should().Be("parallel");
        descriptor.Category.Should().Be("composition");
        descriptor.Description.Should().NotBeNullOrWhiteSpace();
        descriptor.Aliases.Should().Contain(["parallel", "parallel_fanout"]);
        descriptor.Parameters.Should().ContainSingle(parameter => parameter.Name == "branches");
    }

    [Fact]
    public void ResolvePrimitiveDescriptor_ForConnectorCalls_ShouldUseOperationParameterName()
    {
        var connectorDescriptor = AppDemoPlaygroundEndpoints.ResolvePrimitiveDescriptor("connector_call");

        connectorDescriptor.Parameters.Should().Contain(parameter => parameter.Name == "operation");
        connectorDescriptor.Parameters.Should().NotContain(parameter => parameter.Name == "command");
    }

}
