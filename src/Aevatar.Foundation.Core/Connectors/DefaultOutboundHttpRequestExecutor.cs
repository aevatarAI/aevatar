using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Aevatar.Foundation.Abstractions.Connectors;

namespace Aevatar.Foundation.Core.Connectors;

public sealed class DefaultOutboundHttpRequestExecutor : IOutboundHttpRequestExecutor
{
    public const int DefaultTimeoutMs = 30_000;
    public const int DefaultMaxResponseBytes = 65_536;
    public const int DefaultMaxRedirects = 3;

    private static readonly HttpRequestOptionsKey<bool> AllowPrivateNetworkOption =
        new("Aevatar.Foundation.Connectors.AllowPrivateNetwork");

    private static readonly HttpClient SharedHttpClient =
        CreateHardenedHttpClient(new DefaultOutboundHttpDnsResolver());

    private readonly HttpClient _client;
    private readonly IOutboundHttpDnsResolver _dnsResolver;

    public DefaultOutboundHttpRequestExecutor()
        : this(SharedHttpClient)
    {
    }

    public DefaultOutboundHttpRequestExecutor(IOutboundHttpDnsResolver dnsResolver)
        : this(CreateHardenedHttpClient(dnsResolver ?? throw new ArgumentNullException(nameof(dnsResolver))), dnsResolver)
    {
    }

    public DefaultOutboundHttpRequestExecutor(
        HttpClient client,
        IOutboundHttpDnsResolver? dnsResolver = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _dnsResolver = dnsResolver ?? new DefaultOutboundHttpDnsResolver();
    }

