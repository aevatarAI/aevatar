using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Aevatar.Configuration.BackendConsole;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Aevatar.BackendConsole.Hosting;

public sealed class BackendConsoleAssetService(IOptions<BackendConsoleOptions> options)
    : IBackendConsoleAssetService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly IOptions<BackendConsoleOptions> _options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly ConcurrentDictionary<string, string> _resourceCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BackendConsoleRenderedAsset> _renderedCache = new(StringComparer.Ordinal);

    public IResult Serve(BackendConsoleAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var rendered = _renderedCache.GetOrAdd(
            CacheKey(asset),
            _ => BackendConsoleRenderedAsset.Create(Render(asset), asset.ContentType));
        return new BackendConsoleAssetResult(rendered);
    }

    public string Render(BackendConsoleAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var content = _resourceCache.GetOrAdd(
            CacheKey(asset.Assembly, asset.ResourceSuffix),
            _ => LoadResource(asset.Assembly, asset.ResourceSuffix));

        if (!asset.InjectHostConfiguration)
            return content;

        if (string.IsNullOrWhiteSpace(asset.ConfigurationPlaceholder))
            throw new InvalidOperationException("Embedded host asset configuration placeholder is required.");

        if (!content.Contains(asset.ConfigurationPlaceholder, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Embedded host asset '{asset.LogicalName}' must contain {asset.ConfigurationPlaceholder}.");

        return content.Replace(
            asset.ConfigurationPlaceholder,
            BuildConfigJson(_options.Value, asset.ConfigurationProfile),
            StringComparison.Ordinal);
    }

    private static string CacheKey(BackendConsoleAsset asset) =>
        $"{CacheKey(asset.Assembly, asset.ResourceSuffix)}::{asset.ConfigurationPlaceholder}::{asset.ConfigurationProfile}";

    private static string CacheKey(Assembly assembly, string suffix) =>
        assembly.FullName + "::" + suffix;

    private static string LoadResource(Assembly assembly, string suffix)
    {
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal));

        if (resourceName is null)
            throw new InvalidOperationException(
                $"Embedded backend console asset ending with '{suffix}' was not found in '{assembly.GetName().Name}'.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded backend console asset '{resourceName}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string BuildConfigJson(
        BackendConsoleOptions options,
        BackendConsoleAssetConfigurationProfile profile)
    {
        if (profile == BackendConsoleAssetConfigurationProfile.AIAuthentication)
        {
            return JsonSerializer.Serialize(new
            {
                authority = options.OidcAuthority ?? string.Empty,
                clientId = options.OidcClientId ?? string.Empty,
                scope = options.OidcScope ?? string.Empty,
                storageKey = BuildAIStorageKey(options.StorageKey),
            }, JsonOptions);
        }

        var normalized = new Dictionary<string, object?>
        {
            ["authority"] = options.OidcAuthority ?? string.Empty,
            ["clientId"] = options.OidcClientId ?? string.Empty,
            ["scope"] = options.OidcScope ?? string.Empty,
            ["resources"] = options.OidcResources ?? [],
            ["nyxidApi"] = options.NyxApiBaseUrl ?? string.Empty,
            ["nyxidWeb"] = options.NyxWebBaseUrl ?? string.Empty,
            ["storageKey"] = options.StorageKey ?? string.Empty,
            ["defaultReturnPath"] = options.DefaultReturnPath ?? string.Empty,
            ["enableStudioWireInspector"] = options.EnableStudioWireInspector,
        };
        if (profile == BackendConsoleAssetConfigurationProfile.AuthenticationCallback)
            normalized["aiStorageKey"] = BuildAIStorageKey(options.StorageKey);

        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static string BuildAIStorageKey(string? storageKey) =>
        (storageKey ?? string.Empty) + ":ai";
}
