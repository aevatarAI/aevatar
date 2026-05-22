using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;

namespace Aevatar.GAgents.StatusDashboard.Executors;

/// <summary>
/// Generic HTTP probe — performs one request and classifies the outcome by
/// response status code. Covers OIDC discovery, NyxID catalog liveness, self
/// readiness, LLM dry-run, channel relay dry-run, and any other "send one
/// request, expect status in set" check.
///
/// Parameters (case-insensitive keys):
///   Url               required — full URL
///   Method            optional — GET (default), POST, PUT, DELETE
///   ExpectedStatuses  optional — comma-separated, default "200"
///   ContentType       optional — default "application/json"
///   Body              optional — raw request body
///   Header.{name}     optional — request header; supports ${configuration:Key}
///                                 placeholders resolved at probe time
///   ExpectedBodyContains optional — required literal marker in a successful response body
///   ExpectedBodyRegex    optional — required regex marker in a successful response body
///   ForbiddenBodyContains optional — literal marker that must not appear in response body
///   ForbiddenBodyRegex   optional — regex marker that must not appear in response body
///   DegradedOnNon2xx  optional — "true" to mark unexpected non-2xx as degraded
///                                 rather than down (default down)
/// </summary>
public sealed class HttpStatusProbeExecutor : IHealthProbeExecutor
{
    private const string ConfigPlaceholderPrefix = "${configuration:";
    private const string ConfigPlaceholderSuffix = "}";
    private static readonly Regex ConfigPlaceholderRegex = new(
        @"\$\{configuration:(?<key>[^\}]+)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public HttpStatusProbeExecutor(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public string Kind => "http_status";

    public async Task<HealthProbeOutcome> ProbeAsync(HealthProbeTargetDescriptor descriptor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var url = ReadParam(descriptor, "Url");
        if (string.IsNullOrWhiteSpace(url))
            return Failure("missing_parameter", "Parameter 'Url' is required.");

        var resolvedUrl = ResolvePlaceholders(url);
        if (!Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var uri))
            return Failure("invalid_url", $"Resolved URL is not absolute: {resolvedUrl}");

        var method = ReadParam(descriptor, "Method", "GET")!.ToUpperInvariant();
        var expectedStatuses = ParseExpectedStatuses(ReadParam(descriptor, "ExpectedStatuses", "200")!);
        var degradedOnNon2xx = string.Equals(
            ReadParam(descriptor, "DegradedOnNon2xx", "false"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        using var request = new HttpRequestMessage(new HttpMethod(method), uri);

        foreach (var (key, raw) in descriptor.Parameters)
        {
            if (!key.StartsWith("Header.", StringComparison.OrdinalIgnoreCase)) continue;
            var headerName = key.Substring("Header.".Length);
            var headerValue = ResolvePlaceholders(raw);
            if (string.IsNullOrWhiteSpace(headerValue)) continue;
            if (!request.Headers.TryAddWithoutValidation(headerName, headerValue))
                request.Content?.Headers.TryAddWithoutValidation(headerName, headerValue);
        }

        var body = ReadParam(descriptor, "Body");
        if (!string.IsNullOrEmpty(body))
        {
            var contentType = ReadParam(descriptor, "ContentType", "application/json")!;
            request.Content = new StringContent(
                ResolvePlaceholders(body),
                Encoding.UTF8,
                new MediaTypeHeaderValue(contentType).MediaType ?? "application/json");
        }

        var client = _httpClientFactory.CreateClient(nameof(HttpStatusProbeExecutor));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var statusCode = (int)response.StatusCode;
            if (expectedStatuses.Contains(statusCode))
            {
                var bodyAssertion = await ValidateBodyAssertionsAsync(descriptor, response, ct);
                if (bodyAssertion is not null)
                    return bodyAssertion;

                return new HealthProbeOutcome
                {
                    Status = HealthOutcomeStatus.Ok,
                    Detail = $"http_{statusCode}",
                };
            }

            return new HealthProbeOutcome
            {
                Status = degradedOnNon2xx ? HealthOutcomeStatus.Degraded : HealthOutcomeStatus.Down,
                Detail = $"http_{statusCode}",
                ErrorMessage = $"Unexpected status {statusCode}; expected one of {string.Join(",", expectedStatuses)}.",
            };
        }
        catch (HttpRequestException ex)
        {
            return Failure("http_request_failed", ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure("client_timeout", "HttpClient timed out before the per-probe deadline.");
        }
    }

    private static string? ReadParam(HealthProbeTargetDescriptor descriptor, string key, string? fallback = null)
    {
        foreach (var (k, v) in descriptor.Parameters)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrEmpty(v) ? fallback : v;
        }
        return fallback;
    }

    private static async Task<HealthProbeOutcome?> ValidateBodyAssertionsAsync(
        HealthProbeTargetDescriptor descriptor,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var expectedContains = ReadParam(descriptor, "ExpectedBodyContains");
        var expectedRegex = ReadParam(descriptor, "ExpectedBodyRegex");
        var forbiddenContains = ReadParam(descriptor, "ForbiddenBodyContains");
        var forbiddenRegex = ReadParam(descriptor, "ForbiddenBodyRegex");
        if (string.IsNullOrWhiteSpace(expectedContains) &&
            string.IsNullOrWhiteSpace(expectedRegex) &&
            string.IsNullOrWhiteSpace(forbiddenContains) &&
            string.IsNullOrWhiteSpace(forbiddenRegex))
        {
            return null;
        }

        var body = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(ct);

        if (!string.IsNullOrEmpty(expectedContains) &&
            !body.Contains(expectedContains, StringComparison.Ordinal))
        {
            return Failure(
                "body_assertion_failed",
                "Expected response body marker was not found.");
        }

        if (!string.IsNullOrWhiteSpace(expectedRegex) &&
            !BodyRegexMatches(body, expectedRegex, out var regexError))
        {
            return Failure(
                string.IsNullOrEmpty(regexError) ? "body_assertion_failed" : "invalid_body_regex",
                regexError ?? "Expected response body pattern was not found.");
        }

        if (!string.IsNullOrEmpty(forbiddenContains) &&
            body.Contains(forbiddenContains, StringComparison.Ordinal))
        {
            return Failure(
                "body_assertion_failed",
                "Forbidden response body marker was found.");
        }

        string? forbiddenRegexError = null;
        if (!string.IsNullOrWhiteSpace(forbiddenRegex) &&
            BodyRegexMatches(body, forbiddenRegex, out forbiddenRegexError))
        {
            return Failure(
                "body_assertion_failed",
                "Forbidden response body pattern was found.");
        }

        if (!string.IsNullOrEmpty(forbiddenRegexError))
        {
            return Failure("invalid_body_regex", forbiddenRegexError);
        }

        return null;
    }

    private static bool BodyRegexMatches(string body, string pattern, out string? error)
    {
        try
        {
            error = null;
            return Regex.IsMatch(body, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            error = $"Invalid response body regex: {ex.Message}";
            return false;
        }
    }

    private static HashSet<int> ParseExpectedStatuses(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => int.TryParse(t, out var i) ? i : 0)
            .Where(i => i > 0)
            .ToHashSet();

    private string ResolvePlaceholders(string raw)
    {
        if (string.IsNullOrEmpty(raw) || !raw.Contains(ConfigPlaceholderPrefix, StringComparison.Ordinal))
            return raw;
        return ConfigPlaceholderRegex.Replace(raw, m =>
        {
            var key = m.Groups["key"].Value;
            var value = _configuration[key];
            return value ?? string.Empty;
        });
    }

    private static HealthProbeOutcome Failure(string detail, string error) => new()
    {
        Status = HealthOutcomeStatus.Down,
        Detail = detail,
        ErrorMessage = error,
        ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
    };
}
