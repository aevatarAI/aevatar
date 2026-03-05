// ─────────────────────────────────────────────────────────────
// Aevatar.Workflow.Host.Api — Agent-Actor Host HTTP 入口
//
// POST /api/chat   → 调用 workflow 应用层执行 chat run，SSE 返回 AGUI 事件
// GET  /api/agents → 活跃 Agent 列表
// GET  /api/workflows → 可用工作流列表
//
// 依赖 ~/.aevatar/ 配置：config.json、secrets.json、connectors.json；
// LLM API Key 可从环境变量 DEEPSEEK_API_KEY / OPENAI_API_KEY 或 secrets 读取。
// ─────────────────────────────────────────────────────────────

using Aevatar.Bootstrap.Hosting;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Host.Api;
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);

builder.AddAevatarDefaultHost(
    configureHost: options =>
    {
        options.ServiceName = "Aevatar.Workflow.Host.Api";
        options.EnableWebSockets = true;
    });
builder.AddWorkflowCapabilityWithAIDefaults();
builder.AddAevatarWorkflowObservability();

var app = builder.Build();

app.UseAevatarDefaultHost();
app.MapPrometheusScrapingEndpoint();

app.Run();
