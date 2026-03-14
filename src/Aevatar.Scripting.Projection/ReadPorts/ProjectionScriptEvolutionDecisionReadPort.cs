using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Projection.ReadModels;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Scripting.Projection.ReadPorts;

public sealed class ProjectionScriptEvolutionDecisionReadPort : IScriptEvolutionDecisionReadPort
{
    private readonly IServiceProvider _serviceProvider;

    public ProjectionScriptEvolutionDecisionReadPort(
        IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public async Task<ScriptPromotionDecision?> TryGetAsync(
        string proposalId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);

        var storeDispatcher = ResolveStoreDispatcher();
        if (storeDispatcher == null)
            return null;

        var readModel = await storeDispatcher.GetAsync(proposalId, ct);
        if (readModel == null || !TryResolveTerminal(readModel, out var decision))
            return null;

        return decision;
    }

    private IProjectionStoreDispatcher<ScriptEvolutionReadModel, string>? ResolveStoreDispatcher()
    {
        try
        {
            return _serviceProvider.GetService<IProjectionStoreDispatcher<ScriptEvolutionReadModel, string>>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryResolveTerminal(
        ScriptEvolutionReadModel readModel,
        out ScriptPromotionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(readModel);

        if (!TryResolveStatus(readModel, out var accepted, out var status))
        {
            decision = null!;
            return false;
        }

        decision = new ScriptPromotionDecision(
            Accepted: accepted,
            ProposalId: readModel.ProposalId ?? string.Empty,
            ScriptId: readModel.ScriptId ?? string.Empty,
            BaseRevision: readModel.BaseRevision ?? string.Empty,
            CandidateRevision: readModel.CandidateRevision ?? string.Empty,
            Status: status,
            FailureReason: readModel.FailureReason ?? string.Empty,
            DefinitionActorId: readModel.DefinitionActorId ?? string.Empty,
            CatalogActorId: readModel.CatalogActorId ?? string.Empty,
            ValidationReport: new ScriptEvolutionValidationReport(
                IsSuccess: string.Equals(
                    readModel.ValidationStatus,
                    ScriptEvolutionStatuses.Validated,
                    StringComparison.Ordinal),
                Diagnostics: readModel.Diagnostics.ToArray()));
        return true;
    }

    private static bool TryResolveStatus(
        ScriptEvolutionReadModel readModel,
        out bool accepted,
        out string status)
    {
        var promotionStatus = readModel.PromotionStatus ?? string.Empty;
        if (string.Equals(promotionStatus, ScriptEvolutionStatuses.Promoted, StringComparison.Ordinal))
        {
            accepted = true;
            status = promotionStatus;
            return true;
        }

        if (string.Equals(promotionStatus, ScriptEvolutionStatuses.Rejected, StringComparison.Ordinal) ||
            string.Equals(promotionStatus, ScriptEvolutionStatuses.PromotionFailed, StringComparison.Ordinal) ||
            string.Equals(promotionStatus, ScriptEvolutionStatuses.RolledBack, StringComparison.Ordinal))
        {
            accepted = false;
            status = promotionStatus;
            return true;
        }

        accepted = false;
        status = string.Empty;
        return false;
    }
}
