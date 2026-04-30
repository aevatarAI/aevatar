namespace Aevatar.Foundation.Abstractions.TypeSystem;

/// <summary>
/// Static metadata describing how a kind is registered. The runtime CLR type
/// name is exposed for diagnostics + lazy backward-compatibility lookup, not
/// for activation — activation goes through <see cref="AgentImplementation.Factory"/>.
/// </summary>
public sealed record AgentImplementationMetadata(
    string Kind,
    string ImplementationClrTypeName,
    IReadOnlyList<string> LegacyKinds,
    IReadOnlyList<string> LegacyClrTypeNames);
