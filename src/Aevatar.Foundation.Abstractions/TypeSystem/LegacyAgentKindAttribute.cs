namespace Aevatar.Foundation.Abstractions.TypeSystem;

/// <summary>
/// Declares that this agent class also serves a previously-used kind token.
/// Used during identity-only refactors (rename, move, class split) so
/// persisted state pointing at the old kind resolves to the new class
/// without state mutation.
/// </summary>
/// <remarks>
/// Aliases multiple to one: e.g. a new <c>SkillDefinitionGAgent</c> can carry
/// <c>[GAgent("scheduled.skill-definition")]</c> and
/// <c>[LegacyAgentKind("scheduled.skill-runner")]</c> so historical
/// <c>scheduled.skill-runner</c> activations land here. The registry rejects
/// duplicate legacy aliases across distinct primary kinds.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class LegacyAgentKindAttribute : Attribute
{
    public LegacyAgentKindAttribute(string legacyKind)
    {
        AgentKindToken.Validate(legacyKind, nameof(legacyKind));
        LegacyKind = legacyKind;
    }

    public string LegacyKind { get; }
}
