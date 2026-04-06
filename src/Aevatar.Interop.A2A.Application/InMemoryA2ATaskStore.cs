using System.Collections.Concurrent;
using System.Threading.Channels;
using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Abstractions.Models;

namespace Aevatar.Interop.A2A.Application;

/// <summary>内存实现的 A2A Task 存储。仅限开发/测试使用。</summary>
public sealed class InMemoryA2ATaskStore : IA2ATaskStore
{
    private readonly ConcurrentDictionary<string, A2ATask> _tasks = new();
    private readonly ConcurrentDictionary<string, List<Channel<TaskStateUpdate>>> _subscribers = new();

    public Task<A2ATask> CreateTaskAsync(string taskId, string? sessionId, Message message, CancellationToken ct = default)
    {
        var task = new A2ATask
        {
            Id = taskId,
            SessionId = sessionId,
            Status = new A2ATaskStatus
            {
                State = TaskState.Submitted,
                Timestamp = DateTime.UtcNow.ToString("O"),
            },
            History = [message],
        };

        if (!_tasks.TryAdd(taskId, task))
            throw new InvalidOperationException($"Task '{taskId}' already exists.");

        return Task.FromResult(task);
    }

    public Task<A2ATask?> GetTaskAsync(string taskId, CancellationToken ct = default)
    {
        _tasks.TryGetValue(taskId, out var task);
        return Task.FromResult(task);
    }

    public Task<A2ATask> UpdateTaskStateAsync(string taskId, TaskState state, Message? message = null, CancellationToken ct = default)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            throw new KeyNotFoundException($"Task '{taskId}' not found.");

        task.Status = new A2ATaskStatus
        {
            State = state,
            Message = message,
            Timestamp = DateTime.UtcNow.ToString("O"),
        };

        if (message != null)
        {
            task.History ??= [];
            task.History.Add(message);
        }

        // Notify subscribers
        var isFinal = state is TaskState.Completed or TaskState.Failed or TaskState.Canceled;
        NotifySubscribers(taskId, new TaskStateUpdate
        {
            Status = task.Status,
            IsFinal = isFinal,
        });

        return Task.FromResult(task);
    }

    public Task<A2ATask> AddArtifactAsync(string taskId, Artifact artifact, CancellationToken ct = default)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            throw new KeyNotFoundException($"Task '{taskId}' not found.");

        task.Artifacts ??= [];
        task.Artifacts.Add(artifact);

        NotifySubscribers(taskId, new TaskStateUpdate
        {
            Status = task.Status,
            Artifact = artifact,
        });

        return Task.FromResult(task);
    }

    public Task<bool> DeleteTaskAsync(string taskId, CancellationToken ct = default)
    {
        return Task.FromResult(_tasks.TryRemove(taskId, out _));
    }

    public ChannelReader<TaskStateUpdate> SubscribeAsync(string taskId)
    {
        var channel = Channel.CreateBounded<TaskStateUpdate>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        var subscribers = _subscribers.GetOrAdd(taskId, _ => []);
        lock (subscribers)
        {
            subscribers.Add(channel);
        }

        return channel.Reader;
    }

    private void NotifySubscribers(string taskId, TaskStateUpdate update)
    {
        if (!_subscribers.TryGetValue(taskId, out var subscribers))
            return;

        lock (subscribers)
        {
            for (var i = subscribers.Count - 1; i >= 0; i--)
            {
                if (!subscribers[i].Writer.TryWrite(update))
                {
                    subscribers[i].Writer.TryComplete();
                    subscribers.RemoveAt(i);
                }

                if (update.IsFinal)
                {
                    subscribers[i].Writer.TryComplete();
                }
            }

            if (update.IsFinal)
                subscribers.Clear();
        }
    }
}
