using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowCapabilityEndpointsCoverageTests
{
    [Fact]
    public void ChatRunRequestNormalizer_ShouldPreferInlineWorkflowBundle()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            Workflow = "auto",
            AgentId = " actor-1 ",
            WorkflowYamls = ["name: inline"],
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request.Should().BeEquivalentTo(new WorkflowChatRunRequest("hello", null, "actor-1", ["name: inline"]));
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldDefaultToAuto_WhenCreatingNewRun()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request.Should().BeEquivalentTo(
            new WorkflowChatRunRequest("hello", WorkflowRunBehaviorOptions.AutoWorkflowName, null, null));
    }

    [Fact]
    public void CapabilityTraceContext_CreateAcceptedPayload_ShouldUseReceiptValues()
    {
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");

        var payload = CapabilityTraceContext.CreateAcceptedPayload(receipt);

        payload.CommandId.Should().Be("cmd-1");
        payload.CorrelationId.Should().Be("corr-1");
        payload.ActorId.Should().Be("actor-1");
    }

    [Fact]
    public void CapabilityTraceContext_ResolveCorrelationId_ShouldFallbackToCommandId()
    {
        var correlationId = CapabilityTraceContext.ResolveCorrelationId("", "cmd-1");

        correlationId.Should().Be("cmd-1");
    }
}
