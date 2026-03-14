using Aevatar.Scripting.Abstractions.Behaviors;

namespace Aevatar.Scripting.Core.Runtime;

public sealed class ScriptBehaviorArtifact : IAsyncDisposable
{
    private readonly Func<IScriptBehaviorBridge> _createBehavior;
    private readonly Func<ValueTask> _disposeAsync;
    private int _disposed;

    public ScriptBehaviorArtifact(
        string scriptId,
        string revision,
        string packageHash,
        ScriptBehaviorDescriptor descriptor,
        ScriptGAgentContract contract,
        Func<IScriptBehaviorBridge> createBehavior,
        Func<ValueTask>? dispose = null)
    {
        ScriptId = scriptId ?? string.Empty;
        Revision = revision ?? string.Empty;
        PackageHash = packageHash ?? string.Empty;
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        _createBehavior = createBehavior ?? throw new ArgumentNullException(nameof(createBehavior));
        _disposeAsync = dispose ?? (() => ValueTask.CompletedTask);
    }

    public string ScriptId { get; }

    public string Revision { get; }

    public string PackageHash { get; }

    public ScriptBehaviorDescriptor Descriptor { get; }

    public ScriptGAgentContract Contract { get; }

    public IScriptBehaviorBridge CreateBehavior()
    {
        ThrowIfDisposed();
        var behavior = _createBehavior();
        return behavior ?? throw new InvalidOperationException(
            $"Script behavior artifact `{ScriptId}:{Revision}` returned a null behavior instance.");
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return ValueTask.CompletedTask;

        return _disposeAsync();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(ScriptBehaviorArtifact));
    }
}
