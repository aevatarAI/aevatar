using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowParserSagaAuthoringTests
{
    [Fact]
    public void Parse_WhenSagaAuthoringFieldsArePresent_ShouldBindTypedFieldsOnly()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: saga_authoring
            roles: []
            steps:
              - id: create_order
                type: tool_call
                compensation: " cancel_order "
                idempotency_key: " ${run_id}:${step_id} "
                parameters:
                  tool: orders.create
              - id: cancel_order
                type: tool_call
                parameters:
                  tool: orders.cancel
            """);

        var step = workflow.Steps[0];

        step.Compensation.Should().Be("cancel_order");
        step.IdempotencyKey.Should().Be("${run_id}:${step_id}");
        step.Parameters.Should().Contain("tool", "orders.create");
        step.Parameters.Should().NotContainKey("compensation");
        step.Parameters.Should().NotContainKey("idempotency_key");
    }

    [Fact]
    public void Parse_WhenSagaAuthoringFieldsAreAbsent_ShouldLeaveTypedFieldsNullAndShapeUnchanged()
    {
        var workflow = new WorkflowParser().Parse(NoSagaAuthoringYaml);

        var step = workflow.Steps[0];

        step.Next.Should().Be("step_2");
        step.Compensation.Should().BeNull();
        step.IdempotencyKey.Should().BeNull();
        step.Parameters.Should().BeEquivalentTo(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["op"] = "uppercase",
        });
        step.Parameters.Should().NotContainKey("compensation");
        step.Parameters.Should().NotContainKey("idempotency_key");
    }

    [Fact]
    public void Compile_WhenSagaAuthoringFieldsAreAbsent_ShouldPreserveDefinitionSnapshotBytes()
    {
        var workflow = new WorkflowParser().Parse(NoSagaAuthoringYaml);
        WorkflowValidator.Validate(workflow).Should().BeEmpty();
        var actualSnapshotBytes = BuildDefinitionSnapshot(NoSagaAuthoringYaml).ToByteArray();

        actualSnapshotBytes.Should().Equal(Convert.FromBase64String(NoSagaAuthoringSnapshotBase64));
    }

    private const string NoSagaAuthoringYaml =
        """
        name: no_saga_authoring
        roles: []
        steps:
          - id: step_1
            type: transform
            next: step_2
            parameters:
              op: uppercase
          - id: step_2
            type: assign
            parameters:
              target: result
              value: done
        """;

    private const string NoSagaAuthoringSnapshotBase64 =
        "CgxkZWZpbml0aW9uLTESEW5vX3NhZ2FfYXV0aG9yaW5nGtcBbmFtZTogbm9fc2FnYV9hdXRob3JpbmcKcm9sZXM6IFtdCnN0ZXBzOgogIC0gaWQ6IHN0ZXBfMQogICAgdHlwZTogdHJhbnNmb3JtCiAgICBuZXh0OiBzdGVwXzIKICAgIHBhcmFtZXRlcnM6CiAgICAgIG9wOiB1cHBlcmNhc2UKICAtIGlkOiBzdGVwXzIKICAgIHR5cGU6IGFzc2lnbgogICAgcGFyYW1ldGVyczoKICAgICAgdGFyZ2V0OiByZXN1bHQKICAgICAgdmFsdWU6IGRvbmUqB3Njb3BlLTEwAQ==";

    private static WorkflowDefinitionSnapshot BuildDefinitionSnapshot(string workflowYaml) =>
        new()
        {
            DefinitionActorId = "definition-1",
            WorkflowName = "no_saga_authoring",
            WorkflowYaml = workflowYaml,
            ScopeId = "scope-1",
            DefinitionVersion = 1,
        };
}
