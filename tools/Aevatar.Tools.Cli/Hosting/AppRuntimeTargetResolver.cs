using Aevatar.Tools.Cli.Studio.Application.Abstractions;

namespace Aevatar.Tools.Cli.Hosting;

public sealed record AppRuntimeTarget(
    string ConfiguredBaseUrl,
    string EffectiveBaseUrl,
    bool UsesLocalRuntime,
    bool EmbeddedCapabilitiesAvailable);

public sealed class AppRuntimeTargetResolver
{
    private readonly IStudioWorkspaceStore _workspaceStore;
    private readonly string _localBaseUrl;
    private readonly string _defaultBaseUrl;
    private readonly string _legacyDefaultBaseUrl;
    private readonly bool _embeddedCapabilitiesAvailable;

    public AppRuntimeTargetResolver(
        IStudioWorkspaceStore workspaceStore,
        string localBaseUrl,
        string defaultBaseUrl,
        bool embeddedCapabilitiesAvailable,
        string? legacyDefaultRuntimeBaseUrl = null)
    {
        _workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        _localBaseUrl = NormalizeBaseUrl(localBaseUrl);
        _defaultBaseUrl = NormalizeBaseUrl(defaultBaseUrl);
        _legacyDefaultBaseUrl = NormalizeBaseUrl(
            string.IsNullOrWhiteSpace(legacyDefaultRuntimeBaseUrl)
                ? _defaultBaseUrl
                : legacyDefaultRuntimeBaseUrl);
        _embeddedCapabilitiesAvailable = embeddedCapabilitiesAvailable;
    }

    public async Task<AppRuntimeTarget> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _workspaceStore.GetSettingsAsync(cancellationToken);
        var configuredBaseUrl = NormalizeBaseUrl(string.IsNullOrWhiteSpace(settings.RuntimeBaseUrl)
            ? _defaultBaseUrl
            : settings.RuntimeBaseUrl);
        if (IsLegacyDefaultRuntimeBaseUrl(configuredBaseUrl) &&
            !IsSameEndpoint(configuredBaseUrl, _defaultBaseUrl))
        {
            configuredBaseUrl = _defaultBaseUrl;
        }

        var pointsToLocalHost = IsSameEndpoint(configuredBaseUrl, _localBaseUrl);
        var usesLocalRuntime = _embeddedCapabilitiesAvailable && pointsToLocalHost;
        var effectiveBaseUrl = usesLocalRuntime
            ? _localBaseUrl
            : pointsToLocalHost
                ? _defaultBaseUrl
                : configuredBaseUrl;

        return new AppRuntimeTarget(
            ConfiguredBaseUrl: configuredBaseUrl,
            EffectiveBaseUrl: effectiveBaseUrl,
            UsesLocalRuntime: usesLocalRuntime,
            EmbeddedCapabilitiesAvailable: _embeddedCapabilitiesAvailable);
    }

    public Uri BuildAbsoluteUri(string runtimeBaseUrl, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeBaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var baseUri = new Uri($"{NormalizeBaseUrl(runtimeBaseUrl)}/", UriKind.Absolute);
        return new Uri(baseUri, relativePath.TrimStart('/'));
    }

    private static string NormalizeBaseUrl(string url) => url.Trim().TrimEnd('/');

    private bool IsLegacyDefaultRuntimeBaseUrl(string url) =>
        string.Equals(
            NormalizeBaseUrl(url),
            _legacyDefaultBaseUrl,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSameEndpoint(string left, string right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var leftUri) ||
            !Uri.TryCreate(right, UriKind.Absolute, out var rightUri))
        {
            return false;
        }

        return Uri.Compare(
            leftUri,
            rightUri,
            UriComponents.SchemeAndServer | UriComponents.Port,
            UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
    }
}
