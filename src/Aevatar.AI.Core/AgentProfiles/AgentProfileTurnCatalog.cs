using System.Collections.Frozen;
using System.Text;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.AgentProfiles;

public enum AgentProfileTurnDiagnosticCode
{
    ProfileInvalid = 0,
    RouteToolSetUnavailable = 1,
    ToolSetUnavailable = 2,
    ToolDiscoveryFailed = 3,
    ToolNameCollision = 4,
    ToolCapabilityRejected = 5,
    AliasMatched = 6,
    ClassifierMatched = 7,
    ClassifierNoMatch = 8,
    ClassifierFailed = 9,
    ShadowCandidate = 10,
    ExactSkillFetchFailed = 11,
    ExactSkillIdentityMismatch = 12,
    SelectedSkillBodyInvalid = 13,
}

public sealed record AgentProfileTurnDiagnostic(AgentProfileTurnDiagnosticCode Code, string Detail);

public sealed class AgentProfileTurnCatalog
{
    public const int MaximumDiagnostics = 16;
    private const int MaximumDiagnosticDetailUtf8Bytes = 256;

    public AgentProfileTurnCatalog(
        IEnumerable<string> finalAllowedToolNames,
        ProfileRoutingPromptLayer? profilePromptLayer,
        SelectedSkillPromptLayer? selectedSkillPromptLayer,
        string? selectedIntentId,
        string? candidateIntentId,
        IReadOnlyList<AgentProfileTurnDiagnostic>? diagnostics = null,
        IEnumerable<IAgentTool>? routeOwnedTools = null)
    {
        ArgumentNullException.ThrowIfNull(finalAllowedToolNames);

        FinalAllowedToolNames = finalAllowedToolNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        ToolVisibility = new AgentToolVisibilityScope(FinalAllowedToolNames);
        RouteOwnedTools = FreezeTools(routeOwnedTools);
        ProfilePromptLayer = profilePromptLayer;
        SelectedSkillPromptLayer = selectedSkillPromptLayer;
        SelectedIntentId = Normalize(selectedIntentId);
        CandidateIntentId = Normalize(candidateIntentId);
        Diagnostics = CopyDiagnostics(diagnostics);
    }

    public IReadOnlySet<string> FinalAllowedToolNames { get; }

    public AgentToolVisibilityScope ToolVisibility { get; }

    public IReadOnlyDictionary<string, IAgentTool> RouteOwnedTools { get; }

    public ProfileRoutingPromptLayer? ProfilePromptLayer { get; }

    public SelectedSkillPromptLayer? SelectedSkillPromptLayer { get; }

    public string? SelectedIntentId { get; }

    public string? CandidateIntentId { get; }

    public IReadOnlyList<AgentProfileTurnDiagnostic> Diagnostics { get; }

    private static IReadOnlyList<AgentProfileTurnDiagnostic> CopyDiagnostics(
        IReadOnlyList<AgentProfileTurnDiagnostic>? diagnostics)
    {
        if (diagnostics is null or { Count: 0 })
            return [];

        var bounded = diagnostics
            .Take(MaximumDiagnostics)
            .Select(static diagnostic => diagnostic with
            {
                Detail = TruncateUtf8(diagnostic.Detail ?? string.Empty, MaximumDiagnosticDetailUtf8Bytes),
            })
            .ToList();
        return bounded.AsReadOnly();
    }

    private static IReadOnlyDictionary<string, IAgentTool> FreezeTools(IEnumerable<IAgentTool>? tools)
    {
        if (tools is null)
            return FrozenDictionary<string, IAgentTool>.Empty;

        var exactTools = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        var collisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools.Where(static tool => !string.IsNullOrWhiteSpace(tool.Name)))
        {
            var name = tool.Name.Trim();
            if (collisions.Contains(name))
                continue;
            if (!exactTools.TryGetValue(name, out var existing))
            {
                exactTools.Add(name, tool);
                continue;
            }
            if (ReferenceEquals(existing, tool))
                continue;

            exactTools.Remove(name);
            collisions.Add(name);
        }

        return exactTools.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string TruncateUtf8(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
            return value;

        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximumBytes)
                break;

            builder.Append(rune);
            bytes += rune.Utf8SequenceLength;
        }

        return builder.ToString();
    }
}
