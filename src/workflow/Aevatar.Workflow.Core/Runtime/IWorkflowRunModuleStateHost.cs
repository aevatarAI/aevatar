namespace Aevatar.Workflow.Core.Runtime;

public interface IWorkflowRunModuleStateHost
{
    string RunId { get; }

    string? GetModuleStateJson(string moduleName);

    Task UpsertModuleStateJsonAsync(
        string moduleName,
        string stateJson,
        CancellationToken ct = default);

    Task ClearModuleStateAsync(
        string moduleName,
        CancellationToken ct = default);
}
