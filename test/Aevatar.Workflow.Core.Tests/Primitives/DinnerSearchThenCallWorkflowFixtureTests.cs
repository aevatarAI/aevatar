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
        discovery.Parameters["arguments"].Should().Contain("\"${json(steps.initialize_context.json.search_query)}\"");
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
        var optionsShown = workflow.Steps.Should().Contain(step => step.Id == "emit_options_shown").Subject;
        optionsShown.Parameters["payload"].Should().Contain("phone_flow")
            .And.Contain("timeout_policy")
            .And.Contain("hold_all_three_then_wait_for_post_timeout_choice");
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
            ["steps.capture_user_choice.json.restaurant_phone_number"] = "+6511111111",
            ["steps.capture_user_choice.json.budget_cap"] = "120",
            ["steps.capture_user_choice.json.policy"] = "{\"show_options_before_calls\":true,\"money_spend_allowed\":false,\"reservation_calls_auto_allowed\":true}",
            ["steps.capture_user_choice.json.missing_fields"] = "[]",
            ["steps.capture_user_choice.json.search_query"] = "Keong Saik Duxton Singapore romantic dinner Tuesday 7:30pm",
        };
        var evaluator = new WorkflowExpressionEvaluator();

        var initialize = workflow.Steps.Single(step => step.Id == "initialize_context");
        var contextJson = evaluator.Evaluate(initialize.Parameters["value"], variables);
        using var contextDocument = JsonDocument.Parse(contextJson);
        variables["steps.initialize_context.json.search_query"] = contextDocument.RootElement
            .GetProperty("search_query")
            .GetString()!;
        var discovery = workflow.Steps.Single(step => step.Id == "discover_restaurant_candidates");
        var argumentsJson = evaluator.Evaluate(discovery.Parameters["arguments"], variables);

        contextDocument.RootElement.GetProperty("location").GetString().Should().Be("Keong Saik Duxton Singapore");
        contextDocument.RootElement.GetProperty("cuisines").EnumerateArray().Should().HaveCount(2);
        contextDocument.RootElement.GetProperty("policy").GetProperty("show_options_before_calls").GetBoolean().Should().BeTrue();
        contextDocument.RootElement.GetProperty("restaurant_phone_number").GetString().Should().Be("+6511111111");
        using var argumentsDocument = JsonDocument.Parse(argumentsJson);
        var body = argumentsDocument.RootElement.GetProperty("body");
        body.GetProperty("query").GetString().Should()
            .Be("Keong Saik Duxton Singapore romantic dinner Tuesday 7:30pm");
        body.GetProperty("limit").GetInt32().Should().Be(8);
    }

    [Fact]
    public void RenderedParameters_ShouldBuildSearchQueryWhenChatOmitsExplicitQuery()
    {
        var workflow = new WorkflowParser().Parse(File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "workflow-templates",
            "dinner_search_then_call.yaml")));
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["steps.capture_user_choice.output"] = "Plan a dinner date with Priya this week.",
            ["steps.capture_user_choice.json.participant"] = "Priya",
            ["steps.capture_user_choice.json.contact_name"] = "Louis",
            ["steps.capture_user_choice.json.date_window"] = "this week",
            ["steps.capture_user_choice.json.party_size"] = "2",
            ["steps.capture_user_choice.json.location"] = "Keong Saik Duxton Singapore",
            ["steps.capture_user_choice.json.preferred_cuisines"] = "[\"Japanese\",\"Italian\"]",
            ["steps.capture_user_choice.json.restaurant_type"] = "romantic dinner",
            ["steps.capture_user_choice.json.phone_number"] = "+6590000000",
            ["steps.capture_user_choice.json.restaurant_phone_number"] = "+6511111111",
            ["steps.capture_user_choice.json.budget_cap"] = "120",
            ["steps.capture_user_choice.json.policy"] = "{\"show_options_before_calls\":true,\"money_spend_allowed\":false,\"reservation_calls_auto_allowed\":true}",
        };
        var evaluator = new WorkflowExpressionEvaluator();

        var initialize = workflow.Steps.Single(step => step.Id == "initialize_context");
        var contextJson = evaluator.Evaluate(initialize.Parameters["value"], variables);
        using var contextDocument = JsonDocument.Parse(contextJson);
        variables["steps.initialize_context.json.search_query"] = contextDocument.RootElement
            .GetProperty("search_query")
            .GetString()!;
        var discovery = workflow.Steps.Single(step => step.Id == "discover_restaurant_candidates");
        var argumentsJson = evaluator.Evaluate(discovery.Parameters["arguments"], variables);

        contextDocument.RootElement.GetProperty("raw_user_request").GetString()
            .Should().Be("Plan a dinner date with Priya this week.");
        contextDocument.RootElement.GetProperty("participant").GetString().Should().Be("Priya");
        contextDocument.RootElement.GetProperty("contact_name").GetString().Should().Be("Louis");
        contextDocument.RootElement.GetProperty("window").GetString().Should().Be("this week");
        contextDocument.RootElement.GetProperty("cuisines").EnumerateArray()
            .Select(element => element.GetString())
            .Should().Equal("Japanese", "Italian");
        contextDocument.RootElement.GetProperty("search_query").GetString()
            .Should().Be("Keong Saik Duxton Singapore restaurant reservation");
        using var argumentsDocument = JsonDocument.Parse(argumentsJson);
        argumentsDocument.RootElement.GetProperty("body").GetProperty("query").GetString()
            .Should().Be("Keong Saik Duxton Singapore restaurant reservation");
    }

    [Fact]
    public void RenderedOptionsShownEvent_ShouldDescribePhoneAndTimeoutFlow()
    {
        var workflow = new WorkflowParser().Parse(File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "workflow-templates",
            "dinner_search_then_call.yaml")));
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["steps.build_shortlist_from_candidates.json.option_1_name"] = "Option One",
            ["steps.build_shortlist_from_candidates.json.option_1_url"] = "https://example.test/one",
            ["steps.build_shortlist_from_candidates.json.option_1_note"] = "first note",
            ["steps.build_shortlist_from_candidates.json.option_2_name"] = "Option Two",
            ["steps.build_shortlist_from_candidates.json.option_2_url"] = "https://example.test/two",
            ["steps.build_shortlist_from_candidates.json.option_2_note"] = "second note",
            ["steps.build_shortlist_from_candidates.json.option_3_name"] = "Option Three",
            ["steps.build_shortlist_from_candidates.json.option_3_url"] = "https://example.test/three",
            ["steps.build_shortlist_from_candidates.json.option_3_note"] = "third note",
        };
        var evaluator = new WorkflowExpressionEvaluator();

        var optionsShown = workflow.Steps.Single(step => step.Id == "emit_options_shown");
        var payloadJson = evaluator.Evaluate(optionsShown.Parameters["payload"], variables);

        using var payloadDocument = JsonDocument.Parse(payloadJson);
        payloadDocument.RootElement.GetProperty("workflow_status").GetString()
            .Should().Be("waiting_for_user_choice");
        payloadDocument.RootElement.GetProperty("phone_flow").GetString().Should()
            .Contain("Timeout starts hold calls for all three shown venues")
            .And.Contain("never searched phone numbers");
        payloadDocument.RootElement.GetProperty("timeout_policy").GetProperty("timeout_ms")
            .GetInt32().Should().Be(10000);
        payloadDocument.RootElement.GetProperty("options").GetProperty("option_1")
            .GetProperty("name").GetString().Should().Be("Option One");
    }

    [Fact]
    public void RenderedCallParameters_ShouldUseConfiguredReservationDetails()
    {
        var workflow = new WorkflowParser().Parse(File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "workflow-templates",
            "dinner_search_then_call.yaml")));
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["steps.initialize_context.json.restaurant_phone_number"] = "+6511111111",
            ["steps.initialize_context.json.participant"] = "Priya",
            ["steps.initialize_context.json.contact_name"] = "Louis",
            ["steps.initialize_context.json.party_size"] = "2",
            ["steps.initialize_context.json.day"] = "Tuesday",
            ["steps.initialize_context.json.window"] = "this week",
            ["steps.initialize_context.json.time"] = "19:30",
            ["steps.initialize_context.json.backup_times"] = "19:45",
            ["steps.initialize_context.json.phone_number"] = "+6590000000",
            ["steps.initialize_context.json.special_requests"] = "quiet table",
            ["steps.build_shortlist_from_candidates.json.option_1_name"] = "Option One",
            ["steps.build_shortlist_from_candidates.json.option_1_url"] = "https://example.test/one",
        };
        var evaluator = new WorkflowExpressionEvaluator();

        var callStep = workflow.Steps.Single(step => step.Id == "hold_selected_option_1");
        var argumentsJson = evaluator.Evaluate(callStep.Parameters["arguments"], variables);

        using var argumentsDocument = JsonDocument.Parse(argumentsJson);
        var body = argumentsDocument.RootElement.GetProperty("body");
        body.GetProperty("to_number").GetString().Should().Be("+6511111111");
        var variablesJson = body.GetProperty("conversation_initiation_client_data")
            .GetProperty("dynamic_variables");
        variablesJson.GetProperty("restaurant_name").GetString().Should().Be("Option One");
        variablesJson.GetProperty("client_name").GetString().Should().Be("Louis");
        variablesJson.GetProperty("reservation_contact_name").GetString().Should().Be("Louis");
        variablesJson.GetProperty("party_size").GetString().Should().Be("2");
        variablesJson.GetProperty("booking_date").GetString().Should().Be("Tuesday");
        variablesJson.GetProperty("preferred_time").GetString().Should().Be("19:30");
        variablesJson.GetProperty("callback_phone").GetString().Should().Be("+6590000000");
        variablesJson.GetProperty("reservation_details").GetString().Should()
            .Contain("Louis")
            .And.NotContain("Priya")
            .And.Contain("Tuesday")
            .And.Contain("19:30")
            .And.Contain("party size 2")
            .And.Contain("quiet table");
    }

    [Fact]
    public void RouteConfiguredContactName_ShouldFailBeforeCallsWhenConfiguredContactNameIsMissing()
    {
        var workflow = new WorkflowParser().Parse(File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "workflow-templates",
            "dinner_search_then_call.yaml")));
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["steps.build_shortlist_from_candidates.json.search_step_success"] = "true",
            ["steps.initialize_context.json.restaurant_phone_number"] = "+6511111111",
            ["steps.initialize_context.json.contact_name"] = string.Empty,
        };
        var evaluator = new WorkflowExpressionEvaluator();

        var routeShortlist = workflow.Steps.Single(step => step.Id == "route_shortlist_status");
        var shortlistBranch = evaluator.Evaluate(routeShortlist.Parameters["branch.true"], variables);
        var phoneRoute = workflow.Steps.Single(step => step.Id == shortlistBranch);
        var contactBranch = evaluator.Evaluate(phoneRoute.Parameters["branch.false"], variables);
        var contactRoute = workflow.Steps.Single(step => step.Id == contactBranch);
        var contactNameMissing = evaluator.Evaluate(contactRoute.Parameters["on"], variables);

        shortlistBranch.Should().Be("route_configured_restaurant_phone");
        contactBranch.Should().Be("route_configured_contact_name");
        contactNameMissing.Should().Be("true");
        contactRoute.Branches.Should().ContainKey("true").WhoseValue.Should()
            .Be("final_artifact_contact_name_missing");
        workflow.Steps.Single(step => step.Id == "final_artifact_contact_name_missing")
            .Parameters["value"].Should().Contain("prompt_participant_not_used_as_contact_name");
    }

    [Fact]
    public void RouteConfiguredRestaurantPhone_ShouldFailBeforeCallsWhenConfiguredPhoneIsMissing()
    {
        var workflow = new WorkflowParser().Parse(File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "workflow-templates",
            "dinner_search_then_call.yaml")));
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["steps.build_shortlist_from_candidates.json.search_step_success"] = "true",
            ["steps.initialize_context.json.restaurant_phone_number"] = string.Empty,
        };
        var evaluator = new WorkflowExpressionEvaluator();

        var routeShortlist = workflow.Steps.Single(step => step.Id == "route_shortlist_status");
        var shortlistBranch = evaluator.Evaluate(routeShortlist.Parameters["branch.true"], variables);
        var phoneRoute = workflow.Steps.Single(step => step.Id == shortlistBranch);
        var phoneMissing = evaluator.Evaluate(phoneRoute.Parameters["on"], variables);

        shortlistBranch.Should().Be("route_configured_restaurant_phone");
        phoneMissing.Should().Be("true");
        phoneRoute.Branches.Should().ContainKey("true").WhoseValue.Should()
            .Be("final_artifact_restaurant_phone_missing");
        workflow.Steps.Single(step => step.Id == "final_artifact_restaurant_phone_missing")
            .Parameters["value"].Should().Contain("searched_phone_numbers_not_used");
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
