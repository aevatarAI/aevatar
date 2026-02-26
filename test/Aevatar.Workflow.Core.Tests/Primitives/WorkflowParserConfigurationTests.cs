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
}
