using System.Threading.Channels;
using Aevatar.Interop.A2A.Abstractions.Models;

namespace Aevatar.Interop.A2A.Abstractions;

/// <summary>A2A Task state store. Tracks the mapping between A2A tasks and internal actor commands.</summary>
public interface IA2ATaskStore
{
    Task<A2ATask> CreateTaskAsync(string taskId, string? sessionId, Message message, CancellationToken ct = default);
    Task<A2ATask?> GetTaskAsync(string taskId, CancellationToken ct = default);
    Task<A2ATask> UpdateTaskStateAsync(string taskId, TaskState state, Message? message = null, CancellationToken ct = default);
    Task<A2ATask> AddArtifactAsync(string taskId, Artifact artifact, CancellationToken ct = default);
    Task<bool> DeleteTaskAsync(string taskId, CancellationToken ct = default);

    /// <summary>Subscribes to state change notifications for the specified task. Returns a ChannelReader for SSE streaming consumption.</summary>
    ChannelReader<TaskStateUpdate> SubscribeAsync(string taskId);
}

/// <summary>Task state change notification.</summary>
public sealed class TaskStateUpdate
{
    public required A2ATaskStatus Status { get; init; }
    public Artifact? Artifact { get; init; }
    public bool IsFinal { get; init; }
}
