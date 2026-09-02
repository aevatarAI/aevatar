using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public class WorkflowParserConfigurationTests
{
    [Fact]
    public void Parse_WhenClosedWorldModeEnabled_ShouldBindConfiguration()
    {
        var yaml = """
            name: closed_world
            configuration:
              closed_world_mode: true
            roles: []
            steps:
              - id: s1
                type: assign
                parameters:
                  target: x
                  value: "1"
            """;

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Configuration.ClosedWorldMode.Should().BeTrue();
    }

    [Fact]
    public void Parse_WhenConfigurationMissing_ShouldUseDefaultClosedWorldModeFalse()
    {
        var yaml = """
            name: open_world
            roles: []
            steps:
              - id: s1
                type: assign
                parameters:
                  target: x
                  value: "1"
            """;

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Configuration.ClosedWorldMode.Should().BeFalse();
    }

    [Fact]
    public void Parse_WhenRoleUsesExtensions_ShouldBindRoleRuntimeFields()
    {
        var yaml = """
            name: role_extensions
            roles:
              - id: planner
                system_prompt: "You are planner"
                provider: openai
                model: gpt-5.4
                temperature: 0.1
                max_tokens: 512
                max_tool_rounds: 3
                max_history_messages: 50
                connectors: [conn_a, conn_b]
                extensions:
                  event_modules: "llm_handler,tool_handler"
                  event_routes: |
                    event.type == ChatRequestEvent -> llm_handler
            steps:
              - id: s1
                type: assign
                parameters:
                  target: x
                  value: "1"
            """;

        var workflow = new WorkflowParser().Parse(yaml);
        var role = workflow.Roles.Should().ContainSingle().Subject;

        role.Id.Should().Be("planner");
        role.Name.Should().Be("planner");
        role.Provider.Should().Be("openai");
        role.Model.Should().Be("gpt-5.4");
        role.SystemPrompt.Should().Be("You are planner");
        role.Temperature.Should().Be(0.1);
        role.MaxTokens.Should().Be(512);
        role.MaxToolRounds.Should().Be(3);
        role.MaxHistoryMessages.Should().Be(50);
        role.EventModules.Should().Be("llm_handler,tool_handler");
        role.EventRoutes.Should().Contain("event.type");
        role.Connectors.Should().BeEquivalentTo(["conn_a", "conn_b"]);
    }

    [Fact]
    public void Parse_WhenRoleAndStepDeclareAllowedTools_ShouldBindTypedAgentToolScopes()
    {
        var yaml = """
            name: tool_scope
            roles:
              - id: planner
                allowed_tools: [search, calendar]
                tool_sets: [nyxid.connected_services, nyxid.connected_services]
            steps:
              - id: scoped
                type: llm_call
                target_role: planner
                allowed_tools: [calendar]
                tool_sets: [nyxid.connected_services]
                parameters:
                  allowed_tools: [search]
                  tool_sets: [ignored.parameter.set]
                  prompt_prefix: "Use scoped tool"
              - id: no_tools
                type: llm_call
                target_role: planner
                allowed_tools: []
            """;

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Roles.Should().ContainSingle().Subject.AgentToolScope.Should().NotBeNull();
        workflow.Roles[0].AgentToolScope!.AllowedToolNames.Should().Equal("search", "calendar");
        workflow.Roles[0].AgentToolScope!.ToolSetRefs.Should().Equal("nyxid.connected_services");
        workflow.Roles[0].AgentToolScope!.RestrictAllowedToolNames.Should().BeTrue();
        workflow.Roles[0].AgentToolScope!.RestrictToolSets.Should().BeTrue();
        workflow.Roles[0].AgentToolScope!.AllowedToolNames.Should().NotContain("nyxid.connected_services");
        workflow.Steps[0].AgentToolScope.Should().NotBeNull();
        workflow.Steps[0].AgentToolScope!.AllowedToolNames.Should().Equal("calendar");
        workflow.Steps[0].AgentToolScope!.ToolSetRefs.Should().Equal("nyxid.connected_services");
        workflow.Steps[0].AgentToolScope!.RestrictAllowedToolNames.Should().BeTrue();
        workflow.Steps[0].AgentToolScope!.RestrictToolSets.Should().BeTrue();
        workflow.Steps[0].Parameters.Should().NotContainKey("allowed_tools");
        workflow.Steps[0].Parameters.Should().NotContainKey("tool_sets");
        workflow.Steps[1].AgentToolScope.Should().NotBeNull();
        workflow.Steps[1].AgentToolScope!.AllowedToolNames.Should().BeEmpty();
        workflow.Steps[1].AgentToolScope!.RestrictAllowedToolNames.Should().BeTrue();
        workflow.Steps[1].AgentToolScope!.RestrictToolSets.Should().BeFalse();
    }

    [Fact]
    public void Parse_WhenRoleDefinesTopLevelAndExtensionsEventFields_ShouldPreferTopLevel()
    {
        var yaml = """
            name: role_override
            roles:
              - id: reviewer
                event_modules: "top_a,top_b"
                event_routes: "event.type == ChatRequestEvent -> top_a"
                extensions:
                  event_modules: "ext_a,ext_b"
                  event_routes: "event.type == ChatRequestEvent -> ext_a"
            steps:
              - id: s1
                type: assign
                parameters:
                  target: x
                  value: "1"
            """;

        var workflow = new WorkflowParser().Parse(yaml);
        var role = workflow.Roles.Should().ContainSingle().Subject;

        role.EventModules.Should().Be("top_a,top_b");
        role.EventRoutes.Should().Be("event.type == ChatRequestEvent -> top_a");
    }

    [Fact]
    public void Parse_WhenStepTypesUseAliases_ShouldCanonicalizeStepAndStepTypeParameters()
    {
        var yaml = """
            name: alias_normalization
            roles: []
            steps:
              - id: s1
                type: loop
                parameters:
                  step: judge
                  max_iterations: "2"
              - id: s2
                type: cache
                parameters:
                  child_step_type: parallel_fanout
              - id: s3
                type: vote_consensus
            """;

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Steps[0].Type.Should().Be("while");
        workflow.Steps[0].Parameters["step"].Should().Be("evaluate");
        workflow.Steps[1].Type.Should().Be("cache");
        workflow.Steps[1].Parameters["child_step_type"].Should().Be("parallel");
        workflow.Steps[2].Type.Should().Be("vote");
    }

    [Fact]
    public void Parse_WhenLeaseRootParametersProvided_ShouldLiftToParameters()
    {
        var yaml = """
            name: lease_root_parameters
            roles: []
            steps:
              - id: acquire
                type: mutex
                action: acquire
                key: Shared/Resource
                on_conflict: wait
                ttl_ms: 60000
                wait_timeout_ms: 120000
                holder_token_variable: lease_token
            """;

        var workflow = new WorkflowParser().Parse(yaml);
        var step = workflow.Steps.Should().ContainSingle().Subject;

        step.Type.Should().Be("lease");
        step.Parameters["action"].Should().Be("acquire");
        step.Parameters["key"].Should().Be("Shared/Resource");
        step.Parameters["on_conflict"].Should().Be("wait");
        step.Parameters["ttl_ms"].Should().Be("60000");
        step.Parameters["wait_timeout_ms"].Should().Be("120000");
        step.Parameters["holder_token_variable"].Should().Be("lease_token");
    }

    [Fact]
    public void Parse_WhenUsingErgonomicAliases_ShouldCanonicalizeAndApplyDefaults()
    {
        var yaml = """
            name: ergonomic_aliases
            roles: []
            steps:
              - id: s_http_get
                type: http_get
                parameters:
                  connector: demo_http
              - id: s_http_post
                type: http_post
                parameters:
                  connector: demo_http
              - id: s_http_put
                type: http_put
                parameters:
                  connector: demo_http
              - id: s_http_delete
                type: http_delete
                parameters:
                  connector: demo_http
              - id: s_cli
                type: cli_call
                parameters:
                  connector: demo_cli
              - id: s_mcp
                type: mcp_call
                parameters:
                  connector: demo_mcp
                  tool: list_tools
              - id: s_foreach
                type: foreach_llm
                parameters:
                  delimiter: "\\n---\\n"
              - id: s_map_reduce
                type: map_reduce_llm
                parameters:
                  delimiter: "\\n---\\n"
            """;

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Steps.First(s => s.Id == "s_http_get").Type.Should().Be("connector_call");
        workflow.Steps.First(s => s.Id == "s_http_get").Parameters["method"].Should().Be("GET");
        workflow.Steps.First(s => s.Id == "s_http_post").Type.Should().Be("connector_call");
        workflow.Steps.First(s => s.Id == "s_http_post").Parameters["method"].Should().Be("POST");
        workflow.Steps.First(s => s.Id == "s_http_put").Type.Should().Be("connector_call");
        workflow.Steps.First(s => s.Id == "s_http_put").Parameters["method"].Should().Be("PUT");
        workflow.Steps.First(s => s.Id == "s_http_delete").Type.Should().Be("connector_call");
        workflow.Steps.First(s => s.Id == "s_http_delete").Parameters["method"].Should().Be("DELETE");

        workflow.Steps.First(s => s.Id == "s_cli").Type.Should().Be("connector_call");

        var mcp = workflow.Steps.First(s => s.Id == "s_mcp");
        mcp.Type.Should().Be("connector_call");
        mcp.Parameters["operation"].Should().Be("list_tools");

        var forEach = workflow.Steps.First(s => s.Id == "s_foreach");
        forEach.Type.Should().Be("foreach");
        forEach.Parameters["sub_step_type"].Should().Be("llm_call");

        var mapReduce = workflow.Steps.First(s => s.Id == "s_map_reduce");
        mapReduce.Type.Should().Be("map_reduce");
        mapReduce.Parameters["map_step_type"].Should().Be("llm_call");
        mapReduce.Parameters["reduce_step_type"].Should().Be("llm_call");
    }

    [Fact]
    public void Parse_WhenUsingSecureConnectorAlias_ShouldCanonicalizeToSecureConnectorCall()
    {
        var yaml = """
            name: secure_connector_alias
            roles: []
            steps:
              - id: s_secure_connector
                type: secure_connector
                parameters:
                  connector: demo_secure
            """;

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Steps.Should().ContainSingle();
        workflow.Steps[0].Type.Should().Be("secure_connector_call");
        workflow.Steps[0].Parameters["connector"].Should().Be("demo_secure");
    }

    [Fact]
    public void Parse_WhenParametersContainNestedObjects_ShouldSerializeToJsonString()
    {
        var yaml = """
            name: nested_parameters
            roles: []
            steps:
              - id: s1
                type: transform
                parameters:
                  op: trim
                  input:
                    original_sentence: "{{steps.generate.output}}"
                    story: "{{steps.story.output}}"
              - id: done
                type: assign
                parameters:
                  target: result
                  value: "$input"
            """;

        var workflow = new WorkflowParser().Parse(yaml);
        var step = workflow.Steps.First(s => s.Id == "s1");

        step.Parameters["op"].Should().Be("trim");
        step.Parameters["input"].Should().Contain("\"original_sentence\"");
        step.Parameters["input"].Should().Contain("\"story\"");
    }

    [Fact]
    public void Parse_WhenTransformOperationParametersProvided_ShouldLiftTypedSpecAndPreserveMap()
    {
        var yaml = """
            name: transform_operation_lift
            roles: []
            steps:
              - id: sum_amounts
                type: transform
                op: group_by
                group_by: department
                value_field: amount
                aggregate: avg
                precision: 2
            """;

        var workflow = new WorkflowParser().Parse(yaml);
        var step = workflow.Steps.Should().ContainSingle().Subject;

        step.Parameters["op"].Should().Be("group_by");
        step.Parameters["group_by"].Should().Be("department");
        step.Parameters["value_field"].Should().Be("amount");
        step.Parameters["aggregate"].Should().Be("avg");
        step.Parameters["precision"].Should().Be("2");
        step.TransformOperation.Should().NotBeNull();
        step.TransformOperation!.Kind.Should().Be(TransformOperationKind.GroupBy);
        step.TransformOperation.Key.Should().Be("department");
        step.TransformOperation.Value.Should().Be("amount");
        step.TransformOperation.Aggregate.Should().Be(TransformAggregateKind.Avg);
        step.TransformOperation.Precision.Should().Be(2);
    }

    [Fact]
    public void Parse_WhenBranchesProvidedAsList_ShouldNormalizeToDictionary()
    {
        var yaml = """
            name: list_branches
            roles: []
            steps:
              - id: gate
                type: conditional
                parameters:
                  condition: "ready"
                branches:
                  - condition: "true"
                    next: done
                  - condition: "false"
                    next: retry
              - id: retry
                type: assign
                parameters:
                  target: result
                  value: "retrying"
              - id: done
                type: assign
                parameters:
                  target: result
                  value: "ok"
            """;

        var workflow = new WorkflowParser().Parse(yaml);
        var gate = workflow.Steps.First(s => s.Id == "gate");

        gate.Branches.Should().NotBeNull();
        gate.Branches!.Should().ContainKey("true").WhoseValue.Should().Be("done");
        gate.Branches.Should().ContainKey("false").WhoseValue.Should().Be("retry");
    }

    [Fact]
    public void Parse_WhenParallelParametersProvidedAtStepRoot_ShouldMapToParameters()
    {
        var yaml = """
            name: root_parallel_parameters
            roles: []
            steps:
              - id: fanout
                type: parallel
                workers:
                  - worker_a
                  - worker_b
                  - worker_c
                parallel_count: 3
                vote_step_type: vote_consensus
              - id: done
                type: assign
                parameters:
                  target: result
                  value: ok
            """;

        var workflow = new WorkflowParser().Parse(yaml);
        var fanout = workflow.Steps.First(s => s.Id == "fanout");

        fanout.Parameters.Should().ContainKey("workers");
        fanout.Parameters["workers"].Should().Contain("worker_a");
        fanout.Parameters.Should().ContainKey("parallel_count").WhoseValue.Should().Be("3");
        fanout.Parameters.Should().ContainKey("vote_step_type").WhoseValue.Should().Be("vote");
    }

    [Fact]
    public void Parse_WhenVoteAgreementRuleFieldsAtRoot_ShouldLiftToParameters()
    {
        var yaml = """
            name: vote_rule_fields
            roles: []
            steps:
              - id: consensus
                type: vote_consensus
                rule_mode: label_count_constraints
                label_source: annotation
                label_field: vote
                min_approve_count: 2
                max_reject_count: 0
                on_agreed: accepted
                on_rejected: rejected
                winner_policy: first_success
              - id: accepted
                type: assign
                parameters:
                  target: result
                  value: accepted
              - id: rejected
                type: assign
                parameters:
                  target: result
                  value: rejected
            """;

        var workflow = new WorkflowParser().Parse(yaml);
        var vote = workflow.Steps.First(s => s.Id == "consensus");

        vote.Type.Should().Be("vote");
        vote.Parameters["rule_mode"].Should().Be("label_count_constraints");
        vote.Parameters["label_source"].Should().Be("annotation");
        vote.Parameters["label_field"].Should().Be("vote");
        vote.Parameters["min_approve_count"].Should().Be("2");
        vote.Parameters["max_reject_count"].Should().Be("0");
        vote.Parameters["on_agreed"].Should().Be("accepted");
        vote.Parameters["on_rejected"].Should().Be("rejected");
        vote.Parameters["winner_policy"].Should().Be("first_success");
    }

    [Fact]
    public void Parse_WhenParallelVoteRuleFieldsAtRoot_ShouldLiftAsVoteParamFields()
    {
        var yaml = """
            name: parallel_vote_rule_fields
            roles: []
            steps:
              - id: fanout
                type: parallel
                workers: [a, b]
                vote_step_type: vote_consensus
                rule_mode: quorum
                quorum_count: 2
                on_agreed: accepted
              - id: accepted
                type: assign
                parameters:
                  target: result
                  value: accepted
            """;

        var workflow = new WorkflowParser().Parse(yaml);
        var fanout = workflow.Steps.First(s => s.Id == "fanout");

        fanout.Type.Should().Be("parallel");
        fanout.Parameters["vote_step_type"].Should().Be("vote");
        fanout.Parameters["vote_param_rule_mode"].Should().Be("quorum");
        fanout.Parameters["vote_param_quorum_count"].Should().Be("2");
        fanout.Parameters["vote_param_on_agreed"].Should().Be("accepted");
        fanout.Parameters.Should().NotContainKey("rule_mode");
    }

    [Fact]
    public void PrimitiveCatalog_ShouldNotExposeStructuredAgreementAlias()
    {
        WorkflowPrimitiveCatalog.ToCanonicalType("vote_consensus").Should().Be("vote");
        WorkflowPrimitiveCatalog.ToCanonicalType("structured_agreement").Should().Be("structured_agreement");
        WorkflowPrimitiveCatalog.BuiltInCanonicalTypes.Should().Contain("vote");
        WorkflowPrimitiveCatalog.BuiltInCanonicalTypes.Should().NotContain("structured_agreement");
        WorkflowPrimitiveCatalog.BuiltInCanonicalTypes.Should().NotContain("agreement");
    }

    [Fact]
    public void Parse_WhenCommonPrimitiveFieldsAtRoot_ShouldLiftToParameters()
    {
        var yaml = """
            name: root_primitive_fields
            roles: []
            steps:
              - id: wait_root
                type: wait_signal
                signal_name: "approval"
                timeout_ms: 2500
                prompt: "wait here"
              - id: foreach_root
                type: foreach
                delimiter: "\\n---\\n"
                sub_step_type: llm_call
                sub_target_role: helper
              - id: call_root
                type: workflow_call
                workflow: demo_flow
                lifecycle: scope
              - id: done
                type: assign
                parameters:
                  target: result
                  value: ok
            """;

        var workflow = new WorkflowParser().Parse(yaml);

        var wait = workflow.Steps.First(s => s.Id == "wait_root");
        wait.Parameters["signal_name"].Should().Be("approval");
        wait.Parameters["timeout_ms"].Should().Be("2500");
        wait.Parameters["prompt"].Should().Be("wait here");
        wait.TimeoutMs.Should().Be(2500);

        var forEach = workflow.Steps.First(s => s.Id == "foreach_root");
        forEach.Parameters["delimiter"].Should().Be("\\n---\\n");
        forEach.Parameters["sub_step_type"].Should().Be("llm_call");
        forEach.Parameters["sub_target_role"].Should().Be("helper");

        var call = workflow.Steps.First(s => s.Id == "call_root");
        call.Parameters["workflow"].Should().Be("demo_flow");
        call.Parameters["lifecycle"].Should().Be("scope");
    }

    [Fact]
    public void Parse_AutoPatternYaml_ShouldProduceValidWorkflowDefinition()
    {
        var yaml = """
            name: auto_pattern
            roles:
              - id: planner
                system_prompt: "Route request and draft workflow yaml when needed."
              - id: assistant
                system_prompt: "Answer direct questions clearly."
            steps:
              - id: capture_input
                type: assign
                parameters:
                  target: user_request
                  value: "$input"
                next: classify_route
              - id: classify_route
                type: llm_call
                role: planner
                parameters:
                  prompt_prefix: "Return one token: direct or workflow."
                next: route_intent
              - id: route_intent
                type: switch
                branches:
                  workflow: prepare_workflow_request
                  direct: prepare_direct_response
                  _default: prepare_direct_response
              - id: prepare_workflow_request
                type: assign
                parameters:
                  target: user_request
                  value: "${user_request}"
                next: generate_workflow_yaml
              - id: generate_workflow_yaml
                type: llm_call
                role: planner
                next: validate_yaml
              - id: validate_yaml
                type: workflow_yaml_validate
                on_error:
                  strategy: fallback
                  fallback_step: refine_yaml
                next: show_for_approval
              - id: show_for_approval
                type: human_approval
                parameters:
                  prompt: "Approve YAML for execution"
                branches:
                  "true": extract_and_execute
                  "false": refine_yaml
              - id: refine_yaml
                type: llm_call
                role: assistant
                next: validate_yaml
              - id: extract_and_execute
                type: dynamic_workflow
                next: done
              - id: prepare_direct_response
                type: assign
                parameters:
                  target: user_request
                  value: "${user_request}"
                next: reply_direct
              - id: reply_direct
                type: llm_call
                role: assistant
                next: done
              - id: done
                type: assign
                parameters:
                  target: result
                  value: "$input"
            """;
        var parser = new WorkflowParser();

        var workflow = parser.Parse(yaml);

        workflow.Name.Should().Be("auto_pattern");
        workflow.Roles.Should().HaveCount(2);
        workflow.Roles.Should().Contain(r => r.Id == "planner");
        workflow.Roles.Should().Contain(r => r.Id == "assistant");

        workflow.Steps.Should().HaveCount(12);
        workflow.Steps.Should().Contain(s => s.Id == "capture_input" && s.Type == "assign");
        workflow.Steps.Should().Contain(s => s.Id == "classify_route" && s.Type == "llm_call");
        workflow.Steps.Should().Contain(s => s.Id == "route_intent" && s.Type == "switch");
        workflow.Steps.Should().Contain(s => s.Id == "prepare_workflow_request" && s.Type == "assign");
        workflow.Steps.Should().Contain(s => s.Id == "generate_workflow_yaml" && s.Type == "llm_call");
        workflow.Steps.Should().Contain(s => s.Id == "validate_yaml" && s.Type == "workflow_yaml_validate");
        workflow.Steps.Should().Contain(s => s.Id == "show_for_approval" && s.Type == "human_approval");
        workflow.Steps.Should().Contain(s => s.Id == "refine_yaml" && s.Type == "llm_call");
        workflow.Steps.Should().Contain(s => s.Id == "extract_and_execute" && s.Type == "dynamic_workflow");
        workflow.Steps.Should().Contain(s => s.Id == "prepare_direct_response" && s.Type == "assign");
        workflow.Steps.Should().Contain(s => s.Id == "reply_direct" && s.Type == "llm_call");
    }

    [Fact]
    public void Parse_OnErrorFallbackAlias_ShouldMapToFallbackStep()
    {
        var yaml = """
            name: fallback_alias
            steps:
              - id: validate_yaml
                type: workflow_yaml_validate
                on_error:
                  strategy: fallback
                  fallback: refine_yaml
                next: done
              - id: refine_yaml
                type: assign
                parameters:
                  target: output
                  value: "$input"
              - id: done
                type: assign
                parameters:
                  target: output
                  value: "$input"
            """;
        var parser = new WorkflowParser();

        var workflow = parser.Parse(yaml);
        var validateStep = workflow.Steps.Single(step => step.Id == "validate_yaml");

        validateStep.OnError.Should().NotBeNull();
        validateStep.OnError!.Strategy.Should().Be("fallback");
        validateStep.OnError.FallbackStep.Should().Be("refine_yaml");
    }
}
