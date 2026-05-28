using Aevatar.Workflow.Core.Modules;

namespace Aevatar.Integration.Tests;

internal sealed class RecordingWorkflowStepIoDispatchQueue : IWorkflowStepIoDispatchQueue
{
    public List<WorkflowStepIoWorkItem> Items { get; } = [];

    public ValueTask EnqueueAsync(WorkflowStepIoWorkItem item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Items.Add(item);
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<WorkflowStepIoWorkItem> DequeueAllAsync(CancellationToken ct)
    {
        _ = ct;
        return AsyncEnumerable.Empty<WorkflowStepIoWorkItem>();
    }
}
