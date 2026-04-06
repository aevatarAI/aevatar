using Aevatar.Interop.A2A.Abstractions.Models;

namespace Aevatar.Interop.A2A.Abstractions;

/// <summary>A2A 协议适配服务。将 A2A JSON-RPC 请求转换为内部 actor 交互。</summary>
public interface IA2AAdapterService
{
    /// <summary>处理 tasks/send 请求。</summary>
    Task<A2ATask> SendTaskAsync(TaskSendParams sendParams, CancellationToken ct = default);

    /// <summary>处理 tasks/get 请求。</summary>
    Task<A2ATask?> GetTaskAsync(TaskQueryParams queryParams, CancellationToken ct = default);

    /// <summary>处理 tasks/cancel 请求。</summary>
    Task<A2ATask> CancelTaskAsync(TaskIdParams idParams, CancellationToken ct = default);

    /// <summary>获取 Agent Card。</summary>
    AgentCard GetAgentCard(string baseUrl);
}

/// <summary>tasks/send 参数。</summary>
public sealed class TaskSendParams
{
    public required string Id { get; init; }
    public string? SessionId { get; init; }
    public required Message Message { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>tasks/get 参数。</summary>
public sealed class TaskQueryParams
{
    public required string Id { get; init; }
    public int? HistoryLength { get; init; }
}

/// <summary>tasks/cancel 参数。</summary>
public sealed class TaskIdParams
{
    public required string Id { get; init; }
}
