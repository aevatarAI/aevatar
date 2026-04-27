using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.CommandServices;

/// <summary>
/// Dispatches user-config write commands to the <see cref="UserConfigGAgent"/>.
/// Uses <see cref="IStudioActorBootstrap"/> so the actor is created (if
/// absent) and its projection scope is activated atomically before we
/// dispatch the command through <see cref="IActorDispatchPort"/>.
/// </summary>
internal sealed class ActorDispatchUserConfigCommandService : IUserConfigCommandService
{
    private const string ActorIdPrefix = "user-config-";
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

    public Task SaveAsync(UserConfig config, CancellationToken ct = default) =>
        SaveAsync(_scopeResolver.Resolve()?.ScopeId ?? "default", config, ct);

    public async Task SaveAsync(string scopeId, UserConfig config, CancellationToken ct = default)
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

        await DispatchAsync(scopeId, evt, ct);
    }

    public Task SaveGithubUsernameAsync(string scopeId, string githubUsername, CancellationToken ct = default) =>
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

    private async Task DispatchAsync(string scopeId, IMessage payload, CancellationToken ct)
    {
        var actorId = ActorIdPrefix + NormalizeScopeId(scopeId);
        var actor = await _bootstrap.EnsureAsync<UserConfigGAgent>(actorId, ct);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(DirectRoute, actor.Id),
        };

        await _dispatchPort.DispatchAsync(actor.Id, envelope, ct);
    }
}
