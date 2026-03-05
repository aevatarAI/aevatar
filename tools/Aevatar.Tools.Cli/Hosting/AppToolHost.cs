using Aevatar.Bootstrap;
using Aevatar.Bootstrap.Connectors;
using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Tools.Cli.Bridge;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Sdk.DependencyInjection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.Tools.Cli.Hosting;

internal sealed class AppToolHostOptions
{
    public int Port { get; init; } = 6688;
    public bool NoBrowser { get; init; }
    public string? ApiBaseUrl { get; init; }
}

internal static class AppToolHost
{
    public static async Task RunAsync(AppToolHostOptions options, CancellationToken cancellationToken)
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
        builder.Services.Configure<JsonOptions>(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
        builder.Services.AddAevatarWorkflowSdk(sdk => sdk.BaseUrl = sdkBaseUrl);

        if (embeddedWorkflowMode)
        {
            builder.Services.AddAevatarBootstrap(builder.Configuration);
            builder.Services.AddAevatarAIFeatures(builder.Configuration, ai =>
            {
                ai.EnableMEAIProviders = true;
                ai.EnableMEAIToTornadoFailover = true;
                ai.EnableReloadableProviderFactory = true;
                ai.EnableMCPTools = true;
                ai.EnableSkills = true;
            });
            builder.Services.AddWorkflowProjectionReadModelProviders(builder.Configuration);
            builder.Services.AddWorkflowCapability(builder.Configuration);
        }

        var app = builder.Build();
        var loadedConnectors = embeddedWorkflowMode
            ? LoadNamedConnectors(app.Services)
            : Array.Empty<string>();
        PrintBanner(localUrl, sdkBaseUrl, webRootPath, embeddedWorkflowMode, loadedConnectors);

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (!options.NoBrowser)
                BrowserLauncher.Open(localUrl);
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet(
            "/api/app/health",
            () => Results.Json(new
            {
                ok = true,
                service = "aevatar.app",
                mode = embeddedWorkflowMode ? "embedded" : "proxy",
                sdkBaseUrl,
            }));

        AppDemoPlaygroundEndpoints.Map(app, embeddedWorkflowMode);

        if (embeddedWorkflowMode)
            app.MapWorkflowChatInteractionEndpoints();

        AppBridgeEndpoints.Map(app, new AppBridgeRouteOptions
        {
            MapCapabilityRoutes = !embeddedWorkflowMode,
            MapAppAliases = true,
        });

        app.MapFallbackToFile("index.html");
        await app.RunAsync(cancellationToken);
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
        Console.WriteLine($"║  🌐 Playground: {localUrl,-41} ║");
        Console.WriteLine($"║  🔌 SDK base:   {sdkBaseUrl,-41} ║");
        Console.WriteLine($"║  🧠 Mode:       {(embeddedWorkflowMode ? "embedded" : "proxy"),-41} ║");
        Console.WriteLine($"║  🔗 Connectors: {connectorSummary,-41} ║");
        Console.WriteLine($"║  📦 WebRoot:    {webRootPath,-41} ║");
        Console.WriteLine("║  Press Ctrl+C to stop                                      ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
        Console.WriteLine();
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
