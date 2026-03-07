using Aevatar.Workflow.Abstractions;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Runtime;

public class WorkflowPrimitiveExecutionPlannerTests
{
    [Fact]
    public async Task DispatchAsync_ShouldUseStepFamilyHandlerBeforeFallback()
    {
        var handler = new TestStepFamilyHandler(["llm_call"]);
        var fallbackCalls = 0;
        var planner = new WorkflowPrimitiveExecutionPlanner(
            (_, _) =>
            {
                fallbackCalls++;
                return Task.FromResult(true);
            },
            new WorkflowStepFamilyDispatchTable([handler]));

        var request = new StepRequestEvent
        {
            StepId = "s1",
            StepType = "LLM_CALL",
            RunId = "run-1",
        };

        await planner.DispatchAsync(request, CancellationToken.None);

        handler.HandledRequests.Should().ContainSingle().Which.Should().BeSameAs(request);
        fallbackCalls.Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_ShouldFallbackWhenNoStepFamilyHandlerExists()
    {
        var fallbackCalls = 0;
        var planner = new WorkflowPrimitiveExecutionPlanner(
            (_, _) =>
            {
                fallbackCalls++;
                return Task.FromResult(true);
            },
            new WorkflowStepFamilyDispatchTable([]));

        await planner.DispatchAsync(new StepRequestEvent
        {
            StepId = "s1",
            StepType = "custom_step",
            RunId = "run-1",
        }, CancellationToken.None);

        fallbackCalls.Should().Be(1);
    }

    [Fact]
    public void DispatchTable_ShouldRejectDuplicateCanonicalStepTypes()
    {
        var first = new TestStepFamilyHandler(["llm_call"]);
        var second = new TestStepFamilyHandler(["LLM_CALL"]);

        var act = () => new WorkflowStepFamilyDispatchTable([first, second]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*llm_call*");
    }

    private sealed class TestStepFamilyHandler(IReadOnlyCollection<string> supportedStepTypes)
        : IWorkflowStepFamilyHandler
    {
        public IReadOnlyCollection<string> SupportedStepTypes { get; } = supportedStepTypes;

        public List<StepRequestEvent> HandledRequests { get; } = [];

        public Task HandleStepRequestAsync(StepRequestEvent request, CancellationToken ct)
        {
            HandledRequests.Add(request);
            return Task.CompletedTask;
        }
    }
}
