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

    Task<BridgeCallbackTokenIssueResponse> IssueBridgeCallbackTokenAsync(
        BridgeCallbackTokenIssueRequest request,
        CancellationToken cancellationToken = default);

    Task<BridgeIngressResponse> PostBridgeCallbackAsync(
        BridgeIngressRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JsonElement>> GetWorkflowCatalogAsync(
        CancellationToken cancellationToken = default);

    Task<JsonElement?> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default);

    Task<JsonElement?> GetWorkflowDetailAsync(
        string workflowName,
        CancellationToken cancellationToken = default);

    Task<JsonElement?> GetActorSnapshotAsync(
        string actorId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JsonElement>> GetActorTimelineAsync(
        string actorId,
        int take = 200,
        CancellationToken cancellationToken = default);
}
