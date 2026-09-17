using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// Low-cardinality measurements for the request-local tool-catalog pipeline. Catalog digests,
/// profile identities, and intent identities are trace attributes only; they are deliberately not
/// metric tags.
/// </summary>
public static class AgentTurnToolCatalogTelemetry
{
    public const string MeterName = "Aevatar.GenAI";
    public const string ActivitySourceName = "Aevatar.GenAI";
    public const string RegisteredCounterName = "aevatar.agent_turn_tool_catalog.registered";
    public const string DiscoveredCounterName = "aevatar.agent_turn_tool_catalog.discovered";
    public const string AuthorityCounterName = "aevatar.agent_turn_tool_catalog.authority";
    public const string FinalCounterName = "aevatar.agent_turn_tool_catalog.final";
    public const string ForwardedCounterName = "aevatar.agent_turn_tool_catalog.forwarded";
    public const string FilteredCounterName = "aevatar.agent_turn_tool_catalog.filtered";
    public const string RejectedCounterName = "aevatar.agent_turn_tool_catalog.rejected";
    public const string RestrictedEmptyCounterName = "aevatar.agent_turn_tool_catalog.restricted_empty";
    public const string SchemaBytesCounterName = "aevatar.agent_turn_tool_catalog.schema_bytes";
    public const string DegradationCounterName = "aevatar.agent_turn_tool_catalog.degradation";
    public const string ToolRoundCounterName = "aevatar.agent_turn_tool_catalog.tool_round";
    public const string OutcomeCounterName = "aevatar.agent_turn_tool_catalog.outcome";
    public const string TimeToFirstOutputHistogramName =
        "aevatar.agent_turn_tool_catalog.time_to_first_output";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
    private static readonly Counter<long> Registered = Meter.CreateCounter<long>(
        RegisteredCounterName,
        unit: "{tool}",
        description: "Raw request-local tools returned by the selected registered sources.");
    private static readonly Counter<long> Discovered = Meter.CreateCounter<long>(
        DiscoveredCounterName,
        unit: "{tool}",
        description: "Unique exact tools produced by request-local discovery.");
    private static readonly Counter<long> Authority = Meter.CreateCounter<long>(
        AuthorityCounterName,
        unit: "{tool}",
        description: "Exact tools presented to the final authority and policy intersection.");
    private static readonly Counter<long> Final = Meter.CreateCounter<long>(
        FinalCounterName,
        unit: "{tool}",
        description: "Aevatar-owned exact tools frozen into final turn catalogs.");
    private static readonly Counter<long> Forwarded = Meter.CreateCounter<long>(
        ForwardedCounterName,
        unit: "{tool}",
        description: "Caller-declared tools forwarded without Aevatar execution authority.");
    private static readonly Counter<long> Filtered = Meter.CreateCounter<long>(
        FilteredCounterName,
        unit: "{tool}",
        description: "Candidate exact tools removed before final catalog materialization.");
    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>(
        RejectedCounterName,
        unit: "{catalog}",
        description: "Tool discoveries or catalogs rejected fail closed by a typed reason.");
    private static readonly Counter<long> RestrictedEmpty = Meter.CreateCounter<long>(
        RestrictedEmptyCounterName,
        unit: "{catalog}",
        description: "Explicit restricted-empty catalog proofs.");
    private static readonly Counter<long> SchemaBytes = Meter.CreateCounter<long>(
        SchemaBytesCounterName,
        unit: "By",
        description: "Canonical model-visible schema bytes by owned or forwarded kind.");
    private static readonly Counter<long> Degradation = Meter.CreateCounter<long>(
        DegradationCounterName,
        unit: "{event}",
        description: "Bounded typed catalog degradation diagnostics.");
    private static readonly Counter<long> ToolRound = Meter.CreateCounter<long>(
        ToolRoundCounterName,
        unit: "{round}",
        description: "Model tool-loop rounds executed under one frozen catalog.");
    private static readonly Counter<long> Outcome = Meter.CreateCounter<long>(
        OutcomeCounterName,
        unit: "{round}",
        description: "Terminal model-round outcomes under a frozen catalog.");
    private static readonly Histogram<double> TimeToFirstOutput = Meter.CreateHistogram<double>(
        TimeToFirstOutputHistogramName,
        unit: "ms",
        description: "Time from provider invocation to its first model output delta.");

