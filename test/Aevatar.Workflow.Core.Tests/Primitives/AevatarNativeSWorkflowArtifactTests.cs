using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class AevatarNativeSWorkflowArtifactTests
{
    public static TheoryData<string> NativeWorkflowFiles => new()
    {
        "budget-monitoring.yaml",
        "lark-onboarding-email-approval.yaml",
    };

    [Theory]
    [MemberData(nameof(NativeWorkflowFiles))]
    public void NativeSWorkflowYaml_ShouldParseAndPassCoreValidation(string fileName)
    {
        var path = Path.Combine(
            FindRepositoryRoot().FullName,
            "workflows",
            "aevatar-native",
            "s-workflows",
            fileName);
        var yaml = File.ReadAllText(path);

        var workflow = new WorkflowParser().Parse(yaml);
        var errors = WorkflowValidator.Validate(
            workflow,
            new WorkflowValidator.WorkflowValidationOptions
            {
                RequireKnownStepTypes = true,
                KnownStepTypes = new HashSet<string>(
                    WorkflowPrimitiveCatalog.BuiltInCanonicalTypes,
                    StringComparer.OrdinalIgnoreCase),
                DisallowDynamicWorkflowStep = true,
            },
            availableWorkflowNames: null);

        errors.Should().BeEmpty();
        workflow.Name.Should().Be(Path.GetFileNameWithoutExtension(fileName));
        workflow.Steps.Should().NotBeEmpty();
        workflow.Steps.Any(step =>
            step.Type == "tool_call" &&
            step.Parameters.TryGetValue("tool", out var tool) &&
            tool == "use_skill").Should().BeTrue();
        workflow.Steps.Any(step =>
            step.Type == "connector_call" &&
            step.Parameters.TryGetValue("connector", out var connector) &&
            connector.StartsWith("nyxid_", StringComparison.Ordinal)).Should().BeTrue();
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "aevatar.slnx")))
                return current;

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
