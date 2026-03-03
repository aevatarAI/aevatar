using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aevatar.Scripting.Abstractions;

namespace Aevatar.Scripting.Abstractions.Evolution;

public interface IScriptEvolutionProjectionLease
{
    string ActorId { get; }

    string ProposalId { get; }
}

public interface IScriptEvolutionProjectionLifecyclePort
{
    bool ProjectionEnabled { get; }

    Task<IScriptEvolutionProjectionLease?> EnsureActorProjectionAsync(
        string sessionActorId,
        string proposalId,
        CancellationToken ct = default);

    Task AttachLiveSinkAsync(
        IScriptEvolutionProjectionLease lease,
        IScriptEvolutionEventSink sink,
        CancellationToken ct = default);

    Task DetachLiveSinkAsync(
        IScriptEvolutionProjectionLease lease,
        IScriptEvolutionEventSink sink,
        CancellationToken ct = default);

    Task ReleaseActorProjectionAsync(
        IScriptEvolutionProjectionLease lease,
        CancellationToken ct = default);
}

public interface IScriptEvolutionEventSink : IAsyncDisposable
{
    void Push(ScriptEvolutionSessionCompletedEvent evt);

    ValueTask PushAsync(ScriptEvolutionSessionCompletedEvent evt, CancellationToken ct = default);

    void Complete();

    IAsyncEnumerable<ScriptEvolutionSessionCompletedEvent> ReadAllAsync(CancellationToken ct = default);
}

public sealed class ScriptEvolutionEventSinkBackpressureException : InvalidOperationException
{
    public ScriptEvolutionEventSinkBackpressureException()
        : base("Script evolution event channel is full.")
    {
    }
}

public sealed class ScriptEvolutionEventSinkCompletedException : InvalidOperationException
{
    public ScriptEvolutionEventSinkCompletedException()
        : base("Script evolution event channel is completed.")
    {
    }
}

public sealed class ScriptEvolutionEventChannel : IScriptEvolutionEventSink
{
    private readonly Channel<ScriptEvolutionSessionCompletedEvent> _channel;
    private readonly BoundedChannelFullMode _fullMode;

    public ScriptEvolutionEventChannel(
        int capacity = 256,
        BoundedChannelFullMode fullMode = BoundedChannelFullMode.Wait)
    {
        var resolvedCapacity = capacity > 0 ? capacity : 256;
        _fullMode = fullMode;
        _channel = Channel.CreateBounded<ScriptEvolutionSessionCompletedEvent>(new BoundedChannelOptions(resolvedCapacity)
        {
            FullMode = fullMode,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public void Push(ScriptEvolutionSessionCompletedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!_channel.Writer.TryWrite(evt))
            throw ResolveWriteFailureException();
    }

    public async ValueTask PushAsync(ScriptEvolutionSessionCompletedEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (_fullMode == BoundedChannelFullMode.Wait)
        {
            try
            {
                await _channel.Writer.WriteAsync(evt, ct);
            }
            catch (ChannelClosedException)
            {
                throw new ScriptEvolutionEventSinkCompletedException();
            }

            return;
        }

        if (!_channel.Writer.TryWrite(evt))
            throw ResolveWriteFailureException();
    }

    public void Complete() => _channel.Writer.TryComplete();

    public async IAsyncEnumerable<ScriptEvolutionSessionCompletedEvent> ReadAllAsync(
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
            ? new ScriptEvolutionEventSinkCompletedException()
            : new ScriptEvolutionEventSinkBackpressureException();
    }
}
