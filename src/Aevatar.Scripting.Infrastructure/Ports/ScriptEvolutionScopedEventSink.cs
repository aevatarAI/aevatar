using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Scripting.Abstractions;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ScriptEvolutionScopedEventSink : IEventSink<ScriptEvolutionSessionCompletedEvent>
{
    private readonly string _proposalId;
    private readonly IEventSink<ScriptEvolutionSessionCompletedEvent> _inner;

    public ScriptEvolutionScopedEventSink(
        string proposalId,
        IEventSink<ScriptEvolutionSessionCompletedEvent> inner)
    {
        _proposalId = string.IsNullOrWhiteSpace(proposalId)
            ? throw new ArgumentException("Proposal id is required.", nameof(proposalId))
            : proposalId;
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public void Push(ScriptEvolutionSessionCompletedEvent evt)
    {
        if (Matches(evt))
            _inner.Push(evt);
    }

    public ValueTask PushAsync(
        ScriptEvolutionSessionCompletedEvent evt,
        CancellationToken ct = default)
    {
        return Matches(evt)
            ? _inner.PushAsync(evt, ct)
            : ValueTask.CompletedTask;
    }

    public void Complete() => _inner.Complete();

    public IAsyncEnumerable<ScriptEvolutionSessionCompletedEvent> ReadAllAsync(CancellationToken ct = default) =>
        _inner.ReadAllAsync(ct);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private bool Matches(ScriptEvolutionSessionCompletedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return string.Equals(evt.ProposalId ?? string.Empty, _proposalId, StringComparison.Ordinal);
    }
}
