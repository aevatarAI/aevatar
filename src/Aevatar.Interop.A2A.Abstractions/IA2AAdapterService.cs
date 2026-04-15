using Aevatar.Interop.A2A.Abstractions.Models;

namespace Aevatar.Interop.A2A.Abstractions;

/// <summary>A2A protocol adapter service. Converts A2A JSON-RPC requests into internal actor interactions.</summary>
public interface IA2AAdapterService
{
    /// <summary>Handles the tasks/send request.</summary>
    Task<A2ATask> SendTaskAsync(TaskSendParams sendParams, CancellationToken ct = default);

    /// <summary>Handles the tasks/get request.</summary>
    Task<A2ATask?> GetTaskAsync(TaskQueryParams queryParams, CancellationToken ct = default);

    /// <summary>Handles the tasks/cancel request.</summary>
    Task<A2ATask> CancelTaskAsync(TaskIdParams idParams, CancellationToken ct = default);

    /// <summary>Gets the Agent Card.</summary>
    AgentCard GetAgentCard(string baseUrl);
}

/// <summary>tasks/send parameters.</summary>
public sealed class TaskSendParams
{
    public required string Id { get; init; }
    public string? SessionId { get; init; }
    public required Message Message { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>tasks/get parameters.</summary>
public sealed class TaskQueryParams
{
    public required string Id { get; init; }
    public int? HistoryLength { get; init; }
}

/// <summary>tasks/cancel parameters.</summary>
public sealed class TaskIdParams
{
    public required string Id { get; init; }
}
