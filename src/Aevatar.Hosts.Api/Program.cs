// ─────────────────────────────────────────────────────────────
// Aevatar.Hosts.Api — Agent-Actor Host HTTP 入口
//
// POST /api/chat   → 创建/复用 WorkflowGAgent，SSE 返回 AGUI 事件
// GET  /api/agents → 活跃 Agent 列表
// GET  /api/workflows → 可用工作流列表
//
// 依赖 ~/.aevatar/ 配置：config.json、secrets.json、connectors.json；
// LLM API Key 可从环境变量 DEEPSEEK_API_KEY / OPENAI_API_KEY 或 secrets 读取。
// ─────────────────────────────────────────────────────────────

using Aevatar.Hosts.Api.Endpoints;
using Aevatar.Cqrs.Projections.DependencyInjection;
using Aevatar.Hosts.Api.Workflows;
using Aevatar.Bootstrap;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;

var builder = WebApplication.CreateBuilder(args);

// ─── ~/.aevatar/ 配置 ───
builder.Configuration.AddAevatarConfig();
builder.Services.AddAevatarBootstrap(builder.Configuration, options =>
{
    options.EnableMEAIProviders = true;
    options.EnableMCPTools = true;
    options.EnableSkills = true;
});
builder.Services.AddChatProjectionCqrs(options =>
    builder.Configuration.GetSection("ChatProjection").Bind(options));

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

// ─── CORS（开发默认放开；生产要求显式白名单） ───
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
builder.Services.AddCors(o => o.AddPolicy("Default", p =>
{
    if (builder.Environment.IsDevelopment())
    {
        p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
        return;
    }

    if (allowedOrigins is { Length: > 0 })
    {
        p.WithOrigins(allowedOrigins).AllowAnyMethod().AllowAnyHeader();
        return;
    }

    // Production default: deny all cross-origin requests unless explicitly configured.
    p.SetIsOriginAllowed(_ => false);
}));

var app = builder.Build();

// ─── 启动时注册 Connector（~/.aevatar/connectors.json） ───
{
    var registry = app.Services.GetRequiredService<IConnectorRegistry>();
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Aevatar.Hosts.Api.Connectors");
    ConnectorRegistration.RegisterConnectors(registry, logger);
    var names = registry.ListNames();
    if (names.Count > 0)
        logger.LogInformation("Connectors loaded: {Count} [{Names}]", names.Count, string.Join(", ", names));
}

app.UseCors("Default");
app.UseWebSockets();
app.MapChatEndpoints();
app.MapGet("/", () => Results.Ok(new { name = "Aevatar.Hosts.Api", status = "running" }));

app.Run();
