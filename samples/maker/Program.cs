// ─────────────────────────────────────────────────────────────
// MAKER Sample - paper/text analysis with decomposition + voting
//
// Demonstrates the MAKER pattern from:
// "Solving a Million-Step LLM Task with Zero Errors"
// https://arxiv.org/html/2511.09030v1
//
// Usage:
//   cd samples/maker && dotnet run
//   # uses API key from ~/.aevatar/secrets.json (auto-decrypted)
//
//   # Or override with env var:
//   export DEEPSEEK_API_KEY="sk-..."
//   dotnet run
//
//   # Or pass custom text:
//   dotnet run -- "Your paper text here..."
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.AI.LLMProviders.MEAI;
using Aevatar.Workflows.Core;
using Aevatar.Workflows.Core.Connectors;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Sample.Maker;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ─── Load ~/.aevatar/ config ───

var config = new ConfigurationBuilder()
    .AddAevatarConfig()
    .AddEnvironmentVariables()
    .Build();

// ─── Resolve LLM provider ───

var secrets = new AevatarSecretsStore();

// 1) Check env vars first
var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
          ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
          ?? Environment.GetEnvironmentVariable("AEVATAR_LLM_API_KEY");

// 2) Fall back to encrypted secrets.json
string providerName;
string modelName;

if (!string.IsNullOrEmpty(apiKey))
{
    // Env var provided: guess provider from var name
    providerName = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") != null ? "deepseek" : "openai";
    modelName = providerName == "deepseek" ? "deepseek-chat" : "gpt-4o-mini";
}
else
{
    // Read from secrets.json (auto-decrypted from AES-256-GCM)
    var defaultProv = secrets.GetDefaultProvider();
    providerName = defaultProv ?? config["Models:DefaultProvider"] ?? "deepseek";
    modelName = config["Models:DefaultModel"] ?? "deepseek-chat";

    // Try provider-specific API key from secrets
    apiKey = secrets.GetApiKey(providerName);

    // Also try common provider names
    if (string.IsNullOrEmpty(apiKey))
    {
        foreach (var candidate in new[] { "deepseek", "openai", "deepseek-deepseek-chat" })
        {
            apiKey = secrets.GetApiKey(candidate);
            if (!string.IsNullOrEmpty(apiKey))
            {
                providerName = candidate.Contains("deepseek") ? "deepseek" : candidate;
                break;
            }
        }
    }
}

if (string.IsNullOrEmpty(apiKey))
{
    Console.WriteLine("══════════════════════════════════════════════════════════");
    Console.WriteLine("  No LLM API key found.");
    Console.WriteLine();
    Console.WriteLine("  Option 1: Use aevatar CLI to configure secrets:");
    Console.WriteLine("    aevatar config set-secret LLMProviders:Providers:deepseek:ApiKey sk-...");
    Console.WriteLine();
    Console.WriteLine("  Option 2: Set environment variable:");
    Console.WriteLine("    export DEEPSEEK_API_KEY=\"sk-...\"");
    Console.WriteLine();
    Console.WriteLine("  Secrets are read from ~/.aevatar/secrets.json (encrypted).");
    Console.WriteLine("══════════════════════════════════════════════════════════");
    return;
}

// Normalize provider
var isDeepSeek = providerName.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
if (isDeepSeek)
{
    providerName = "deepseek";
    if (!modelName.Contains("deepseek")) modelName = "deepseek-chat";
}

// ─── Build services ───

var services = new ServiceCollection();

services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
services.AddAevatarRuntime();
services.AddAevatarConfig();
services.AddAevatarCognitive();
services.AddSingleton<IEventModuleFactory, MakerModuleFactory>();

if (isDeepSeek)
{
    services.AddMEAIProviders(f => f
        .RegisterOpenAI("deepseek", modelName, apiKey, baseUrl: "https://api.deepseek.com/v1")
        .SetDefault("deepseek"));
}
else
{
    services.AddMEAIProviders(f => f
        .RegisterOpenAI(providerName, modelName, apiKey)
        .SetDefault(providerName));
}

var sp = services.BuildServiceProvider();
var runtime = sp.GetRequiredService<IActorRuntime>();
var streams = sp.GetRequiredService<IStreamProvider>();
var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Maker");
var connectorLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Maker.Connectors");

// ─── Register named connectors from ~/.aevatar/connectors.json ───
var connectorRegistry = sp.GetRequiredService<IConnectorRegistry>();
var connectorEntries = AevatarConnectorConfig.LoadConnectors();
if (connectorEntries.Count == 0)
{
    var localConnectorPath = Path.Combine(AppContext.BaseDirectory, "connectors", "maker.connectors.json");
    if (!File.Exists(localConnectorPath))
        localConnectorPath = Path.Combine(Directory.GetCurrentDirectory(), "connectors", "maker.connectors.json");
    if (File.Exists(localConnectorPath))
    {
        connectorLogger.LogInformation("Loading sample connectors from {Path}", localConnectorPath);
        connectorEntries = AevatarConnectorConfig.LoadConnectors(localConnectorPath);
    }
}

