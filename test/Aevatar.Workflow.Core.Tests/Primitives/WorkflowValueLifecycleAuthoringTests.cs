using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowValueLifecycleAuthoringTests
{
    [Fact]
    public void Parse_ShouldBindTypedValueLifecycleOutsideParameterBag()
    {
        var workflow = Parse(
            """
            name: release_values
            roles: []
            steps:
              - id: reduce
                type: assign
                value_lifecycle:
                  release_variables_after_success:
                    - raw_pages
                parameters:
                  target: result
                  value: reduced
            """);

        var step = workflow.Steps.Should().ContainSingle().Subject;
        step.ValueLifecycle.Should().NotBeNull();
        step.ValueLifecycle!.ReleaseVariablesAfterSuccess.Should().Equal("raw_pages");
        step.Parameters.Should().NotContainKey("value_lifecycle");
        WorkflowValidator.Validate(workflow).Should().BeEmpty();
    }

    [Theory]
    [InlineData("[]", "不能为空")]
    [InlineData("[raw_pages, raw_pages]", "重复释放变量")]
    [InlineData("['']", "空变量名")]
    [InlineData("[input]", "保留名称")]
    [InlineData("[reduce]", "保留名称")]
    [InlineData("[steps.producer.output]", "保留名称")]
    [InlineData("[workflow.usage.total_tokens]", "保留名称")]
    [InlineData("[workflow_call.invocation_id]", "保留名称")]
    public void Validate_ShouldRejectInvalidReleaseTargets(string values, string expectedError)
    {
        var workflow = Parse(
            $$"""
            name: invalid_release
            roles: []
            steps:
              - id: reduce
                type: assign
                value_lifecycle:
                  release_variables_after_success: {{values}}
                parameters:
                  target: result
                  value: reduced
            """);

        WorkflowValidator.Validate(workflow).Should().Contain(error =>
            error.Contains(expectedError, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldRejectNestedValueLifecycle()
    {
        var workflow = Parse(
            """
            name: nested_release
            roles: []
            steps:
              - id: parent
                type: sequence
                children:
                  - id: child
                    type: assign
                    value_lifecycle:
                      release_variables_after_success: [raw_pages]
                    parameters:
                      target: result
                      value: reduced
            """);

        WorkflowValidator.Validate(workflow).Should().Contain(error =>
            error.Contains("只允许声明在顶层步骤", StringComparison.Ordinal));
    }

    private static WorkflowDefinition Parse(string yaml) => new WorkflowParser().Parse(yaml);
}
