using System.Text.Json;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Expressions;
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
        WorkflowRunInputContract.RequiresJsonInput(workflow).Should().BeTrue();
        var capture = workflow.Steps.Should().Contain(step => step.Id == "capture_user_choice").Subject;
        capture.Parameters["value"].Should().Be("$input");
        var initialize = workflow.Steps.Should().Contain(step => step.Id == "initialize_context").Subject;
        initialize.Parameters["value"].Should().Contain("steps.capture_user_choice.json.location")
            .And.Contain("steps.capture_user_choice.json.budget_cap")
            .And.Contain("missing_fields");
        var discovery = workflow.Steps.Should().Contain(step => step.Id == "discover_restaurant_candidates").Subject;
        discovery.Parameters["arguments"].Should().Contain("\"${json(steps.capture_user_choice.json.search_query)}\"");
        yaml.Should().NotContain("Keong Saik Duxton Singapore")
            .And.NotContain("\"participant\":\"Priya\"")
            .And.NotContain("\"day\":\"Friday\"");
    }

    [Fact]
    public void RenderedParameters_ShouldRemainValidJsonForStructuredInput()
    {
        var workflow = new WorkflowParser().Parse(File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "workflows",
            "dinner_date_mock.yaml")));
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["steps.capture_user_choice.json.task_id"] = "task-uc5",
            ["steps.capture_user_choice.json.raw_user_request"] = "I want to book dinner on Tuesday",
            ["steps.capture_user_choice.json.participant"] = "Priya",
            ["steps.capture_user_choice.json.window"] = "Tuesday evening",
            ["steps.capture_user_choice.json.party_size"] = "2",
            ["steps.capture_user_choice.json.day"] = "Tuesday",
            ["steps.capture_user_choice.json.time"] = "19:30",
            ["steps.capture_user_choice.json.location"] = "Keong Saik Duxton Singapore",
            ["steps.capture_user_choice.json.cuisines"] = "[\"Japanese\",\"Italian\"]",
            ["steps.capture_user_choice.json.restaurant_type"] = "romantic dinner",
            ["steps.capture_user_choice.json.phone_number"] = "+6590000000",
            ["steps.capture_user_choice.json.budget_cap"] = "120",
            ["steps.capture_user_choice.json.policy"] = "{\"show_options_before_calls\":true,\"money_spend_allowed\":false,\"reservation_calls_auto_allowed\":true}",
            ["steps.capture_user_choice.json.missing_fields"] = "[]",
            ["steps.capture_user_choice.json.search_query"] = "Keong Saik Duxton Singapore romantic dinner Tuesday 7:30pm",
        };
        var evaluator = new WorkflowExpressionEvaluator();

        var initialize = workflow.Steps.Single(step => step.Id == "initialize_context");
        var contextJson = evaluator.Evaluate(initialize.Parameters["value"], variables);
        var discovery = workflow.Steps.Single(step => step.Id == "discover_restaurant_candidates");
        var argumentsJson = evaluator.Evaluate(discovery.Parameters["arguments"], variables);

        using var contextDocument = JsonDocument.Parse(contextJson);
        contextDocument.RootElement.GetProperty("location").GetString().Should().Be("Keong Saik Duxton Singapore");
        contextDocument.RootElement.GetProperty("cuisines").EnumerateArray().Should().HaveCount(2);
        contextDocument.RootElement.GetProperty("policy").GetProperty("show_options_before_calls").GetBoolean().Should().BeTrue();
        using var argumentsDocument = JsonDocument.Parse(argumentsJson);
        argumentsDocument.RootElement.GetProperty("query").GetString().Should()
            .Be("Keong Saik Duxton Singapore romantic dinner Tuesday 7:30pm");
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
