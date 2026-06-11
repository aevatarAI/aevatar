using System.Text.RegularExpressions;

namespace Aevatar.Foundation.Abstractions.TypeSystem;

/// <summary>
/// Stable, business-meaningful identifier for a kind of agent.
/// Persisted in <c>RuntimeActorIdentity.Kind</c> and resolved via
/// <see cref="IAgentKindRegistry"/>; intentionally decoupled from the
/// runtime CLR type name so renames / moves / splits do not mutate
/// authoritative state.
/// </summary>
/// <remarks>
/// Kinds are unversioned: <c>scheduled.skill-runner-v2</c> is rejected
/// because schema evolution within a kind goes through proto3 field rules
/// or the state-version migration mechanism (see issue #500), not through
/// kind rename.
/// </remarks>
public static class AgentKindToken
{
    /// <summary>
    /// Two or more dot-separated segments, each lowercase letters / digits,
    /// with hyphens allowed inside (but not leading) the non-prefix segments.
    /// </summary>
    public const string FormatPattern = "^[a-z0-9]+(\\.[a-z0-9]+(-[a-z0-9]+)*)+$";

    /// <summary>
    /// Tail used by versioned tokens (forbidden) — captured separately so the
    /// rejection error can be specific instead of "format mismatch".
    /// </summary>
    public const string ForbiddenVersionedTailPattern = "-v\\d+$";

    private static readonly Regex Format = new(FormatPattern, RegexOptions.Compiled);
    private static readonly Regex VersionedTail = new(ForbiddenVersionedTailPattern, RegexOptions.Compiled);

    public static bool IsValid(string kind) =>
        !string.IsNullOrEmpty(kind) && Format.IsMatch(kind) && !VersionedTail.IsMatch(kind);

    public static void Validate(string kind, string parameterName = "kind")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind, parameterName);

        if (VersionedTail.IsMatch(kind))
            throw new ArgumentException(
                $"Agent kind '{kind}' is forbidden: kinds are never versioned. " +
                "Schema evolution within a kind goes through proto3 field rules or state-version migration, not through kind rename.",
                parameterName);

        if (!Format.IsMatch(kind))
            throw new ArgumentException(
                $"Agent kind '{kind}' must use the format '<module>.<entity>' " +
                "(lowercase letters, digits and hyphens; hyphens not at segment edges; multiple dot-separated segments allowed). " +
                "Examples: 'scheduled.skill-runner', 'channels.bot-registration'.",
                parameterName);
    }
}
