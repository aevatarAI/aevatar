using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.QueryPorts;

/// <summary>
/// Reads user configuration from the projection document store.
/// Zero write path. Pure query semantics.
/// </summary>
public sealed class ProjectionUserConfigQueryPort : IUserConfigQueryPort
{
    private readonly IProjectionDocumentReader<UserConfigCurrentStateDocument, string> _documentReader;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly string _defaultLocalRuntimeBaseUrl;
    private readonly string _defaultRemoteRuntimeBaseUrl;

    public ProjectionUserConfigQueryPort(
        IProjectionDocumentReader<UserConfigCurrentStateDocument, string> documentReader,
        IAppScopeResolver scopeResolver,
        IUserConfigDefaults userConfigDefaults)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        var resolvedDefaults = userConfigDefaults ?? throw new ArgumentNullException(nameof(userConfigDefaults));
        _defaultLocalRuntimeBaseUrl = UserConfigRuntime.NormalizeBaseUrl(
            resolvedDefaults.LocalRuntimeBaseUrl,
            UserConfigRuntimeDefaults.LocalRuntimeBaseUrl);
        _defaultRemoteRuntimeBaseUrl = UserConfigRuntime.NormalizeBaseUrl(
            resolvedDefaults.RemoteRuntimeBaseUrl,
            UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl);
    }

    public Task<UserConfig> GetAsync(CancellationToken ct = default) =>
        GetAsync(
            UserConfigResourceKey.ForOwnerScope(_scopeResolver.Resolve()?.ScopeId ?? "default"),
            ct);

    public async Task<UserConfig> GetAsync(
        UserConfigResourceKey resource,
        CancellationToken ct = default)
    {
        var actorId = UserConfigActorIdMapper.Build(resource);
        var document = await _documentReader.GetAsync(actorId, ct);

        if (document is null)
            return CreateDefaultConfig();

        var llmSelection = MapSelection(document.LlmSelection);
        return new UserConfig(
            DefaultModel: document.DefaultModel,
            PreferredLlmRoute: UserLlmSelectionRoute.Resolve(llmSelection) ?? string.Empty,
            RuntimeMode: string.IsNullOrEmpty(document.RuntimeMode)
                ? UserConfigRuntimeDefaults.LocalMode
                : document.RuntimeMode,
            LocalRuntimeBaseUrl: string.IsNullOrEmpty(document.LocalRuntimeBaseUrl)
                ? _defaultLocalRuntimeBaseUrl
                : document.LocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl: string.IsNullOrEmpty(document.RemoteRuntimeBaseUrl)
                ? _defaultRemoteRuntimeBaseUrl
                : document.RemoteRuntimeBaseUrl,
            GithubUsername: NormalizeOptional(document.GithubUsername),
            MaxToolRounds: document.MaxToolRounds,
            LlmSelection: llmSelection);
    }

    private UserConfig CreateDefaultConfig() =>
        new(
            DefaultModel: string.Empty,
            PreferredLlmRoute: string.Empty,
            RuntimeMode: UserConfigRuntimeDefaults.LocalMode,
            LocalRuntimeBaseUrl: _defaultLocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl: _defaultRemoteRuntimeBaseUrl,
            GithubUsername: null,
            LlmSelection: null);

    private static UserLlmSelectionValue? MapSelection(UserLlmSelection? selection)
    {
        if (selection is null)
            return null;

        var kind = selection.RouteKind switch
        {
            UserLlmRouteKind.Unspecified => UserLlmSelectionKind.Unspecified,
            UserLlmRouteKind.Gateway => UserLlmSelectionKind.Gateway,
            UserLlmRouteKind.NyxIdUserService => UserLlmSelectionKind.NyxIdUserService,
            _ => throw new ArgumentOutOfRangeException(nameof(selection)),
        };

        return new UserLlmSelectionValue(
            kind,
            selection.RouteValue,
            selection.NyxIdUserServiceId,
            selection.ServiceSlugSnapshot);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
