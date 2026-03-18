using Aevatar.Bootstrap.Hosting;
using Aevatar.Bootstrap;
using Aevatar.Bootstrap.Connectors;
using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Tools.Cli.Bridge;
using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using Aevatar.Tools.Cli.Studio.Application.DependencyInjection;
using Aevatar.Tools.Cli.Studio.Host.Controllers;
using Aevatar.Tools.Cli.Studio.Infrastructure.DependencyInjection;
using Aevatar.Tools.Cli.Studio.Infrastructure.Storage;
using Aevatar.Workflow.Extensions.Bridge;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Infrastructure.Workflows;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.Tools.Cli.Hosting;

internal sealed class AppToolHostOptions
{
    public int Port { get; init; } = 6688;
    public bool NoBrowser { get; init; }
    public string? ApiBaseUrl { get; init; }
    public TaskCompletionSource<bool>? StartedSignal { get; init; }
}

internal static class AppToolHost
{
    public static async Task RunAsync(AppToolHostOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var port = options.Port <= 0 ? 6688 : options.Port;
            var localUrl = $"http://localhost:{port}";
            var sdkBaseUrl = CliAppConfigStore.ResolveApiBaseUrl(options.ApiBaseUrl, localUrl, out var warning);
            if (!string.IsNullOrWhiteSpace(warning))
                Console.WriteLine($"[warn] {warning}");

            var embeddedWorkflowMode = ShouldUseEmbeddedWorkflow(localUrl, sdkBaseUrl);

            var toolDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
                ?? Environment.CurrentDirectory;
            var webRootCandidates = new[]
            {
                Path.Combine(Environment.CurrentDirectory, "tools", "Aevatar.Tools.Cli", "wwwroot", "playground"),
                Path.Combine(AevatarPaths.RepoRoot, "tools", "Aevatar.Tools.Cli", "wwwroot", "playground"),
                Path.Combine(Environment.CurrentDirectory, "demos", "Aevatar.Demos.Workflow.Web", "wwwroot"),
                Path.Combine(AevatarPaths.RepoRoot, "demos", "Aevatar.Demos.Workflow.Web", "wwwroot"),
                Path.Combine(toolDir, "wwwroot", "playground"),
            };
            var webRootPath = webRootCandidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "index.html")))
                ?? webRootCandidates[0];

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ContentRootPath = toolDir,
                WebRootPath = webRootPath,
            });

            builder.WebHost.UseUrls(localUrl);
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Configuration.AddAevatarConfig();
            var nyxIdAuthEnabled = NyxIdAppAuthentication.ResolveIsEnabled(builder.Configuration, embeddedWorkflowMode);
            var nyxIdAuthOptions = NyxIdAppAuthentication.BuildOptions(builder.Configuration);
            builder.Services.Configure<JsonOptions>(json =>
            {
                json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            });
            builder.Services
                .AddControllers()
                .AddApplicationPart(typeof(EditorController).Assembly)
                .AddJsonOptions(json =>
                {
                    json.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                    json.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                });

            if (nyxIdAuthEnabled)
            {
                builder.Services.AddNyxIdAppAuthentication(nyxIdAuthOptions);
                builder.Services.AddSingleton<IStudioBackendRequestAuthSnapshotProvider, NyxIdStudioBackendRequestAuthSnapshotProvider>();
            }
            else
                builder.Services.AddHttpContextAccessor();

            var backendClient = builder.Services
                .AddHttpClient("AppBridgeBackend", client => client.BaseAddress = new Uri(sdkBaseUrl))
                .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                });
            if (nyxIdAuthEnabled)
            {
                backendClient.AddHttpMessageHandler<NyxIdAccessTokenHandler>();
            }

            var nyxProxyClient = builder.Services.AddHttpClient(ChronoStorageCatalogBlobClient.NyxProxyHttpClientName);
            if (nyxIdAuthEnabled)
            {
                nyxProxyClient.AddHttpMessageHandler<NyxIdAccessTokenHandler>();
            }

            builder.Services.AddSingleton<IAppScopeResolver, DefaultAppScopeResolver>();
            builder.Services.AddStudioInfrastructure(builder.Configuration);
            builder.Services.AddStudioApplication();
            builder.Services.AddSingleton<ScriptEditorValidationService>();
            builder.Services.AddSingleton(sp => new AppScopedWorkflowService(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<Aevatar.Tools.Cli.Studio.Application.Abstractions.IWorkflowYamlDocumentService>(),
                sp.GetService<IScopeWorkflowQueryPort>(),
                sp.GetService<IScopeWorkflowCommandPort>(),
                sp.GetService<Aevatar.Workflow.Application.Abstractions.Runs.IWorkflowActorBindingReader>()));
            builder.Services.Configure<StudioStorageOptions>(storage =>
            {
                storage.DefaultRuntimeBaseUrl = localUrl;
                storage.ForceLocalRuntime = embeddedWorkflowMode;
            });

            if (embeddedWorkflowMode)
            {
                builder.Services.AddSingleton<WorkflowGeneratePromptCatalog>();
                builder.Services.AddSingleton<WorkflowGenerateOrchestrator>();
                builder.Services.AddSingleton<WorkflowGenerateActorService>();
                builder.Services.AddSingleton<ScriptGeneratePromptCatalog>();
                builder.Services.AddSingleton<ScriptGenerateOrchestrator>();
                builder.Services.AddSingleton<ScriptGenerateActorService>();
                builder.Services.AddSingleton<Aevatar.Foundation.Core.Configurations.IAgentClassDefaultsProvider<Aevatar.AI.Core.AIAgentConfig>, WorkflowGenerateAgentDefaultsProvider>();
                builder.Services.AddHostedService<WorkflowGenerateActorBootstrapHostedService>();
                builder.Services.AddHostedService<ScriptGenerateActorBootstrapHostedService>();
                builder.AddAevatarDefaultHost(options =>
                {
                    options.ServiceName = "aevatar.app";
                    options.EnableWebSockets = true;
                    options.EnableConnectorBootstrap = false;
                    options.MapRootHealthEndpoint = false;
                });
                builder.AddAevatarPlatform(options =>
                {
                    options.EnableMakerExtensions = true;
                    options.ConfigureAIFeatures = ai =>
                    {
                        ai.EnableMEAIToTornadoFailover = true;
                        ai.EnableReloadableProviderFactory = true;
                    };
                });
                builder.AddGAgentServiceCapabilityBundle();
                builder.Services.AddWorkflowBridgeExtensions();
                builder.Services.PostConfigure<WorkflowDefinitionFileSourceOptions>(options =>
                {
                    AddWorkflowDirectoryIfMissing(options.WorkflowDirectories, Path.Combine(toolDir, "workflows"));
                    AddWorkflowDirectoryIfMissing(options.WorkflowDirectories, Path.Combine(AevatarPaths.RepoRoot, "tools", "Aevatar.Tools.Cli", "workflows"));
                    AddWorkflowDirectoryIfMissing(options.WorkflowDirectories, Path.Combine(Environment.CurrentDirectory, "tools", "Aevatar.Tools.Cli", "workflows"));
                });
            }

            var app = builder.Build();
            var loadedConnectors = embeddedWorkflowMode
                ? LoadNamedConnectors(app.Services)
                : Array.Empty<string>();
            PrintBanner(localUrl, sdkBaseUrl, webRootPath, embeddedWorkflowMode, loadedConnectors);

            app.Lifetime.ApplicationStarted.Register(() =>
            {
                options.StartedSignal?.TrySetResult(true);
                if (!options.NoBrowser)
                    BrowserLauncher.Open(localUrl);
            });

            if (nyxIdAuthEnabled)
                app.UseNyxIdAppProtection();

            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.MapControllers();
            app.MapNyxIdAppEndpoints(nyxIdAuthEnabled);

            if (embeddedWorkflowMode)
                app.UseAevatarDefaultHost();

            app.MapGet(
                "/api/app/health",
                () => Results.Json(new
                {
                    ok = true,
                    service = "aevatar.app",
                    mode = embeddedWorkflowMode ? "embedded" : "proxy",
                    sdkBaseUrl,
                }));

            AppBridgeEndpoints.Map(app, new AppBridgeRouteOptions
            {
                MapCapabilityRoutes = !embeddedWorkflowMode,
            });
            AppStudioEndpoints.Map(app, embeddedWorkflowMode);

            app.MapFallbackToFile("index.html");
            await app.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            options.StartedSignal?.TrySetCanceled(cancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            options.StartedSignal?.TrySetException(ex);
            throw;
        }
    }

    private static bool ShouldUseEmbeddedWorkflow(string localUrl, string sdkBaseUrl)
    {
        if (!Uri.TryCreate(localUrl, UriKind.Absolute, out var localUri))
            return true;
        if (!Uri.TryCreate(sdkBaseUrl, UriKind.Absolute, out var sdkUri))
            return true;

        var compare = Uri.Compare(
            localUri,
            sdkUri,
            UriComponents.SchemeAndServer | UriComponents.Port,
            UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase);
        return compare == 0;
    }

    private static void PrintBanner(
        string localUrl,
        string sdkBaseUrl,
        string webRootPath,
        bool embeddedWorkflowMode,
        IReadOnlyList<string> loadedConnectors)
    {
        var connectorSummary = loadedConnectors.Count == 0 ? "-" : string.Join(", ", loadedConnectors);
        if (connectorSummary.Length > 41)
            connectorSummary = connectorSummary[..38] + "...";

        Console.WriteLine();
        Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                      aevatar app                          ║");
        Console.WriteLine("╠═══════════════════════════════════════════════════════════╣");
        Console.WriteLine($"║  🌐 Studio:     {localUrl,-41} ║");
        Console.WriteLine($"║  🔌 SDK base:   {sdkBaseUrl,-41} ║");
        Console.WriteLine($"║  🧠 Mode:       {(embeddedWorkflowMode ? "embedded" : "proxy"),-41} ║");
        Console.WriteLine($"║  🔗 Connectors: {connectorSummary,-41} ║");
        Console.WriteLine($"║  📦 WebRoot:    {webRootPath,-41} ║");
        Console.WriteLine("║  Press Ctrl+C to stop                                      ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
        Console.WriteLine();
    }

    private static void AddWorkflowDirectoryIfMissing(ICollection<string> directories, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return;

        string normalizedCandidate;
        try
        {
            normalizedCandidate = Path.GetFullPath(candidate);
        }
        catch
        {
            return;
        }

        foreach (var existing in directories)
        {
            if (string.IsNullOrWhiteSpace(existing))
                continue;

            try
            {
                if (string.Equals(
                        Path.GetFullPath(existing),
                        normalizedCandidate,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch
            {
                // Ignore malformed existing paths and continue.
            }
        }

        directories.Add(candidate);
    }

    internal static IReadOnlyList<string> LoadNamedConnectors(IServiceProvider services, string? connectorsJsonPath = null)
    {
        using var scope = services.CreateScope();
        var scoped = scope.ServiceProvider;
        var logger = scoped.GetRequiredService<ILoggerFactory>().CreateLogger("Aevatar.App.Connectors");
        var registry = scoped.GetService<IConnectorRegistry>();
        if (registry == null)
        {
            logger.LogWarning("IConnectorRegistry is not registered. Skip connector loading.");
            return [];
        }

        var connectorBuilders = scoped.GetServices<IConnectorBuilder>().ToList();
        if (connectorBuilders.Count == 0)
        {
            logger.LogWarning("No IConnectorBuilder registered. Skip connector loading.");
            return registry.ListNames();
        }

        var loadedCount = ConnectorRegistration.RegisterConnectors(
            registry,
            connectorBuilders,
            logger,
            connectorsJsonPath);
        var names = registry.ListNames();

        if (loadedCount == 0 && names.Count == 0)
        {
            logger.LogWarning(
                "No named connectors were loaded from {ConnectorsPath}.",
                connectorsJsonPath ?? AevatarPaths.ConnectorsJson);
        }

        return names;
    }
}