    public async Task<OutboundHttpResponse> ExecuteAsync(
        OutboundHttpRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var targetUri))
            return Failure("http_request url must be an absolute URL");

        var method = NormalizeMethod(request.Method);
        var timeoutMs = ClampOrDefault(request.TimeoutMs, 100, 300_000, DefaultTimeoutMs);
        var maxResponseBytes = ClampOrDefault(request.MaxResponseBytes, 1, 10 * 1024 * 1024, DefaultMaxResponseBytes);
        var maxRedirects = ClampOrDefault(request.MaxRedirects, 0, 10, DefaultMaxRedirects);
        var currentUri = ApplyQuery(targetUri, request.Query);
        var currentMethod = method;
        var body = request.Body ?? string.Empty;
        var contentType = string.IsNullOrWhiteSpace(request.ContentType)
            ? "application/json"
            : request.ContentType.Trim();
        var sw = Stopwatch.StartNew();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        for (var redirectCount = 0; ; redirectCount++)
        {
            var policyError = await ValidateTargetAsync(currentUri, request, timeoutCts.Token);
            if (!string.IsNullOrWhiteSpace(policyError))
                return Failure(policyError, currentMethod, currentUri, sw);

            using var message = BuildRequestMessage(
                currentMethod,
                currentUri,
                request,
                body,
                contentType);

            try
            {
                using var response = await _client.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirectCount >= maxRedirects)
                        return Failure("http redirect limit exceeded", currentMethod, currentUri, sw);

                    if (response.Headers.Location == null)
                        return Failure("http redirect response missing Location header", currentMethod, currentUri, sw);

                    currentUri = ResolveRedirectUri(currentUri, response.Headers.Location);
                    if (response.StatusCode == HttpStatusCode.SeeOther)
                    {
                        currentMethod = "GET";
                        body = string.Empty;
                    }

                    continue;
                }

                var read = await ReadContentAsync(response.Content, maxResponseBytes, timeoutCts.Token);
                if (read.Exceeded)
                    return Failure($"http response exceeded {maxResponseBytes} bytes", currentMethod, currentUri, sw);

                sw.Stop();
                var metadata = BuildMetadata(response, currentMethod, currentUri, sw.Elapsed.TotalMilliseconds);
                return new OutboundHttpResponse
                {
                    Success = response.IsSuccessStatusCode,
                    Output = read.Text,
                    Error = response.IsSuccessStatusCode ? string.Empty : BuildHttpErrorMessage(response, read.Text),
                    Metadata = metadata,
                };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Failure($"http timeout after {timeoutMs}ms", currentMethod, currentUri, sw);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return Failure(ex.Message, currentMethod, currentUri, sw);
            }
        }
    }

    private static HttpRequestMessage BuildRequestMessage(
        string method,
        Uri uri,
        OutboundHttpRequest request,
        string body,
        string contentType)
    {
        var message = new HttpRequestMessage(new HttpMethod(method), uri);
        message.Options.Set(AllowPrivateNetworkOption, request.AllowPrivateNetwork);

        foreach (var (key, value) in request.Headers)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            message.Headers.TryAddWithoutValidation(key.Trim(), value);
        }

        if (!string.IsNullOrWhiteSpace(request.Authorization) &&
            AuthenticationHeaderValue.TryParse(request.Authorization.Trim(), out var authorization))
        {
            message.Headers.Authorization = authorization;
        }

        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey) &&
            !message.Headers.Contains("Idempotency-Key"))
        {
            message.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey.Trim());
        }

        if (!message.Headers.Accept.Any())
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrEmpty(body) || method is not ("GET" or "HEAD"))
            message.Content = new StringContent(body, Encoding.UTF8, contentType);

        return message;
    }

    private static HttpClient CreateHardenedHttpClient(IOutboundHttpDnsResolver dnsResolver) =>
        new(
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = (context, ct) => ConnectValidatedSocketAsync(context, dnsResolver, ct),
            });

    private static async ValueTask<Stream> ConnectValidatedSocketAsync(
        SocketsHttpConnectionContext context,
        IOutboundHttpDnsResolver dnsResolver,
        CancellationToken ct)
    {
        var allowPrivateNetwork =
            context.InitialRequestMessage?.Options.TryGetValue(AllowPrivateNetworkOption, out var configured) == true &&
            configured;
        var addresses = await ResolveAddressesAsync(context.DnsEndPoint.Host, dnsResolver, ct);
        if (addresses.Count == 0)
            throw new HttpRequestException($"http DNS resolution returned no addresses for '{context.DnsEndPoint.Host}'");

        if (!allowPrivateNetwork && addresses.Any(IsBlockedAddress))
            throw new HttpRequestException($"http target '{context.DnsEndPoint.Host}' resolved to a blocked destination");

        var errors = new List<Exception>();
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                errors.Add(ex);
                socket.Dispose();
            }
        }

        throw new HttpRequestException(
            $"http connection failed for '{context.DnsEndPoint.Host}'",
            new AggregateException(errors));
    }

    private async ValueTask<string> ValidateTargetAsync(
        Uri uri,
        OutboundHttpRequest request,
        CancellationToken ct)
    {
        if (!uri.IsAbsoluteUri)
            return "http target must be absolute";

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !(request.AllowInsecureHttp &&
              string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            return "http target must use HTTPS";
        }

        var addresses = await ResolveAddressesAsync(uri.Host, ct);
        if (addresses.Count == 0)
            return $"http DNS resolution returned no addresses for '{uri.Host}'";

        if (!request.AllowPrivateNetwork && addresses.Any(IsBlockedAddress))
            return $"http target '{uri.Host}' resolved to a blocked destination";

        return string.Empty;
    }

    private async ValueTask<IReadOnlyList<IPAddress>> ResolveAddressesAsync(
        string host,
        CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
            return [literal];

        return await ResolveAddressesAsync(host, _dnsResolver, ct);
    }

    private static async ValueTask<IReadOnlyList<IPAddress>> ResolveAddressesAsync(
        string host,
        IOutboundHttpDnsResolver dnsResolver,
        CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
            return [literal];

        return await dnsResolver.GetHostAddressesAsync(host, ct);
    }

    private static bool IsBlockedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) ||
            IPAddress.IsLoopback(address.MapToIPv6()) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None) ||
            address.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ||
            address.IsIPv4MappedToIPv6)
        {
            var bytes = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).GetAddressBytes();
            return bytes[0] == 0 ||
                   bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   bytes[0] == 169 && bytes[1] == 254 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||
                   bytes[0] >= 224;
        }

        return address.IsIPv6LinkLocal ||
               address.IsIPv6Multicast ||
               address.IsIPv6SiteLocal ||
               IsUniqueLocalIPv6(address);
    }

    private static bool IsUniqueLocalIPv6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc;
    }

    private static Uri ApplyQuery(Uri uri, IReadOnlyDictionary<string, string> query)
    {
        if (query.Count == 0)
            return uri;

        var builder = new UriBuilder(uri);
        var existing = builder.Query;
        if (existing.StartsWith("?", StringComparison.Ordinal))
            existing = existing[1..];

        var appended = string.Join(
            "&",
            query
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .Select(pair =>
                    string.Concat(
                        Uri.EscapeDataString(pair.Key.Trim()),
                        "=",
                        Uri.EscapeDataString(pair.Value ?? string.Empty))));
        builder.Query = string.IsNullOrWhiteSpace(existing)
            ? appended
            : string.Concat(existing, "&", appended);
        return builder.Uri;
    }

    private static Uri ResolveRedirectUri(Uri currentUri, Uri location) =>
        location.IsAbsoluteUri ? location : new Uri(currentUri, location);

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Found or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect or
            HttpStatusCode.MultipleChoices;

    private static async ValueTask<(bool Exceeded, string Text)> ReadContentAsync(
        HttpContent content,
        int maxResponseBytes,
        CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(Math.Min(maxResponseBytes, 8192));
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0)
                break;

            if (buffer.Length + read > maxResponseBytes)
                return (true, string.Empty);

            buffer.Write(chunk, 0, read);
        }

        return (false, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static Dictionary<string, string> BuildMetadata(
        HttpResponseMessage response,
        string method,
        Uri uri,
        double durationMs) =>
        new()
        {
            ["connector.http.status_code"] = ((int)response.StatusCode).ToString(),
            ["connector.http.reason"] = response.ReasonPhrase ?? string.Empty,
            ["connector.http.method"] = method,
            ["connector.http.url"] = uri.ToString(),
            ["connector.http.duration_ms"] = durationMs.ToString("F2"),
        };

    private static OutboundHttpResponse Failure(string error) =>
        new()
        {
            Success = false,
            Error = error,
        };

    private static OutboundHttpResponse Failure(
        string error,
        string method,
        Uri uri,
        Stopwatch sw)
    {
        sw.Stop();
        return new OutboundHttpResponse
        {
            Success = false,
            Error = error,
            Metadata = new Dictionary<string, string>
            {
                ["connector.http.method"] = method,
                ["connector.http.url"] = uri.ToString(),
                ["connector.http.duration_ms"] = sw.Elapsed.TotalMilliseconds.ToString("F2"),
            },
        };
    }

    private static string BuildHttpErrorMessage(HttpResponseMessage response, string body)
    {
        var baseMessage = $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();
        var detail = TryExtractErrorDetail(body);
        return string.IsNullOrWhiteSpace(detail) ? baseMessage : $"{baseMessage}: {detail}";
    }

    private static string TryExtractErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return RawBodyPreview(body);

            if (document.RootElement.TryGetProperty("description", out var description) &&
                description.ValueKind == JsonValueKind.String)
            {
                return description.GetString()?.Trim() ?? string.Empty;
            }

            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String)
            {
                return error.GetString()?.Trim() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            return RawBodyPreview(body);
        }

        return RawBodyPreview(body);
    }

    private static string RawBodyPreview(string body)
    {
        var trimmed = body.Trim();
        return trimmed.Length <= 200 ? trimmed : $"{trimmed[..200]}...";
    }

    private static string NormalizeMethod(string? method) =>
        string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();

    private static int ClampOrDefault(int value, int min, int max, int fallback)
    {
        if (value <= 0)
            return fallback;
        return Math.Clamp(value, min, max);
    }

    private sealed class DefaultOutboundHttpDnsResolver : IOutboundHttpDnsResolver
    {
        public async ValueTask<IReadOnlyList<IPAddress>> GetHostAddressesAsync(
            string host,
            CancellationToken ct = default) =>
            await Dns.GetHostAddressesAsync(host, ct);
    }
}
