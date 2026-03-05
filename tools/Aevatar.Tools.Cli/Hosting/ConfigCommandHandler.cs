using System.Diagnostics;
using System.Globalization;
using Aevatar.Tools.Config;

namespace Aevatar.Tools.Cli.Hosting;

internal static class ConfigCommandHandler
{
    private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromMilliseconds(500);

    internal sealed record ConfigUiEnsureResult(
        string Url,
        int Port,
        bool Started);

    public static Task RunUiAsync(int port, bool noBrowser, CancellationToken cancellationToken)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "wwwroot", "config"),
            Path.Combine(baseDirectory, "wwwroot"),
            Path.GetFullPath(Path.Combine(baseDirectory, "../../../../tools/Aevatar.Tools.Config/wwwroot")),
            Path.Combine(Environment.CurrentDirectory, "tools", "Aevatar.Tools.Config", "wwwroot"),
        };

        return ConfigToolHost.RunAsync(
            new ConfigToolHostOptions
            {
                Port = port,
                NoBrowser = noBrowser,
                BannerTitle = "aevatar config",
                WebRootCandidates = candidates,
            },
            cancellationToken);
    }

    public static async Task<ConfigUiEnsureResult> EnsureUiAsync(
        int port,
        bool noBrowser,
        CancellationToken cancellationToken)
    {
        var normalizedPort = port > 0 ? port : 6677;
        var url = $"http://localhost:{normalizedPort}";
        var health = await ProbeConfigHealthAsync(url, cancellationToken);
        if (health == ConfigHealthStatus.ReachableButNotConfigUi)
        {
            throw new InvalidOperationException(
                $"Port {normalizedPort} is occupied by a non-config-ui service. " +
                $"Please free the port or run with --port <newPort>.");
        }

        if (health == ConfigHealthStatus.HealthyConfigUi)
            return new ConfigUiEnsureResult(url, normalizedPort, Started: false);

        if (!TryStartConfigProcess(normalizedPort, noBrowser, out var startError))
            throw new InvalidOperationException(startError);

        await WaitForConfigReadyAsync(url, normalizedPort, cancellationToken);
        return new ConfigUiEnsureResult(url, normalizedPort, Started: true);
    }

    private static async Task WaitForConfigReadyAsync(
        string baseUrl,
        int port,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        while (DateTime.UtcNow - startedAt < StartupTimeout)
        {
            var health = await ProbeConfigHealthAsync(baseUrl, cancellationToken);
            if (health == ConfigHealthStatus.HealthyConfigUi)
                return;

            if (health == ConfigHealthStatus.ReachableButNotConfigUi)
            {
                throw new InvalidOperationException(
                    $"Port {port} is now responding, but it is not the aevatar config service.");
            }

            await Task.Delay(StartupPollInterval, cancellationToken);
        }

        throw new TimeoutException(
            $"aevatar config ui did not become ready on port {port} " +
            $"within {StartupTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds.");
    }

    private static async Task<ConfigHealthStatus> ProbeConfigHealthAsync(
        string baseUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = HealthProbeTimeout };
            using var response = await http.GetAsync(
                $"{baseUrl.TrimEnd('/')}/api/health",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return ConfigHealthStatus.ReachableButNotConfigUi;

            var payload = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            return string.Equals(payload, "ok", StringComparison.OrdinalIgnoreCase)
                ? ConfigHealthStatus.HealthyConfigUi
                : ConfigHealthStatus.ReachableButNotConfigUi;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ConfigHealthStatus.Unreachable;
        }
        catch (HttpRequestException)
        {
            return ConfigHealthStatus.Unreachable;
        }
    }

    private static bool TryStartConfigProcess(int port, bool noBrowser, out string error)
    {
        error = string.Empty;
        try
        {
            var startInfo = BuildConfigUiStartInfo(port, noBrowser);
            var process = Process.Start(startInfo);
            if (process == null)
            {
                error = "Failed to launch aevatar config ui process.";
                return false;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await process.StandardOutput.ReadToEndAsync();
                    await process.StandardError.ReadToEndAsync();
                }
                catch
                {
                    // Best effort drain.
                }
            });
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to launch aevatar config ui process: {ex.Message}";
            return false;
        }
    }

    private static ProcessStartInfo BuildConfigUiStartInfo(int port, bool noBrowser)
    {
        var args = new List<string>
        {
            "config",
            "ui",
            "--port",
            port.ToString(CultureInfo.InvariantCulture),
        };

        if (noBrowser)
            args.Add("--no-browser");

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            processPath = "aevatar";

        var startInfo = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (IsDotnetHost(processPath))
        {
            var assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            startInfo.ArgumentList.Add(assemblyPath);
        }

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        return startInfo;
    }

    private static bool IsDotnetHost(string processPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(processPath);
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private enum ConfigHealthStatus
    {
        HealthyConfigUi = 0,
        ReachableButNotConfigUi = 1,
        Unreachable = 2,
    }
}
