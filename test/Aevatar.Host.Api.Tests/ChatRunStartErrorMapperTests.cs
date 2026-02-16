using Aevatar.Host.Api.Endpoints;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Host.Api.Tests;

public class ChatRunStartErrorMapperTests
{
    [Theory]
    [InlineData(WorkflowChatRunStartError.AgentNotFound, StatusCodes.Status404NotFound)]
    [InlineData(WorkflowChatRunStartError.WorkflowNotFound, StatusCodes.Status404NotFound)]
    [InlineData(WorkflowChatRunStartError.AgentTypeNotSupported, StatusCodes.Status400BadRequest)]
    [InlineData(WorkflowChatRunStartError.ProjectionDisabled, StatusCodes.Status503ServiceUnavailable)]
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
}
