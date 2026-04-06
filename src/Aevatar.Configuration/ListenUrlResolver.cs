using Microsoft.Extensions.Configuration;

namespace Aevatar.Configuration;

public static class ListenUrlResolver
{
    public static string ResolveListenUrls(
        string? explicitListenUrls,
        IConfiguration configuration,
        string? configurationKey,
        int defaultPort)
    {
        var configured = FirstNonEmpty(
            explicitListenUrls,
            string.IsNullOrWhiteSpace(configurationKey) ? null : configuration[configurationKey!],
            configuration["ASPNETCORE_URLS"],
            Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));

        return string.IsNullOrWhiteSpace(configured)
            ? $"http://localhost:{defaultPort}"
            : configured.Trim();
    }

    public static string ResolveBrowserUrl(string listenUrls, int defaultPort)
    {
        foreach (var candidate in SplitListenUrls(listenUrls))
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
                continue;

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var builder = new UriBuilder(uri)
            {
                Host = NormalizeBrowserHost(uri.Host),
                Path = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty,
            };
            return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        return $"http://localhost:{defaultPort}";
    }

    private static IEnumerable<string> SplitListenUrls(string? listenUrls) =>
        string.IsNullOrWhiteSpace(listenUrls)
            ? []
            : listenUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeBrowserHost(string host) =>
        host switch
        {
            "*" or "+" or "0.0.0.0" or "[::]" or "::" => "localhost",
            _ => host,
        };

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
