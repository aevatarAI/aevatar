using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptEvolutionSnapshotQueryService : IScriptEvolutionProjectionQueryPort
{
    private readonly RuntimeScriptActorAccessor _actorAccessor;
    private readonly RuntimeScriptQueryClient _queryClient;
    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly TimeSpan _queryTimeout;
    private readonly QueryScriptEvolutionProposalSnapshotRequestAdapter _queryAdapter = new();

    public RuntimeScriptEvolutionSnapshotQueryService(
        RuntimeScriptActorAccessor actorAccessor,
        RuntimeScriptQueryClient queryClient,
        IScriptingActorAddressResolver addressResolver,
        IScriptingPortTimeouts timeouts)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        _queryTimeout = (timeouts ?? throw new ArgumentNullException(nameof(timeouts)))
            .GetEvolutionSnapshotQueryTimeout();
    }

    public async Task<ScriptEvolutionProposalSnapshot?> GetProposalSnapshotAsync(
        string proposalId,
        CancellationToken ct = default)
    {
        var normalizedProposalId = proposalId?.Trim() ?? string.Empty;
        if (normalizedProposalId.Length == 0)
            return null;

        var actorId = _addressResolver.GetEvolutionSessionActorId(normalizedProposalId);
        var actor = await _actorAccessor.GetAsync(actorId);
        if (actor == null)
            return null;

        var response = await _queryClient.QueryActorAsync<ScriptEvolutionProposalSnapshotRespondedEvent>(
            actor,
            ScriptingQueryRouteConventions.EvolutionReplyStreamPrefix,
            _queryTimeout,
            (requestId, replyStreamId) => _queryAdapter.Map(actorId, requestId, replyStreamId, normalizedProposalId),
            static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
            ScriptingQueryRouteConventions.BuildEvolutionSnapshotTimeoutMessage,
            ct);
        if (!response.Found)
            return null;

        return new ScriptEvolutionProposalSnapshot
        {
            ProposalId = response.ProposalId ?? string.Empty,
            ScriptId = response.ScriptId ?? string.Empty,
            BaseRevision = response.BaseRevision ?? string.Empty,
            CandidateRevision = response.CandidateRevision ?? string.Empty,
            ValidationStatus = MapValidationStatus(response),
            PromotionStatus = MapPromotionStatus(response),
            RollbackStatus = string.Empty,
            Completed = response.Completed,
            Accepted = response.Accepted,
            FailureReason = response.FailureReason ?? string.Empty,
            DefinitionActorId = response.DefinitionActorId ?? string.Empty,
            CatalogActorId = response.CatalogActorId ?? string.Empty,
            Diagnostics = [.. response.Diagnostics],
        };
    }

    private static string MapValidationStatus(ScriptEvolutionProposalSnapshotRespondedEvent response)
    {
        if (!response.Completed)
            return ScriptEvolutionStatuses.Pending;

        if (response.Accepted)
            return ScriptEvolutionStatuses.Validated;

        return string.Equals(response.Status, ScriptEvolutionStatuses.PromotionFailed, StringComparison.Ordinal)
            ? ScriptEvolutionStatuses.Validated
            : ScriptEvolutionStatuses.ValidationFailed;
    }

    private static string MapPromotionStatus(ScriptEvolutionProposalSnapshotRespondedEvent response)
    {
        if (!response.Completed)
            return ScriptEvolutionStatuses.Pending;

        return response.Status switch
        {
            ScriptEvolutionStatuses.Promoted => ScriptEvolutionStatuses.Promoted,
            ScriptEvolutionStatuses.PromotionFailed => ScriptEvolutionStatuses.PromotionFailed,
            ScriptEvolutionStatuses.Rejected => ScriptEvolutionStatuses.Rejected,
            _ => response.Accepted
                ? ScriptEvolutionStatuses.Promoted
                : ScriptEvolutionStatuses.Rejected,
        };
    }
}
