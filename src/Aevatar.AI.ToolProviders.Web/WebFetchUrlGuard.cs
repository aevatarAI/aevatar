using System.Net;
using System.Net.Sockets;

namespace Aevatar.AI.ToolProviders.Web;

/// <summary>
/// Validates a candidate URL before an LLM-driven WebFetch hits it. Rejects
/// non-HTTP(S) schemes and any host that resolves to a loopback, private, or
/// link-local IP — those targets are common SSRF pivots (cloud instance
/// metadata at 169.254.169.254, internal Docker/K8s networks, etc.).
/// </summary>
public static class WebFetchUrlGuard
{
    public static WebFetchValidationResult Validate(string? candidate)
    {
        var trimmed = candidate?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return WebFetchValidationResult.Reject("empty_url");

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return WebFetchValidationResult.Reject("invalid_url");

        if (uri.Scheme is not ("http" or "https"))
            return WebFetchValidationResult.Reject("unsupported_scheme");

        if (string.IsNullOrEmpty(uri.Host))
            return WebFetchValidationResult.Reject("missing_host");

        // Reject hostnames that resolve to an obviously dangerous range. The
        // common SSRF entry points are literal IPs ("http://169.254.169.254")
        // or "localhost" — both are checked here. We deliberately do not run a
        // full DNS resolution at validation time because that's racy (DNS can
        // change between check and fetch); the goal is to block the obvious
        // cases and rely on outbound network policy for the rest.
        if (IsHostLiteralIp(uri.Host, out var address) && IsBlockedAddress(address))
            return WebFetchValidationResult.Reject("blocked_private_address");

        if (IsLoopbackHostname(uri.Host))
            return WebFetchValidationResult.Reject("blocked_loopback_hostname");

        return WebFetchValidationResult.Accept(uri.ToString());
    }

    public static async Task<WebFetchValidationResult> ValidateResolvedAsync(
        string? candidate,
        CancellationToken ct = default)
    {
        var validation = Validate(candidate);
        if (!validation.IsAllowed)
            return validation;

        var uri = new Uri(validation.NormalizedUrl!);
        if (IsHostLiteralIp(uri.Host, out _))
            return validation;

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return WebFetchValidationResult.Reject("host_resolution_failed");
        }

        return addresses.Length == 0 || addresses.Any(IsBlockedAddress)
            ? WebFetchValidationResult.Reject("blocked_private_address")
            : validation;
    }

    private static bool IsHostLiteralIp(string host, out IPAddress address)
    {
        var stripped = host.StartsWith('[') && host.EndsWith(']')
            ? host[1..^1]
            : host;
        return IPAddress.TryParse(stripped, out address!);
    }

    private static bool IsBlockedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return IsBlockedIpv4(address.GetAddressBytes());

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal)
                return true;
            // IPv4-mapped IPv6: defer to the embedded IPv4 check.
            if (address.IsIPv4MappedToIPv6)
                return IsBlockedIpv4(address.MapToIPv4().GetAddressBytes());
        }

        return false;
    }

    private static bool IsBlockedIpv4(byte[] octets)
    {
        if (octets.Length != 4)
            return false;

        // 10.0.0.0/8
        if (octets[0] == 10)
            return true;
        // 127.0.0.0/8 (loopback)
        if (octets[0] == 127)
            return true;
        // 169.254.0.0/16 (link-local / cloud metadata)
        if (octets[0] == 169 && octets[1] == 254)
            return true;
        // 172.16.0.0/12
        if (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
            return true;
        // 192.168.0.0/16
        if (octets[0] == 192 && octets[1] == 168)
            return true;
        // 100.64.0.0/10 (carrier-grade NAT)
        if (octets[0] == 100 && octets[1] >= 64 && octets[1] <= 127)
            return true;
        // 192.0.0.0/24 (IETF protocol assignments)
        if (octets[0] == 192 && octets[1] == 0 && octets[2] == 0)
            return true;
        // 198.18.0.0/15 (benchmarking / internal test networks)
        if (octets[0] == 198 && (octets[1] == 18 || octets[1] == 19))
            return true;
        // 0.0.0.0/8 (unspecified)
        if (octets[0] == 0)
            return true;
        // 224.0.0.0/4 (multicast and reserved high ranges)
        if (octets[0] >= 224)
            return true;

        return false;
    }

    private static bool IsLoopbackHostname(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "ip6-localhost", StringComparison.OrdinalIgnoreCase);
}

public readonly record struct WebFetchValidationResult(bool IsAllowed, string? NormalizedUrl, string? RejectionCode)
{
    public static WebFetchValidationResult Accept(string normalizedUrl) =>
        new(true, normalizedUrl, null);

    public static WebFetchValidationResult Reject(string code) =>
        new(false, null, code);
}
