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

namespace Aevatar.Studio.Hosting.Endpoints;

public static class ExplorerEndpoints
{
    public static IEndpointRouteBuilder MapExplorerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/explorer").WithTags("Explorer");
        group.MapGet("/manifest", HandleGetManifestAsync);
        group.MapGet("/grep", HandleGrepAsync);
        group.MapGet("/files/{*key}", HandleGetFileAsync);
        group.MapPut("/files/{*key}", HandlePutFileAsync);
        group.MapDelete("/files/{*key}", HandleDeleteFileAsync);
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

            // Need any context to carry the scope/bucket/auth — use a dummy key
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
        CancellationToken ct)
    {
        try
        {
            var scope = scopeResolver.Resolve(http);
            if (scope == null)
                return Results.BadRequest(new { error = "Not authenticated" });

            if (blobClient == null)
                return Results.NotFound(new { error = $"File not found: {key}" });

            var context = blobClient.TryResolveContext(string.Empty, key);
            if (context == null)
                return Results.NotFound(new { error = $"File not found: {key}" });

            var data = await blobClient.TryDownloadAsync(context, ct);
            if (data == null)
                return Results.NotFound(new { error = $"File not found: {key}" });

            var contentType = key switch
            {
                _ when key.EndsWith(".json") || key.EndsWith(".jsonl") => "application/json",
                _ when key.EndsWith(".yaml") || key.EndsWith(".yml") => "text/yaml",
                _ when key.EndsWith(".cs") => "text/plain",
                _ => "application/octet-stream",
            };
            return Results.Text(Encoding.UTF8.GetString(data), contentType);
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
        CancellationToken ct)
    {
        try
        {
            var scope = scopeResolver.Resolve(http);
            if (scope == null)
                return Results.BadRequest(new { error = "Not authenticated" });

            if (blobClient == null)
                return Results.StatusCode(503);

            var context = blobClient.TryResolveContext(string.Empty, key);
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
        CancellationToken ct)
    {
        try
        {
            var scope = scopeResolver.Resolve(http);
            if (scope == null)
                return Results.BadRequest(new { error = "Not authenticated" });

            if (blobClient == null)
                return Results.StatusCode(503);

            var context = blobClient.TryResolveContext(string.Empty, key);
            if (context == null)
                return Results.StatusCode(503);

            await blobClient.DeleteIfExistsAsync(context, ct);
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
}
