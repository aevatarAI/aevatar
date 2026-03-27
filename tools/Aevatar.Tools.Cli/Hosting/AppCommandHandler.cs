using System.Text.Json;

namespace Aevatar.Tools.Cli.Hosting;

internal static class AppCommandHandler
{
    private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(3);

    public static async Task RunAsync(string? url, bool noBrowser, CancellationToken cancellationToken)
    {
        var baseUrl = ResolveBaseUrl(url);

        Console.WriteLine($"Checking Mainnet host at {baseUrl} ...");
        var healthy = await ProbeMainnetAsync(baseUrl, cancellationToken);

        if (!healthy)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  Mainnet host is not reachable at {baseUrl}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Start it first:");
            Console.Error.WriteLine("    dotnet run --project src/Aevatar.Mainnet.Host.Api");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Or specify a different URL:");
            Console.Error.WriteLine("    aevatar app --url https://api.aevatar.ai");
            return;
        }

        Console.WriteLine($"Mainnet host is running at {baseUrl}");

        if (noBrowser)
        {
            Console.WriteLine($"Studio URL: {baseUrl}/studio");
            return;
        }

        var studioUrl = $"{baseUrl}/studio";
        BrowserLauncher.Open(studioUrl);
        Console.WriteLine($"Opened browser: {studioUrl}");
    }

    private static string ResolveBaseUrl(string? cliOverride)
    {
        if (!string.IsNullOrWhiteSpace(cliOverride))
            return cliOverride.Trim().TrimEnd('/');

        var configured = CliAppConfigStore.GetApiBaseUrl(out _);
        return configured?.TrimEnd('/') ?? "http://localhost:5080";
    }

    private static async Task<bool> ProbeMainnetAsync(
        string baseUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = HealthProbeTimeout };
            using var response = await http.GetAsync(baseUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return false;

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
                return false;

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            return root.TryGetProperty("status", out var statusProp) &&
                   string.Equals(statusProp.GetString(), "running", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
