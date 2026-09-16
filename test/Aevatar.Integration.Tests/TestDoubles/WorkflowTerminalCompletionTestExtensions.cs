using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Execution;
using FluentAssertions;

namespace Aevatar.Integration.Tests;

internal static class WorkflowTerminalCompletionTestExtensions
{
    /// <summary>
    /// The run actor clears the kernel's persisted terminal completion when it applies the
    /// terminal run event, and observers consume what was published. An isolated kernel test
    /// must mirror that hand-off before asserting late-delivery behaviour; otherwise the kernel
    /// keeps re-publishing the pending completion as durable recovery.
    /// </summary>
    public static async Task AcknowledgeTerminalCompletionAsync(this TestEventHandlerContext ctx)
    {
        var kernelState = ctx.LoadState<WorkflowExecutionKernelState>(WorkflowExecutionKernel.ModuleStateKey);
        kernelState.PendingWorkflowCompletion.Should().NotBeNull();
        if (WorkflowExecutionKernel.NormalizeTerminalState(kernelState))
            await ctx.ClearStateAsync(WorkflowExecutionKernel.ModuleStateKey);
        else
            await ctx.SaveStateAsync(WorkflowExecutionKernel.ModuleStateKey, kernelState);
        ctx.Published.Clear();
    }
}
