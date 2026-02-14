// ─────────────────────────────────────────────────────────────
// Aevatar.Api — Agent-Actor Host HTTP entry point
//
// POST /api/chat   → Create/reuse WorkflowGAgent, SSE return AGUI events
// GET  /api/agents → Active Agent list
// GET  /api/workflows → Available workflow list
//
// Runtime selection (env: AEVATAR_RUNTIME or config Runtime:Provider):
//   "Local"   (default) → In-process LocalActorRuntime
//   "Orleans" → Distributed Orleans + Kafka (requires running Silo)
//
// Environment variables (Orleans mode):
//   KAFKA_BOOTSTRAP     - Kafka bootstrap servers (default: localhost:9092)
//   ORLEANS_CLUSTER_ID  - Cluster ID (default: aevatar-dev)
//   ORLEANS_SERVICE_ID  - Service ID (default: aevatar)
// ─────────────────────────────────────────────────────────────

using Aevatar.Api.Endpoints;
using Aevatar.Api.Workflows;
using Aevatar.Cognitive;
using Aevatar.Config;
using Aevatar.Connectors;
using Aevatar.DependencyInjection;
using Aevatar.EventModules;
using Aevatar.AI.MEAI;
using Aevatar.Orleans.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// ─── ~/.aevatar/ config ───
builder.Configuration.AddAevatarConfig();
builder.Services.AddAevatarConfig();

// ─── Runtime selection ───
// Default: "Local" (in-process), set to "Orleans" for distributed mode.
var runtimeProvider = Environment.GetEnvironmentVariable("AEVATAR_RUNTIME")
                   ?? builder.Configuration["Runtime:Provider"]
                   ?? "Local";

if (string.Equals(runtimeProvider, "Orleans", StringComparison.OrdinalIgnoreCase))
{
    // Orleans mode: connect to external Silo via Orleans Client + Kafka
    var kafkaBootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP") ?? "localhost:9092";
    var clusterId = Environment.GetEnvironmentVariable("ORLEANS_CLUSTER_ID") ?? "aevatar-dev";
    var serviceId = Environment.GetEnvironmentVariable("ORLEANS_SERVICE_ID") ?? "aevatar";

    builder.UseAevatarOrleansRuntime(kafkaBootstrap, clusterId, serviceId);
}

// Common services (TryAdd: Orleans registrations take priority if present)
builder.Services.AddAevatarRuntime();
builder.Services.AddAevatarCognitive();
builder.Services.AddSingleton<IEventModuleFactory, CognitiveModuleFactory>();

// ─── LLM Provider（MEAI：OpenAI / DeepSeek） ───
var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
          ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
          ?? Environment.GetEnvironmentVariable("AEVATAR_LLM_API_KEY");

if (string.IsNullOrEmpty(apiKey))
{
    var secrets = new AevatarSecretsStore();
    var defaultProvider = secrets.GetDefaultProvider() ?? builder.Configuration["Models:DefaultProvider"] ?? "deepseek";
    apiKey = secrets.GetApiKey(defaultProvider);
    if (string.IsNullOrEmpty(apiKey))
    {
        foreach (var name in new[] { "deepseek", "openai" })
        {
            apiKey = secrets.GetApiKey(name);
            if (!string.IsNullOrEmpty(apiKey)) break;
        }
    }
}

// Always register LLM factory so RoleGAgent can resolve it; without apiKey we register empty (clear error when used).
if (!string.IsNullOrEmpty(apiKey))
{
    var useDeepSeek = (Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") != null)
                   || (builder.Configuration["Models:DefaultProvider"]?.Contains("deepseek", StringComparison.OrdinalIgnoreCase) == true);
    if (useDeepSeek)
        builder.Services.AddMEAIProviders(f => f
            .RegisterOpenAI("deepseek", "deepseek-chat", apiKey, baseUrl: "https://api.deepseek.com/v1")
            .SetDefault("deepseek"));
    else
        builder.Services.AddMEAIProviders(f => f
            .RegisterOpenAI("openai", "gpt-4o-mini", apiKey)
            .SetDefault("openai"));
}
else
    builder.Services.AddMEAIProviders(_ => { });

// ─── 工作流注册表（应用目录 + repo 根 workflows + CWD + ~/.aevatar/workflows） ───
builder.Services.AddSingleton(sp =>
{
    var registry = new WorkflowRegistry();
    var appWorkflows = Path.Combine(AppContext.BaseDirectory, "workflows");
    var repoRootWorkflows = AevatarPaths.RepoRootWorkflows;
    var cwdWorkflows = Path.Combine(Directory.GetCurrentDirectory(), "workflows");
    var aevatarWorkflows = AevatarPaths.Workflows;
    if (Directory.Exists(appWorkflows)) registry.LoadFromDirectory(appWorkflows);
    if (Directory.Exists(repoRootWorkflows)) registry.LoadFromDirectory(repoRootWorkflows);
    if (Directory.Exists(cwdWorkflows)) registry.LoadFromDirectory(cwdWorkflows);
    if (Directory.Exists(aevatarWorkflows)) registry.LoadFromDirectory(aevatarWorkflows);
    // 内置 direct 覆盖同名，保证未传 workflow 时默认一定带 role: assistant，能正常调 LLM
    registry.Register("direct", WorkflowRegistry.BuiltInDirectYaml);
    return registry;
});

// ─── CORS（开发用） ───
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// ─── 启动时注册 Connector（~/.aevatar/connectors.json） ───
{
    var registry = app.Services.GetRequiredService<IConnectorRegistry>();
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Aevatar.Api.Connectors");
    Aevatar.Api.ConnectorRegistration.RegisterConnectors(registry, logger);
    var names = registry.ListNames();
    if (names.Count > 0)
        logger.LogInformation("Connectors loaded: {Count} [{Names}]", names.Count, string.Join(", ", names));
}

app.UseCors();
app.MapChatEndpoints();
app.MapGet("/", () => Results.Ok(new { name = "Aevatar.Api", status = "running" }));

app.Run();
