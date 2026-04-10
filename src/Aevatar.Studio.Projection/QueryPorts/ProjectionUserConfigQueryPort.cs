using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.QueryPorts;

/// <summary>
/// Reads user configuration from the projection document store.
/// Zero write path. Pure query semantics.
/// </summary>
public sealed class ProjectionUserConfigQueryPort : IUserConfigQueryPort
{
    private const string WriteActorIdPrefix = "user-config-";

    private readonly IProjectionDocumentReader<UserConfigCurrentStateDocument, string> _documentReader;
    private readonly IAppScopeResolver _scopeResolver;

    public ProjectionUserConfigQueryPort(
        IProjectionDocumentReader<UserConfigCurrentStateDocument, string> documentReader,
        IAppScopeResolver scopeResolver)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
    }

    public async Task<UserConfig> GetAsync(CancellationToken ct = default)
    {
        var actorId = WriteActorIdPrefix + (_scopeResolver.Resolve()?.ScopeId ?? "default");
        var document = await _documentReader.GetAsync(actorId, ct);

        if (document is null)
            return CreateDefaultConfig();

        return new UserConfig(
            DefaultModel: document.DefaultModel,
            PreferredLlmRoute: string.IsNullOrEmpty(document.PreferredLlmRoute)
                ? UserConfigLlmRouteDefaults.Gateway
                : document.PreferredLlmRoute,
            RuntimeMode: string.IsNullOrEmpty(document.RuntimeMode)
                ? UserConfigRuntimeDefaults.LocalMode
                : document.RuntimeMode,
            LocalRuntimeBaseUrl: string.IsNullOrEmpty(document.LocalRuntimeBaseUrl)
                ? UserConfigRuntimeDefaults.LocalRuntimeBaseUrl
                : document.LocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl: string.IsNullOrEmpty(document.RemoteRuntimeBaseUrl)
                ? UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl
                : document.RemoteRuntimeBaseUrl,
            MaxToolRounds: document.MaxToolRounds);
    }

    private static UserConfig CreateDefaultConfig() =>
        new(
            DefaultModel: string.Empty,
            PreferredLlmRoute: UserConfigLlmRouteDefaults.Gateway,
            RuntimeMode: UserConfigRuntimeDefaults.LocalMode,
            LocalRuntimeBaseUrl: UserConfigRuntimeDefaults.LocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl: UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl);
}
