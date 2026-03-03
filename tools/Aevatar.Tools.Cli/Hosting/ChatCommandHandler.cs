using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Aevatar.Configuration;

namespace Aevatar.Tools.Cli.Hosting;

internal static class ChatCommandHandler
{
    private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromMilliseconds(500);

    public static async Task RunAsync(
        string? message,
        int port,
        string? urlOverride,
        CancellationToken cancellationToken)
    {
        var prompt = (message ?? string.Empty).Trim();
        if (prompt.Length == 0)
        {
            Console.Error.WriteLine("Chat message is required. Example: aevatar chat \"hello\".");
            return;
        }

        var normalizedPort = port > 0 ? port : 6688;
        var localBaseUrl = $"http://localhost:{normalizedPort}";

        string apiBaseUrl;
        try
        {
            apiBaseUrl = CliAppConfigStore.ResolveApiBaseUrl(urlOverride, localBaseUrl, out var warning);
            if (!string.IsNullOrWhiteSpace(warning))
                Console.WriteLine($"[warn] {warning}");
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return;
        }

        var health = await ProbeAppHealthAsync(localBaseUrl, cancellationToken);
        if (health == AppHealthStatus.ReachableButNotAevatar)
        {
            Console.Error.WriteLine(
                $"Port {normalizedPort} is occupied by a non-aevatar service. " +
                $"Please free the port or run with --port <newPort>.");
            return;
        }

        if (health == AppHealthStatus.Unreachable)
        {
            Console.WriteLine($"aevatar app is not running on port {normalizedPort}. Starting app...");
            if (!TryStartAppProcess(normalizedPort, apiBaseUrl, localBaseUrl, out var startError))
            {
                Console.Error.WriteLine(startError);
                return;
            }

            try
            {
                await WaitForAppReadyAsync(localBaseUrl, normalizedPort, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return;
            }
        }

        var uiUrl = BuildUiUrl(localBaseUrl, prompt);
        BrowserLauncher.Open(uiUrl);
        Console.WriteLine($"Opened aevatar app UI: {uiUrl}");
    }

    public static void SetApiBaseUrl(string url)
    {
        try
        {
            CliAppConfigStore.SetApiBaseUrl(url);
            var configured = CliAppConfigStore.GetApiBaseUrl(out _);
            Console.WriteLine($"Saved chat API base URL: {configured}");
            Console.WriteLine($"Config file: {AevatarPaths.ConfigJson}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
        }
    }

    public static void GetApiBaseUrl()
    {
        var value = CliAppConfigStore.GetApiBaseUrl(out var warning);
        if (!string.IsNullOrWhiteSpace(warning))
            Console.WriteLine($"[warn] {warning}");

        if (string.IsNullOrWhiteSpace(value))
        {
            Console.WriteLine("Chat API base URL is not configured.");
            return;
        }

        Console.WriteLine(value);
    }

    public static void ClearApiBaseUrl()
    {
        try
        {
            var removed = CliAppConfigStore.ClearApiBaseUrl();
            Console.WriteLine(removed
                ? "Cleared chat API base URL."
                : "Chat API base URL is already empty.");
            Console.WriteLine($"Config file: {AevatarPaths.ConfigJson}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
        }
    }

    private static string BuildUiUrl(string localBaseUrl, string prompt)
    {
        var encodedPrompt = Uri.EscapeDataString(prompt);
        return $"{localBaseUrl.TrimEnd('/')}/?chat={encodedPrompt}";
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
                $"{localBaseUrl.TrimEnd('/')}/api/app/health",
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
            appArgs.Add("--api-base");
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

    private enum AppHealthStatus
    {
        HealthyAevatar = 0,
        ReachableButNotAevatar = 1,
        Unreachable = 2,
    }
}
