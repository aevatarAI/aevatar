using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Aevatar.Studio.Infrastructure.Storage;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Hosting.Endpoints;

public static class ExplorerEndpoints
{
    public static IEndpointRouteBuilder MapExplorerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/explorer").WithTags("Explorer");
        group.MapGet("/manifest", HandleGetManifestAsync);
        group.MapGet("/grep", HandleGrepAsync);
        group.MapGet("/files/{**key}", HandleGetFileAsync);
        group.MapPut("/files/{**key}", HandlePutFileAsync);
        group.MapDelete("/files/{**key}", HandleDeleteFileAsync);
        return app;
    }

    /// <summary>
    /// Lists all objects under this scope's directory in chrono-storage.
    /// Returns an empty manifest if chrono-storage is unavailable or the scope has no files.
    /// </summary>
    private static async Task<IResult> HandleGetManifestAsync(
        HttpContext http,
        [FromServices] IAppScopeResolver scopeResolver,
        [FromServices] ChronoStorageCatalogBlobClient? blobClient,
        CancellationToken ct)
    {
        try
        {
            var scope = scopeResolver.Resolve(http);
            if (scope == null)
                return Results.BadRequest(new { error = "Not authenticated" });

            if (blobClient == null)
                return Results.Ok(new ChronoStorageCatalogBlobClient.StorageManifest());

            var context = blobClient.TryResolveContext(string.Empty, "_");
            if (context == null)
                return Results.Ok(new ChronoStorageCatalogBlobClient.StorageManifest());

            var result = await blobClient.ListObjectsAsync(context, cancellationToken: ct);

            var files = result.Objects
                .Select(o => new ChronoStorageCatalogBlobClient.ManifestEntry
                {
                    Key = o.Key,
                    Type = InferType(o.Key),
                    Name = InferName(o.Key),
                    UpdatedAt = o.LastModified,
                })
                .ToList();

            // Auto-create connectors.json if it doesn't exist yet.
            if (!files.Any(f => f.Key == "connectors.json"))
            {
                try
                {
                    var connectorsContext = blobClient.TryResolveContext(string.Empty, "connectors.json");
                    if (connectorsContext != null)
                    {
                        await blobClient.UploadAsync(connectorsContext, "[]"u8.ToArray(), "application/json", ct);
                        files.Add(new ChronoStorageCatalogBlobClient.ManifestEntry
                        {
                            Key = "connectors.json",
                            Type = "connectors",
                            Name = "connectors",
                        });
                    }
                }
                catch
                {
                    // Best-effort; don't fail the manifest if auto-create fails.
                }
            }

            return Results.Ok(new ChronoStorageCatalogBlobClient.StorageManifest { Files = files });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> HandleGetFileAsync(
        HttpContext http,
        string key,
        [FromServices] IAppScopeResolver scopeResolver,
        [FromServices] ChronoStorageCatalogBlobClient? blobClient,
        [FromServices] IOptions<ConnectorCatalogStorageOptions> storageOptions,
        CancellationToken ct)
    {
        try
        {
            key = NormalizeFileKey(key);
            var scope = scopeResolver.Resolve(http);
            if (scope == null)
                return Results.BadRequest(new { error = "Not authenticated" });

            if (blobClient == null)
                return Results.NotFound(new { error = $"File not found: {key}" });

            var resolved = await TryDownloadFromKnownPrefixesAsync(blobClient, key, storageOptions.Value, ct);
            if (resolved is null)
                return Results.NotFound(new { error = $"File not found: {key}" });

            var contentType = key switch
            {
                _ when key.EndsWith(".json") || key.EndsWith(".jsonl") => "application/json",
                _ when key.EndsWith(".yaml") || key.EndsWith(".yml") => "text/yaml",
                _ when key.EndsWith(".cs") => "text/plain",
                _ => "application/octet-stream",
            };
            return Results.Text(Encoding.UTF8.GetString(resolved.Value.Payload), contentType);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> HandlePutFileAsync(
        HttpContext http,
        string key,
        [FromServices] IAppScopeResolver scopeResolver,
        [FromServices] ChronoStorageCatalogBlobClient? blobClient,
        [FromServices] IOptions<ConnectorCatalogStorageOptions> storageOptions,
        CancellationToken ct)
    {
        try
        {
            key = NormalizeFileKey(key);
            var scope = scopeResolver.Resolve(http);
            if (scope == null)
                return Results.BadRequest(new { error = "Not authenticated" });

            if (InferType(key) is "workflow" or "script")
                return Results.BadRequest(new { error = "Workflow and script files are managed by Studio. Use the Studio API to edit them." });

            if (blobClient == null)
                return Results.StatusCode(503);

            var context = await TryResolveWritableContextAsync(blobClient, key, storageOptions.Value, ct);
            if (context == null)
                return Results.StatusCode(503);

            var body = await new StreamReader(http.Request.Body).ReadToEndAsync(ct);
            var bytes = Encoding.UTF8.GetBytes(body);
            var contentType = key switch
            {
                _ when key.EndsWith(".json") || key.EndsWith(".jsonl") => "application/json",
                _ when key.EndsWith(".yaml") || key.EndsWith(".yml") => "text/yaml",
                _ when key.EndsWith(".cs") => "text/plain",
                _ => "application/octet-stream",
            };
            await blobClient.UploadAsync(context, bytes, contentType, ct);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> HandleDeleteFileAsync(
        HttpContext http,
        string key,
        [FromServices] IAppScopeResolver scopeResolver,
        [FromServices] ChronoStorageCatalogBlobClient? blobClient,
        [FromServices] IOptions<ConnectorCatalogStorageOptions> storageOptions,
        CancellationToken ct)
    {
        try
        {
            key = NormalizeFileKey(key);
            var scope = scopeResolver.Resolve(http);
            if (scope == null)
                return Results.BadRequest(new { error = "Not authenticated" });

            if (InferType(key) is "workflow" or "script")
                return Results.BadRequest(new { error = "Workflow and script files are managed by Studio. Use the Studio API to delete them." });

            if (blobClient == null)
                return Results.StatusCode(503);

            foreach (var prefix in GetCandidatePrefixesForKey(key, storageOptions.Value))
            {
                var context = blobClient.TryResolveContext(prefix, key);
                if (context == null) continue;

                await blobClient.DeleteIfExistsAsync(context, ct);
            }

            return Results.NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Searches file contents in chrono-storage using a regex or literal pattern.
    /// Returns matching lines with file key, line number, and snippet.
    /// </summary>
    private static async Task<IResult> HandleGrepAsync(
        HttpContext http,
        [FromQuery] string pattern,
        [FromQuery] string? glob,
        [FromQuery] int? maxResults,
        [FromServices] IAppScopeResolver scopeResolver,
        [FromServices] ChronoStorageCatalogBlobClient? blobClient,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return Results.BadRequest(new { error = "Query parameter 'pattern' is required." });

            var scope = scopeResolver.Resolve(http);
            if (scope == null)
                return Results.BadRequest(new { error = "Not authenticated" });

            if (blobClient == null)
                return Results.Ok(new GrepResponse());

            var context = blobClient.TryResolveContext(string.Empty, "_");
            if (context == null)
                return Results.Ok(new GrepResponse());

            var limit = Math.Clamp(maxResults ?? 50, 1, 100);

            // Compile regex (treat as literal if invalid regex)
            Regex regex;
            try
            {
                regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
            }
            catch (RegexParseException)
            {
                regex = new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
            }

            var listResult = await blobClient.ListObjectsAsync(context, cancellationToken: ct);

            var textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".json", ".jsonl", ".yaml", ".yml", ".cs", ".md", ".txt", ".xml", ".proto", ".csx" };

            var candidates = listResult.Objects
                .Where(o => textExtensions.Any(ext => o.Key.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                .Where(o => string.IsNullOrWhiteSpace(glob) || MatchGlob(o.Key, glob))
                .Where(o => (o.Size ?? 0) <= 1_048_576) // skip files > 1MB
                .Take(500)
                .ToList();

            var matches = new List<GrepMatch>();
            foreach (var obj in candidates)
            {
                if (matches.Count >= limit) break;

                var fileContext = blobClient.TryResolveContext(string.Empty, obj.Key);
                if (fileContext == null) continue;

                var data = await blobClient.TryDownloadAsync(fileContext, ct);
                if (data == null) continue;

                var content = Encoding.UTF8.GetString(data);
                var lines = content.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (matches.Count >= limit) break;

                    if (regex.IsMatch(lines[i]))
                    {
                        matches.Add(new GrepMatch
                        {
                            Key = obj.Key,
                            LineNumber = i + 1,
                            Snippet = lines[i].TrimEnd('\r'),
                        });
                    }
                }
            }

            return Results.Ok(new GrepResponse { Matches = matches });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Simple glob matching: supports * and ** patterns.</summary>
    private static bool MatchGlob(string path, string glob)
    {
        // Convert glob to regex: ** → .*, * → [^/]*, ? → [^/]
        var regexPattern = "^" + Regex.Escape(glob)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]") + "$";
        return Regex.IsMatch(path, regexPattern, RegexOptions.IgnoreCase);
    }

    private sealed class GrepMatch
    {
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("lineNumber")] public int LineNumber { get; set; }
        [JsonPropertyName("snippet")] public string Snippet { get; set; } = "";
    }

    private sealed class GrepResponse
    {
        [JsonPropertyName("matches")] public List<GrepMatch> Matches { get; set; } = new();
    }

    private static string InferType(string key)
    {
        var dir = key.Contains('/') ? key[..key.IndexOf('/')] : string.Empty;
        return dir switch
        {
            "workflows" => "workflow",
            "scripts" => "script",
            "chat-histories" => "chat-history",
            _ when key == "config.json" => "config",
            _ when key == "roles.json" => "roles",
            _ when key == "connectors.json" => "connectors",
            _ => "file",
        };
    }

    private static string? InferName(string key)
    {
        var fileName = key.Split('/').Last();
        var dotIdx = fileName.LastIndexOf('.');
        return dotIdx > 0 ? fileName[..dotIdx] : fileName;
    }

    private static string NormalizeFileKey(string key) =>
        Uri.UnescapeDataString(key ?? string.Empty).Trim('/');

    /// <summary>
    /// Returns the bucket-level prefix that a given key is stored under, matching the prefix
    /// used by the corresponding storage store.
    /// </summary>
    private static string GetPrefixForKey(string key, ConnectorCatalogStorageOptions opts)
    {
        if (key.StartsWith("chat-histories/", StringComparison.Ordinal))
            return opts.UserConfigPrefix;

        return InferType(key) switch
        {
            "connectors" => opts.Prefix,
            "roles" => opts.RolesPrefix,
            "config" => opts.UserConfigPrefix,
            _ when string.Equals(key, "actors.json", StringComparison.Ordinal) => opts.UserConfigPrefix,
            _ => string.Empty,
        };
    }

    private static IEnumerable<string> GetCandidatePrefixesForKey(string key, ConnectorCatalogStorageOptions opts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var preferred = GetPrefixForKey(key, opts);
        if (seen.Add(preferred))
            yield return preferred;

        foreach (var candidate in new[] { string.Empty, opts.Prefix, opts.RolesPrefix, opts.UserConfigPrefix })
        {
            if (seen.Add(candidate ?? string.Empty))
                yield return candidate ?? string.Empty;
        }
    }

    private static async Task<(
        ChronoStorageCatalogBlobClient.RemoteScopeContext Context,
        byte[] Payload)?> TryDownloadFromKnownPrefixesAsync(
        ChronoStorageCatalogBlobClient blobClient,
        string key,
        ConnectorCatalogStorageOptions opts,
        CancellationToken ct)
    {
        foreach (var prefix in GetCandidatePrefixesForKey(key, opts))
        {
            var context = blobClient.TryResolveContext(prefix, key);
            if (context == null) continue;

            var payload = await blobClient.TryDownloadAsync(context, ct);
            if (payload != null)
                return (context, payload);
        }

        return null;
    }

    private static async Task<ChronoStorageCatalogBlobClient.RemoteScopeContext?> TryResolveWritableContextAsync(
        ChronoStorageCatalogBlobClient blobClient,
        string key,
        ConnectorCatalogStorageOptions opts,
        CancellationToken ct)
    {
        var existing = await TryDownloadFromKnownPrefixesAsync(blobClient, key, opts, ct);
        if (existing is not null)
            return existing.Value.Context;

        return blobClient.TryResolveContext(GetPrefixForKey(key, opts), key);
    }
}
