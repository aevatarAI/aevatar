using System.Net.Sockets;
using Aevatar.Configuration;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.App.Tests;

public sealed class AppHostFixture : IAsyncLifetime
{
    private readonly Dictionary<string, string?> _originalEnvironment = new(StringComparer.Ordinal);
    private CancellationTokenSource? _shutdownCts;
    private Task? _hostTask;

    public HttpClient Client { get; private set; } = null!;
    public Uri BaseUri { get; private set; } = null!;
    public string BaseUrl => BaseUri.ToString().TrimEnd('/');
    public string TempRootDirectory { get; private set; } = string.Empty;
    public string AevatarHomeDirectory { get; private set; } = string.Empty;
    public string SeedWorkflowFilePath { get; private set; } = string.Empty;
    public string SeedWorkflowYaml { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        TempRootDirectory = Path.Combine(Path.GetTempPath(), "aevatar-app-tests", Guid.NewGuid().ToString("N"));
        AevatarHomeDirectory = Path.Combine(TempRootDirectory, "home");
        Directory.CreateDirectory(TempRootDirectory);
        Directory.CreateDirectory(Path.Combine(AevatarHomeDirectory, "workflows"));

        SetEnvironmentVariable("AEVATAR_HOME", AevatarHomeDirectory);
        SetEnvironmentVariable("Cli__App__NyxId__Enabled", "false");
        SetEnvironmentVariable("Cli__App__Connectors__ChronoStorage__Enabled", "false");

        var sourceWorkflowPath = Path.Combine(
            AevatarPaths.RepoRoot,
            "tools",
            "Aevatar.Tools.Cli",
            "workflows",
            "telegram_openclaw_bridge_chat.yaml");
        SeedWorkflowYaml = await File.ReadAllTextAsync(sourceWorkflowPath);
        SeedWorkflowFilePath = Path.Combine(AevatarHomeDirectory, "workflows", "smoke_workflow.yaml");
        await File.WriteAllTextAsync(SeedWorkflowFilePath, SeedWorkflowYaml);

        var port = GetFreeTcpPort();
        BaseUri = new Uri($"http://localhost:{port}/");
        Client = new HttpClient
        {
            BaseAddress = BaseUri,
            Timeout = TimeSpan.FromSeconds(30),
        };

        _shutdownCts = new CancellationTokenSource();
        var startedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hostTask = Task.Run(() => AppToolHost.RunAsync(
            new AppToolHostOptions
            {
                Port = port,
                NoBrowser = true,
                StartedSignal = startedSignal,
            },
            _shutdownCts.Token));

        await startedSignal.Task.WaitAsync(TimeSpan.FromSeconds(45));
        await EnsureHealthyAsync();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();

        if (_shutdownCts != null)
        {
            _shutdownCts.Cancel();
        }

        if (_hostTask != null)
        {
            try
            {
                await _hostTask.WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdownCts?.Dispose();
        RestoreEnvironmentVariables();

        if (!string.IsNullOrWhiteSpace(TempRootDirectory) && Directory.Exists(TempRootDirectory))
        {
            Directory.Delete(TempRootDirectory, recursive: true);
        }
    }

    public async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> PostJsonAsync(string path, object? payload, HttpStatusCode expectedStatusCode = HttpStatusCode.OK, CancellationToken cancellationToken = default)
    {
        using var response = await Client.PostAsJsonAsync(path, payload, cancellationToken);
        response.StatusCode.Should().Be(expectedStatusCode);
        return await ReadJsonAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> PutJsonAsync(string path, object? payload, HttpStatusCode expectedStatusCode = HttpStatusCode.OK, CancellationToken cancellationToken = default)
    {
        using var response = await Client.PutAsJsonAsync(path, payload, cancellationToken);
        response.StatusCode.Should().Be(expectedStatusCode);
        return await ReadJsonAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> WaitForJsonAsync(
        string path,
        Func<JsonDocument, bool> predicate,
        TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        while (true)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            try
            {
                var document = await GetJsonAsync(path, timeoutCts.Token);
                if (predicate(document))
                {
                    return document;
                }

                document.Dispose();
            }
            catch (HttpRequestException) when (!timeoutCts.IsCancellationRequested)
            {
            }

            await Task.Yield();
        }
    }

    private async Task EnsureHealthyAsync()
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            try
            {
                using var response = await Client.GetAsync("/api/app/health", timeoutCts.Token);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException) when (!timeoutCts.IsCancellationRequested)
            {
            }

            await Task.Yield();
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        content.Should().NotBeNullOrWhiteSpace();
        return JsonDocument.Parse(content);
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private void SetEnvironmentVariable(string name, string value)
    {
        if (!_originalEnvironment.ContainsKey(name))
        {
            _originalEnvironment[name] = Environment.GetEnvironmentVariable(name);
        }

        Environment.SetEnvironmentVariable(name, value);
    }

    private void RestoreEnvironmentVariables()
    {
        foreach (var pair in _originalEnvironment)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }
}
