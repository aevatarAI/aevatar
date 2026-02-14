// ─────────────────────────────────────────────────────────────
// Gateway — 极薄的 Chat 转换层
// 可组合的 DI 注册：选择性引用 AI Provider 和扩展
// ─────────────────────────────────────────────────────────────

using Aevatar.Bootstrap;
using Aevatar.Configuration;
using Aevatar.Hosts.Gateway;

var builder = WebApplication.CreateBuilder(args);

// ─── ~/.aevatar/ 配置加载 ───
builder.Configuration.AddAevatarConfig();
builder.Services.AddAevatarBootstrap(builder.Configuration, options =>
{
    options.EnableMEAIProviders = true;
    options.EnableMCPTools = true;
    options.EnableSkills = true;
});

var app = builder.Build();

// ─── 端点 ───
app.MapChatEndpoints();
app.MapGet("/", () => Results.Ok(new { name = "Aevatar.Hosts.Gateway", status = "running" }));

app.Run();
