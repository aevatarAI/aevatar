namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Default <see cref="IInteractiveReplyCollector"/> backed by <see cref="AsyncLocal{T}"/>
/// so every async turn sees its own capture slot without explicit plumbing.
/// </summary>
public sealed class AsyncLocalInteractiveReplyCollector : IInteractiveReplyCollector
{
    private readonly AsyncLocal<Scope?> _current = new();

    /// <inheritdoc />
    public IDisposable BeginScope()
    {
        var scope = new Scope(this, _current.Value);
        _current.Value = scope;
        return scope;
    }

    /// <inheritdoc />
    public bool Capture(MessageContent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        var scope = _current.Value;
        if (scope is null)
            return false;

        scope.Intent = intent.Clone();
        return true;
    }

    /// <inheritdoc />
    public MessageContent? TryTake()
    {
        var scope = _current.Value;
        if (scope is null)
            return null;

        var captured = scope.Intent;
        scope.Intent = null;
        return captured;
    }

    private sealed class Scope : IDisposable
    {
        private readonly AsyncLocalInteractiveReplyCollector _owner;
        private readonly Scope? _parent;
        private bool _disposed;

        public Scope(AsyncLocalInteractiveReplyCollector owner, Scope? parent)
        {
            _owner = owner;
            _parent = parent;
        }

        public MessageContent? Intent { get; set; }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _owner._current.Value = _parent;
        }
    }
}
