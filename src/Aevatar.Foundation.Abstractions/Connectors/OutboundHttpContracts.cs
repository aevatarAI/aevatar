using System.Net;

namespace Aevatar.Foundation.Abstractions.Connectors;

public interface IOutboundHttpRequestExecutor
{
    Task<OutboundHttpResponse> ExecuteAsync(
        OutboundHttpRequest request,
        CancellationToken ct = default);
}

public interface IOutboundHttpDnsResolver
{
    ValueTask<IReadOnlyList<IPAddress>> GetHostAddressesAsync(
        string host,
        CancellationToken ct = default);
}

public sealed class OutboundHttpRequest
{
    public string Method { get; init; } = "GET";

    public string Url { get; init; } = "";

    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    public string Authorization { get; init; } = "";

    public string IdempotencyKey { get; init; } = "";

    public string Body { get; init; } = "";

    public string ContentType { get; init; } = "";

    public int TimeoutMs { get; init; }

    public int MaxResponseBytes { get; init; }

    public int MaxRedirects { get; init; }

    public bool AllowInsecureHttp { get; init; }

    public bool AllowPrivateNetwork { get; init; }
}

public sealed class OutboundHttpResponse
{
    public bool Success { get; init; }

    public string Output { get; init; } = "";

    public string Error { get; init; } = "";

    public Dictionary<string, string> Metadata { get; init; } = [];
}
