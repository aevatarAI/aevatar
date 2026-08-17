using Aevatar.AIWorkspace.Application.Abstractions;

namespace Aevatar.AIWorkspace.Application;

public sealed class AIWorkspaceOverviewQueryService(
    IAIWorkspaceAgentsQueryService agents,
    IAIWorkspaceActivityQueryService activity)
    : IAIWorkspaceOverviewQueryService
{
    public async Task<AIWorkspaceQueryResult<AIWorkspaceOverviewView>> QueryAsync(
        string scopeId,
        int take,
        CancellationToken ct = default)
    {
        if (!AIWorkspaceQueryPolicy.IsValidPageSize(take))
            return AIWorkspaceQueryPolicy.InvalidPageSize<AIWorkspaceOverviewView>();

        var agentsTask = agents.QueryAsync(scopeId, new AIWorkspaceAgentsQuery(Take: take), ct);
        var activityTask = activity.QueryAsync(scopeId, new AIWorkspaceActivityQuery(Take: take), ct);
        await Task.WhenAll(agentsTask, activityTask).ConfigureAwait(false);

        var agentResult = await agentsTask.ConfigureAwait(false);
        var activityResult = await activityTask.ConfigureAwait(false);
        var agentView = agentResult.Value ?? UnavailableAgents(scopeId, agentResult.Failure);
        var activityView = activityResult.Value ?? UnavailableActivity(scopeId, activityResult.Failure);

        return AIWorkspaceQueryResult<AIWorkspaceOverviewView>.Success(new AIWorkspaceOverviewView(
            "independent_read_models",
            new AIWorkspaceOverviewAgentsView(
                ToOverviewSource(agentView.Owned),
                ToOverviewSource(agentView.SystemTemplates)),
            activityView.Conversations,
            activityView.Runs));
    }

    private static AIWorkspaceOverviewSourceView ToOverviewSource(AIWorkspaceAgentCollectionView source) =>
        new(
            source.Source,
            source.Availability,
            source.TotalCount,
            source.AuthorityStateVersion,
            source.UpdatedAtUtc,
            source.Error);

    private static AIWorkspaceAgentsView UnavailableAgents(
        string scopeId,
        AIWorkspaceQueryFailure? failure)
    {
        var error = new AIWorkspaceSourceErrorView(
            failure?.Code ?? "AGENT_PROFILES_UNAVAILABLE",
            failure?.Message ?? "Agent Profile catalogs are temporarily unavailable.");
        return new AIWorkspaceAgentsView(
            "independent_read_models",
            new AIWorkspaceAgentCollectionView(
                "agent_profile_catalog",
                "scope",
                scopeId,
                AIWorkspaceSourceAvailability.Unavailable,
                [],
                null,
                null,
                null,
                null,
                error),
            new AIWorkspaceAgentCollectionView(
                "agent_profile_catalog",
                "system",
                null,
                AIWorkspaceSourceAvailability.Unavailable,
                [],
                null,
                null,
                null,
                null,
                error));
    }

    private static AIWorkspaceActivityView UnavailableActivity(
        string scopeId,
        AIWorkspaceQueryFailure? failure)
    {
        var conversations = AIWorkspaceActivityQueryService.UnavailableConversations(scopeId);
        var runs = AIWorkspaceActivityQueryService.UnavailableRuns(scopeId);
        if (failure is null)
            return new AIWorkspaceActivityView("independent_read_models", conversations, runs);

        var error = new AIWorkspaceSourceErrorView(failure.Code, failure.Message);
        return new AIWorkspaceActivityView(
            "independent_read_models",
            conversations with { Error = error },
            runs with { Error = error });
    }
}
