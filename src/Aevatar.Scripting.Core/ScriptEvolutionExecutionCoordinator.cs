using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;

namespace Aevatar.Scripting.Core;

public sealed class ScriptEvolutionExecutionCoordinator
{
    private readonly IScriptEvolutionFlowPort _evolutionFlowPort;
    private readonly IScriptingActorAddressResolver _addressResolver;

    public ScriptEvolutionExecutionCoordinator(
        IScriptEvolutionFlowPort evolutionFlowPort,
        IScriptingActorAddressResolver addressResolver)
    {
        _evolutionFlowPort = evolutionFlowPort ?? throw new ArgumentNullException(nameof(evolutionFlowPort));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
    }

    public async Task<ScriptEvolutionExecutionPlan> ExecuteAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var defaultDefinitionActorId = _addressResolver.GetDefinitionActorId(proposal.ScriptId);
        var defaultCatalogActorId = _addressResolver.GetCatalogActorId();
        var events = new List<IMessage>
        {
            new ScriptEvolutionProposedEvent
            {
                ProposalId = proposal.ProposalId,
                ScriptId = proposal.ScriptId,
                BaseRevision = proposal.BaseRevision,
                CandidateRevision = proposal.CandidateRevision,
                CandidateSourceHash = proposal.CandidateSourceHash,
                Reason = proposal.Reason,
            },
            new ScriptEvolutionBuildRequestedEvent
            {
                ProposalId = proposal.ProposalId,
                ScriptId = proposal.ScriptId,
                CandidateRevision = proposal.CandidateRevision,
            },
        };

        try
        {
            var flowResult = await _evolutionFlowPort.ExecuteAsync(proposal, ct);
            if (flowResult.Status == ScriptEvolutionFlowStatus.PolicyRejected)
            {
                AppendRejectedCompletion(
                    events,
                    proposal,
                    proposal.CandidateRevision,
                    flowResult.FailureReason ?? string.Empty,
                    Array.Empty<string>(),
                    ScriptEvolutionStatuses.Rejected,
                    defaultDefinitionActorId,
                    defaultCatalogActorId);
                return new ScriptEvolutionExecutionPlan(events);
            }

            var validation = flowResult.ValidationReport ?? ScriptEvolutionValidationReport.Empty;
            events.Add(new ScriptEvolutionValidatedEvent
            {
                ProposalId = proposal.ProposalId,
                ScriptId = proposal.ScriptId,
                CandidateRevision = proposal.CandidateRevision,
                IsValid = validation.IsSuccess,
                Diagnostics = { validation.Diagnostics },
            });

            if (flowResult.Status == ScriptEvolutionFlowStatus.Promoted)
            {
                var promotion = flowResult.Promotion
                    ?? throw new InvalidOperationException("Promotion result is required when flow is promoted.");
                var promotedCatalogActorId = string.IsNullOrWhiteSpace(promotion.CatalogActorId)
                    ? defaultCatalogActorId
                    : promotion.CatalogActorId;

                events.Add(new ScriptEvolutionPromotedEvent
                {
                    ProposalId = proposal.ProposalId,
                    ScriptId = proposal.ScriptId,
                    CandidateRevision = promotion.PromotedRevision,
                    DefinitionActorId = promotion.DefinitionActorId,
                    CatalogActorId = promotion.CatalogActorId,
                });
                events.Add(ScriptEvolutionSessionGAgent.BuildCompletedEvent(
                    accepted: true,
                    proposal,
                    status: ScriptEvolutionStatuses.Promoted,
                    failureReason: string.Empty,
                    definitionActorId: promotion.DefinitionActorId,
                    catalogActorId: promotedCatalogActorId,
                    diagnostics: validation.Diagnostics));
                return new ScriptEvolutionExecutionPlan(events);
            }

            if (flowResult.Status == ScriptEvolutionFlowStatus.PromotionFailed)
            {
                var failureReason = flowResult.FailureReason ?? string.Empty;
                events.Add(new ScriptEvolutionRejectedEvent
                {
                    ProposalId = proposal.ProposalId,
                    ScriptId = proposal.ScriptId,
                    CandidateRevision = proposal.CandidateRevision,
                    FailureReason = ScriptEvolutionSessionGAgent.TagPromotionFailedFailureReason(failureReason),
                });

                var failurePromotion = flowResult.Promotion;
                events.Add(ScriptEvolutionSessionGAgent.BuildCompletedEvent(
                    accepted: false,
                    proposal,
                    status: ScriptEvolutionSessionGAgent.StatusPromotionFailed,
                    failureReason: failureReason,
                    definitionActorId: string.IsNullOrWhiteSpace(failurePromotion?.DefinitionActorId)
                        ? defaultDefinitionActorId
                        : failurePromotion.DefinitionActorId,
                    catalogActorId: string.IsNullOrWhiteSpace(failurePromotion?.CatalogActorId)
                        ? defaultCatalogActorId
                        : failurePromotion.CatalogActorId,
                    diagnostics: validation.Diagnostics));
                return new ScriptEvolutionExecutionPlan(events);
            }

            AppendRejectedCompletion(
                events,
                proposal,
                proposal.CandidateRevision,
                flowResult.FailureReason ?? string.Empty,
                validation.Diagnostics,
                ScriptEvolutionStatuses.Rejected,
                defaultDefinitionActorId,
                defaultCatalogActorId);
            return new ScriptEvolutionExecutionPlan(events);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendRejectedCompletion(
                events,
                proposal,
                proposal.CandidateRevision,
                ex.Message,
                Array.Empty<string>(),
                ScriptEvolutionStatuses.Rejected,
                defaultDefinitionActorId,
                defaultCatalogActorId);
            return new ScriptEvolutionExecutionPlan(events);
        }
    }

    private static void AppendRejectedCompletion(
        ICollection<IMessage> events,
        ScriptEvolutionProposal proposal,
        string candidateRevision,
        string failureReason,
        IReadOnlyList<string> diagnostics,
        string status,
        string definitionActorId,
        string catalogActorId)
    {
        events.Add(new ScriptEvolutionRejectedEvent
        {
            ProposalId = proposal.ProposalId,
            ScriptId = proposal.ScriptId,
            CandidateRevision = candidateRevision,
            FailureReason = failureReason,
        });
        events.Add(ScriptEvolutionSessionGAgent.BuildCompletedEvent(
            accepted: false,
            proposal,
            status,
            failureReason,
            definitionActorId,
            catalogActorId,
            diagnostics));
    }
}

public sealed record ScriptEvolutionExecutionPlan(
    IReadOnlyList<IMessage> DomainEvents);
