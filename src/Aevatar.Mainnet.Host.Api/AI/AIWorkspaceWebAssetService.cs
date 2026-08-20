using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace Aevatar.Mainnet.Host.Api.AI;

internal interface IAIWorkspaceWebAssetService
{
    IResult ServePage(HttpContext http);

    IResult ServeAsset(HttpContext http, string? path);
}

internal sealed class AIWorkspaceWebAssetService : IAIWorkspaceWebAssetService
{
    private const string IndexFileName = "index.html";
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private static readonly Regex ContentHashPattern = new(
        @"\.[0-9a-f]{8,}\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly string _rootPath;
    private readonly string _rootPrefix;

    public AIWorkspaceWebAssetService(
        IWebHostEnvironment environment,
        IOptions<AIWorkspaceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);

        var configuredPath = options.Value.StaticAssetsPath.Trim();
        _rootPath = Path.GetFullPath(
            Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(environment.ContentRootPath, configuredPath));
        _rootPrefix = _rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                      Path.DirectorySeparatorChar;
    }

    public IResult ServePage(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var indexPath = Path.Combine(_rootPath, IndexFileName);
        if (!File.Exists(indexPath))
        {
            return AIWorkspaceEndpoints.Error(
                StatusCodes.Status503ServiceUnavailable,
                "AI_CONSOLE_UNAVAILABLE",
                "AI workspace assets are not installed on this host.");
        }

        http.Response.Headers.CacheControl = "no-store";
        return Results.File(indexPath, "text/html; charset=utf-8");
    }

    public IResult ServeAsset(HttpContext http, string? path)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (!File.Exists(Path.Combine(_rootPath, IndexFileName)))
        {
            return AIWorkspaceEndpoints.Error(
                StatusCodes.Status503ServiceUnavailable,
                "AI_CONSOLE_UNAVAILABLE",
                "AI workspace assets are not installed on this host.");
        }

        if (!TryResolveAssetPath(path, out var assetPath) || !File.Exists(assetPath))
            return Results.NotFound();

        if (!ContentTypes.TryGetContentType(assetPath, out var contentType))
            contentType = "application/octet-stream";

        http.Response.Headers.CacheControl = ContentHashPattern.IsMatch(Path.GetFileName(assetPath))
            ? "public,max-age=31536000,immutable"
            : "no-cache";
        return Results.File(
            assetPath,
            contentType,
            lastModified: File.GetLastWriteTimeUtc(assetPath),
            enableRangeProcessing: true);
    }

    private bool TryResolveAssetPath(string? path, out string assetPath)
    {
        assetPath = string.Empty;
        var normalized = path?.Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(normalized) ||
            normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Split('/').Any(static segment => segment is "." or ".."))
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(
                _rootPath,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(_rootPrefix, comparison))
            return false;

        assetPath = candidate;
        return true;
    }
}
