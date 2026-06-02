using Aevatar.Workflow.Infrastructure.Workflows;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowLibraryClassifierTests
{
    [Theory]
    [MemberData(nameof(ClassificationCases))]
    public void Classify_ShouldAssignLibraryGroupAndLabels(
        string workflowName,
        string? sourceKind,
        string? category,
        WorkflowLibraryClassification expected)
    {
        var actual = WorkflowLibraryClassifier.Classify(workflowName, sourceKind!, category!);

        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("assign", 5)]
    [InlineData(" ASSIGN ", 5)]
    [InlineData("007_custom", 7)]
    [InlineData("7custom", null)]
    [InlineData("custom_7", null)]
    [InlineData("abc_custom", null)]
    public void TryGetWorkflowIndex_ShouldResolveLegacyAndPrefixedNames(
        string? workflowName,
        int? expected)
    {
        WorkflowLibraryClassifier.TryGetWorkflowIndex(workflowName!).Should().Be(expected);
    }

    public static IEnumerable<object?[]> ClassificationCases()
    {
        yield return
        [
            "assign",
            "HOME",
            null,
            new WorkflowLibraryClassification(
                "your-workflows",
                "Your Workflows",
                5,
                true,
                false,
                "Saved"),
        ];
        yield return
        [
            "custom",
            "cwd",
            null,
            new WorkflowLibraryClassification(
                "your-workflows",
                "Your Workflows",
                0,
                true,
                false,
                "Workspace"),
        ];
        yield return
        [
            "counter-machine",
            "turing",
            null,
            new WorkflowLibraryClassification(
                "advanced-patterns",
                "Advanced Patterns",
                901,
                true,
                false,
                "Advanced"),
        ];
        yield return
        [
            "minsky-register",
            "turing",
            null,
            new WorkflowLibraryClassification(
                "advanced-patterns",
                "Advanced Patterns",
                902,
                true,
                false,
                "Advanced"),
        ];
        yield return
        [
            "other-machine",
            "turing",
            null,
            new WorkflowLibraryClassification(
                "advanced-patterns",
                "Advanced Patterns",
                999,
                true,
                false,
                "Advanced"),
        ];
        yield return
        [
            "transform",
            "repo",
            null,
            new WorkflowLibraryClassification(
                "primitive-examples",
                "Primitive Mini Examples",
                1,
                false,
                true,
                "Mini"),
        ];
        yield return
        [
            "llm_call",
            "repo",
            null,
            new WorkflowLibraryClassification(
                "ai-workflows",
                "AI & Human Workflows",
                8,
                true,
                false,
                "Starter"),
        ];
        yield return
        [
            "human_input_basic_auto_resume",
            "repo",
            null,
            new WorkflowLibraryClassification(
                "ai-workflows",
                "AI & Human Workflows",
                39,
                true,
                false,
                "Interactive"),
        ];
        yield return
        [
            "connector_cli_demo",
            "repo",
            null,
            new WorkflowLibraryClassification(
                "integration-workflows",
                "Integrations & Tools",
                50,
                true,
                false,
                "Integration"),
        ];
        yield return
        [
            "demo_template",
            "repo",
            null,
            new WorkflowLibraryClassification(
                "advanced-patterns",
                "Advanced Patterns",
                17,
                true,
                false,
                "Advanced"),
        ];
        yield return
        [
            "subworkflow_level1",
            "repo",
            null,
            new WorkflowLibraryClassification(
                "advanced-patterns",
                "Advanced Patterns",
                48,
                true,
                false,
                "Advanced"),
        ];
        yield return
        [
            "workflow_call_multilevel",
            "repo",
            null,
            new WorkflowLibraryClassification(
                "advanced-patterns",
                "Advanced Patterns",
                49,
                true,
                false,
                "Advanced"),
        ];
        yield return
        [
            "200_custom",
            "builtin",
            "llm",
            new WorkflowLibraryClassification(
                "starter-workflows",
                "Starter Workflows",
                200,
                true,
                false,
                "Built-in"),
        ];
        yield return
        [
            "custom",
            "app",
            "llm",
            new WorkflowLibraryClassification(
                "starter-workflows",
                "Starter Workflows",
                100,
                true,
                false,
                "Bundled"),
        ];
        yield return
        [
            "custom",
            "repo",
            "other",
            new WorkflowLibraryClassification(
                "starter-workflows",
                "Starter Workflows",
                200,
                true,
                false,
                "Starter"),
        ];
        yield return
        [
            "custom",
            "demo",
            "other",
            new WorkflowLibraryClassification(
                "starter-workflows",
                "Starter Workflows",
                200,
                true,
                false,
                "Starter"),
        ];
        yield return
        [
            "custom",
            "external",
            "other",
            new WorkflowLibraryClassification(
                "starter-workflows",
                "Starter Workflows",
                200,
                true,
                false,
                "Workflow"),
        ];
        yield return
        [
            "custom",
            null,
            "LLM",
            new WorkflowLibraryClassification(
                "starter-workflows",
                "Starter Workflows",
                100,
                true,
                false,
                "Workflow"),
        ];
    }
}
