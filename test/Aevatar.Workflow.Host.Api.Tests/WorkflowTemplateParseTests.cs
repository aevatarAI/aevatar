using Aevatar.Configuration;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using Aevatar.Workflow.Core;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

/// <summary>
/// Every YAML under the repository's startup workflow directories must round-trip through
/// <see cref="WorkflowParser"/> and pass <see cref="WorkflowValidator"/>: the catalog file
/// loader only stores raw strings, so without this test a malformed definition would surface
/// only at first run time.
/// </summary>
public class WorkflowTemplateParseTests
{
    public static TheoryData<string, string> WorkflowFiles()
    {
        var data = new TheoryData<string, string>();
        foreach (var directoryName in new[] { "workflows", AevatarPaths.WorkflowTemplatesDirectoryName })
        {
            var directory = Path.Combine(FindRepositoryRoot(), directoryName);
            foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                         .Where(static file =>
                             file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                             file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(static file => file, StringComparer.Ordinal))
            {
                data.Add(directoryName, Path.GetFileName(file));
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(WorkflowFiles))]
    public void StartupWorkflow_ShouldParse(string directoryName, string fileName)
    {
        var yaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), directoryName, fileName));

        var definition = new WorkflowParser().Parse(yaml);

        definition.Name.Should().NotBeNullOrWhiteSpace();
        definition.Steps.Should().NotBeEmpty();
        WorkflowValidator.Validate(definition).Should().BeEmpty();
        WorkflowValidator.Validate(
                definition,
                new WorkflowValidator.WorkflowValidationOptions
                {
                    RequireExplicitLlmAgentToolScopes = true,
                },
                availableWorkflowNames: null)
            .Should()
            .BeEmpty("repository workflows are rebound under the current tool-catalog policy at startup");
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
