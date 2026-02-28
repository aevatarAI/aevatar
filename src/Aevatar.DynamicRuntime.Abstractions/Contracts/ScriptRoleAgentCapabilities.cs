using System.Threading;

namespace Aevatar.DynamicRuntime.Abstractions.Contracts;

public interface IScriptRoleAgentLlmClient
{
    Task<string> ChatAsync(
        string prompt,
        string? systemPrompt = null,
        string? providerName = null,
        string? model = null,
        CancellationToken ct = default);
}

public interface IScriptRoleAgentClient
{
    Task<string> ChatAsync(
        string prompt,
        string? systemPrompt = null,
        string? providerName = null,
        string? model = null,
        CancellationToken ct = default);
}

public interface IScriptRoleAgentCapabilities
{
    IScriptRoleAgentClient RoleAgent { get; }
    IScriptRoleAgentLlmClient LLM { get; }
}

public static class ScriptRoleAgentContext
{
    private static readonly AsyncLocal<IScriptRoleAgentCapabilities?> AmbientCapabilities = new();

    public static IScriptRoleAgentCapabilities Current =>
        AmbientCapabilities.Value ?? throw new InvalidOperationException("Script role agent capabilities are not available in current execution context.");

    public static IDisposable BeginScope(IScriptRoleAgentCapabilities capabilities)
    {
        if (capabilities == null)
            throw new ArgumentNullException(nameof(capabilities));

        var previous = AmbientCapabilities.Value;
        AmbientCapabilities.Value = capabilities;
        return new Scope(() => AmbientCapabilities.Value = previous);
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
