using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Aevatar.Workflow.Application.Abstractions.Runs;

public abstract record WorkflowRunEvent
{
    public abstract string Type { get; }

    public long? Timestamp { get; init; }
}

public sealed record WorkflowRunStartedEvent : WorkflowRunEvent
{
    public override string Type => "RUN_STARTED";

    public required string ThreadId { get; init; }
}

public sealed record WorkflowRunFinishedEvent : WorkflowRunEvent
{
    public override string Type => "RUN_FINISHED";

    public required string ThreadId { get; init; }

    public object? Result { get; init; }
}

public sealed record WorkflowRunErrorEvent : WorkflowRunEvent
{
    public override string Type => "RUN_ERROR";

    public required string Message { get; init; }

    public string? Code { get; init; }
}

public sealed record WorkflowStepStartedEvent : WorkflowRunEvent
{
    public override string Type => "STEP_STARTED";

    public required string StepName { get; init; }
}

public sealed record WorkflowStepFinishedEvent : WorkflowRunEvent
{
    public override string Type => "STEP_FINISHED";

    public required string StepName { get; init; }
}

public sealed record WorkflowTextMessageStartEvent : WorkflowRunEvent
{
    public override string Type => "TEXT_MESSAGE_START";

    public required string MessageId { get; init; }

    public required string Role { get; init; }
}

public sealed record WorkflowTextMessageContentEvent : WorkflowRunEvent
{
    public override string Type => "TEXT_MESSAGE_CONTENT";

    public required string MessageId { get; init; }

    public required string Delta { get; init; }
}

public sealed record WorkflowTextMessageEndEvent : WorkflowRunEvent
{
    public override string Type => "TEXT_MESSAGE_END";

    public required string MessageId { get; init; }
}

public sealed record WorkflowStateSnapshotEvent : WorkflowRunEvent
{
    public override string Type => "STATE_SNAPSHOT";

    public required object Snapshot { get; init; }
}

public sealed record WorkflowToolCallStartEvent : WorkflowRunEvent
{
    public override string Type => "TOOL_CALL_START";

    public required string ToolCallId { get; init; }

    public required string ToolName { get; init; }
}

public sealed record WorkflowToolCallEndEvent : WorkflowRunEvent
{
    public override string Type => "TOOL_CALL_END";

    public required string ToolCallId { get; init; }

    public string? Result { get; init; }
}

public sealed record WorkflowCustomEvent : WorkflowRunEvent
{
    public override string Type => "CUSTOM";

    public required string Name { get; init; }

    public object? Value { get; init; }
}

public interface IWorkflowRunEventSink : IAsyncDisposable
{
    void Push(WorkflowRunEvent evt);

    ValueTask PushAsync(WorkflowRunEvent evt, CancellationToken ct = default);

    void Complete();

    IAsyncEnumerable<WorkflowRunEvent> ReadAllAsync(CancellationToken ct = default);
}

public sealed class WorkflowRunEventSinkBackpressureException : InvalidOperationException
{
    public WorkflowRunEventSinkBackpressureException()
        : base("Workflow run event channel is full.")
    {
    }
}

public sealed class WorkflowRunEventSinkCompletedException : InvalidOperationException
{
    public WorkflowRunEventSinkCompletedException()
        : base("Workflow run event channel is completed.")
    {
    }
}

public sealed class WorkflowRunEventChannel : IWorkflowRunEventSink
{
    private readonly Channel<WorkflowRunEvent> _channel;
    private readonly BoundedChannelFullMode _fullMode;

    public WorkflowRunEventChannel(
        int capacity = 1024,
        BoundedChannelFullMode fullMode = BoundedChannelFullMode.Wait)
    {
        var resolvedCapacity = capacity > 0 ? capacity : 1024;
        _fullMode = fullMode;
        _channel = Channel.CreateBounded<WorkflowRunEvent>(new BoundedChannelOptions(resolvedCapacity)
        {
            FullMode = fullMode,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public void Push(WorkflowRunEvent evt)
    {
        if (!_channel.Writer.TryWrite(evt))
            throw ResolveWriteFailureException();
    }

    public async ValueTask PushAsync(WorkflowRunEvent evt, CancellationToken ct = default)
    {
        if (_fullMode == BoundedChannelFullMode.Wait)
        {
            try
            {
                await _channel.Writer.WriteAsync(evt, ct);
            }
            catch (ChannelClosedException)
            {
                throw new WorkflowRunEventSinkCompletedException();
            }

            return;
        }

        if (!_channel.Writer.TryWrite(evt))
            throw ResolveWriteFailureException();
    }

    public void Complete() => _channel.Writer.TryComplete();

    public async IAsyncEnumerable<WorkflowRunEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
            yield return evt;
    }

    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private Exception ResolveWriteFailureException()
    {
        return _channel.Reader.Completion.IsCompleted
            ? new WorkflowRunEventSinkCompletedException()
            : new WorkflowRunEventSinkBackpressureException();
    }
}
