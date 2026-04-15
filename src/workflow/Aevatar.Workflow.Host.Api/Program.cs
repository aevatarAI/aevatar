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

using Aevatar.Bootstrap.Hosting;
using Aevatar.Foundation.VoicePresence.Hosting;
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
builder.AddAevatarWorkflowObservability();

var app = builder.Build();

app.UseAevatarDefaultHost();
app.MapVoicePresenceWebSocket("/ws/voice/{actorId}");

app.Run();
