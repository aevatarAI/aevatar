using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
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
public sealed class UserConfigGAgent : GAgentBase<UserConfigGAgentState>
{
    [EventHandler(EndpointName = "updateConfig")]
    public async Task HandleConfigUpdated(UserConfigUpdatedEvent evt)
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
            .OrCurrent();
    }

    private static UserConfigGAgentState ApplyConfigUpdated(
        UserConfigGAgentState state, UserConfigUpdatedEvent evt)
    {
        return new UserConfigGAgentState
        {
            DefaultModel = evt.DefaultModel,
            PreferredLlmRoute = evt.PreferredLlmRoute,
            RuntimeMode = evt.RuntimeMode,
            LocalRuntimeBaseUrl = evt.LocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl = evt.RemoteRuntimeBaseUrl,
            MaxToolRounds = evt.MaxToolRounds,
        };
    }

}
