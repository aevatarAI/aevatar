using System.Threading;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.DynamicRuntime.Abstractions.Contracts;

public interface IScriptRoleAgentRuntime
{
    EventEnvelope CurrentEnvelope { get; }
    IReadOnlyList<EventEnvelope> PublishedEnvelopes { get; }
    Any? UpdatedState { get; }
    Task<string> ChatAsync(
        string prompt,
        string? systemPrompt = null,
        string? providerName = null,
        string? model = null,
        CancellationToken ct = default);

    Task PublishAsync(
        IMessage payload,
        EventDirection direction = EventDirection.Self,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    Task<Any?> GetStateAsync(CancellationToken ct = default);

    Task SetStateAsync(IMessage value, CancellationToken ct = default);
}

public static class ScriptRoleAgentContext
{
    private static readonly AsyncLocal<IScriptRoleAgentRuntime?> AmbientRuntime = new();

    public static IScriptRoleAgentRuntime Current =>
        AmbientRuntime.Value ?? throw new InvalidOperationException("Script role agent runtime is not available in current execution context.");

    public static IDisposable BeginScope(IScriptRoleAgentRuntime runtime)
    {
        if (runtime == null)
            throw new ArgumentNullException(nameof(runtime));

        var previous = AmbientRuntime.Value;
        AmbientRuntime.Value = runtime;
        return new Scope(() => AmbientRuntime.Value = previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly Action _disposeAction;
        private int _disposed;

        public Scope(Action disposeAction)
        {
            _disposeAction = disposeAction;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _disposeAction();
        }
    }
}

public static class ScriptRoleAgentRuntimeExtensions
{
    public static async Task<TState?> GetStateAsync<TState>(
        this IScriptRoleAgentRuntime runtime,
        CancellationToken ct = default)
        where TState : class, IMessage<TState>, new()
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var any = await runtime.GetStateAsync(ct);
        var descriptor = new TState().Descriptor;
        return any != null && any.Is(descriptor)
            ? any.Unpack<TState>()
            : null;
    }
}
