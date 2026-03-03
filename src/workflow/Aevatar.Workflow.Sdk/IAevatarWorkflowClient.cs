using System.Text.Json;
using Aevatar.Workflow.Sdk.Contracts;

namespace Aevatar.Workflow.Sdk;

public interface IAevatarWorkflowClient
{
    IAsyncEnumerable<WorkflowEvent> StartRunStreamAsync(
        ChatRunRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkflowRunResult> RunToCompletionAsync(
        ChatRunRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkflowResumeResponse> ResumeAsync(
        WorkflowResumeRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkflowSignalResponse> SignalAsync(
        WorkflowSignalRequest request,
        CancellationToken cancellationToken = default);

    Task<JsonElement?> GetActorSnapshotAsync(
        string actorId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JsonElement>> GetActorTimelineAsync(
        string actorId,
        int take = 200,
        CancellationToken cancellationToken = default);
}
