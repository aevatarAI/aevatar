using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using Aevatar.Workflow.Core;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

/// <summary>
/// Every YAML under the repository's workflows/ directory must round-trip through
/// <see cref="WorkflowParser"/> and pass <see cref="WorkflowValidator"/>: the catalog file
/// loader only stores raw strings, so without this test a malformed template would surface
/// only at first run time.
/// </summary>
public class WorkflowTemplateParseTests
{
    public static TheoryData<string> TemplateFiles()
    {
        var data = new TheoryData<string>();
        var directory = Path.Combine(FindRepositoryRoot(), "workflows");
        foreach (var file in Directory.EnumerateFiles(directory, "*.yaml", SearchOption.TopDirectoryOnly).OrderBy(f => f))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(TemplateFiles))]
    public void Template_ShouldParse(string fileName)
    {
        var yaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "workflows", fileName));

        var definition = new WorkflowParser().Parse(yaml);

        definition.Name.Should().NotBeNullOrWhiteSpace();
        definition.Steps.Should().NotBeEmpty();
        WorkflowValidator.Validate(definition).Should().BeEmpty();
        definition.Steps
            .Where(step => step.Type == "await_job" || step.Type == "async_job")
            .Should()
            .BeEmpty();
        WorkflowAuthorizationDependencyEvaluator.Evaluate(definition).ExternalCapabilities
            .Should()
            .NotContain(capability =>
                capability.CapabilityCase ==
                Aevatar.Workflow.Abstractions.ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService,
                "repository startup workflows cannot embed a tenant-owned NyxID UserService identity");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
