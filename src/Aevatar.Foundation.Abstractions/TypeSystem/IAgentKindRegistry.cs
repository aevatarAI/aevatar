namespace Aevatar.Foundation.Abstractions.TypeSystem;

/// <summary>
/// Maps stable business <c>AgentKind</c> tokens to runtime
/// <see cref="AgentImplementation"/> handles. Persisted actor identity
/// resolves by primary kind only; CLR type names are diagnostics, not
/// business identity.
/// </summary>
public interface IAgentKindRegistry
{
    /// <summary>
    /// Resolves a primary kind token to its current
    /// implementation. Throws <see cref="UnknownAgentKindException"/> when
    /// no implementation is registered for the given kind.
    /// </summary>
    AgentImplementation Resolve(string kind);

    /// <summary>
    /// Resolves a primary kind token to its current
    /// implementation without throwing when no implementation is registered.
    /// </summary>
    bool TryResolve(string kind, out AgentImplementation implementation);

    /// <summary>
    /// Resolves the primary kind for an in-process agent type. This is only
    /// for typed convenience APIs such as <c>CreateAsync&lt;TAgent&gt;</c>;
    /// persisted identity still stores the returned kind.
    /// </summary>
    bool TryGetKindForAgentType(Type agentType, out string kind);

    /// <summary>
    /// Inverse of <see cref="Resolve(string)"/> for diagnostics: given an
    /// implementation handle (or its kind), return the canonical kind. Used
    /// by tests and migration tooling; not on the activation hot path.
    /// </summary>
    bool TryGetKind(AgentImplementation implementation, out string kind);
}

/// <summary>
/// Thrown when the registry cannot resolve the requested kind.
/// </summary>
public sealed class UnknownAgentKindException : InvalidOperationException
{
    public UnknownAgentKindException(string kind)
        : base($"No agent implementation is registered for kind '{kind}'. " +
               "Decorate the implementation class with [GAgent(\"" + kind + "\")] and register it with IAgentKindRegistry.")
    {
        Kind = kind;
    }

    public string Kind { get; }
}
