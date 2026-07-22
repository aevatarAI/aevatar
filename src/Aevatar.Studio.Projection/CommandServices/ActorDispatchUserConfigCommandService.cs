using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.CommandServices;

/// <summary>
/// Dispatches user-config write commands to the <see cref="UserConfigGAgent"/>.
/// Uses <see cref="IStudioActorBootstrap"/> so the actor is created if absent
/// before we dispatch the command through <see cref="IActorDispatchPort"/>.
/// </summary>
internal sealed class ActorDispatchUserConfigCommandService : IUserConfigCommandService
{
    private const string DirectRoute = "aevatar.studio.projection.user-config";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IAppScopeResolver _scopeResolver;

    public ActorDispatchUserConfigCommandService(
        IStudioActorBootstrap bootstrap,
        IActorDispatchPort dispatchPort,
        IAppScopeResolver scopeResolver)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
    }

    public Task<UserConfigSaveReceipt> SaveAsync(UserConfig config, CancellationToken ct = default) =>
        SaveAsync(_scopeResolver.Resolve()?.ScopeId ?? "default", config, ct);

    public Task<UserConfigSaveReceipt> UpdateAsync(
        UserConfigResourceKey resource,
        UserConfigUpdate update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var command = new UpdateUserConfigCommand();
        if (update.DefaultModel is not null)
            command.DefaultModel = update.DefaultModel;
        if (update.LlmSelection is not null)
            command.LlmSelection = MapSelection(update.LlmSelection);
        if (update.RuntimeMode is not null)
            command.RuntimeMode = update.RuntimeMode;
        if (update.LocalRuntimeBaseUrl is not null)
            command.LocalRuntimeBaseUrl = update.LocalRuntimeBaseUrl;
        if (update.RemoteRuntimeBaseUrl is not null)
            command.RemoteRuntimeBaseUrl = update.RemoteRuntimeBaseUrl;
        if (update.GithubUsername is not null)
            command.GithubUsername = update.GithubUsername;
        if (update.MaxToolRounds.HasValue)
            command.MaxToolRounds = update.MaxToolRounds.Value;

        return DispatchAsync(resource, command, ct);
    }

    public async Task<UserConfigSaveReceipt> SaveAsync(string scopeId, UserConfig config, CancellationToken ct = default)
    {
        var evt = new UserConfigUpdatedEvent
        {
            DefaultModel = config.DefaultModel,
            PreferredLlmRoute = UserConfigLlmRoute.Normalize(config.PreferredLlmRoute),
            RuntimeMode = UserConfigRuntime.NormalizeMode(config.RuntimeMode),
            LocalRuntimeBaseUrl = UserConfigRuntime.NormalizeBaseUrl(
                config.LocalRuntimeBaseUrl,
                UserConfigRuntimeDefaults.LocalRuntimeBaseUrl),
            RemoteRuntimeBaseUrl = UserConfigRuntime.NormalizeBaseUrl(
                config.RemoteRuntimeBaseUrl,
                UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl),
            GithubUsername = NormalizeOptional(config.GithubUsername) ?? string.Empty,
            MaxToolRounds = config.MaxToolRounds,
        };

        return await DispatchAsync(scopeId, evt, ct).ConfigureAwait(false);
    }

    public Task<UserConfigSaveReceipt> SaveGithubUsernameAsync(string scopeId, string githubUsername, CancellationToken ct = default) =>
        DispatchAsync(
            scopeId,
            new UserConfigGithubUsernameUpdatedEvent
            {
                GithubUsername = NormalizeOptional(githubUsername) ?? string.Empty,
            },
            ct);

    private static string NormalizeScopeId(string? scopeId) =>
        string.IsNullOrWhiteSpace(scopeId) ? "default" : scopeId.Trim();

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static UserLlmSelection MapSelection(UserLlmSelectionValue selection) =>
        new()
        {
            RouteKind = selection.Kind switch
            {
                UserLlmSelectionKind.Unspecified => UserLlmRouteKind.Unspecified,
                UserLlmSelectionKind.Gateway => UserLlmRouteKind.Gateway,
                UserLlmSelectionKind.NyxIdUserService => UserLlmRouteKind.NyxIdUserService,
                _ => throw new ArgumentOutOfRangeException(nameof(selection)),
            },
            RouteValue = selection.RouteValue,
            NyxIdUserServiceId = selection.NyxIdUserServiceId,
            ServiceSlugSnapshot = selection.ServiceSlugSnapshot,
        };

    private Task<UserConfigSaveReceipt> DispatchAsync(string scopeId, IMessage payload, CancellationToken ct) =>
        DispatchAsync(
            UserConfigResourceKey.ForOwnerScope(NormalizeScopeId(scopeId)),
            payload,
            ct);

    private async Task<UserConfigSaveReceipt> DispatchAsync(
        UserConfigResourceKey resource,
        IMessage payload,
        CancellationToken ct)
    {
        var actorId = UserConfigActorIdMapper.Build(resource);
        // Refactor (iter56/cluster-910-projection-activation-cleanup):
        //   old=command-path pre-dispatch activation
        //   new=committed-state plan provider
        //   user-config commands no longer synchronously start materialization.
        var actor = await _bootstrap.EnsureAsync<UserConfigGAgent>(actorId, ct);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(DirectRoute, actor.Id),
        };

        var admission = await _dispatchPort.DispatchAsync(actor.Id, envelope, ct).ConfigureAwait(false);

        return new UserConfigSaveReceipt(
            Accepted: admission.Accepted,
            CommandId: admission.CommandId,
            AckStage: admission.Accepted
                ? UserConfigCommandAckStage.Accepted
                : UserConfigCommandAckStage.AdmissionRejected,
            ActorId: admission.ActorId,
            CorrelationId: admission.CorrelationId,
            AckedAtUtc: admission.AckedAt);
    }
}
