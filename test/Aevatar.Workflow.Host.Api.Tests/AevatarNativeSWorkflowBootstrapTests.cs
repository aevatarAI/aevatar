using Aevatar.Configuration;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.Workflows;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class AevatarNativeSWorkflowBootstrapTests
{
    [Fact]
    public void AddWorkflowCapability_ShouldIncludeAevatarNativeSWorkflowDirectory()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddWorkflowCapability(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<WorkflowDefinitionFileSourceOptions>>().Value;
        var expectedDirectory = Path.GetFullPath(Path.Combine(
            AevatarPaths.RepoRoot,
            "workflows",
            "aevatar-native",
            "s-workflows"));

        options.WorkflowDirectories
            .Select(Path.GetFullPath)
            .Should()
            .Contain(expectedDirectory);
    }

    [Fact]
    public void WorkflowDefinitionFileLoader_ShouldLoadAevatarNativeSWorkflowsIntoCatalog()
    {
        var catalog = new WorkflowDefinitionCatalog();
        var loader = new WorkflowDefinitionFileLoader();
        var directory = Path.Combine(
            AevatarPaths.RepoRoot,
            "workflows",
            "aevatar-native",
            "s-workflows");

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
