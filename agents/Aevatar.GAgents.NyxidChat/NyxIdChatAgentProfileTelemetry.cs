using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Observability;
using Aevatar.GAgents.NyxidChat.AgentProfiles;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatAgentProfileTelemetry
{
    public static AgentProfileTelemetryContext CreateContext(
        AgentProfileExecutionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return new AgentProfileTelemetryContext(
            binding.Source.ProfileId,
            binding.Source.StateVersion,
            binding.Source.PublishedRevision,
            Convert.ToHexString(binding.Source.PublishedSnapshotSha256.Span).ToLowerInvariant(),
            Convert.ToHexString(binding.DeterministicBindingSha256.Span).ToLowerInvariant(),
            binding.Admission.ActivationMode.ToString().ToLowerInvariant(),
            binding.Admission.RolloutRelease,
            binding.Admission.RolloutStage);
    }

    public static void RecordRouteDecision(
        AgentProfileTelemetryContext context,
        AgentProfileTurnAuthorityPreparation preparation,
        string outcome,
        double durationMs)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(preparation);

        var routeDiagnostic = preparation.Diagnostics.FirstOrDefault(static diagnostic => diagnostic.Code is
            AgentProfileTurnDiagnosticCode.AliasMatched or
            AgentProfileTurnDiagnosticCode.ClassifierMatched or
            AgentProfileTurnDiagnosticCode.ClassifierNoMatch or
            AgentProfileTurnDiagnosticCode.ClassifierFailed);
        var authority = preparation.Authority;
        var degradation = authority.DegradationReasons
            .FirstOrDefault(static reason => reason != AgentProfileTurnDegradationReason.Unspecified);
        var routingMode = routeDiagnostic?.Code switch
        {
            AgentProfileTurnDiagnosticCode.AliasMatched => "alias",
            AgentProfileTurnDiagnosticCode.ClassifierMatched or
                AgentProfileTurnDiagnosticCode.ClassifierNoMatch or
                AgentProfileTurnDiagnosticCode.ClassifierFailed => "classifier",
            _ => "none",
        };
        AgentProfileTelemetry.RecordRouteDecision(
            context,
            routingMode,
            authority.CandidateRoute?.IntentId ?? string.Empty,
            degradation == AgentProfileTurnDegradationReason.Unspecified
                ? outcome
                : degradation.ToString().ToLowerInvariant(),
            routeDiagnostic?.Code.ToString().ToLowerInvariant() ?? string.Empty,
            Math.Max(0, durationMs));
    }

    public static void RecordMaterialization(
        AgentProfileTelemetryContext context,
        AgentProfileTurnAuthorityState committedAuthority,
        AgentProfileTurnCatalogMaterialization materialization)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(committedAuthority);
        ArgumentNullException.ThrowIfNull(materialization);

        var catalog = materialization.Catalog;
        var selectedSkill = catalog.SelectedSkillPromptLayer;
        if (committedAuthority.SelectedExactSkillRef is { } selectedRef)
        {
            AgentProfileTelemetry.RecordSelectedSkill(
                context,
                selectedRef.Guid,
                selectedRef.LiteralVersion);
        }

        AgentProfileTelemetry.RecordPromptAndToolMaterialization(
            context,
            selectedSkill is not null
                ? "selected_skill"
                : catalog.ProfilePromptLayer is not null ? "profile" : "recovery",
            selectedSkill?.ActualUtf8Bytes ?? 0,
            catalog.FinalAllowedToolNames.Count,
            selectedSkill is not null ? "ok" : "degraded");
    }
}
