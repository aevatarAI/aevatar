using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Web;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.Workflow.Infrastructure.ExternalCapabilities;

internal sealed class NyxIdServiceApiWebFallbackPort(
    INyxIdApiClientFactory nyxIdApiClientFactory,
    IWebApiClient webApiClient) : IServiceApiWebFallbackPort
{
    private const int MaxSearchResults = 5;

    public async Task<ServiceApiWebFallbackResult> ResolveAsync(
        ResolveServiceApiWebFallbackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var token = request.Access.NyxIdCallerCredential?.SourceReadableUserBearerToken ??
                    request.Access.NyxIdOrganizationBearerToken;
        if (string.IsNullOrWhiteSpace(token))
            return Exhausted(ServiceApiFallbackExhaustedReason.WebResearchFailed,
                "The official Web contract fallback could not read the NyxID service catalog.");

        var client = nyxIdApiClientFactory.CreateClient();
        var serviceJson = await client.GetServiceAsync(
            token,
            request.Input.TargetUserServiceId,
            cancellationToken);
        var catalogSlug = ReadFirstString(serviceJson, "catalog_service_slug", "service_slug", "slug");
        if (string.IsNullOrWhiteSpace(catalogSlug))
            return Exhausted(ServiceApiFallbackExhaustedReason.OfficialDocumentationNotFound,
                "Official API documentation was not found for the selected service.");

        var catalogJson = await client.GetCatalogEntryAsync(token, catalogSlug, cancellationToken);
        var officialUrls = ReadOfficialUrls(catalogJson);
        if (officialUrls.Count == 0)
            return Exhausted(ServiceApiFallbackExhaustedReason.OfficialDocumentationNotFound,
                "Official API documentation was not found for the selected service.");

        var officialHosts = officialUrls
            .Select(static url => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty)
            .Where(static host => host.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var query = $"site:{officialHosts.Order(StringComparer.OrdinalIgnoreCase).First()} " +
                    $"{request.Input.NormalizedCapability} official API documentation";
        var search = await SearchWithOneRetryAsync(token, query, cancellationToken);
        if (search.Error is not null)
            return Exhausted(ServiceApiFallbackExhaustedReason.WebResearchFailed,
                "Official API documentation research failed.");

        var candidates = officialUrls
            .Concat(search.Results.Select(static item => item.Url))
            .Where(url => IsOfficialUrl(url, officialHosts))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxSearchResults)
            .ToArray();
        if (candidates.Length == 0)
            return Exhausted(ServiceApiFallbackExhaustedReason.OfficialDocumentationNotFound,
                "Official API documentation was not found for the selected service.");

        var fetchedAny = false;
        foreach (var candidateUrl in candidates)
        {
            var fetch = await FetchWithOneRetryAsync(candidateUrl, cancellationToken);
            if (!string.IsNullOrWhiteSpace(fetch.RedirectUrl))
            {
                if (!IsOfficialUrl(fetch.RedirectUrl, officialHosts))
                    continue;
                fetch = await FetchWithOneRetryAsync(fetch.RedirectUrl, cancellationToken);
            }

            if (fetch.Error is not null || fetch.StatusCode is < 200 or >= 300 || string.IsNullOrWhiteSpace(fetch.Body))
                continue;

            fetchedAny = true;
            var selector = TryReadOpenApiSelector(
                fetch.Body,
                request.Input.TargetUserServiceId,
                request.Input.NormalizedCapability);
            if (selector is null)
                continue;

            var canonicalUrl = string.IsNullOrWhiteSpace(fetch.OriginalUrl) ? candidateUrl : fetch.OriginalUrl;
            return new ServiceApiWebFallbackResult
            {
                RequestShapeCandidate = new OfficialWebRequestShapeCandidate
                {
                    Provenance = new OfficialWebContractProvenance
                    {
                        CanonicalUrl = canonicalUrl,
                        SourceTitle = search.Results.FirstOrDefault(item =>
                            string.Equals(item.Url, candidateUrl, StringComparison.Ordinal))?.Title ?? catalogSlug,
                        FetchedContentDigest = Convert.ToHexStringLower(
                            SHA256.HashData(Encoding.UTF8.GetBytes(fetch.Body))),
                    },
                    Selector = selector,
                },
            };
        }

        return Exhausted(
            fetchedAny
                ? ServiceApiFallbackExhaustedReason.OfficialHttpContractNotEstablished
                : ServiceApiFallbackExhaustedReason.WebResearchFailed,
            fetchedAny
                ? "Official documentation did not establish an exact supported HTTP contract."
                : "Official API documentation research failed.");
    }

    private async Task<WebSearchResult> SearchWithOneRetryAsync(
        string token,
        string query,
        CancellationToken cancellationToken)
    {
        var result = await webApiClient.SearchAsync(token, query, MaxSearchResults, cancellationToken);
        return result.Error is null
            ? result
            : await webApiClient.SearchAsync(token, query, MaxSearchResults, cancellationToken);
    }

    private async Task<WebFetchResult> FetchWithOneRetryAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var result = await webApiClient.FetchUrlAsync(string.Empty, url, cancellationToken);
        return result.Error is null && result.StatusCode < 500
            ? result
            : await webApiClient.FetchUrlAsync(string.Empty, url, cancellationToken);
    }

    private static HashSet<string> ReadOfficialUrls(string json)
    {
        using var document = TryParse(json);
        if (document is null)
            return [];

        var urls = new HashSet<string>(StringComparer.Ordinal);
        CollectStrings(document.RootElement, urls,
            "documentation_url", "openapi_spec_url", "openapi_url", "homepage_url");
        urls.RemoveWhere(static value =>
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        return urls;
    }

    private static NyxIdRequestSelector? TryReadOpenApiSelector(
        string body,
        string userServiceId,
        string normalizedCapability)
    {
        using var document = TryParse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("openapi", out var version) ||
            version.ValueKind != JsonValueKind.String ||
            !(version.GetString() ?? string.Empty).StartsWith("3.", StringComparison.Ordinal) ||
            !document.RootElement.TryGetProperty("paths", out var paths) ||
            paths.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var requiredTerms = Terms(normalizedCapability);
        var matches = new List<(string Path, string Method, JsonElement Operation, int Score)>();
        foreach (var pathProperty in paths.EnumerateObject())
        {
            if (pathProperty.Value.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var methodProperty in pathProperty.Value.EnumerateObject())
            {
                if (!IsHttpMethod(methodProperty.Name) || methodProperty.Value.ValueKind != JsonValueKind.Object)
                    continue;
                var searchable = string.Join(' ',
                    pathProperty.Name,
                    ReadString(methodProperty.Value, "operationId"),
                    ReadString(methodProperty.Value, "summary"),
                    ReadString(methodProperty.Value, "description")).ToLowerInvariant();
                var score = requiredTerms.Count(term => searchable.Contains(term, StringComparison.Ordinal));
                if (requiredTerms.Length > 0 && score != requiredTerms.Length)
                    continue;
                matches.Add((pathProperty.Name, methodProperty.Name, methodProperty.Value.Clone(), score));
            }
        }

        if (matches.Count != 1)
            return null;

        var match = matches[0];
        var method = ParseMethod(match.Method);
        if (method == NyxIdRequestMethod.Unspecified)
            return null;

        var selector = new NyxIdRequestSelector
        {
            UserServiceId = userServiceId,
            Method = method,
            PathTemplate = match.Path,
            BodyMode = NyxIdRequestBodyMode.None,
            ResponseMode = NyxIdRequestResponseMode.Text,
            Risk = method switch
            {
                NyxIdRequestMethod.Get or NyxIdRequestMethod.Head or NyxIdRequestMethod.Options =>
                    NyxIdOperationRisk.ReadOnly,
                NyxIdRequestMethod.Delete => NyxIdOperationRisk.Destructive,
                _ => NyxIdOperationRisk.Write,
            },
        };

        if (match.Operation.TryGetProperty("parameters", out var parameters) &&
            parameters.ValueKind == JsonValueKind.Array)
        {
            foreach (var parameter in parameters.EnumerateArray())
            {
                var name = ReadString(parameter, "name");
                var location = ReadString(parameter, "in");
                if (name.Length == 0)
                    return null;
                if (string.Equals(location, "query", StringComparison.Ordinal))
                    selector.QueryParameters.Add(name);
                else if (string.Equals(location, "header", StringComparison.Ordinal))
                    selector.HeaderParameters.Add(name);
            }
        }

        if (match.Operation.TryGetProperty("requestBody", out var requestBody) &&
            requestBody.ValueKind == JsonValueKind.Object)
        {
            if (!requestBody.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Object ||
                !content.TryGetProperty("application/json", out _))
            {
                return null;
            }
            selector.BodyMode = NyxIdRequestBodyMode.Json;
            selector.BodyRequired = requestBody.TryGetProperty("required", out var required) &&
                                    required.ValueKind == JsonValueKind.True;
        }

        return NyxIdRequestSelectorContract.TryNormalize(selector, out var normalized, out _)
            ? normalized
            : null;
    }

    private static string? ReadFirstString(string json, params string[] names)
    {
        using var document = TryParse(json);
        if (document is null)
            return null;
        foreach (var name in names)
        {
            var value = FindString(document.RootElement, name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    private static string? FindString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
                var nested = FindString(property.Value, name);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, name);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        return null;
    }

    private static void CollectStrings(JsonElement element, ISet<string> values, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    values.Add(property.Value.GetString()!);
                CollectStrings(property.Value, values, names);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectStrings(item, values, names);
        }
    }

    private static bool IsOfficialUrl(string url, IReadOnlySet<string> officialHosts) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        officialHosts.Any(host =>
            string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith('.' + host, StringComparison.OrdinalIgnoreCase));

    private static bool IsHttpMethod(string value) =>
        value is "get" or "head" or "options" or "post" or "put" or "patch" or "delete";

    private static NyxIdRequestMethod ParseMethod(string value) => value switch
    {
        "get" => NyxIdRequestMethod.Get,
        "head" => NyxIdRequestMethod.Head,
        "options" => NyxIdRequestMethod.Options,
        "post" => NyxIdRequestMethod.Post,
        "put" => NyxIdRequestMethod.Put,
        "patch" => NyxIdRequestMethod.Patch,
        "delete" => NyxIdRequestMethod.Delete,
        _ => NyxIdRequestMethod.Unspecified,
    };

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string[] Terms(string value) =>
        value.Split([' ', '-', '_', '/', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static term => term.ToLowerInvariant())
            .Where(static term => term.Length > 2)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static JsonDocument? TryParse(string value)
    {
        try
        {
            return JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ServiceApiWebFallbackResult Exhausted(
        ServiceApiFallbackExhaustedReason reason,
        string safeMessage) =>
        new()
        {
            FallbackExhausted = new ServiceApiFallbackExhausted
            {
                Reason = reason,
                SafeMessage = safeMessage,
            },
        };
}
