// ─────────────────────────────────────────────────────────────
// Gateway — 极薄的 Chat 转换层
// 可组合的 DI 注册：选择性引用 AI Provider 和扩展
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.MCP;
using Aevatar.AI.Skills;
using Aevatar.Cognitive;
using Aevatar.Config;
using Aevatar.DependencyInjection;
using Aevatar.EventModules;
using Aevatar.Gateway;

var builder = WebApplication.CreateBuilder(args);

// ─── ~/.aevatar/ 配置加载 ───
builder.Configuration.AddAevatarConfig();
builder.Services.AddAevatarConfig();

// ─── 基础运行时（Stream + Actor + 存储） ───
builder.Services.AddAevatarRuntime();

// ─── Cognitive Module Factory ───
builder.Services.AddSingleton<IEventModuleFactory, CognitiveModuleFactory>();

// ─── LLM Provider（可选一个或多个） ───

// MEAI Provider（OpenAI / DeepSeek / Azure）
// var openaiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
// var deepseekKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "";
// builder.Services.AddMEAIProviders(f => f
//     .RegisterOpenAI("openai", "gpt-4o-mini", openaiKey)
//     .RegisterOpenAI("deepseek", "deepseek-chat", deepseekKey, "https://api.deepseek.com/v1")
//     .SetDefault("deepseek"));

// LLMTornado Provider（Anthropic / Google）
// var claudeKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
// builder.Services.AddTornadoProviders(f => f
//     .Register("anthropic", LlmTornado.Code.LLmProviders.Anthropic, claudeKey, "claude-sonnet-4-20250514")
//     .SetDefault("anthropic"));

// ─── MCP Tools（可选） ───
// builder.Services.AddMCPTools(o => o
//     .AddServer("filesystem", "npx", "-y", "@modelcontextprotocol/server-filesystem", "/tmp"));

// ─── Skills（可选） ───
// builder.Services.AddSkills(o => o
//     .ScanDirectory("~/.aevatar/skills"));

var app = builder.Build();

// ─── 端点 ───
app.MapChatEndpoints();
app.MapGet("/", () => Results.Ok(new { name = "Aevatar Gateway", status = "running" }));

app.Run();
