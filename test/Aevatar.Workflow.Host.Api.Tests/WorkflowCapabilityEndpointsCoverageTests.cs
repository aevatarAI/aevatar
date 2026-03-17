using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowCapabilityEndpointsCoverageTests
{
    [Fact]
    public void ChatRunRequestNormalizer_ShouldPreserveWorkflowName_WhenInlineWorkflowBundleIsProvided()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            Workflow = "auto",
            AgentId = " actor-1 ",
            SessionId = " session-1 ",
            WorkflowYamls = ["name: inline"],
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request.Should().BeEquivalentTo(
            new WorkflowChatRunRequest(
                "hello",
                "auto",
                "actor-1",
                SessionId: "session-1",
                WorkflowYamls: ["name: inline"],
                Metadata: new Dictionary<string, string>()));
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldAcceptLegacyWorkflowYamlAlias()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            AgentId = " actor-1 ",
            WorkflowYaml = "name: inline",
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request.Should().BeEquivalentTo(
            new WorkflowChatRunRequest(
                "hello",
                null,
                "actor-1",
                SessionId: null,
                WorkflowYamls: ["name: inline"],
                Metadata: new Dictionary<string, string>()));
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectBlankLegacyWorkflowYaml()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            WorkflowYaml = "   ",
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidWorkflowYaml);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectMixedLegacyAndBundleWorkflowYaml()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            WorkflowYaml = "name: legacy",
            WorkflowYamls = ["name: bundle"],
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidWorkflowYaml);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldLeaveWorkflowUnset_WhenCreatingNewRun()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request.Should().BeEquivalentTo(
            new WorkflowChatRunRequest(
                "hello",
                null,
                null,
                null,
                Metadata: new Dictionary<string, string>()));
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
