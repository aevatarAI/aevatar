using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowDefinitionParserExternalCapabilityTests
{
    [Theory]
    [MemberData(nameof(InvalidNyxIdAuthoringCases))]
    public async Task DefinitionParser_ShouldReturnTypedReadiness_ForInvalidNyxIdAuthoring(
        string workflowYaml,
        ExternalCapabilityReadinessStatus expectedStatus,
        string expectedCode,
        ExternalCapabilityRemediationActionKind expectedRemediation)
    {
        var parser = new WorkflowDefinitionParser([new WorkflowCoreModulePack()]);

        var result = await parser.ParseWorkflowYamlAsync(workflowYaml);

        result.Succeeded.Should().BeFalse();
        result.ExternalCapabilityReadiness.Should().NotBeNull();
        result.ExternalCapabilityReadiness!.Status.Should().Be(expectedStatus);
        result.ExternalCapabilityReadiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be(expectedCode);
        result.ExternalCapabilityReadiness.Remediations.Should().ContainSingle().Which.ActionKind.Should()
            .Be(expectedRemediation);
        result.ExternalCapabilityReadiness.ToString().Should().NotContain("caller-controlled-digest");
    }

    public static TheoryData<string, ExternalCapabilityReadinessStatus, string,
        ExternalCapabilityRemediationActionKind> InvalidNyxIdAuthoringCases =>
        new()
        {
            {
                """
                name: legacy-proof
                steps:
                  - id: call
                    type: tool_call
                    capability:
                      nyxid_operation:
                        user_service_id: us-alpha
                        operation_id: get-resource
                    parameters:
                      tool: nyxid_proxy
                      arguments: '{"contract_digest":"caller-controlled-digest"}'
                """,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED",
                ExternalCapabilityRemediationActionKind.RebindWorkflow
            },
            {
                """
                name: dynamic-selector
                steps:
                  - id: call
                    type: tool_call
                    capability:
                      nyxid_operation:
                        user_service_id: ${input}
                        operation_id: get-resource
                    parameters:
                      tool: nyxid_proxy
                      arguments: '{"query":{}}'
                """,
                ExternalCapabilityReadinessStatus.OperationSelectionRequired,
                "NYXID_OPERATION_SELECTION_REQUIRED",
                ExternalCapabilityRemediationActionKind.SelectOperation
            },
            {
                """
                name: invalid-arguments
                steps:
                  - id: call
                    type: tool_call
                    capability:
                      nyxid_operation:
                        user_service_id: us-alpha
                        operation_id: get-resource
                    parameters:
                      tool: nyxid_proxy
                      arguments: '{"unsupported_slot":{}}'
                """,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "NYXID_OPERATION_ARGUMENT_INVALID",
                ExternalCapabilityRemediationActionKind.RebindWorkflow
            },
        };
}
