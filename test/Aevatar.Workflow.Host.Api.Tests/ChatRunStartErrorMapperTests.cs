using System.Text.Json;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Host.Api.Tests;

public class ChatRunStartErrorMapperTests
{
    [Theory]
    [InlineData(WorkflowChatRunStartError.AgentNotFound, StatusCodes.Status404NotFound)]
    [InlineData(WorkflowChatRunStartError.WorkflowNotFound, StatusCodes.Status404NotFound)]
    [InlineData(WorkflowChatRunStartError.AgentTypeNotSupported, StatusCodes.Status400BadRequest)]
    [InlineData(WorkflowChatRunStartError.ProjectionDisabled, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(WorkflowChatRunStartError.ProjectionUnavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(WorkflowChatRunStartError.WorkflowBindingMismatch, StatusCodes.Status409Conflict)]
    [InlineData(WorkflowChatRunStartError.AgentWorkflowNotConfigured, StatusCodes.Status409Conflict)]
    [InlineData(WorkflowChatRunStartError.InvalidWorkflowYaml, StatusCodes.Status400BadRequest)]
    [InlineData(WorkflowChatRunStartError.WorkflowNameMismatch, StatusCodes.Status400BadRequest)]
    [InlineData(WorkflowChatRunStartError.InvalidCallerCredential, StatusCodes.Status400BadRequest)]
    [InlineData(WorkflowChatRunStartError.InvalidConversationInput, StatusCodes.Status400BadRequest)]
    [InlineData(WorkflowChatRunStartError.InvalidConversationId, StatusCodes.Status400BadRequest)]
    [InlineData(WorkflowChatRunStartError.ConversationNotFound, StatusCodes.Status404NotFound)]
    [InlineData(WorkflowChatRunStartError.ChatHistoryReservationUnavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(WorkflowChatRunStartError.IdempotencyConflict, StatusCodes.Status409Conflict)]
    [InlineData(WorkflowChatRunStartError.None, StatusCodes.Status400BadRequest)]
    public void ToHttpStatusCode_ShouldMapExpectedCode(
        WorkflowChatRunStartError error,
        int expected)
    {
        ChatRunStartErrorMapper.ToHttpStatusCode(error).Should().Be(expected);
    }

    [Fact]
    public void ToCommandError_WorkflowNotFound_ShouldMapExpectedPayload()
    {
        var mapped = ChatRunStartErrorMapper.ToCommandError(WorkflowChatRunStartError.WorkflowNotFound);

        mapped.Code.Should().Be("WORKFLOW_NOT_FOUND");
        mapped.Message.Should().Be(WorkflowChatRunStartErrorGuidance.WorkflowNotFound);
        mapped.Message.Should().Contain("current scope catalog");
        mapped.Message.Should().Contain("list the current scope workflows");
        mapped.Message.Should().Contain("actor_id");
        mapped.Message.Should().Contain("descriptor's workflow name");
        mapped.Message.Should().Contain("not the stable scope workflow_id");
        mapped.Message.Should().Contain("list_external_workflow_capabilities");
        mapped.Message.Should().Contain("exact typed selector");
        mapped.Message.Should().Contain("structured descriptor");
        mapped.Message.Should().NotContain("user_service_id + slug + operation contract");
        mapped.Message.Should().NotContain("without a slug");
        mapped.Message.Should().NotContain("discovered slug/path");
        mapped.Message.Should().Contain("use_skill");
        mapped.Message.Should().Contain("workflow_id");
    }

    [Fact]
    public void ToCommandError_WorkflowBindingMismatch_ShouldMapExpectedPayload()
    {
        var mapped = ChatRunStartErrorMapper.ToCommandError(WorkflowChatRunStartError.WorkflowBindingMismatch);

        mapped.Code.Should().Be("WORKFLOW_BINDING_MISMATCH");
        mapped.Message.Should().Be("Actor is bound to a different workflow.");
    }

    [Fact]
    public void ToCommandError_InvalidWorkflowYaml_ShouldMapExpectedPayload()
    {
        var mapped = ChatRunStartErrorMapper.ToCommandError(WorkflowChatRunStartError.InvalidWorkflowYaml);

        mapped.Code.Should().Be("INVALID_WORKFLOW_YAML");
        mapped.Message.Should().Be("Workflow YAML is invalid.");
    }

    [Fact]
    public void ToCommandError_ProjectionUnavailable_ShouldMapExpectedPayload()
    {
        var mapped = ChatRunStartErrorMapper.ToCommandError(WorkflowChatRunStartError.ProjectionUnavailable);

        mapped.Code.Should().Be("WORKFLOW_PROJECTION_UNAVAILABLE");
        mapped.Message.Should().Be("Workflow projection is unavailable.");
    }

    [Fact]
    public void ToCommandError_InvalidCallerCredential_ShouldMapExpectedPayload()
    {
        var mapped = ChatRunStartErrorMapper.ToCommandError(WorkflowChatRunStartError.InvalidCallerCredential);

        mapped.Code.Should().Be("INVALID_CALLER_CREDENTIAL");
        mapped.Message.Should().Be("Caller credential is invalid.");
    }

    [Theory]
    [InlineData(WorkflowChatRunStartError.InvalidConversationInput, "INVALID_CONVERSATION_INPUT", "Conversation input is invalid.")]
    [InlineData(WorkflowChatRunStartError.InvalidConversationId, "INVALID_CONVERSATION_ID", "Conversation id is invalid.")]
    [InlineData(WorkflowChatRunStartError.ConversationNotFound, "CONVERSATION_NOT_FOUND", "Conversation was not found.")]
    [InlineData(WorkflowChatRunStartError.ChatHistoryReservationUnavailable, "CHAT_HISTORY_RESERVATION_UNAVAILABLE", "Chat history reservation is unavailable.")]
    public void ToCommandError_ConversationErrors_ShouldMapExpectedPayload(
        WorkflowChatRunStartError error,
        string expectedCode,
        string expectedMessage)
    {
        var mapped = ChatRunStartErrorMapper.ToCommandError(error);

        mapped.Code.Should().Be(expectedCode);
        mapped.Message.Should().Be(expectedMessage);
    }

    [Fact]
    public void ToErrorBody_ShouldMapFailureDetailReadinessShape()
    {
        var readiness = new ExternalCapabilityReadiness
        {
            Status = ExternalCapabilityReadinessStatus.ContractDrift,
            SelectedSelector = new ExternalWorkflowCapabilitySelector
            {
                HostConnector = new HostConnectorCapabilityRef
                {
                    ConnectorCapabilityRef = "connector/ref",
                    OperationId = "operation-1",
                },
            },
        };
        readiness.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = ExternalCapabilityReadinessStatus.ContractDrift,
            Code = "NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED",
            SafeMessage = "Rebind the workflow operation.",
        });
        readiness.Remediations.Add(new ExternalCapabilityRemediation
        {
            ActionKind = ExternalCapabilityRemediationActionKind.RebindWorkflow,
            Label = "Rebind workflow",
            TrustedLocator = " nyxid:services ",
        });
        readiness.Sources.Add(new ExternalCapabilitySourceStamp
        {
            SourceKind = ExternalCapabilitySourceKind.ConnectorCatalog,
            SourceId = "connector-catalog:scope-alpha",
            SourceVersion = 7,
        });
        var detail = WorkflowChatRunStartFailureDetail.Create(
            WorkflowChatRunStartError.InvalidWorkflowYaml,
            "nyxid_proxy derived field 'contract_digest' cannot be authored.",
            readiness);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ChatRunStartErrorMapper.ToErrorBody(detail)));
        var root = json.RootElement;
        root.GetProperty("code").GetString().Should().Be("INVALID_WORKFLOW_YAML");
        root.GetProperty("message").GetString().Should().Be("nyxid_proxy derived field 'contract_digest' cannot be authored.");
        var readinessJson = root.GetProperty("externalCapabilityReadiness");
        readinessJson.GetProperty("status").GetString().Should().Be("contract_drift");
        readinessJson.GetProperty("selectedCapability").GetProperty("userServiceId").ValueKind.Should().Be(JsonValueKind.Null);
        readinessJson.GetProperty("selectedCapability").GetProperty("endpointId").ValueKind.Should().Be(JsonValueKind.Null);
        readinessJson.GetProperty("selectedCapability").GetProperty("operationId").GetString().Should().Be("operation-1");
        readinessJson.GetProperty("selectedCapability").GetProperty("connectorCapabilityRef").GetString().Should().Be("connector/ref");
        readinessJson.GetProperty("blockers")[0].GetProperty("code").GetString().Should().Be("NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED");
        readinessJson.GetProperty("blockers")[0].GetProperty("status").GetString().Should().Be("contract_drift");
        readinessJson.GetProperty("remediations")[0].GetProperty("actionKind").GetString().Should().Be("rebind_workflow");
        readinessJson.GetProperty("remediations")[0].GetProperty("trustedLocator").GetString().Should().Be("nyxid:services");
        readinessJson.GetProperty("sources")[0].GetProperty("sourceKind").GetString().Should().Be("connector_catalog");
        readinessJson.GetProperty("sources")[0].GetProperty("sourceVersion").GetInt64().Should().Be(7);
    }

    [Fact]
    public void ToErrorBody_ShouldKeepNyxIdEndpointIdentityOutOfConnectorOperationField()
    {
        var readiness = new ExternalCapabilityReadiness
        {
            Status = ExternalCapabilityReadinessStatus.OperationSelectionRequired,
            SelectedSelector = new ExternalWorkflowCapabilitySelector
            {
                NyxIdOperation = new NyxIdOperationSelector
                {
                    UserServiceId = "usvc-alpha",
                    EndpointId = "endpoint-alpha",
                },
            },
        };
        var detail = WorkflowChatRunStartFailureDetail.Create(
            WorkflowChatRunStartError.InvalidWorkflowYaml,
            "External workflow capability admission failed.",
            readiness);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ChatRunStartErrorMapper.ToErrorBody(detail)));
        var selected = json.RootElement.GetProperty("externalCapabilityReadiness")
            .GetProperty("selectedCapability");

        selected.GetProperty("userServiceId").GetString().Should().Be("usvc-alpha");
        selected.GetProperty("endpointId").GetString().Should().Be("endpoint-alpha");
        selected.GetProperty("operationId").ValueKind.Should().Be(JsonValueKind.Null);
        selected.GetProperty("connectorCapabilityRef").ValueKind.Should().Be(JsonValueKind.Null);
        selected.TryGetProperty("requestContractDigest", out _).Should().BeFalse();
    }

    [Fact]
    public void ToErrorBody_ShouldKeepPublishedCapabilityJsonShapeUnchanged()
    {
        var readiness = new ExternalCapabilityReadiness
        {
            Status = ExternalCapabilityReadinessStatus.ContractDrift,
            SelectedCapability = new ExternalWorkflowCapabilityRef
            {
                NyxIdUserService = new NyxIdUserServiceCapabilityRef
                {
                    UserServiceId = "usvc-published-alpha",
                    EndpointId = "endpoint-published-alpha",
                },
            },
        };
        var detail = WorkflowChatRunStartFailureDetail.Create(
            WorkflowChatRunStartError.InvalidWorkflowYaml,
            "External workflow capability admission failed.",
            readiness);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ChatRunStartErrorMapper.ToErrorBody(detail)));
        var selected = json.RootElement.GetProperty("externalCapabilityReadiness")
            .GetProperty("selectedCapability");

        selected.GetProperty("userServiceId").GetString().Should().Be("usvc-published-alpha");
        selected.GetProperty("endpointId").GetString().Should().Be("endpoint-published-alpha");
        selected.TryGetProperty("requestContractDigest", out _).Should().BeFalse();
    }

    [Fact]
    public void ToErrorBody_ShouldMapExplicitRequestSelectorToSafeExactIdentity()
    {
        var request = ExplicitRequest("usvc-explicit-alpha");
        request.HeaderParameters.Add("If-Match");
        var readiness = new ExternalCapabilityReadiness
        {
            Status = ExternalCapabilityReadinessStatus.ContractDrift,
            SelectedSelector = new ExternalWorkflowCapabilitySelector
            {
                NyxIdRequest = request,
            },
        };
        var detail = WorkflowChatRunStartFailureDetail.Create(
            WorkflowChatRunStartError.InvalidWorkflowYaml,
            "External workflow capability admission failed.",
            readiness);

        var serialized = JsonSerializer.Serialize(ChatRunStartErrorMapper.ToErrorBody(detail));
        using var json = JsonDocument.Parse(serialized);
        var selected = json.RootElement.GetProperty("externalCapabilityReadiness")
            .GetProperty("selectedCapability");

        selected.GetProperty("userServiceId").GetString().Should().Be("usvc-explicit-alpha");
        selected.GetProperty("requestContractDigest").GetString().Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdRequestContractDigest(request));
        selected.GetProperty("endpointId").ValueKind.Should().Be(JsonValueKind.Null);
        selected.GetProperty("operationId").ValueKind.Should().Be(JsonValueKind.Null);
        selected.GetProperty("connectorCapabilityRef").ValueKind.Should().Be(JsonValueKind.Null);
        serialized.Should().NotContain("/api/private/{resource_id}");
        serialized.Should().NotContain("If-Match");
    }

    [Fact]
    public void ToErrorBody_ShouldMapExplicitRequestCapabilityToAuthoredRequestIdentity()
    {
        var request = ExplicitRequest("usvc-capability-alpha");
        var readiness = new ExternalCapabilityReadiness
        {
            Status = ExternalCapabilityReadinessStatus.ContractDrift,
            SelectedCapability = new ExternalWorkflowCapabilityRef
            {
                NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
                {
                    Request = request,
                    ServiceSlugSnapshot = "server-slug-must-not-be-identity",
                    ContractDigest = "server-proof-digest-must-not-be-operation-id",
                },
            },
        };
        var detail = WorkflowChatRunStartFailureDetail.Create(
            WorkflowChatRunStartError.InvalidWorkflowYaml,
            "External workflow capability admission failed.",
            readiness);

        var serialized = JsonSerializer.Serialize(ChatRunStartErrorMapper.ToErrorBody(detail));
        using var json = JsonDocument.Parse(serialized);
        var selected = json.RootElement.GetProperty("externalCapabilityReadiness")
            .GetProperty("selectedCapability");

        selected.GetProperty("userServiceId").GetString().Should().Be("usvc-capability-alpha");
        selected.GetProperty("requestContractDigest").GetString().Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdRequestContractDigest(request));
        selected.GetProperty("endpointId").ValueKind.Should().Be(JsonValueKind.Null);
        selected.GetProperty("operationId").ValueKind.Should().Be(JsonValueKind.Null);
        serialized.Should().NotContain("server-slug-must-not-be-identity");
        serialized.Should().NotContain("server-proof-digest-must-not-be-operation-id");
    }

    private static NyxIdRequestSelector ExplicitRequest(string userServiceId) =>
        new()
        {
            UserServiceId = userServiceId,
            Method = NyxIdRequestMethod.Get,
            PathTemplate = "/api/private/{resource_id}",
            BodyMode = NyxIdRequestBodyMode.None,
            ResponseMode = NyxIdRequestResponseMode.Text,
        };
}
