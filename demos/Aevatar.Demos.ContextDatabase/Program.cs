// Aevatar Context Database Demo
// 演示 aevatar:// 虚拟文件系统的核心功能：
//   store    — Context Store CRUD（无需 LLM）
//   browse   — 浏览 aevatar:// 虚拟文件系统（无需 LLM）
//   pipeline — 端到端管线：写入资源 → LLM 生成 L0/L1 → 提取记忆 → 写入记忆
//
// 用法:
//   dotnet run --project demos/Aevatar.Demos.ContextDatabase store
//   dotnet run --project demos/Aevatar.Demos.ContextDatabase browse
//   dotnet run --project demos/Aevatar.Demos.ContextDatabase pipeline

using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.LLMProviders.MEAI;
using Aevatar.Configuration;
using Aevatar.Context.Abstractions;
using Aevatar.Context.Core;
using Aevatar.Context.Extraction;
using Aevatar.Context.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "store";

Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════════════╗");
Console.WriteLine("║       Aevatar Context Database Demo              ║");
Console.WriteLine("╚══════════════════════════════════════════════════╝");
Console.WriteLine();

switch (command)
{
    case "store":
        await RunStoreDemo();
        break;
    case "browse":
        await RunBrowseDemo();
        break;
    case "pipeline":
        await RunPipelineDemo();
        break;
    default:
        Console.WriteLine($"Unknown command: {command}");
        Console.WriteLine("Available: store, browse, pipeline");
        break;
}

return;

// ═══════════════════════════════════════════════════════════
//  store — Context Store CRUD（无需 LLM）
// ═══════════════════════════════════════════════════════════

