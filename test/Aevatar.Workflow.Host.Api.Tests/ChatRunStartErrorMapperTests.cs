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
        mapped.Message.Should().Contain("nyxid_proxy");
        mapped.Message.Should().Contain("slug/path");
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
}
