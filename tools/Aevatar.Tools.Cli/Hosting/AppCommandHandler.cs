using System.Diagnostics;
using System.Text.Json;

namespace Aevatar.Tools.Cli.Hosting;

internal static class AppCommandHandler
{
    private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PortReleaseTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PortReleasePollInterval = TimeSpan.FromMilliseconds(200);

    public static async Task RunAsync(int port, bool noBrowser, string? apiBase, CancellationToken cancellationToken)
    {
        var normalizedPort = port > 0 ? port : 6688;
        var localBaseUrl = $"http://localhost:{normalizedPort}";

        var health = await ProbeAppHealthAsync(localBaseUrl, cancellationToken);
        if (health == AppHealthStatus.HealthyAevatar)
        {
            Console.WriteLine($"aevatar app is already running on port {normalizedPort}.");
            OpenAppUi(localBaseUrl, noBrowser);
            return;
        }

        if (health == AppHealthStatus.ReachableButNotAevatar)
        {
            Console.Error.WriteLine(
                $"Port {normalizedPort} is occupied by a non-aevatar service. " +
                $"Use `aevatar app restart --port {normalizedPort}` to force restart.");
            return;
        }

        string apiBaseUrl;
        try
        {
            apiBaseUrl = CliAppConfigStore.ResolveApiBaseUrl(apiBase, localBaseUrl, out var warning);
            if (!string.IsNullOrWhiteSpace(warning))
                Console.WriteLine($"[warn] {warning}");
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return;
        }

        await StartHostAsync(normalizedPort, noBrowser, apiBaseUrl, localBaseUrl, cancellationToken);
    }

    public static async Task RestartAsync(int port, bool noBrowser, string? apiBase, CancellationToken cancellationToken)
    {
        var normalizedPort = port > 0 ? port : 6688;
        var localBaseUrl = $"http://localhost:{normalizedPort}";

        string apiBaseUrl;
        try
        {
            apiBaseUrl = CliAppConfigStore.ResolveApiBaseUrl(apiBase, localBaseUrl, out var warning);
            if (!string.IsNullOrWhiteSpace(warning))
                Console.WriteLine($"[warn] {warning}");
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return;
        }

        var pids = ListListeningPids(normalizedPort);
        if (pids.Count == 0)
        {
            Console.WriteLine($"No listening process found on port {normalizedPort}. Starting fresh app...");
        }
        else
        {
            Console.WriteLine($"Force restarting app on port {normalizedPort}. PID(s): {string.Join(", ", pids)}");
            foreach (var pid in pids)
                KillProcess(pid);

            var released = await WaitForPortReleaseAsync(normalizedPort, cancellationToken);
            if (!released)
            {
                Console.Error.WriteLine(
                    $"Failed to release port {normalizedPort}. " +
                    "Please terminate the process manually and retry.");
                return;
            }
        }

        await StartHostAsync(normalizedPort, noBrowser, apiBaseUrl, localBaseUrl, cancellationToken);
    }

    private static async Task StartHostAsync(
        int port,
        bool noBrowser,
        string apiBaseUrl,
        string localBaseUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            await AppToolHost.RunAsync(new AppToolHostOptions
            {
                Port = port,
                NoBrowser = noBrowser,
                ApiBaseUrl = apiBaseUrl,
            }, cancellationToken);
        }
        catch (Exception ex) when (IsAddressInUseFailure(ex))
        {
            var health = await ProbeAppHealthAsync(localBaseUrl, cancellationToken);
            if (health == AppHealthStatus.HealthyAevatar)
            {
                Console.WriteLine($"aevatar app is already running on port {port}.");
                OpenAppUi(localBaseUrl, noBrowser);
                return;
            }

            Console.Error.WriteLine(
                $"Port {port} is already in use and is not a healthy aevatar app endpoint.");
        }
    }

    private static void OpenAppUi(string localBaseUrl, bool noBrowser)
    {
        if (noBrowser)
        {
            Console.WriteLine($"aevatar app URL: {localBaseUrl}");
            return;
        }

        BrowserLauncher.Open(localBaseUrl);
        Console.WriteLine($"Opened aevatar app UI: {localBaseUrl}");
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

    private static IReadOnlyList<int> ListListeningPids(int port)
    {
        try
        {
            var startInfo = new ProcessStartInfo("lsof")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add($"-tiTCP:{port}");
            startInfo.ArgumentList.Add("-sTCP:LISTEN");

            using var process = Process.Start(startInfo);
            if (process == null)
                return [];

            var output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit(2000);

            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => int.TryParse(line, out var pid) ? pid : -1)
                .Where(pid => pid > 0)
                .Distinct()
                .OrderBy(pid => pid)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static void KillProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
                return;

            Console.WriteLine($"Stopping process pid={pid}...");
            process.Kill(entireProcessTree: true);
        }
        catch (PlatformNotSupportedException)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                    return;
                process.Kill();
            }
            catch
            {
            }
        }
        catch
        {
        }
    }

    private static async Task<bool> WaitForPortReleaseAsync(int port, CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        while (DateTime.UtcNow - startedAt < PortReleaseTimeout)
        {
            if (ListListeningPids(port).Count == 0)
                return true;

            await Task.Delay(PortReleasePollInterval, cancellationToken);
        }

        return ListListeningPids(port).Count == 0;
    }

    private static bool IsAddressInUseFailure(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("eaddrinuse", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("only one usage of each socket address", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private enum AppHealthStatus
    {
        HealthyAevatar = 0,
        ReachableButNotAevatar = 1,
        Unreachable = 2,
    }
}
