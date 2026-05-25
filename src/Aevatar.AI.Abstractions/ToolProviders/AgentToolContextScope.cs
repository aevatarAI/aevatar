namespace Aevatar.AI.Abstractions.ToolProviders;

// Refactor (iter24/cluster-002-agent-tool-context-generic-metadata-bag):
//   Old pattern: callers pushed raw Metadata dictionaries into AsyncLocal.
//   New principle: AsyncLocal carries a typed execution context; this helper only scopes push/restore.
public sealed class AgentToolContextScope : IDisposable
{
    private readonly AgentToolExecutionContext? _previous;

    private AgentToolContextScope(AgentToolExecutionContext? previous)
    {
        _previous = previous;
    }

    public static AgentToolContextScope Push(AgentToolExecutionContext? context)
    {
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = context;
        return new AgentToolContextScope(previous);
    }

    public void Dispose()
    {
        AgentToolRequestContext.Current = _previous;
    }
}
