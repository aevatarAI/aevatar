using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Adapters;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowCommandPolicyAndAdapterTests
{
    [Fact]
    public void WorkflowCommandContextPolicy_Create_ShouldValidateTarget()
    {
        var policy = new WorkflowCommandContextPolicy();

        Action act = () => policy.Create(" ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WorkflowCommandContextPolicy_Create_ShouldGenerateIdsAndCopyMetadata()
    {
        var policy = new WorkflowCommandContextPolicy();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["k1"] = "v1",
        };

        var context = policy.Create("actor-1", metadata);

        context.TargetId.Should().Be("actor-1");
        context.CommandId.Should().NotBeNullOrWhiteSpace();
        context.CorrelationId.Should().Be(context.CommandId);
        context.Metadata.Should().ContainKey("k1").WhoseValue.Should().Be("v1");

        metadata["k1"] = "mutated";
        context.Metadata["k1"].Should().Be("v1");
    }

    [Fact]
    public void WorkflowCommandContextPolicy_Create_ShouldRespectProvidedIds()
    {
        var policy = new WorkflowCommandContextPolicy();

        var context = policy.Create(
            "actor-2",
            commandId: "cmd-2",
            correlationId: "corr-2");

        context.CommandId.Should().Be("cmd-2");
        context.CorrelationId.Should().Be("corr-2");
    }

    [Fact]
    public async Task WorkflowCommandExecutionServiceAdapter_ShouldBridgeInnerResult()
    {
        var inner = new FakeWorkflowRunCommandService
        {
            Result = new WorkflowChatRunExecutionResult(
                WorkflowChatRunStartError.None,
                new WorkflowChatRunStarted("actor-1", "direct", "cmd-1"),
                new WorkflowChatRunFinalizeResult(WorkflowProjectionCompletionStatus.Completed, true)),
        };

        var adapter = new WorkflowCommandExecutionServiceAdapter(inner);

        var result = await adapter.ExecuteAsync(
            new WorkflowChatRunRequest("hello", "direct", "actor-1"),
            static (_, _) => ValueTask.CompletedTask,
            onStartedAsync: static (_, _) => ValueTask.CompletedTask,
            CancellationToken.None);

        inner.ExecuteCalls.Should().Be(1);
        result.Error.Should().Be(WorkflowChatRunStartError.None);
        result.Started.Should().NotBeNull();
        result.Started!.RunActorId.Should().Be("actor-1");
        result.FinalizeResult!.ProjectionCompleted.Should().BeTrue();
    }

    private sealed class FakeWorkflowRunCommandService : IWorkflowRunCommandService
    {
        public WorkflowChatRunExecutionResult Result { get; set; } =
            new(WorkflowChatRunStartError.None, null, null);

        public int ExecuteCalls { get; private set; }

        public Task<WorkflowChatRunExecutionResult> ExecuteAsync(
            WorkflowChatRunRequest request,
            Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatRunStarted, CancellationToken, ValueTask>? onStartedAsync = null,
            CancellationToken ct = default)
        {
            ExecuteCalls++;
            return Task.FromResult(Result);
        }
    }
}
