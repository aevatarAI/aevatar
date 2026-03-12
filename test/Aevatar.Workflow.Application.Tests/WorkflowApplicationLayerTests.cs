using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowApplicationLayerTests
{
    [Fact]
    public void WorkflowRunCompletionPolicy_ShouldResolveFinishedAndFailedFrames()
    {
        var policy = new WorkflowRunCompletionPolicy();

        policy.TryResolve(new WorkflowRunEventEnvelope
        {
            RunFinished = new WorkflowRunFinishedEventPayload(),
        }, out var finishedStatus).Should().BeTrue();
        finishedStatus.Should().Be(WorkflowProjectionCompletionStatus.Completed);

        policy.TryResolve(new WorkflowRunEventEnvelope
        {
            RunError = new WorkflowRunErrorEventPayload(),
        }, out var failedStatus).Should().BeTrue();
        failedStatus.Should().Be(WorkflowProjectionCompletionStatus.Failed);

        policy.TryResolve(new WorkflowRunEventEnvelope
        {
            Custom = new WorkflowCustomEventPayload { Name = "noop" },
        }, out var unknownStatus).Should().BeFalse();
        unknownStatus.Should().Be(WorkflowProjectionCompletionStatus.Unknown);
    }

    [Fact]
    public void WorkflowDirectFallbackPolicy_ShouldNotFallback_WhenDirectWorkflowWithoutInlineYaml()
    {
        var policy = new WorkflowDirectFallbackPolicy(new WorkflowRunBehaviorOptions());
        var request = new WorkflowChatRunRequest("hello", "direct", null, null);

        var shouldFallback = policy.ShouldFallback(
            request,
            new WorkflowDirectFallbackTriggerException("boom"));

        shouldFallback.Should().BeFalse();
    }

    [Fact]
    public void WorkflowDirectFallbackPolicy_ShouldNotFallback_WhenOperationCanceled()
    {
        var options = new WorkflowRunBehaviorOptions();
        options.DirectFallbackWorkflowWhitelist.Add("analysis");
        var policy = new WorkflowDirectFallbackPolicy(options);
        var request = new WorkflowChatRunRequest("hello", "analysis", null, null);

        var shouldFallback = policy.ShouldFallback(
            request,
            new OperationCanceledException());

        shouldFallback.Should().BeFalse();
    }

    [Fact]
    public void WorkflowDirectFallbackPolicy_ShouldFallback_WhenWhitelistedWorkflowFails()
    {
        var options = new WorkflowRunBehaviorOptions();
        options.DirectFallbackWorkflowWhitelist.Add("analysis");
        var policy = new WorkflowDirectFallbackPolicy(options);
        var request = new WorkflowChatRunRequest("hello", "analysis", null, null);

        var shouldFallback = policy.ShouldFallback(
            request,
            new WorkflowDirectFallbackTriggerException("boom"));

        shouldFallback.Should().BeTrue();
        policy.ToFallbackRequest(request).WorkflowName.Should().Be(WorkflowRunBehaviorOptions.DirectWorkflowName);
    }
}