foreach (var entry in connectorEntries)
{
    switch (entry.Type.ToLowerInvariant())
    {
        case "http":
        {
            if (string.IsNullOrWhiteSpace(entry.Http.BaseUrl))
            {
                connectorLogger.LogWarning("Skip connector {Name}: http.baseUrl is required", entry.Name);
                break;
            }

            connectorRegistry.Register(new HttpConnector(
                entry.Name,
                entry.Http.BaseUrl,
                entry.Http.AllowedMethods,
                entry.Http.AllowedPaths,
                entry.Http.AllowedInputKeys,
                entry.Http.DefaultHeaders,
                entry.TimeoutMs));
            break;
        }
        case "cli":
        {
            if (string.IsNullOrWhiteSpace(entry.Cli.Command))
            {
                connectorLogger.LogWarning("Skip connector {Name}: cli.command is required", entry.Name);
                break;
            }
            if (entry.Cli.Command.Contains("://", StringComparison.OrdinalIgnoreCase))
            {
                connectorLogger.LogWarning("Skip connector {Name}: cli.command must be a preinstalled command, got {Command}",
                    entry.Name, entry.Cli.Command);
                break;
            }

            connectorRegistry.Register(new CliConnector(
                entry.Name,
                entry.Cli.Command,
                entry.Cli.FixedArguments,
                entry.Cli.AllowedOperations,
                entry.Cli.AllowedInputKeys,
                entry.Cli.WorkingDirectory,
                entry.Cli.Environment,
                entry.TimeoutMs));
            break;
        }
        case "mcp":
        {
            if (string.IsNullOrWhiteSpace(entry.Mcp.Command))
            {
                connectorLogger.LogWarning("Skip connector {Name}: mcp.command is required", entry.Name);
                break;
            }

            var server = new MCPServerConfig
            {
                Name = string.IsNullOrWhiteSpace(entry.Mcp.ServerName) ? entry.Name : entry.Mcp.ServerName,
                Command = entry.Mcp.Command,
                Arguments = entry.Mcp.Arguments,
                Environment = entry.Mcp.Environment,
            };

            connectorRegistry.Register(new MCPConnector(
                entry.Name,
                server,
                entry.Mcp.DefaultTool,
                entry.Mcp.AllowedTools,
                entry.Mcp.AllowedInputKeys,
                logger: connectorLogger));
            break;
        }
        default:
            connectorLogger.LogWarning("Skip connector {Name}: unsupported type {Type}", entry.Name, entry.Type);
            break;
    }
}

var registeredConnectorNames = connectorRegistry.ListNames();
logger.LogInformation("Connector registry loaded: {Count} connector(s) [{Names}]",
    registeredConnectorNames.Count,
    registeredConnectorNames.Count == 0 ? "-" : string.Join(", ", registeredConnectorNames));

logger.LogInformation("LLM: provider={Provider}, model={Model}, key={KeyPreview}...",
    providerName, modelName, apiKey.Length > 8 ? apiKey[..8] : "***");

// ─── Load workflow YAML ───

var workflowPath = Path.Combine(AppContext.BaseDirectory, "workflows", "maker_analysis.yaml");
if (!File.Exists(workflowPath))
    workflowPath = Path.Combine(Directory.GetCurrentDirectory(), "workflows", "maker_analysis.yaml");

if (!File.Exists(workflowPath))
{
    logger.LogError("Workflow YAML not found at {Path}", workflowPath);
    return;
}

var workflowYaml = File.ReadAllText(workflowPath);
logger.LogInformation("Loaded workflow: {Path}", workflowPath);

// ─── Create WorkflowGAgent ───

var actor = await runtime.CreateAsync<WorkflowGAgent>("maker-root");
var workflowName = "maker_analysis";
if (actor.Agent is WorkflowGAgent wf)
{
    wf.State.WorkflowYaml = workflowYaml;
    wf.State.WorkflowName = workflowName;
    await wf.ActivateAsync();
    workflowName = wf.State.WorkflowName;
}

logger.LogInformation("WorkflowGAgent created: {Id}", actor.Id);

// ─── Subscribe to stream ───

var recorder = new MakerRunRecorder(actor.Id);
var stream = streams.GetStream(actor.Id);
var tcs = new TaskCompletionSource<string>();
var timeoutMinutes = 10;
if (int.TryParse(config["Maker:TimeoutMinutes"], out var cfgTimeoutMinutes) && cfgTimeoutMinutes > 0)
    timeoutMinutes = cfgTimeoutMinutes;
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
logger.LogInformation("Run timeout: {TimeoutMinutes} minutes", timeoutMinutes);