static async Task RunStoreDemo()
{
    var sp = BuildMinimalServices();
    var store = sp.GetRequiredService<IContextStore>();

    Console.WriteLine("=== Context Store CRUD ===");
    Console.WriteLine();

    // 写入
    var docUri = AevatarUri.Parse("aevatar://resources/demo-project/architecture.md");
    await store.WriteAsync(docUri, """
        # Demo Project Architecture
        ## 概述
        这是一个演示项目的架构文档。
        ## 模块
        - **API 层**: REST API + WebSocket
        - **业务层**: 工作流引擎 + AI Agent
        - **数据层**: Event Sourcing + CQRS
        - **基础设施**: 配置管理 + 日志
        """);
    Console.WriteLine($"  ✓ 写入: {docUri}");

    var readmeUri = AevatarUri.Parse("aevatar://resources/demo-project/readme.md");
    await store.WriteAsync(readmeUri, "# Demo Project\nA demonstration project for Aevatar Context Database.");
    Console.WriteLine($"  ✓ 写入: {readmeUri}");

    var memoryUri = AevatarUri.Parse("aevatar://user/demo-user/memories/preferences/code-style.md");
    await store.WriteAsync(memoryUri, "用户偏好 TypeScript + React，使用 4 空格缩进，喜欢函数式编程风格。");
    Console.WriteLine($"  ✓ 写入用户记忆: {memoryUri}");

    Console.WriteLine();

    // 读取
    Console.WriteLine("--- 读取 ---");
    var content = await store.ReadAsync(docUri);
    Console.WriteLine($"  {docUri}: {content[..Math.Min(80, content.Length)]}...");
    Console.WriteLine();

    // 列举
    Console.WriteLine("--- 列举 aevatar://resources/demo-project/ ---");
    var entries = await store.ListAsync(AevatarUri.Parse("aevatar://resources/demo-project/"));
    foreach (var e in entries)
        Console.WriteLine($"  {(e.IsDirectory ? "📁" : "📄")} {e.Name}");
    Console.WriteLine();

    // 存在性
    Console.WriteLine("--- 存在性检查 ---");
    Console.WriteLine($"  {docUri}: {await store.ExistsAsync(docUri)}");
    Console.WriteLine($"  aevatar://resources/missing.md: {await store.ExistsAsync(AevatarUri.Parse("aevatar://resources/missing.md"))}");
    Console.WriteLine();

    // Glob
    Console.WriteLine("--- Glob **/*.md ---");
    var files = await store.GlobAsync("**/*.md", AevatarUri.Parse("aevatar://resources/"));
    foreach (var f in files)
        Console.WriteLine($"  📄 {f}");

    Console.WriteLine();
    Console.WriteLine("✅ Store 演示完成");
}

// ═══════════════════════════════════════════════════════════
//  browse — 浏览虚拟文件系统
// ═══════════════════════════════════════════════════════════

static async Task RunBrowseDemo()
{
    var sp = BuildMinimalServices();
    var store = sp.GetRequiredService<IContextStore>();

    Console.WriteLine("=== 浏览 aevatar:// ===");
    Console.WriteLine();

    string[] scopes = ["skills", "resources", "user", "agent", "session"];
    foreach (var scope in scopes)
    {
        var scopeUri = AevatarUri.Create(scope);
        Console.Write($"  aevatar://{scope}/");
        if (!await store.ExistsAsync(scopeUri)) { Console.WriteLine(" (空)"); continue; }
        Console.WriteLine();
        await PrintTree(store, scopeUri, "    ", 0, 3);
    }

    Console.WriteLine();
    Console.WriteLine($"物理路径: {AevatarPaths.Root}");
    Console.WriteLine("✅ 浏览完成");
}

static async Task PrintTree(IContextStore store, AevatarUri uri, string indent, int depth, int maxDepth)
{
    if (depth >= maxDepth) { Console.WriteLine($"{indent}..."); return; }
    try
    {
        var entries = await store.ListAsync(uri);
        foreach (var e in entries)
        {
            Console.WriteLine($"{indent}{(e.IsDirectory ? "📁" : "📄")} {e.Name}");
            if (e.IsDirectory) await PrintTree(store, e.Uri, indent + "  ", depth + 1, maxDepth);
        }
    }
    catch { /* dir may not exist */ }
}

// ═══════════════════════════════════════════════════════════
//  pipeline — 端到端 LLM 管线
// ═══════════════════════════════════════════════════════════

static async Task RunPipelineDemo()
{
    // ─── 1. 解析 LLM 配置 ───
    var secrets = new AevatarSecretsStore();
    var (providerName, apiKey) = ResolveLLMConfig(secrets);

    if (string.IsNullOrEmpty(apiKey))
    {
        Console.WriteLine("❌ 未找到 LLM API Key。");
        Console.WriteLine();
        Console.WriteLine("  请先配置:");
        Console.WriteLine("    aevatar-config");
        Console.WriteLine("    # 或");
        Console.WriteLine("    export DEEPSEEK_API_KEY=\"sk-...\"");
        return;
    }

    var isDeepSeek = providerName.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
    var modelName = isDeepSeek ? "deepseek-chat" : "gpt-4o-mini";
    Console.WriteLine($"  LLM: provider={providerName}, model={modelName}, key={apiKey[..Math.Min(8, apiKey.Length)]}...");
    Console.WriteLine();

    // ─── 2. 构建服务 ───
    var services = new ServiceCollection();
    services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
    services.AddContextStore();
    services.AddContextExtraction();
    services.AddContextMemory();

    if (isDeepSeek)
        services.AddMEAIProviders(f => f.RegisterOpenAI("deepseek", modelName, apiKey, baseUrl: "https://api.deepseek.com/v1").SetDefault("deepseek"));
    else
        services.AddMEAIProviders(f => f.RegisterOpenAI(providerName, modelName, apiKey).SetDefault(providerName));

    var sp = services.BuildServiceProvider();
    var store = sp.GetRequiredService<IContextStore>();
    var generator = sp.GetRequiredService<IContextLayerGenerator>();
    var processor = sp.GetRequiredService<SemanticProcessor>();
    var extractor = sp.GetRequiredService<IMemoryExtractor>();

    // ─── 3. 写入示例资源 ───
    Console.WriteLine("=== Step 1: 写入资源 ===");
    var resources = new Dictionary<string, string>
    {
        ["aevatar://resources/api-guide/authentication.md"] = """
            # API Authentication Guide

            ## JWT Token
            所有 API 请求需要在 Header 中携带 JWT Token:
            ```
            Authorization: Bearer <token>
            ```

            ## 获取 Token
            POST /api/auth/login
            Body: { "username": "...", "password": "..." }
            Response: { "token": "...", "expiresIn": 3600 }

            ## 刷新 Token
            POST /api/auth/refresh
            Header: Authorization: Bearer <expired-token>
            """,
        ["aevatar://resources/api-guide/endpoints.md"] = """
            # API Endpoints

            ## Chat
            - POST /api/chat — 发起对话
            - GET /api/ws/chat — WebSocket 实时对话

            ## Workflow
            - POST /api/workflows — 创建工作流
            - GET /api/workflows/{id} — 查询工作流状态
            - POST /api/workflows/{id}/execute — 执行工作流

            ## Agent
            - GET /api/agents — 列举所有 Agent
            - POST /api/agents/{id}/chat — 与指定 Agent 对话
            """,
        ["aevatar://resources/api-guide/error-codes.md"] = """
            # Error Codes

            | Code | Meaning |
            |------|---------|
            | 401  | Unauthorized — JWT Token 缺失或过期 |
            | 403  | Forbidden — 无权限访问该资源 |
            | 404  | Not Found — 资源不存在 |
            | 429  | Rate Limit — 请求频率超限，稍后重试 |
            | 500  | Internal Error — 服务器内部错误 |

            ## 错误响应格式
            ```json
            { "error": "message", "code": 401, "traceId": "..." }
            ```
            """,
    };

    foreach (var (uri, content) in resources)
    {
        await store.WriteAsync(AevatarUri.Parse(uri), content);
        Console.WriteLine($"  ✓ {uri}");
    }
    Console.WriteLine();

    // ─── 4. LLM 生成 L0/L1 摘要 ───
    Console.WriteLine("=== Step 2: LLM 生成 L0/L1 摘要 ===");
    Console.WriteLine("  调用 SemanticProcessor.ProcessTreeAsync（自底向上遍历）...");
    Console.WriteLine();

    var rootUri = AevatarUri.Parse("aevatar://resources/api-guide/");
    await processor.ProcessTreeAsync(rootUri);

    // 展示生成结果
    Console.WriteLine("  --- 生成的 L0 摘要（.abstract.md） ---");
    var l0 = await store.GetAbstractAsync(rootUri);
    Console.WriteLine($"  📋 aevatar://resources/api-guide/: {l0}");
    Console.WriteLine();

    Console.WriteLine("  --- 生成的 L1 概览（.overview.md） ---");
    var l1 = await store.GetOverviewAsync(rootUri);
    if (l1 != null)
    {
        var lines = l1.Split('\n');
        foreach (var line in lines.Take(15))
            Console.WriteLine($"  │ {line}");
        if (lines.Length > 15)
            Console.WriteLine($"  │ ... ({lines.Length - 15} more lines)");
    }
    Console.WriteLine();

    // ─── 5. LLM 记忆提取 ───
    Console.WriteLine("=== Step 3: LLM 记忆提取 ===");
    Console.WriteLine("  模拟一段对话，调用 LLMMemoryExtractor.ExtractAsync...");
    Console.WriteLine();

    var conversation = new List<string>
    {
        "User: 我是张三，在字节跳动做后端开发，主要用 Go 和 Python。",
        "Assistant: 你好张三！了解了，你在字节跳动做后端开发。有什么我可以帮忙的吗？",
        "User: 帮我看看这个 gRPC 接口为什么超时了，我用的是 context.WithTimeout 3 秒。",
        "Assistant: 3 秒对 gRPC 调用来说可能太短了，特别是跨数据中心的调用。建议改为 10 秒并加 retry。",
        "User: 谢谢，我习惯用 4 空格缩进，代码注释用中文。以后帮我生成代码的时候注意这些。",
        "Assistant: 明白了，我会记住你的编码偏好：4 空格缩进，中文注释。",
        "User: 对了，上周五的上线出了个 P0 事故，Redis 集群扩容后 key 路由错了，最后通过一致性哈希修复的。",
        "Assistant: 这是一个典型的分布式缓存扩容问题。一致性哈希是正确的解决方案。我记录下这个案例。",
    };

    var memories = await extractor.ExtractAsync(conversation);

    Console.WriteLine($"  提取到 {memories.Count} 条记忆:");
    Console.WriteLine();
    foreach (var m in memories)
    {
        var icon = m.Category switch
        {
            MemoryCategory.Profile => "👤",
            MemoryCategory.Preferences => "⚙️",
            MemoryCategory.Entities => "🏷️",
            MemoryCategory.Events => "📅",
            MemoryCategory.Cases => "💡",
            MemoryCategory.Patterns => "🔄",
            _ => "📝",
        };
        Console.WriteLine($"  {icon} [{m.Category}] {m.Content}");
        if (!string.IsNullOrEmpty(m.Source))
            Console.WriteLine($"     └ 来源: {m.Source}");
    }
    Console.WriteLine();

    // ─── 6. 将记忆写入 Context Store ───
    Console.WriteLine("=== Step 4: 写入记忆到 Context Store ===");
    var userId = "demo-user";
    var agentId = "demo-agent";

    foreach (var m in memories)
    {
        var scope = m.Category switch
        {
            MemoryCategory.Profile => $"user/{userId}/memories/profile",
            MemoryCategory.Preferences => $"user/{userId}/memories/preferences",
            MemoryCategory.Entities => $"user/{userId}/memories/entities",
            MemoryCategory.Events => $"user/{userId}/memories/events",
            MemoryCategory.Cases => $"agent/{agentId}/memories/cases",
            MemoryCategory.Patterns => $"agent/{agentId}/memories/patterns",
            _ => $"user/{userId}/memories",
        };
        var slug = new string(m.Content.Take(20).Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        var ts = DateTimeOffset.UtcNow.ToString("HHmmss");
        var fileUri = AevatarUri.Parse($"aevatar://{scope}/{ts}-{slug}.md");
        await store.WriteAsync(fileUri, m.Content);
        Console.WriteLine($"  ✓ {fileUri}");
    }
    Console.WriteLine();

    // ─── 7. 最终结果 ───
    Console.WriteLine("=== 最终结果: aevatar:// 目录结构 ===");
    Console.WriteLine();

    foreach (var scope in new[] { "resources", "user", "agent" })
    {
        var scopeUri = AevatarUri.Create(scope);
        if (!await store.ExistsAsync(scopeUri)) continue;
        Console.WriteLine($"  aevatar://{scope}/");
        await PrintTree(store, scopeUri, "    ", 0, 4);
    }

    Console.WriteLine();
    Console.WriteLine("=== 管线总结 ===");
    Console.WriteLine($"  资源文件: {resources.Count} 个");
    Console.WriteLine($"  L0 摘要: {(l0 != null ? "✓" : "✗")}");
    Console.WriteLine($"  L1 概览: {(l1 != null ? "✓" : "✗")}");
    Console.WriteLine($"  提取记忆: {memories.Count} 条");
    Console.WriteLine($"  LLM Provider: {providerName} ({modelName})");
    Console.WriteLine($"  物理路径: {AevatarPaths.Root}");
    Console.WriteLine();
    Console.WriteLine("✅ 端到端管线演示完成");
    Console.WriteLine();
    Console.WriteLine("在 Host.Api 中启用:");
    Console.WriteLine("  options.EnableContextDatabase = true;");
}

// ═══════════════════════════════════════════════════════════
//  辅助方法
// ═══════════════════════════════════════════════════════════

static ServiceProvider BuildMinimalServices()
{
    var services = new ServiceCollection();
    services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
    services.AddContextStore();
    return services.BuildServiceProvider();
}

static (string Provider, string? ApiKey) ResolveLLMConfig(AevatarSecretsStore secrets)
{
    // 1) 环境变量
    var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
              ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
              ?? Environment.GetEnvironmentVariable("AEVATAR_LLM_API_KEY");

    if (!string.IsNullOrEmpty(apiKey))
    {
        var provider = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") != null ? "deepseek" : "openai";
        return (provider, apiKey);
    }

    // 2) secrets.json
    var defaultProv = secrets.GetDefaultProvider() ?? "deepseek";
    apiKey = secrets.GetApiKey(defaultProv);

    if (!string.IsNullOrEmpty(apiKey))
        return (defaultProv, apiKey);

    // 3) fallback
    foreach (var candidate in new[] { "deepseek", "openai" })
    {
        apiKey = secrets.GetApiKey(candidate);
        if (!string.IsNullOrEmpty(apiKey))
            return (candidate, apiKey);
    }

    return ("deepseek", null);
}
