// ─────────────────────────────────────────────────────────────
// Aevatar.Host.Api — Agent-Actor Host HTTP 入口
//
// POST /api/chat   → 调用 workflow 应用层执行 chat run，SSE 返回 AGUI 事件
// GET  /api/agents → 活跃 Agent 列表
// GET  /api/workflows → 可用工作流列表
//
// 依赖 ~/.aevatar/ 配置：config.json、secrets.json、connectors.json；
// LLM API Key 可从环境变量 DEEPSEEK_API_KEY / OPENAI_API_KEY 或 secrets 读取。
// ─────────────────────────────────────────────────────────────

using Aevatar.Host.Api.Endpoints;
using Aevatar.Host.Api.Startup;
using Aevatar.CQRS.Core.DependencyInjection;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Bootstrap;
using Aevatar.Configuration;

var builder = WebApplication.CreateBuilder(args);

// ─── ~/.aevatar/ 配置 ───
builder.Configuration.AddAevatarConfig();
builder.Services.AddAevatarBootstrap(builder.Configuration, options =>
{
    options.EnableMEAIProviders = true;
    options.EnableMCPTools = true;
    options.EnableSkills = true;
});
builder.Services.AddCqrsCore(options =>
{
    options.DefaultSubsystem = "workflow";
});
builder.Services.AddWorkflowSubsystemProfile(builder.Configuration);
builder.Services.AddHostedService<ConnectorBootstrapHostedService>();

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

app.UseCors("Default");
app.UseWebSockets();
app.MapChatEndpoints();
app.MapGet("/", () => Results.Ok(new { name = "Aevatar.Host.Api", status = "running" }));

app.Run();
