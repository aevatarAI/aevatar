using System.Text.Json;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class DinnerSearchThenCallWorkflowFixtureTests
{
    [Fact]
    public void Parse_ShouldUseChatAssembledContextInput()
    {
        var yaml = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "workflow-templates",
            "dinner_search_then_call.yaml"));

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Name.Should().Be("dinner_search_then_call");
        WorkflowRunInputContract.RequiresJsonInput(workflow).Should().BeTrue();
        var capture = workflow.Steps.Should().Contain(step => step.Id == "capture_user_choice").Subject;
        capture.Parameters["value"].Should().Be("$input");
        var initialize = workflow.Steps.Should().Contain(step => step.Id == "initialize_context").Subject;
        initialize.Parameters["value"].Should().Contain("steps.capture_user_choice.json.location")
            .And.Contain("steps.capture_user_choice.json.budget_cap")
            .And.Contain("missing_fields");
        var discovery = workflow.Steps.Should().Contain(step => step.Id == "discover_restaurant_candidates").Subject;
        discovery.Parameters["tool"].Should().Be("nyxid_proxy");
        discovery.Parameters["arguments"].Should().Contain("\"${json(steps.capture_user_choice.json.search_query)}\"");
        var holdSelectedOption = workflow.Steps.Should()
            .Contain(step => step.Id == "hold_selected_option_1")
            .Subject;
        holdSelectedOption.Capability.Should().NotBeNull();
        holdSelectedOption.Capability!.NyxIdRequest.PathTemplate.Should()
            .Be("/v1/convai/twilio/outbound-call");
        yaml.Should().NotContain("Keong Saik Duxton Singapore")
            .And.NotContain("\"participant\":\"Priya\"")
            .And.NotContain("\"day\":\"Friday\"")
            .And.NotContain("+6580102726")
            .And.NotContain("normalize_reservation_request");
    }

    [Fact]
    public void Parse_ShouldHoldAllVenuesAfterUserChoiceTimeout()
    {
        var yaml = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "workflow-templates",
            "dinner_search_then_call.yaml"));

        var workflow = new WorkflowParser().Parse(yaml);

        var timeoutMarker = workflow.Steps.Should().Contain(step => step.Id == "mark_silence_timeout").Subject;
        timeoutMarker.Next.Should().Be("hold_candidate_option_1");
        timeoutMarker.Parameters["value"].Should().Contain("hold_all")
            .And.Contain("automatically holding all shown venues");

        workflow.Steps.Should().Contain(step => step.Id == "hold_candidate_option_1");
        workflow.Steps.Should().Contain(step => step.Id == "hold_candidate_option_2");
        workflow.Steps.Should().Contain(step => step.Id == "hold_candidate_option_3");
        workflow.Steps.Should().Contain(step => step.Id == "publish_holds_wait_state");
        workflow.Steps.Should().Contain(step => step.Id == "wait_for_post_timeout_choice");
        workflow.Steps.Should().Contain(step => step.Id == "release_unselected_after_confirm_option_1");

        var waitingArtifact = workflow.Steps.Should()
            .Contain(step => step.Id == "final_artifact_waiting_after_holds")
            .Subject;
        waitingArtifact.Parameters["value"].Should().Contain("timeout_auto_hold_all_waiting_for_user_choice")
            .And.Contain("all_three_venues_held_after_timeout")
            .And.Contain("configured_restaurant_phone_used");
    }

    [Fact]
    public void RenderedParameters_ShouldRemainValidJsonForStructuredInput()
    {
        var workflow = new WorkflowParser().Parse(File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "workflow-templates",
            "dinner_search_then_call.yaml")));
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
        var body = argumentsDocument.RootElement.GetProperty("body");
        body.GetProperty("query").GetString().Should()
            .Be("Keong Saik Duxton Singapore romantic dinner Tuesday 7:30pm");
        body.GetProperty("limit").GetInt32().Should().Be(8);
    }

    [Fact]
    public void RenderedCallParameters_ShouldUseConfiguredRestaurantPhoneNumber()
    {
        var workflow = new WorkflowParser().Parse(File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "workflow-templates",
            "dinner_search_then_call.yaml")));
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["steps.capture_user_choice.json.restaurant_phone_number"] = "+6511111111",
            ["steps.capture_user_choice.json.participant"] = "Priya",
            ["steps.capture_user_choice.json.party_size"] = "2",
            ["steps.capture_user_choice.json.day"] = "Tuesday",
            ["steps.capture_user_choice.json.time"] = "19:30",
            ["steps.capture_user_choice.json.backup_times"] = "19:45",
            ["steps.capture_user_choice.json.phone_number"] = "+6590000000",
            ["steps.capture_user_choice.json.special_requests"] = "quiet table",
            ["steps.build_shortlist_from_candidates.json.option_1_name"] = "Option One",
            ["steps.build_shortlist_from_candidates.json.option_1_url"] = "https://example.test/one",
        };
        var evaluator = new WorkflowExpressionEvaluator();

        var callStep = workflow.Steps.Single(step => step.Id == "hold_selected_option_1");
        var argumentsJson = evaluator.Evaluate(callStep.Parameters["arguments"], variables);

        using var argumentsDocument = JsonDocument.Parse(argumentsJson);
        var body = argumentsDocument.RootElement.GetProperty("body");
        body.GetProperty("to_number").GetString().Should().Be("+6511111111");
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
