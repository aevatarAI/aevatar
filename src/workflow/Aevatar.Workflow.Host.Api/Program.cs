// ─────────────────────────────────────────────────────────────
// Aevatar.Workflow.Host.Api — Agent-Actor Host HTTP 入口
//
// workflow 启动统一通过已注册 service 的 scope API 触发，当前宿主只保留 run control/query 能力
// GET  /api/agents → 活跃 Agent 列表
// GET  /api/workflows → 可用工作流列表
//
// 依赖 ~/.aevatar/ 配置：config.json、secrets.json、connectors.json；
// LLM API Key 可从环境变量 DEEPSEEK_API_KEY / OPENAI_API_KEY 或 secrets 读取。
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Infrastructure.ChronoSandbox;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Audit.Core.DependencyInjection;
using Aevatar.Authentication.Hosting;
using Aevatar.Authentication.Providers.NyxId;
using Aevatar.Bootstrap.Hosting;
using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Host.Api;

var builder = WebApplication.CreateBuilder(args);

builder.AddAevatarDefaultHost(
    configureHost: options =>
    {
        options.ServiceName = "Aevatar.Workflow.Host.Api";
        options.EnableWebSockets = true;
    });
builder.AddAevatarPlatform();
builder.Services.AddAuditTrailCore(builder.Configuration);
if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddInMemoryAuditTrailForDevelopment();
builder.AddWorkflowAgentToolAdmission();
builder.Services.AddNyxIdAuthentication();
builder.AddAevatarAuthentication();
// NyxID-backed current-user resolver plus aevatar admin access policy.
builder.Services.AddNyxIdPlatformAuthorization(builder.Configuration);
builder.Services.AddChronoSandboxCodeExecution();
builder.Services.AddNyxIdTools(options =>
{
    // Override the single default (NyxIdToolOptions.DefaultBaseUrl) only when config provides a
    // non-empty value; an absent/empty config key must NOT clobber the default to null.
    var nyxTransportBaseUrl = FirstConfiguredValue(
        builder.Configuration,
        "Aevatar:NyxId:InternalApiBaseUrl",
        "Aevatar:NyxId:ApiBaseUrl",
        "Aevatar:NyxId:Authority",
        "Cli:App:NyxId:Authority",
        "Aevatar:Authentication:Authority");
    if (nyxTransportBaseUrl is not null)
        options.BaseUrl = nyxTransportBaseUrl;
    if (long.TryParse(builder.Configuration["Aevatar:NyxId:ProxyFileArtifactMaxBytes"], out var maxBytes))
        options.ProxyFileArtifactMaxBytes = maxBytes;
});
builder.Services.AddScheduledDispatchCapability(builder.Configuration);
builder.AddAevatarWorkflowObservability();

var app = builder.Build();

app.UseAevatarDefaultHost();

app.Run();

static string? FirstConfiguredValue(IConfiguration configuration, params string[] keys)
{
    foreach (var key in keys)
    {
        var value = configuration[key];
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();
    }

    return null;
}
