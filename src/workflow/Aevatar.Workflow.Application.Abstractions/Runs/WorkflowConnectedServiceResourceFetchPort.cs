namespace Aevatar.Workflow.Application.Abstractions.Runs;

public sealed record WorkflowConnectedServiceResourceFetchRoute(
    string Provider,
    string Operation,
    string ResourceKind);

public sealed record WorkflowConnectedServiceResourceFetchRequest(
    WorkflowConnectedServiceResourceFetchRoute Route,
    string SourceMessageId,
    string SourceResourceKey,
    WorkflowCallerCredential CallerCredential);

public sealed record WorkflowConnectedServiceResourceFetchResult(
    bool Succeeded,
    ReadOnlyMemory<byte> Content,
    string? MediaType = null,
    string? FileName = null,
    string? Detail = null,
    int StatusCode = 0);

public interface IWorkflowConnectedServiceResourceFetchAdapter
{
    IReadOnlyList<WorkflowConnectedServiceResourceFetchRoute> Routes { get; }

    ValueTask<WorkflowConnectedServiceResourceFetchResult> FetchAsync(
        WorkflowConnectedServiceResourceFetchRequest request,
        CancellationToken cancellationToken = default);
}