await using var sub = await stream.SubscribeAsync<EventEnvelope>(envelope =>
{
    if (envelope.Payload == null) return Task.CompletedTask;
    recorder.RecordEnvelope(envelope);
    var typeUrl = envelope.Payload.TypeUrl;

    if (typeUrl.Contains("TextMessageStartEvent"))
    {
        var evt = envelope.Payload.Unpack<TextMessageStartEvent>();
        logger.LogInformation("Role stream started: agent={AgentId}, session={SessionId}",
            string.IsNullOrWhiteSpace(evt.AgentId) ? envelope.PublisherId : evt.AgentId,
            evt.SessionId);
    }

    if (typeUrl.Contains("TextMessageEndEvent"))
    {
        var evt = envelope.Payload.Unpack<TextMessageEndEvent>();
        var publisher = string.IsNullOrWhiteSpace(envelope.PublisherId) ? "(unknown)" : envelope.PublisherId;

        // Print every role agent's full LLM reply.
        if (!string.Equals(publisher, actor.Id, StringComparison.Ordinal))
        {
            Console.WriteLine();
            Console.WriteLine($"================ ROLE LLM REPLY [{publisher}] ================");
            Console.WriteLine(evt.Content);
            Console.WriteLine("==============================================================");
            Console.WriteLine();
        }
    }

    if (typeUrl.Contains("WorkflowCompletedEvent"))
    {
        var evt = envelope.Payload.Unpack<WorkflowCompletedEvent>();
        if (evt.Success)
        {
            logger.LogInformation("Workflow completed successfully");
            tcs.TrySetResult(evt.Output);
        }
        else
        {
            logger.LogError("Workflow failed: {Error}", evt.Error);
            tcs.TrySetResult($"[ERROR] {evt.Error}");
        }
    }

    if (typeUrl.Contains("StepCompletedEvent"))
    {
        var evt = envelope.Payload.Unpack<StepCompletedEvent>();
        var preview = evt.Output.Length > 100 ? evt.Output[..100] + "..." : evt.Output;
        logger.LogInformation("Step {StepId}: success={Success} | {Preview}",
            evt.StepId, evt.Success, preview);
    }

    return Task.CompletedTask;
});

// ─── Input text ───

var inputText = args.Length > 0
    ? string.Join(" ", args)
    : """
      Large language models (LLMs) have achieved remarkable breakthroughs in reasoning,
      insights, and tool use, but chaining these abilities into extended processes at the
      scale of those routinely executed by humans, organizations, and societies has remained
      out of reach. The models have a persistent error rate that prevents scale-up.
      This paper describes MAKER, the first system that successfully solves a task with over
      one million LLM steps with zero errors. The approach relies on an extreme decomposition
      of a task into subtasks, each of which can be tackled by focused microagents. The high
      level of modularity resulting from the decomposition allows error correction to be
      applied at each step through an efficient multi-agent voting scheme. This combination
      of extreme decomposition and error correction makes scaling possible.
      """;

logger.LogInformation("Input: {Len} chars", inputText.Length);

// ─── Send request ───

var runStartedAt = DateTimeOffset.UtcNow;
var chatEvt = new ChatRequestEvent { Prompt = inputText, SessionId = "maker-demo" };
var envelope = new EventEnvelope
{
    Id = Guid.NewGuid().ToString("N"),
    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
    Payload = Any.Pack(chatEvt),
    PublisherId = "maker-cli",
    Direction = EventDirection.Self,
};

await actor.HandleEventAsync(envelope);

// ─── Wait for result ───

var timedOut = false;
try
{
    var result = await tcs.Task.WaitAsync(cts.Token);
    Console.WriteLine();
    Console.WriteLine("═══════════════════════════════════════════════════════════");
    Console.WriteLine("  MAKER Analysis Result");
    Console.WriteLine("═══════════════════════════════════════════════════════════");
    Console.WriteLine(result);
    Console.WriteLine("═══════════════════════════════════════════════════════════");
}
catch (OperationCanceledException)
{
    timedOut = true;
    logger.LogWarning("Workflow timed out after {TimeoutMinutes} minutes", timeoutMinutes);
}

// ─── Persist execution trace report (JSON + HTML) ───

var runEndedAt = DateTimeOffset.UtcNow;
var topology = new List<MakerTopologyEdge>();
var allActors = await runtime.GetAllAsync();
foreach (var a in allActors)
{
    var parent = await a.GetParentIdAsync();
    if (!string.IsNullOrWhiteSpace(parent))
        topology.Add(new MakerTopologyEdge(parent, a.Id));
}

var report = recorder.BuildReport(
    workflowName,
    workflowPath,
    providerName,
    modelName,
    inputText,
    runStartedAt,
    runEndedAt,
    timedOut,
    topology);

var (jsonPath, htmlPath) = MakerRunReportWriter.BuildDefaultPaths(Path.Combine("artifacts", "maker"));
await MakerRunReportWriter.WriteAsync(report, jsonPath, htmlPath);
logger.LogInformation("Execution report saved: json={JsonPath}, html={HtmlPath}", jsonPath, htmlPath);
Console.WriteLine($"Execution JSON: {jsonPath}");
Console.WriteLine($"Execution HTML: {htmlPath}");

// ─── Cleanup ───

await runtime.DestroyAsync(actor.Id);
(sp as IDisposable)?.Dispose();