    public static void RecordDiscovery(
        int registeredToolCount,
        int discoveredToolCount,
        string outcome,
        string denyReason = "")
    {
        var normalizedOutcome = NormalizeOutcome(outcome);
        var tags = StageTags("discovery", "unspecified", normalizedOutcome);
        Registered.Add(Math.Max(0, registeredToolCount), tags);
        Discovered.Add(Math.Max(0, discoveredToolCount), tags);
        SetCurrentTraceTags(
            registeredToolCount,
            discoveredToolCount,
            finalToolCount: null,
            schemaBytes: null,
            catalogDigest: null,
            profileIdentity: null,
            intentIdentity: null,
            turnClass: "unspecified",
            denyReason);
    }

    public static void RecordCatalog(
        AgentTurnToolCatalogProof proof,
        int authorityToolCount,
        int filteredToolCount,
        IReadOnlyList<AgentProfileTurnDiagnostic> diagnostics,
        string? profileIdentity,
        string? intentIdentity,
        bool hasUnresolvedConnectedServiceSelectors)
    {
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var turnClass = ResolveTurnClass(proof);
        var tags = StageTags("final", turnClass, "accepted");
        Authority.Add(Math.Max(0, authorityToolCount), tags);
        Final.Add(proof.ToolCount, tags);
        Filtered.Add(Math.Max(0, filteredToolCount), tags);
        SchemaBytes.Add(proof.SchemaBytes, tags.With("aevatar.agent_turn_tool_catalog.schema_kind", "owned"));
        if (proof.ToolCount == 0)
            RestrictedEmpty.Add(1, tags);

        foreach (var diagnostic in diagnostics.Where(static diagnostic => IsDegradation(diagnostic.Code)))
        {
            Degradation.Add(1, tags.With(
                "aevatar.agent_turn_tool_catalog.degradation_reason",
                diagnostic.Code.ToString()));
        }

        if (hasUnresolvedConnectedServiceSelectors)
        {
            Degradation.Add(1, tags.With(
                "aevatar.agent_turn_tool_catalog.degradation_reason",
                "UnresolvedConnectedServiceSelectors"));
        }

        using var activity = ActivitySource.StartActivity(
            "agent_turn_tool_catalog materialize",
            ActivityKind.Internal);
        var target = activity ?? Activity.Current;
        SetTraceTags(
            target,
            registeredToolCount: null,
            discoveredToolCount: null,
            proof.ToolCount,
            proof.SchemaBytes,
            proof.CatalogDigest,
            profileIdentity,
            intentIdentity,
            turnClass,
            denyReason: string.Empty);
        target?.SetTag(
            "aevatar.agent_turn_tool_catalog.authority_count",
            Math.Max(0, authorityToolCount));
        target?.SetTag(
            "aevatar.agent_turn_tool_catalog.filtered_count",
            Math.Max(0, filteredToolCount));
        target?.SetTag(
            "aevatar.agent_turn_tool_catalog.restricted_empty",
            proof.ToolCount == 0);
    }

    public static void RecordRestrictedEmptyProof(AgentTurnToolCatalogProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var turnClass = ResolveTurnClass(proof);
        var tags = StageTags("final", turnClass, "accepted");
        RestrictedEmpty.Add(1, tags);
        using var activity = ActivitySource.StartActivity(
            "agent_turn_tool_catalog restricted_empty",
            ActivityKind.Internal);
        SetTraceTags(
            activity ?? Activity.Current,
            registeredToolCount: null,
            discoveredToolCount: null,
            finalToolCount: 0,
            schemaBytes: 0,
            proof.CatalogDigest,
            profileIdentity: null,
            intentIdentity: null,
            turnClass,
            denyReason: string.Empty);
    }

    public static void RecordForwarded(
        int forwardedToolCount,
        int forwardedSchemaBytes,
        string phase)
    {
        var normalizedPhase = NormalizePhase(phase);
        var tags = StageTags(normalizedPhase, "responses", "accepted");
        Forwarded.Add(Math.Max(0, forwardedToolCount), tags);
        SchemaBytes.Add(
            Math.Max(0, forwardedSchemaBytes),
            tags.With("aevatar.agent_turn_tool_catalog.schema_kind", "forwarded"));
        Activity.Current?.SetTag(
            "aevatar.agent_turn_tool_catalog.forwarded_count",
            Math.Max(0, forwardedToolCount));
        Activity.Current?.SetTag(
            "aevatar.agent_turn_tool_catalog.forwarded_schema_bytes",
            Math.Max(0, forwardedSchemaBytes));
    }

