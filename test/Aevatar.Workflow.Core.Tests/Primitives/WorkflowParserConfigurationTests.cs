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
                model: gpt-4o-mini
                temperature: 0.1
                max_tokens: 512
                max_tool_rounds: 3
                max_history_messages: 50
                stream_buffer_capacity: 128
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
        role.Model.Should().Be("gpt-4o-mini");
        role.SystemPrompt.Should().Be("You are planner");
        role.Temperature.Should().Be(0.1);
        role.MaxTokens.Should().Be(512);
        role.MaxToolRounds.Should().Be(3);
        role.MaxHistoryMessages.Should().Be(50);
        role.StreamBufferCapacity.Should().Be(128);
        role.EventModules.Should().Be("llm_handler,tool_handler");
        role.EventRoutes.Should().Contain("event.type");
        role.Connectors.Should().BeEquivalentTo(["conn_a", "conn_b"]);
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
                system_prompt: "Decide whether input is yaml."
              - id: assistant
                system_prompt: "Refine yaml content."
            steps:
              - id: capture_input
                type: assign
                parameters:
                  target: raw_input
                  value: "$input"
                next: classify
              - id: classify
                type: llm_call
                role: planner
                next: check_is_yaml
              - id: check_is_yaml
                type: conditional
                parameters:
                  condition: "name:"
                branches:
                  "true": show_for_approval
                  "false": done
              - id: show_for_approval
                type: human_approval
                parameters:
                  prompt: "Approve YAML for execution"
                next: refine_yaml
              - id: refine_yaml
                type: llm_call
                role: assistant
                next: extract_and_execute
              - id: extract_and_execute
                type: dynamic_workflow
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

        workflow.Steps.Should().HaveCount(7);
        workflow.Steps.Should().Contain(s => s.Id == "capture_input" && s.Type == "assign");
        workflow.Steps.Should().Contain(s => s.Id == "classify" && s.Type == "llm_call");
        workflow.Steps.Should().Contain(s => s.Id == "check_is_yaml" && s.Type == "conditional");
        workflow.Steps.Should().Contain(s => s.Id == "show_for_approval" && s.Type == "human_approval");
        workflow.Steps.Should().Contain(s => s.Id == "refine_yaml" && s.Type == "llm_call");
        workflow.Steps.Should().Contain(s => s.Id == "extract_and_execute" && s.Type == "dynamic_workflow");
    }
}
