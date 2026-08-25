namespace Aevatar.Foundation.Abstractions.TypeSystem;

/// <summary>
/// Static metadata describing how a kind is registered. The runtime CLR type
/// name is exposed for diagnostics only; activation goes through
/// <see cref="AgentImplementation.Factory"/> after primary kind resolution.
/// </summary>
public sealed record AgentImplementationMetadata(
    string Kind,
    string ImplementationClrTypeName,
    int StateSchemaVersion = 0);
