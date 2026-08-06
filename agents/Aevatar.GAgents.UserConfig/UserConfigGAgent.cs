using Aevatar.AI.Abstractions.LLMProviders;
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
    public static string ProjectionKind => "user-config";

    [EventHandler(EndpointName = "updateConfigDelta")]
    public async Task HandleUpdateConfig(UpdateUserConfigCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        await PersistDomainEventAsync(BuildUpdatedEvent(State, command));
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
            LLMSelectionPolicy.ValidateSelection(command.LlmSelection);

        var selection = command.LlmSelection?.Clone() ?? state.LlmSelection?.Clone();
        var defaultModel = command.LlmSelection is null
            ? state.DefaultModel
            : LLMSelectionPolicy.CompatibilityDefaultModel(command.LlmSelection);
        var preferredRoute = command.LlmSelection is null
            ? state.PreferredLlmRoute
            : LLMSelectionPolicy.CompatibilityRoute(command.LlmSelection);
        var evt = new UserConfigUpdatedEvent
        {
            DefaultModel = defaultModel,
            PreferredLlmRoute = preferredRoute,
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
