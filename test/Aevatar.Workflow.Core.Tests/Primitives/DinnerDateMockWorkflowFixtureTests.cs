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
            "workflow-templates",
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
        discovery.Type.Should().Be("assign");
        discovery.Parameters["value"].Should().Contain("mock_catalog")
            .And.Contain("${json(steps.initialize_context.json.search_query)}");
        yaml.Should().NotContain("tool: web_search");
        yaml.Should().NotContain("Keong Saik Duxton Singapore")
            .And.NotContain("\"participant\":\"Priya\"")
            .And.NotContain("\"day\":\"Friday\"");
    }

    [Fact]
    public void Parse_ShouldAutoHoldAllVenuesAfterUserChoiceTimeoutAndReleaseUnselectedAfterChoice()
    {
        var yaml = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "workflow-templates",
            "dinner_date_mock.yaml"));

        var workflow = new WorkflowParser().Parse(yaml);

        var optionsShown = workflow.Steps.Should().Contain(step => step.Id == "emit_options_shown").Subject;
        optionsShown.Next.Should().Be("wait_for_user_choice_timeout");

        var timeoutWait = workflow.Steps.Should().Contain(step => step.Id == "wait_for_user_choice_timeout").Subject;
        timeoutWait.Type.Should().Be("wait_signal");
        timeoutWait.Next.Should().Be("normalize_user_choice");
        timeoutWait.Parameters["signal_name"].Should().Be("dinner_date_user_choice");
        timeoutWait.Parameters["timeout_ms"].Should().Be("10000");
        timeoutWait.OnError.Should().NotBeNull();
        timeoutWait.OnError!.Strategy.Should().Be("fallback");
        timeoutWait.OnError.FallbackStep.Should().Be("mark_silence_timeout");

        var normalizer = workflow.Steps.Should().Contain(step => step.Id == "normalize_user_choice").Subject;
        normalizer.Parameters["prompt_prefix"].Should().Contain("steps.wait_for_user_choice_timeout.output")
            .And.NotContain("steps.capture_user_choice.output");

        var timeoutMarker = workflow.Steps.Should().Contain(step => step.Id == "mark_silence_timeout").Subject;
        timeoutMarker.Next.Should().Be("hold_candidate_option_1");
        timeoutMarker.Parameters["value"].Should().Contain("hold_all")
            .And.Contain("automatically holding all shown venues");

        workflow.Steps.Should().Contain(step => step.Id == "hold_candidate_option_1").Subject
            .Next.Should().Be("hold_candidate_option_2");
        workflow.Steps.Should().Contain(step => step.Id == "hold_candidate_option_2").Subject
            .Next.Should().Be("hold_candidate_option_3");
        workflow.Steps.Should().Contain(step => step.Id == "hold_candidate_option_3").Subject
            .Next.Should().Be("publish_holds_wait_state");

        var waitForPostTimeoutChoice = workflow.Steps.Should()
            .Contain(step => step.Id == "wait_for_post_timeout_choice")
            .Subject;
        waitForPostTimeoutChoice.Type.Should().Be("wait_signal");
        waitForPostTimeoutChoice.Parameters["signal_name"].Should().Be("dinner_date_user_choice_after_timeout");

        var route = workflow.Steps.Should().Contain(step => step.Id == "route_post_timeout_choice").Subject;
        route.Branches.Should().ContainKey("option_1").WhoseValue.Should().Be("release_unselected_after_confirm_option_1");
        route.Branches.Should().ContainKey("option_2").WhoseValue.Should().Be("release_unselected_after_confirm_option_2");
        route.Branches.Should().ContainKey("option_3").WhoseValue.Should().Be("release_unselected_after_confirm_option_3");

        workflow.Steps.Should().Contain(step => step.Id == "release_unselected_after_confirm_option_1").Subject
            .Parameters["value"].Should().Contain("\"released_options\":[\"option_2\",\"option_3\"]");
        workflow.Steps.Should().Contain(step => step.Id == "final_artifact_post_timeout_confirmed_option_1").Subject
            .Parameters["value"].Should().Contain("post_timeout_choice_releases_unselected_venues");

        var waitingArtifact = workflow.Steps.Should()
            .Contain(step => step.Id == "final_artifact_waiting_after_holds")
            .Subject;
        waitingArtifact.Parameters["value"].Should().Contain("timeout_auto_hold_all_waiting_for_user_choice")
            .And.Contain("all_three_venues_held_after_timeout")
            .And.Contain("post_timeout_user_choice");
    }

    [Fact]
    public void RenderedParameters_ShouldRemainValidJsonForStructuredInput()
    {
        var workflow = new WorkflowParser().Parse(File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "workflow-templates",
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
            ["steps.initialize_context.json.search_query"] = "Keong Saik Duxton Singapore romantic dinner Tuesday 7:30pm",
        };
        var evaluator = new WorkflowExpressionEvaluator();

        var initialize = workflow.Steps.Single(step => step.Id == "initialize_context");
        var contextJson = evaluator.Evaluate(initialize.Parameters["value"], variables);
        var discovery = workflow.Steps.Single(step => step.Id == "discover_restaurant_candidates");
        var candidatesJson = evaluator.Evaluate(discovery.Parameters["value"], variables);

        using var contextDocument = JsonDocument.Parse(contextJson);
        contextDocument.RootElement.GetProperty("location").GetString().Should().Be("Keong Saik Duxton Singapore");
        contextDocument.RootElement.GetProperty("cuisines").EnumerateArray().Should().HaveCount(2);
        contextDocument.RootElement.GetProperty("policy").GetProperty("show_options_before_calls").GetBoolean().Should().BeTrue();
        using var candidatesDocument = JsonDocument.Parse(candidatesJson);
        candidatesDocument.RootElement.GetProperty("source").GetString().Should().Be("mock_catalog");
        candidatesDocument.RootElement.GetProperty("query").GetString().Should()
            .Be("Keong Saik Duxton Singapore romantic dinner Tuesday 7:30pm");
        candidatesDocument.RootElement.GetProperty("results").EnumerateArray().Should().HaveCount(3);
    }

    [Fact]
    public void RenderedParameters_ShouldNormalizeSemanticAliasesFromChatInput()
    {
        var workflow = new WorkflowParser().Parse(File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "workflow-templates",
            "dinner_date_mock.yaml")));
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["steps.capture_user_choice.output"] = "Plan a dinner date with Priya this Friday at 7:30 PM",
            ["steps.capture_user_choice.json.companion_name"] = "Priya",
            ["steps.capture_user_choice.json.date"] = "2026-09-04",
            ["steps.capture_user_choice.json.time"] = "19:30",
            ["steps.capture_user_choice.json.party_size"] = "2",
            ["steps.capture_user_choice.json.location_context"] = "Keong Saik Duxton Singapore",
            ["steps.capture_user_choice.json.preferred_cuisines"] = "[\"Japanese\",\"Italian\"]",
            ["steps.capture_user_choice.json.contact_phone_number"] = "+6590000000",
            ["steps.capture_user_choice.json.budget_cap"] = "120",
            ["steps.capture_user_choice.json.policy"] = "{\"show_options_before_calls\":true,\"money_spend_allowed\":false,\"reservation_calls_auto_allowed\":true}",
        };
        var evaluator = new WorkflowExpressionEvaluator();

        var initialize = workflow.Steps.Single(step => step.Id == "initialize_context");
        var contextJson = evaluator.Evaluate(initialize.Parameters["value"], variables);

        using var contextDocument = JsonDocument.Parse(contextJson);
        var context = contextDocument.RootElement;
        context.GetProperty("raw_user_request").GetString().Should().Be("Plan a dinner date with Priya this Friday at 7:30 PM");
        context.GetProperty("participant").GetString().Should().Be("Priya");
        context.GetProperty("day").GetString().Should().Be("2026-09-04");
        context.GetProperty("time").GetString().Should().Be("19:30");
        context.GetProperty("party_size").GetInt32().Should().Be(2);
        context.GetProperty("location").GetString().Should().Be("Keong Saik Duxton Singapore");
        context.GetProperty("cuisines").EnumerateArray().Select(element => element.GetString())
            .Should().Equal("Japanese", "Italian");
        context.GetProperty("phone_number").GetString().Should().Be("+6590000000");
        context.GetProperty("budget_cap").GetInt32().Should().Be(120);
        context.GetProperty("policy").GetProperty("reservation_calls_auto_allowed").GetBoolean().Should().BeTrue();
        context.GetProperty("search_query").GetString().Should().Be("Keong Saik Duxton Singapore");
        context.GetProperty("missing_fields").EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public void RenderedTimeoutReleaseArtifact_ShouldRemainValidJson()
    {
        var workflow = new WorkflowParser().Parse(File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "workflow-templates",
            "dinner_date_mock.yaml")));
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["steps.hold_candidate_option_1.json.venue"] = "Option One",
            ["steps.hold_candidate_option_2.json.venue"] = "Option Two",
            ["steps.hold_candidate_option_3.json.venue"] = "Option Three",
            ["steps.release_unselected_after_confirm_option_1.json.released"] = "[\"Option Two\",\"Option Three\"]",
            ["steps.release_unselected_after_confirm_option_1.json.released_options"] = "[\"option_2\",\"option_3\"]",
        };
        var evaluator = new WorkflowExpressionEvaluator();

        var finalArtifact = workflow.Steps.Single(step => step.Id == "final_artifact_post_timeout_confirmed_option_1");
        var renderedJson = evaluator.Evaluate(finalArtifact.Parameters["value"], variables);

        using var document = JsonDocument.Parse(renderedJson);
        document.RootElement.GetProperty("path").GetString().Should().Be("timeout_auto_hold_then_user_selected");
        document.RootElement.GetProperty("summary").GetString().Should()
            .Be("Option One is selected. Released Option Two and Option Three.");
        document.RootElement.GetProperty("kept").GetString().Should().Be("Option One");
        document.RootElement.GetProperty("released_options").EnumerateArray()
            .Select(element => element.GetString())
            .Should().Equal("option_2", "option_3");
        document.RootElement.GetProperty("success_contract")
            .GetProperty("post_timeout_choice_releases_unselected_venues")
            .GetBoolean()
            .Should().BeTrue();
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
