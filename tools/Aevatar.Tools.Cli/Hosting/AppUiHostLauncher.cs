using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Aevatar.Tools.Cli.Hosting;

internal static class AppUiHostLauncher
{
    private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromMilliseconds(500);

    public static async Task<string> EnsureReadyAsync(
        int port,
        string apiBaseUrl,
        CancellationToken cancellationToken)
    {
        var normalizedPort = port > 0 ? port : 6688;
        var localBaseUrl = $"http://localhost:{normalizedPort}";

        var health = await ProbeAppHealthAsync(localBaseUrl, cancellationToken);
        if (health == AppHealthStatus.ReachableButNotAevatar)
        {
            throw new InvalidOperationException(
                $"Port {normalizedPort} is occupied by a non-aevatar service. " +
                "Please free the port or run with --port <newPort>.");
        }

        if (health == AppHealthStatus.Unreachable)
        {
            Console.WriteLine($"aevatar app is not running on port {normalizedPort}. Starting app...");
            if (!TryStartAppProcess(normalizedPort, apiBaseUrl, localBaseUrl, out var startError))
                throw new InvalidOperationException(startError);

            await WaitForAppReadyAsync(localBaseUrl, normalizedPort, cancellationToken);
        }

        await UpdateRuntimeUrlAsync(localBaseUrl, apiBaseUrl, cancellationToken);
        return localBaseUrl;
    }

    private static async Task WaitForAppReadyAsync(
        string localBaseUrl,
        int port,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        while (DateTime.UtcNow - startedAt < StartupTimeout)
        {
            var health = await ProbeAppHealthAsync(localBaseUrl, cancellationToken);
            if (health == AppHealthStatus.HealthyAevatar)
                return;

            if (health == AppHealthStatus.ReachableButNotAevatar)
            {
                throw new InvalidOperationException(
                    $"Port {port} is now responding, but it is not the aevatar app process.");
            }

            await Task.Delay(StartupPollInterval, cancellationToken);
        }

        throw new TimeoutException(
            $"aevatar app did not become ready on port {port} within {StartupTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds.");
    }

    private static async Task<AppHealthStatus> ProbeAppHealthAsync(
        string localBaseUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = HealthProbeTimeout };
            using var response = await http.GetAsync(
                $"{localBaseUrl.TrimEnd('/')}/api/health",
                cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return AppHealthStatus.ReachableButNotAevatar;

            if (string.IsNullOrWhiteSpace(payload))
                return AppHealthStatus.ReachableButNotAevatar;

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
            var service = root.TryGetProperty("service", out var serviceProp)
                ? serviceProp.GetString()
                : null;
            if (ok && string.Equals(service, "aevatar.app", StringComparison.Ordinal))
                return AppHealthStatus.HealthyAevatar;

            return AppHealthStatus.ReachableButNotAevatar;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AppHealthStatus.Unreachable;
        }
        catch (HttpRequestException)
        {
            return AppHealthStatus.Unreachable;
        }
    }

    private static async Task UpdateRuntimeUrlAsync(
        string localBaseUrl,
        string apiBaseUrl,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = HealthProbeTimeout };
        using var response = await http.PutAsJsonAsync(
            $"{localBaseUrl.TrimEnd('/')}/api/_proxy/runtime-url",
            new RuntimeUrlUpdate(apiBaseUrl.TrimEnd('/')),
            cancellationToken);

        if (response.IsSuccessStatusCode)
            return;

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"Failed to configure aevatar app runtime URL to '{apiBaseUrl}': HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {detail}".Trim());
    }

    private static bool TryStartAppProcess(
        int port,
        string apiBaseUrl,
        string localBaseUrl,
        out string error)
    {
        error = string.Empty;
        try
        {
            var startInfo = BuildAppStartInfo(port, apiBaseUrl, localBaseUrl);
            var process = Process.Start(startInfo);
            if (process == null)
            {
                error = "Failed to launch aevatar app process.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to launch aevatar app process: {ex.Message}";
            return false;
        }
    }

    private static ProcessStartInfo BuildAppStartInfo(int port, string apiBaseUrl, string localBaseUrl)
    {
        var appArgs = new List<string>
        {
            "app",
            "--no-browser",
            "--port",
            port.ToString(CultureInfo.InvariantCulture),
        };

        if (!string.Equals(apiBaseUrl, localBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            appArgs.Add("--url");
            appArgs.Add(apiBaseUrl);
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            processPath = "aevatar";

        var startInfo = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        if (IsDotnetHost(processPath))
        {
            var assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            startInfo.ArgumentList.Add(assemblyPath);
        }

        foreach (var arg in appArgs)
            startInfo.ArgumentList.Add(arg);

        return startInfo;
    }

    private static bool IsDotnetHost(string processPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(processPath);
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record RuntimeUrlUpdate(string RuntimeBaseUrl);

    private enum AppHealthStatus
    {
        HealthyAevatar = 0,
        ReachableButNotAevatar = 1,
        Unreachable = 2,
    }
}
