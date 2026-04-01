using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.Foundation.Abstractions.Connectors;

namespace Aevatar.Bootstrap.Connectors;

/// <summary>
/// HTTP connector with domain/method/path allowlist safeguards.
/// </summary>
public sealed class HttpConnector : IConnector
{
    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient? _client;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly string _httpClientName;
    private readonly IConnectorRequestAuthorizationProvider? _authorizationProvider;
    private readonly Uri _baseUri;
    private readonly HashSet<string> _allowedMethods;
    private readonly string[] _allowedPathPatterns;
    private readonly HashSet<string> _allowedInputKeys;
    private readonly Dictionary<string, string> _defaultHeaders;
    private readonly int _defaultTimeoutMs;

    public HttpConnector(
        string name,
        string baseUrl,
        IEnumerable<string>? allowedMethods = null,
        IEnumerable<string>? allowedPaths = null,
        IEnumerable<string>? allowedInputKeys = null,
        IDictionary<string, string>? defaultHeaders = null,
        int timeoutMs = 30_000,
        IHttpClientFactory? httpClientFactory = null,
        string? httpClientName = null,
        IConnectorRequestAuthorizationProvider? authorizationProvider = null,
        HttpClient? client = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required", nameof(name));
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("baseUrl is required", nameof(baseUrl));

        Name = name;
        _baseUri = new Uri(baseUrl, UriKind.Absolute);
        _allowedMethods = new HashSet<string>(
            (allowedMethods ?? ["POST"]).Select(x => x.ToUpperInvariant()),
            StringComparer.OrdinalIgnoreCase);
        _allowedPathPatterns = (allowedPaths ?? ["/"])
            .Select(NormalizePathPattern)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _allowedInputKeys = new HashSet<string>(allowedInputKeys ?? [], StringComparer.OrdinalIgnoreCase);
        _defaultHeaders = defaultHeaders?.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase)
                          ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _defaultTimeoutMs = Math.Clamp(timeoutMs, 100, 300_000);
        _client = client;
        _httpClientFactory = httpClientFactory;
        _httpClientName = string.IsNullOrWhiteSpace(httpClientName) ? Name : httpClientName.Trim();
        _authorizationProvider = authorizationProvider;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Type => "http";

