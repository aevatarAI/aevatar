namespace Aevatar.Foundation.Abstractions.TypeSystem;

/// <summary>
/// Maps stable business <c>AgentKind</c> tokens to runtime
/// <see cref="AgentImplementation"/> handles. Replaces CLR-name reflection
/// (<c>Type.GetType</c> + AppDomain scan) as the activation lookup path
/// for <c>RuntimeActorGrain</c>, so persisted authoritative state never
/// references runtime-incidental implementation details.
/// </summary>
public interface IAgentKindRegistry
{
    /// <summary>
    /// Resolves a primary or legacy kind token to its current
    /// implementation. Throws <see cref="UnknownAgentKindException"/> when
    /// no implementation is registered for the given kind.
    /// </summary>
    AgentImplementation Resolve(string kind);

    /// <summary>
    /// Best-effort reverse lookup used during the Phase 1 transition: given
    /// a persisted CLR full name (from legacy <c>RuntimeActorGrainState.AgentTypeName</c>),
    /// return the canonical kind that currently owns it. Matches both the
    /// implementation's current <c>Type.FullName</c> and any
    /// <c>[LegacyClrTypeName]</c> aliases declared on registered classes.
    /// </summary>
    bool TryResolveKindByClrTypeName(string clrFullName, out string kind);

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
               "Either decorate the implementation class with [GAgent(\"" + kind + "\")] " +
               "or declare a [LegacyAgentKind(\"" + kind + "\")] alias on the class that handles it now.")
    {
        Kind = kind;
    }

    public string Kind { get; }
}
