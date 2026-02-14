using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.Foundation.Abstractions.Connectors;

namespace Aevatar.Workflows.Core.Connectors;

/// <summary>
/// HTTP connector with domain/method/path allowlist safeguards.
/// </summary>
public sealed class HttpConnector : IConnector
{
    private readonly HttpClient _client;
    private readonly Uri _baseUri;
    private readonly HashSet<string> _allowedMethods;
    private readonly HashSet<string> _allowedPaths;
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
        HttpClient? client = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required", nameof(name));
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("baseUrl is required", nameof(baseUrl));

        Name = name;
        _baseUri = new Uri(baseUrl, UriKind.Absolute);
        _allowedMethods = new HashSet<string>(
            (allowedMethods ?? ["POST"]).Select(x => x.ToUpperInvariant()),
            StringComparer.OrdinalIgnoreCase);
        _allowedPaths = new HashSet<string>(
            (allowedPaths ?? ["/"]).Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);
        _allowedInputKeys = new HashSet<string>(allowedInputKeys ?? [], StringComparer.OrdinalIgnoreCase);
        _defaultHeaders = defaultHeaders?.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase)
                          ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _defaultTimeoutMs = Math.Clamp(timeoutMs, 100, 300_000);
        _client = client ?? new HttpClient();
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

        var rawPath = request.Operation;
        if (string.IsNullOrWhiteSpace(rawPath) &&
            request.Parameters.TryGetValue("path", out var p) &&
            !string.IsNullOrWhiteSpace(p))
        {
            rawPath = p;
        }

        var normalizedPath = NormalizePath(rawPath);
        if (!_allowedPaths.Contains("/") && !_allowedPaths.Contains(normalizedPath))
        {
            return new ConnectorResponse
            {
                Success = false,
                Error = $"http path '{normalizedPath}' is not allowed",
                Metadata = new Dictionary<string, string> { ["connector.http.path"] = normalizedPath },
            };
        }

        var targetUri = new Uri(_baseUri, normalizedPath);
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

            using var response = await _client.SendAsync(msg, timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            sw.Stop();

            return new ConnectorResponse
            {
                Success = response.IsSuccessStatusCode,
                Output = body,
                Error = response.IsSuccessStatusCode ? "" : $"{(int)response.StatusCode} {response.ReasonPhrase}",
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
}
