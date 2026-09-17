namespace Aevatar.Foundation.Abstractions.TypeSystem;

/// <summary>
/// Declares the stable business kind for an agent class. Persisted state
/// references the kind, not the CLR type name; the registry maps kind to
/// implementation at activation time.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GAgentAttribute : Attribute
{
    public GAgentAttribute(string kind)
    {
        AgentKindToken.Validate(kind, nameof(kind));
        Kind = kind;
    }

    public string Kind { get; }

    /// <summary>
    /// Highest persisted business-state schema version this implementation can
    /// activate. Version zero is the legacy protobuf-compatible baseline.
    /// </summary>
    public int StateSchemaVersion { get; set; }
}
