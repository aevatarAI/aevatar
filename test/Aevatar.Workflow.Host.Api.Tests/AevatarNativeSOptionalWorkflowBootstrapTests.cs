using Aevatar.Configuration;
using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Infrastructure.Workflows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class AevatarNativeSOptionalWorkflowBootstrapTests
{
    [Fact]
    public void WorkflowDefinitionFileLoader_ShouldLoadAevatarNativeSOptionalWorkflowsOnlyWhenRequested()
    {
        var catalog = new WorkflowDefinitionCatalog();
        var loader = new WorkflowDefinitionFileLoader();
        var directory = Path.Combine(
            AevatarPaths.RepoRoot,
            "workflows",
            "aevatar-native",
            "optional-workflows");

        var loaded = loader.LoadInto(
            catalog,
            [directory],
            NullLogger.Instance,
            WorkflowDefinitionDuplicatePolicy.Throw);

        loaded.Should().Be(2);
        catalog.GetNames().Should().Contain(["budget-monitoring", "lark-onboarding-email-approval"]);
        var budgetYaml = catalog.GetYaml("budget-monitoring");
        budgetYaml.Should().Contain("name: budget-monitoring");
        var onboardingYaml = catalog.GetYaml("lark-onboarding-email-approval");
        onboardingYaml.Should().Contain("name: lark-onboarding-email-approval");
    }
}