    /// <inheritdoc />
    public async Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
    {
        var method = request.Parameters.TryGetValue("method", out var m) && !string.IsNullOrWhiteSpace(m)
            ? m.ToUpperInvariant()
            : "POST";
        if (!_allowedMethods.Contains(method))
        {
            return new ConnectorResponse
            {
                Success = false,
                Error = $"http method '{method}' is not allowed",
                Metadata = new Dictionary<string, string> { ["connector.http.method"] = method },
            };
        }

        var rawPath = request.Parameters.TryGetValue("path", out var p) &&
                      !string.IsNullOrWhiteSpace(p)
            ? p
            : request.Operation;

        var normalizedPath = NormalizePath(rawPath);
        var targetUri = BuildTargetUri(_baseUri, normalizedPath);
        if (!string.Equals(targetUri.Scheme, _baseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(targetUri.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            targetUri.Port != _baseUri.Port)
        {
            return new ConnectorResponse
            {
                Success = false,
                Error = "target url escapes configured base_url",
            };
        }

        var absoluteTargetPath = NormalizePath(targetUri.AbsolutePath);
        if (!IsPathAllowed(normalizedPath, absoluteTargetPath))
        {
            return new ConnectorResponse
            {
                Success = false,
                Error = $"http path '{normalizedPath}' is not allowed",
                Metadata = new Dictionary<string, string>
                {
                    ["connector.http.path"] = normalizedPath,
                    ["connector.http.absolute_path"] = absoluteTargetPath,
                },
            };
        }

        var timeoutMs = request.Parameters.TryGetValue("timeout_ms", out var t) && int.TryParse(t, out var parsedTimeout)
            ? Math.Clamp(parsedTimeout, 100, 300_000)
            : _defaultTimeoutMs;

        if (_allowedInputKeys.Count > 0 && !TryValidatePayloadKeys(request.Payload, _allowedInputKeys, out var schemaError))
        {
            return new ConnectorResponse
            {
                Success = false,
                Error = schemaError,
                Metadata = new Dictionary<string, string>
                {
                    ["connector.http.method"] = method,
                    ["connector.http.url"] = targetUri.ToString(),
                },
            };
        }

        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            using var msg = new HttpRequestMessage(new HttpMethod(method), targetUri);
            foreach (var (key, value) in _defaultHeaders)
                msg.Headers.TryAddWithoutValidation(key, value);

            if (_authorizationProvider != null)
                await _authorizationProvider.ApplyAsync(msg, timeoutCts.Token);

            ApplyRequestMetadataAuthorization(msg, request.Metadata);

            if (request.Parameters.TryGetValue("content_type", out var contentType) &&
                !string.IsNullOrWhiteSpace(contentType))
            {
                msg.Content = new StringContent(request.Payload ?? "", Encoding.UTF8, contentType);
            }
            else if (method is not "GET" and not "HEAD")
            {
                msg.Content = new StringContent(request.Payload ?? "", Encoding.UTF8, "application/json");
            }

            if (!msg.Headers.Accept.Any())
                msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await ResolveClient().SendAsync(msg, timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            sw.Stop();

            return new ConnectorResponse
            {
                Success = response.IsSuccessStatusCode,
                Output = body,
                Error = response.IsSuccessStatusCode ? "" : BuildHttpErrorMessage(response, body),
                Metadata = new Dictionary<string, string>
                {
                    ["connector.http.status_code"] = ((int)response.StatusCode).ToString(),
                    ["connector.http.reason"] = response.ReasonPhrase ?? "",
                    ["connector.http.method"] = method,
                    ["connector.http.url"] = targetUri.ToString(),
                    ["connector.http.duration_ms"] = sw.Elapsed.TotalMilliseconds.ToString("F2"),
                },
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new ConnectorResponse
            {
                Success = false,
                Error = $"http timeout after {timeoutMs}ms",
                Metadata = new Dictionary<string, string>
                {
                    ["connector.http.method"] = method,
                    ["connector.http.url"] = targetUri.ToString(),
                    ["connector.http.duration_ms"] = sw.Elapsed.TotalMilliseconds.ToString("F2"),
                },
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectorResponse
            {
                Success = false,
                Error = ex.Message,
                Metadata = new Dictionary<string, string>
                {
                    ["connector.http.method"] = method,
                    ["connector.http.url"] = targetUri.ToString(),
                    ["connector.http.duration_ms"] = sw.Elapsed.TotalMilliseconds.ToString("F2"),
                },
            };
        }
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var p = path.Trim();
        if (!p.StartsWith('/')) p = "/" + p;
        return p;
    }

    private static string NormalizePathPattern(string? pathPattern)
    {
        if (string.IsNullOrWhiteSpace(pathPattern))
            return "/";

        var pattern = pathPattern.Trim();
        if (!pattern.StartsWith('/'))
            pattern = "/" + pattern;
        return pattern;
    }

    private static Uri BuildTargetUri(Uri baseUri, string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(baseUri.AbsolutePath) ||
            string.Equals(baseUri.AbsolutePath, "/", StringComparison.Ordinal))
        {
            return new Uri(baseUri, normalizedPath);
        }

        var combinedPath = baseUri.AbsolutePath.TrimEnd('/') + normalizedPath;
        var builder = new UriBuilder(baseUri)
        {
            Path = combinedPath,
        };
        return builder.Uri;
    }

    private static string BuildHttpErrorMessage(HttpResponseMessage response, string body)
    {
        var baseMessage = $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();
        var detail = TryExtractErrorDetail(body);
        if (string.IsNullOrWhiteSpace(detail))
            return baseMessage;

        return $"{baseMessage}: {detail}";
    }

    private static string TryExtractErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("description", out var description) &&
                    description.ValueKind == JsonValueKind.String)
                {
                    return description.GetString()?.Trim() ?? string.Empty;
                }

                if (doc.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString()?.Trim() ?? string.Empty;
                }
            }
        }
        catch
        {
            // Ignore parsing failures and fallback to raw body preview.
        }

        var trimmed = body.Trim();
        return trimmed.Length <= 200 ? trimmed : $"{trimmed[..200]}...";
    }

    private HttpClient ResolveClient()
    {
        if (_httpClientFactory != null)
            return _httpClientFactory.CreateClient(_httpClientName);
        if (_client != null)
            return _client;
        return SharedHttpClient;
    }

    private bool IsPathAllowed(string requestPath, string absoluteTargetPath)
    {
        foreach (var pattern in _allowedPathPatterns)
        {
            if (string.Equals(pattern, "/", StringComparison.Ordinal))
                return true;

            if (PathMatchesPattern(pattern, requestPath) || PathMatchesPattern(pattern, absoluteTargetPath))
                return true;
        }

        return false;
    }

    private static bool PathMatchesPattern(string pattern, string candidate)
    {
        if (string.Equals(pattern, candidate, StringComparison.OrdinalIgnoreCase))
            return true;

        if (pattern.EndsWith("/*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^2];
            if (string.Equals(candidate, prefix, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (!pattern.Contains('*', StringComparison.Ordinal))
            return false;

        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(candidate, regexPattern, RegexOptions.IgnoreCase);
    }

    private static bool TryValidatePayloadKeys(string payload, HashSet<string> allowedKeys, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(payload)) return true;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "payload schema violation: expected JSON object";
                return false;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!allowedKeys.Contains(prop.Name))
                {
                    error = $"payload schema violation: key '{prop.Name}' is not allowlisted";
                    return false;
                }
            }

            return true;
        }
        catch
        {
            error = "payload schema violation: invalid JSON";
            return false;
        }
    }

    private static void ApplyRequestMetadataAuthorization(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (request.Headers.Authorization != null || metadata == null || metadata.Count == 0)
            return;

        if (!metadata.TryGetValue(ConnectorRequest.HttpAuthorizationMetadataKey, out var authorization) ||
            string.IsNullOrWhiteSpace(authorization))
        {
            return;
        }

        if (!AuthenticationHeaderValue.TryParse(authorization.Trim(), out var parsed))
            return;

        request.Headers.Authorization = parsed;
    }
}
