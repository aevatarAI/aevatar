using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.UserConfig;

/// <summary>
/// User-scoped actor that owns the user configuration state.
/// Replaces the chrono-storage backed <c>ChronoStorageUserConfigStore</c>.
///
/// Actor ID: <c>user-config-{scopeId}</c> (per-scope).
/// </summary>
[GAgent("user.config")]
public sealed class UserConfigGAgent : GAgentBase<UserConfigGAgentState>, IProjectedActor
{
    private const string GatewayRoute = "/api/v1/llm/gateway/v1";

    public static string ProjectionKind => "user-config";


    [EventHandler(EndpointName = "updateConfig")]
    public async Task HandleConfigUpdated(UserConfigUpdatedEvent evt)
    {
        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "updateConfigDelta")]
    public async Task HandleUpdateConfig(UpdateUserConfigCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        await PersistDomainEventAsync(BuildUpdatedEvent(State, command));
    }

    [EventHandler(EndpointName = "updateGithubUsername")]
    public async Task HandleGithubUsernameUpdated(UserConfigGithubUsernameUpdatedEvent evt)
    {
        await PersistDomainEventAsync(evt);
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
    }

    protected override UserConfigGAgentState TransitionState(
        UserConfigGAgentState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<UserConfigUpdatedEvent>(ApplyConfigUpdated)
            .On<UserConfigGithubUsernameUpdatedEvent>(ApplyGithubUsernameUpdated)
            .OrCurrent();
    }

    internal static UserConfigUpdatedEvent BuildUpdatedEvent(
        UserConfigGAgentState state,
        UpdateUserConfigCommand command)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        if (command.LlmSelection is not null)
            ValidateSelection(command.LlmSelection);

        var selection = command.LlmSelection?.Clone() ?? state.LlmSelection?.Clone();
        var evt = new UserConfigUpdatedEvent
        {
            DefaultModel = command.HasDefaultModel ? command.DefaultModel : state.DefaultModel,
            PreferredLlmRoute = command.LlmSelection is null
                ? state.PreferredLlmRoute
                : command.LlmSelection.RouteValue,
            RuntimeMode = command.HasRuntimeMode ? command.RuntimeMode : state.RuntimeMode,
            LocalRuntimeBaseUrl = command.HasLocalRuntimeBaseUrl
                ? command.LocalRuntimeBaseUrl
                : state.LocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl = command.HasRemoteRuntimeBaseUrl
                ? command.RemoteRuntimeBaseUrl
                : state.RemoteRuntimeBaseUrl,
            MaxToolRounds = command.HasMaxToolRounds ? command.MaxToolRounds : state.MaxToolRounds,
            GithubUsername = command.HasGithubUsername ? command.GithubUsername : state.GithubUsername,
        };
        if (selection is not null)
            evt.LlmSelection = selection;
        return evt;
    }

    private static void ValidateSelection(Aevatar.GAgents.UserConfig.UserLlmSelection selection)
    {
        var rawRoute = selection.RouteValue ?? string.Empty;
        var rawId = selection.NyxIdUserServiceId ?? string.Empty;
        var rawSlug = selection.ServiceSlugSnapshot ?? string.Empty;
        var route = rawRoute.Trim();
        var id = rawId.Trim();
        var slug = rawSlug.Trim();
        if (route != rawRoute || id != rawId || slug != rawSlug)
            throw new InvalidOperationException("user_llm_selection_not_canonical");

        switch (selection.RouteKind)
        {
            case UserLlmRouteKind.Gateway when route == GatewayRoute && id.Length == 0 && slug.Length == 0:
                return;
            case UserLlmRouteKind.NyxIdUserService
                when id.Length > 0 &&
                     slug.Length > 0 &&
                     !slug.Contains('/') &&
                     string.Equals(route, $"/api/v1/proxy/s/{slug}", StringComparison.Ordinal):
                return;
            default:
                throw new InvalidOperationException("user_llm_selection_invalid");
        }
    }

    private static UserConfigGAgentState ApplyConfigUpdated(
        UserConfigGAgentState state, UserConfigUpdatedEvent evt)
    {
        var updated = new UserConfigGAgentState
        {
            DefaultModel = evt.DefaultModel,
            PreferredLlmRoute = evt.PreferredLlmRoute,
            RuntimeMode = evt.RuntimeMode,
            LocalRuntimeBaseUrl = evt.LocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl = evt.RemoteRuntimeBaseUrl,
            MaxToolRounds = evt.MaxToolRounds,
            GithubUsername = evt.GithubUsername,
        };
        if (evt.LlmSelection is not null)
            updated.LlmSelection = evt.LlmSelection.Clone();
        return updated;
    }

    private static UserConfigGAgentState ApplyGithubUsernameUpdated(
        UserConfigGAgentState state, UserConfigGithubUsernameUpdatedEvent evt)
    {
        var updated = new UserConfigGAgentState
        {
            DefaultModel = state.DefaultModel,
            PreferredLlmRoute = state.PreferredLlmRoute,
            RuntimeMode = state.RuntimeMode,
            LocalRuntimeBaseUrl = state.LocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl = state.RemoteRuntimeBaseUrl,
            MaxToolRounds = state.MaxToolRounds,
            GithubUsername = evt.GithubUsername,
        };
        if (state.LlmSelection is not null)
            updated.LlmSelection = state.LlmSelection.Clone();
        return updated;
    }
}
