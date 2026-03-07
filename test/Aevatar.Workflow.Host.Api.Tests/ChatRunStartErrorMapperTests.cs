using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Host.Api.Tests;

public class ChatRunStartErrorMapperTests
{
    [Theory]
    [InlineData(WorkflowChatRunStartError.DefinitionActorNotFound, StatusCodes.Status404NotFound)]
    [InlineData(WorkflowChatRunStartError.WorkflowNotFound, StatusCodes.Status404NotFound)]
    [InlineData(WorkflowChatRunStartError.DefinitionActorTypeNotSupported, StatusCodes.Status400BadRequest)]
    [InlineData(WorkflowChatRunStartError.DefinitionBindingMismatch, StatusCodes.Status409Conflict)]
    [InlineData(WorkflowChatRunStartError.DefinitionActorWorkflowNotConfigured, StatusCodes.Status409Conflict)]
    [InlineData(WorkflowChatRunStartError.InvalidWorkflowYaml, StatusCodes.Status400BadRequest)]
    [InlineData(WorkflowChatRunStartError.WorkflowNameMismatch, StatusCodes.Status400BadRequest)]
    [InlineData(WorkflowChatRunStartError.DefinitionSourceConflict, StatusCodes.Status400BadRequest)]
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
        mapped.Message.Should().Be("Workflow not found.");
    }

    [Fact]
    public void ToCommandError_DefinitionBindingMismatch_ShouldMapExpectedPayload()
    {
        var mapped = ChatRunStartErrorMapper.ToCommandError(WorkflowChatRunStartError.DefinitionBindingMismatch);

        mapped.Code.Should().Be("DEFINITION_BINDING_MISMATCH");
        mapped.Message.Should().Be("Definition actor is bound to a different workflow.");
    }

    [Fact]
    public void ToCommandError_InvalidWorkflowYaml_ShouldMapExpectedPayload()
    {
        var mapped = ChatRunStartErrorMapper.ToCommandError(WorkflowChatRunStartError.InvalidWorkflowYaml);

        mapped.Code.Should().Be("INVALID_WORKFLOW_YAML");
        mapped.Message.Should().Be("Workflow YAML is invalid.");
    }
}