    public static void RecordShadowCandidate(
        AgentTurnToolCatalogProof proof,
        string? profileIdentity,
        string? intentIdentity)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var turnClass = ResolveTurnClass(proof);
        var tags = StageTags("shadow", turnClass, "accepted");
        Authority.Add(proof.ToolCount, tags);
        SchemaBytes.Add(
            proof.SchemaBytes,
            tags.With("aevatar.agent_turn_tool_catalog.schema_kind", "shadow_candidate"));
        using var activity = ActivitySource.StartActivity(
            "agent_turn_tool_catalog shadow_candidate",
            ActivityKind.Internal);
        var target = activity ?? Activity.Current;
        SetTraceTags(
            target,
            registeredToolCount: null,
            discoveredToolCount: null,
            proof.ToolCount,
            proof.SchemaBytes,
            proof.CatalogDigest,
            profileIdentity,
            intentIdentity,
            turnClass,
            denyReason: string.Empty);
        target?.SetTag("aevatar.agent_turn_tool_catalog.shadow_candidate", true);
    }

    public static void RecordRejected(string reason, string stage)
    {
        var normalizedStage = NormalizePhase(stage);
        var normalizedReason = NormalizeReason(reason);
        Rejected.Add(
            1,
            StageTags(normalizedStage, "unspecified", "rejected").With(
                "aevatar.agent_turn_tool_catalog.deny_reason",
                normalizedReason));
        Activity.Current?.SetTag(
            "aevatar.agent_turn_tool_catalog.deny_reason",
            normalizedReason);
    }

    public static void RecordToolRound(AgentTurnToolCatalogProof? proof, int round)
    {
        var turnClass = ResolveTurnClass(proof);
        ToolRound.Add(1, StageTags("model_round", turnClass, "started"));
        Activity.Current?.SetTag("aevatar.agent_turn_tool_catalog.tool_round", Math.Max(0, round));
    }

    public static void RecordTimeToFirstOutput(
        AgentTurnToolCatalogProof? proof,
        TimeSpan elapsed)
    {
        var turnClass = ResolveTurnClass(proof);
        TimeToFirstOutput.Record(
            Math.Max(0, elapsed.TotalMilliseconds),
            StageTags("model_round", turnClass, "first_output"));
    }

    public static void RecordOutcome(AgentTurnToolCatalogProof? proof, string outcome)
    {
        var normalizedOutcome = NormalizeOutcome(outcome);
        var turnClass = ResolveTurnClass(proof);
        Outcome.Add(1, StageTags("model_round", turnClass, normalizedOutcome));
        Activity.Current?.SetTag(
            "aevatar.agent_turn_tool_catalog.task_outcome",
            normalizedOutcome);
    }

    private static bool IsDegradation(AgentProfileTurnDiagnosticCode code) =>
        code is not AgentProfileTurnDiagnosticCode.AliasMatched and
            not AgentProfileTurnDiagnosticCode.ClassifierMatched and
            not AgentProfileTurnDiagnosticCode.ShadowCandidate;

    /// <remarks>
    /// The connected class is decided by what the turn actually injected, not by the budget shape.
    /// Every sealed profile budget carries the connected read/write caps as defence in depth, so a
    /// budget-shape test would report ordinary profiled chat as connected and make the two
    /// indistinguishable in the very dimension the rollout comparison needs.
    /// </remarks>
    private static string ResolveTurnClass(AgentTurnToolCatalogProof? proof)
    {
        var budget = proof?.Budget;
        if (budget is null)
            return "unrestricted_legacy";
        if (budget == AgentTurnToolCatalogBudget.Voice)
            return "voice";
        if (budget == AgentTurnToolCatalogBudget.WorkflowOrAdmin)
            return "workflow_or_admin";
        if (budget == AgentTurnToolCatalogBudget.Coding)
            return "coding";
        if (proof!.ConnectedReadToolCount > 0 || proof.ConnectedWriteToolCount > 0)
            return "connected";

        return "ordinary";
    }

    private static string NormalizeOutcome(string? outcome) => outcome?.Trim().ToLowerInvariant() switch
    {
        "accepted" => "accepted",
        "started" => "started",
        "first_output" => "first_output",
        "stop" => "stop",
        "tool_calls" => "tool_calls",
        "length" => "length",
        "content_filter" => "content_filter",
        "cancelled" => "cancelled",
        "canceled" => "cancelled",
        "failed" => "failed",
        "rejected" => "rejected",
        "success" => "success",
        _ => "other",
    };

    private static string NormalizePhase(string? phase) => phase?.Trim().ToLowerInvariant() switch
    {
        "discovery" => "discovery",
        "final" => "final",
        "ingress" => "ingress",
        "runtime" => "runtime",
        "model_round" => "model_round",
        "shadow" => "shadow",
        _ => "other",
    };

    private static string NormalizeReason(string? reason)
    {
        if (Enum.TryParse<AgentTurnToolCatalogFailureCode>(reason, ignoreCase: true, out var catalogCode))
            return catalogCode.ToString();
        if (Enum.TryParse<AgentToolDiscoveryFailureCode>(reason, ignoreCase: true, out var discoveryCode))
            return discoveryCode.ToString();
        return "Other";
    }

    private static TagList StageTags(string stage, string turnClass, string outcome) =>
        new()
        {
            { "aevatar.agent_turn_tool_catalog.stage", stage },
            { "aevatar.agent_turn_tool_catalog.turn_class", turnClass },
            { "aevatar.agent_turn_tool_catalog.outcome", outcome },
        };

    private static void SetCurrentTraceTags(
        int? registeredToolCount,
        int? discoveredToolCount,
        int? finalToolCount,
        int? schemaBytes,
        string? catalogDigest,
        string? profileIdentity,
        string? intentIdentity,
        string turnClass,
        string? denyReason) =>
        SetTraceTags(
            Activity.Current,
            registeredToolCount,
            discoveredToolCount,
            finalToolCount,
            schemaBytes,
            catalogDigest,
            profileIdentity,
            intentIdentity,
            turnClass,
            denyReason);

    private static void SetTraceTags(
        Activity? activity,
        int? registeredToolCount,
        int? discoveredToolCount,
        int? finalToolCount,
        int? schemaBytes,
        string? catalogDigest,
        string? profileIdentity,
        string? intentIdentity,
        string turnClass,
        string? denyReason)
    {
        if (activity is null)
            return;
        if (registeredToolCount.HasValue)
            activity.SetTag("aevatar.agent_turn_tool_catalog.registered_count", registeredToolCount.Value);
        if (discoveredToolCount.HasValue)
            activity.SetTag("aevatar.agent_turn_tool_catalog.discovered_count", discoveredToolCount.Value);
        if (finalToolCount.HasValue)
            activity.SetTag("aevatar.agent_turn_tool_catalog.final_count", finalToolCount.Value);
        if (schemaBytes.HasValue)
            activity.SetTag("aevatar.agent_turn_tool_catalog.schema_bytes", schemaBytes.Value);
        if (!string.IsNullOrWhiteSpace(catalogDigest))
            activity.SetTag("aevatar.agent_turn_tool_catalog.digest", catalogDigest);
        if (!string.IsNullOrWhiteSpace(profileIdentity))
            activity.SetTag("aevatar.agent_turn_tool_catalog.profile", profileIdentity.Trim());
        if (!string.IsNullOrWhiteSpace(intentIdentity))
            activity.SetTag("aevatar.agent_turn_tool_catalog.intent", intentIdentity.Trim());
        activity.SetTag("aevatar.agent_turn_tool_catalog.turn_class", turnClass);
        if (!string.IsNullOrWhiteSpace(denyReason))
            activity.SetTag("aevatar.agent_turn_tool_catalog.deny_reason", NormalizeReason(denyReason));
    }

    private static TagList With(this TagList tags, string name, object value)
    {
        var copy = new TagList();
        foreach (var tag in tags)
            copy.Add(tag.Key, tag.Value);
        copy.Add(name, value);
        return copy;
    }
}
