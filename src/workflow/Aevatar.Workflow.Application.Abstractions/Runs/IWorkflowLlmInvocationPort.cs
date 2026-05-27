using Aevatar.Workflow.Abstractions;
using Google.Protobuf;

namespace Aevatar.Workflow.Application.Abstractions.Runs;

// Refactor (iter129/cluster-triage-workflow-llm-nyx-coupling): Old pattern: workflow LLM invocation returned one buffered final-result event. New principle: the port exposes a stream of workflow-owned typed protobuf events.
public interface IWorkflowLlmInvocationPort
{
    IAsyncEnumerable<WorkflowLlmInvocationEvent> InvokeAsync(
        WorkflowLlmExecutionIntent intent,
        CancellationToken ct = default);
}

public sealed record WorkflowLlmInvocationEvent(IMessage Payload);
