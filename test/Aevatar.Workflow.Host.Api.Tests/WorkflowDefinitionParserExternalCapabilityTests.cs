using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
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

    [Fact]
    public async Task DefinitionParser_ShouldParseInlineWorkflowBundle_WhenDocumentsAreDistinct()
    {
        const string entryYaml = "name: main\nroles:\n  - id: assistant\n    name: Assistant\nsteps:\n  - id: reply\n    type: llm_call\n    target_role: assistant";
        const string childYaml = "name: child\nroles:\n  - id: assistant\n    name: Assistant\nsteps:\n  - id: reply\n    type: llm_call\n    target_role: assistant";
        var parser = new WorkflowDefinitionParser([new WorkflowCoreModulePack()]);

        var result = await parser.ParseInlineWorkflowBundleAsync(
            [
                new WorkflowChatInlineYamlDocument(string.Empty, entryYaml),
                new WorkflowChatInlineYamlDocument(string.Empty, childYaml),
            ]);

        result.Succeeded.Should().BeTrue();
        result.EntryWorkflowName.Should().Be("main");
        result.EntryWorkflowYaml.Should().Be(entryYaml);
        result.WorkflowYamlsByName.Should().ContainKey("main");
        result.WorkflowYamlsByName.Should().ContainKey("child");
    }

    [Fact]
    public async Task DefinitionParser_ShouldRejectInlineWorkflowBundle_WhenNamesAreDuplicated()
    {
        var parser = new WorkflowDefinitionParser([new WorkflowCoreModulePack()]);

        var result = await parser.ParseInlineWorkflowBundleAsync(
            [
                new WorkflowChatInlineYamlDocument(string.Empty, "name: main\nroles:\n  - id: assistant\n    name: Assistant\nsteps:\n  - id: reply\n    type: llm_call\n    target_role: assistant"),
                new WorkflowChatInlineYamlDocument(string.Empty, "name: main\nroles:\n  - id: assistant\n    name: Assistant\nsteps:\n  - id: reply\n    type: llm_call\n    target_role: assistant"),
            ]);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("Duplicate workflow name 'main' in workflowYamls.");
    }

    [Fact]
    public async Task DefinitionParser_ShouldRejectInlineWorkflowBundle_WhenDocumentNameMismatchesYamlName()
    {
        var parser = new WorkflowDefinitionParser([new WorkflowCoreModulePack()]);

        var result = await parser.ParseInlineWorkflowBundleAsync(
            [new WorkflowChatInlineYamlDocument("requested", "name: actual\nroles:\n  - id: assistant\n    name: Assistant\nsteps:\n  - id: reply\n    type: llm_call\n    target_role: assistant")]);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("workflowYamls[0] document name 'requested' does not match workflow name 'actual'.");
    }

    [Fact]
    public async Task DefinitionParser_ShouldPreserveReadiness_WhenInlineWorkflowBundleCapabilityIsInvalid()
    {
        const string workflowYaml =
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
            """;
        var parser = new WorkflowDefinitionParser([new WorkflowCoreModulePack()]);

        var result = await parser.ParseInlineWorkflowBundleAsync(
            [new WorkflowChatInlineYamlDocument(string.Empty, workflowYaml)]);

        result.Succeeded.Should().BeFalse();
        result.ExternalCapabilityReadiness.Should().NotBeNull();
        result.ExternalCapabilityReadiness!.Status.Should().Be(ExternalCapabilityReadinessStatus.ContractDrift);
        result.ExternalCapabilityReadiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED");
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
