using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public class WorkflowLlmRuntimePolicyTests
{
    [Fact]
    public void RequiresLlmProvider_WhenLlmCallTargetsRoleWithEventModule_ShouldReturnFalse()
    {
        var yaml = """
            name: control_plane_report
            roles:
              - id: reporter
                name: Reporter
                event_modules: "report_module"
            steps:
              - id: report
                type: llm_call
                role: reporter
            """;

        var definition = new WorkflowParser().Parse(yaml);

        WorkflowLlmRuntimePolicy.RequiresLlmProvider(definition).Should().BeFalse();
    }

    [Fact]
    public void RequiresLlmProvider_WhenLlmCallHasNoEventModuleOverride_ShouldReturnTrue()
    {
        var yaml = """
            name: direct_llm
            roles:
              - id: writer
                name: Writer
                system_prompt: "Write a short answer."
            steps:
              - id: reply
                type: llm_call
                role: writer
            """;

        var definition = new WorkflowParser().Parse(yaml);

        WorkflowLlmRuntimePolicy.RequiresLlmProvider(definition).Should().BeTrue();
    }
}
