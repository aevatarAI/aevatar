using Aevatar.Interop.A2A.Abstractions.Models;

namespace Aevatar.Interop.A2A.Abstractions;

/// <summary>A2A Task 状态存储。跟踪 A2A 任务与内部 actor command 的映射。</summary>
public interface IA2ATaskStore
{
    Task<A2ATask> CreateTaskAsync(string taskId, string? sessionId, Message message, CancellationToken ct = default);
    Task<A2ATask?> GetTaskAsync(string taskId, CancellationToken ct = default);
    Task<A2ATask> UpdateTaskStateAsync(string taskId, TaskState state, Message? message = null, CancellationToken ct = default);
    Task<A2ATask> AddArtifactAsync(string taskId, Artifact artifact, CancellationToken ct = default);
    Task<bool> DeleteTaskAsync(string taskId, CancellationToken ct = default);
}
