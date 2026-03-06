using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public interface IWorkflowRunActorResolver
{
    Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct = default);
}

public sealed record WorkflowActorResolutionResult(
    IActor? RunActor,
    string WorkflowNameForRun,
    string? DefinitionActorId,
    WorkflowChatRunStartError Error);
