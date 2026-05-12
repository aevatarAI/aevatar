using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Demos.Inspector.Demo;
using Aevatar.Demos.Inspector.ReadModels;
using Aevatar.Demos.Inspector.Telemetry;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Studio.Projection.Metadata;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Projection.DependencyInjection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Demos.Inspector;

public static class InspectorApplication
{
    private const int DefaultPort = 5100;

    public static WebApplication CreateApp(string[] args)
    {
        var builder = CreateBuilder(args);
        return Build(builder);
    }

    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var options = InspectorRuntimeOptions.Parse(args, DefaultPort);
        var contentRoot = ResolveContentRoot();
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = contentRoot,
            WebRootPath = "wwwroot",
        });
        builder.WebHost.UseKestrel();
        builder.Services.AddRouting();
        builder.Services.AddSingleton(options);
        builder.WebHost.UseUrls(options.Url);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Projection:Document:Providers:InMemory:Enabled"] = "true",
            ["Projection:Graph:Providers:InMemory:Enabled"] = "true",
            ["Projection:Policies:Environment"] = "Development",
        });
        return builder;
    }

    public static WebApplication Build(WebApplicationBuilder builder)
    {
        ConfigureServices(builder.Services, builder.Configuration);
        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        MapEndpoints(app);

        var options = app.Services.GetRequiredService<InspectorRuntimeOptions>();
        if (options.OpenBrowser)
            app.Lifetime.ApplicationStarted.Register(() => OpenBrowser(options.Url));

        return app;
    }

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JsonOptions>(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            json.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        services.AddAevatarRuntime();
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();

        services.AddProjectionMaterializationRuntimeCore<
            StudioMaterializationContext,
            StudioMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<StudioMaterializationContext>>(
            scopeKey => new StudioMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new StudioMaterializationRuntimeLease(context));
        services.TryAddSingleton<StudioProjectionPort>();
        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<GAgentRegistryCurrentStateDocument>,
            GAgentRegistryCurrentStateDocumentMetadataProvider>();
        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            GAgentRegistryCurrentStateProjector>();
        services.AddInMemoryDocumentProjectionStore<GAgentRegistryCurrentStateDocument, string>(
            keySelector: static document => document.ActorId,
            keyFormatter: static key => key,
            defaultSortSelector: static document => document.UpdatedAt);

        services.AddWorkflowExecutionProjectionCQRS();
        services.AddWorkflowProjectionReadModelProviders(configuration);

        services.TryAddSingleton<InspectorTelemetryBroadcaster>();
        services.TryAddSingleton<InspectorGAgentRegistryService>();
        services.TryAddSingleton<InspectorReadModelQueryService>();
        services.TryAddSingleton<InspectorDemoScenarioService>();
        services.TryAddTransient<InspectorTransformerAgent>();
        services.TryAddTransient<InspectorCollectorAgent>();
        services.TryAddTransient<InspectorCounterAgent>();
    }

    public static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/inspector/actors", async (
            InspectorGAgentRegistryService registry,
            CancellationToken ct) => Results.Ok(await registry.ListActorsAsync(ct)));

        app.MapGet("/api/inspector/workflow-runs", async (
            InspectorReadModelQueryService readModels,
            CancellationToken ct) => Results.Ok(await readModels.ListWorkflowRunsAsync(ct)));

        app.MapGet("/api/inspector/readmodels", async (
            InspectorReadModelQueryService readModels,
            CancellationToken ct) => Results.Ok(await readModels.ListReadModelsAsync(ct)));

        app.MapGet("/api/inspector/readmodels/{name}", async (
            string name,
            InspectorReadModelQueryService readModels,
            CancellationToken ct) =>
        {
            var result = await readModels.GetReadModelAsync(name, ct);
            return result == null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapGet("/api/inspector/events", async (
            InspectorTelemetryBroadcaster telemetry,
            HttpContext context,
            CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";
            context.Response.ContentType = "text/event-stream";

            await foreach (var frame in telemetry.ReadAllAsync(ct))
            {
                var json = JsonSerializer.Serialize(frame, InspectorJson.Options);
                await context.Response.WriteAsync("event: activity\n", ct);
                await context.Response.WriteAsync($"data: {json}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }
        });

        app.MapPost("/api/inspector/demo/hierarchy", async (
            InspectorDemoScenarioService demo,
            CancellationToken ct) => Results.Ok(await demo.RunHierarchyAsync(ct)));
    }

    public static void PrintStartupBanner(InspectorRuntimeOptions options)
    {
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║              Aevatar Inspector Demo                     ║");
        Console.WriteLine("╠══════════════════════════════════════════════════════════╣");
        Console.WriteLine($"║  Web UI: {options.Url,-48}║");
        Console.WriteLine("║  Demo:   POST /api/inspector/demo/hierarchy             ║");
        Console.WriteLine("║  Press Ctrl+C to stop                                  ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
        Console.WriteLine();
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else
                Process.Start("xdg-open", url);
        }
        catch
        {
            // Opening a local browser is best effort only.
        }
    }

    private static string ResolveContentRoot()
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        if (HasWebRoot(currentDirectory))
            return currentDirectory;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (HasWebRoot(directory.FullName))
                return directory.FullName;

            directory = directory.Parent;
        }

        return currentDirectory;
    }

    private static bool HasWebRoot(string path) =>
        File.Exists(Path.Combine(path, "wwwroot", "index.html"));
}

public sealed record InspectorRuntimeOptions(int Port, string Url, bool OpenBrowser)
{
    public static InspectorRuntimeOptions Parse(string[] args, int defaultPort)
    {
        var port = defaultPort;
        var noBrowser = args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase);
        var portIndex = Array.FindIndex(args, x => string.Equals(x, "--port", StringComparison.OrdinalIgnoreCase));
        if (portIndex >= 0 && portIndex + 1 < args.Length && int.TryParse(args[portIndex + 1], out var parsed))
            port = parsed;

        if (port is 5000 or 5050)
            throw new InvalidOperationException("Inspector demo must not use port 5000 or 5050.");

        return new InspectorRuntimeOptions(port, $"http://localhost:{port}", !noBrowser);
    }
}

internal static class InspectorJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
