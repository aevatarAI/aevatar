using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class DinnerDateMockWorkflowFixtureTests
{
    [Fact]
    public void Parse_ShouldUseChatAssembledContextInput()
    {
        var yaml = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "workflows",
            "dinner_date_mock.yaml"));

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Name.Should().Be("dinner_date_mock");
        var initialize = workflow.Steps.Should().Contain(step => step.Id == "initialize_context").Subject;
        initialize.Parameters["value"].Should().Contain("input.location")
            .And.Contain("input.budget_cap")
            .And.Contain("missing_fields");
        var discovery = workflow.Steps.Should().Contain(step => step.Id == "discover_restaurant_candidates").Subject;
        discovery.Parameters["arguments"].Should().Contain("input.search_query");
        yaml.Should().NotContain("Keong Saik Duxton Singapore")
            .And.NotContain("\"participant\":\"Priya\"")
            .And.NotContain("\"day\":\"Friday\"");
    }

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "aevatar.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
