// ─────────────────────────────────────────────────────────────
// MAKER Demo - paper/text analysis with decomposition + voting
//
// Demonstrates the MAKER pattern from:
// "Solving a Million-Step LLM Task with Zero Errors"
// https://arxiv.org/html/2511.09030v1
//
// Usage:
//   cd demos/Aevatar.Demos.Maker && dotnet run
//   # uses API key from ~/.aevatar/secrets.json (auto-decrypted)
//
//   # deterministic verification mode (no API key required):
//   dotnet run -- --mode deterministic
//
//   # Or override with env var:
//   export DEEPSEEK_API_KEY="sk-..."
//   dotnet run
//
//   # Or pass custom text:
//   dotnet run -- --mode llm -- "Your paper text here..."
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Core.Agents;
using Aevatar.Bootstrap;
using Aevatar.Bootstrap.Connectors;
using Aevatar.AI.LLMProviders.MEAI;
using Aevatar.Workflow.Core;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Extensions.Maker;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Demos.Maker;
using Aevatar.Maker.Projection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ─── Load ~/.aevatar/ config ───

var config = new ConfigurationBuilder()
    .AddAevatarConfig()
    .AddEnvironmentVariables()
    .Build();

MakerCliOptions options;
try
{
    options = MakerCliOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.WriteLine($"Invalid arguments: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine(MakerCliOptions.BuildHelpText());
    return;
}

if (options.ShowHelp)
{
    Console.WriteLine(MakerCliOptions.BuildHelpText());
    return;
}

var runMode = options.Mode;
var deterministicMode = options.IsDeterministicMode;

// ─── Resolve LLM provider ───

var secrets = new AevatarSecretsStore();

string? apiKey = null;
string providerName;
string modelName;
var isDeepSeek = false;

if (deterministicMode)
{
    providerName = "deterministic";
    modelName = "maker-deterministic";
}
else
{
    // 1) Check env vars first
    apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
          ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
          ?? Environment.GetEnvironmentVariable("AEVATAR_LLM_API_KEY");

    // 2) Fall back to encrypted secrets.json
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
    isDeepSeek = providerName.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
    if (isDeepSeek)
    {
        providerName = "deepseek";
        if (!modelName.Contains("deepseek")) modelName = "deepseek-chat";
    }
}

// ─── Build services ───

var services = new ServiceCollection();

services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
services.AddAevatarRuntime();
services.AddAevatarConfig();
services.AddAevatarWorkflow();
services.AddWorkflowMakerExtensions();
services.AddSingleton<IRoleAgentTypeResolver, RoleGAgentTypeResolver>();

if (deterministicMode)
{
    var deterministicProvider = new DeterministicMakerProvider();
    services.AddSingleton<ILLMProvider>(deterministicProvider);
    services.AddSingleton<ILLMProviderFactory>(deterministicProvider);
}
else if (isDeepSeek)
{
    services.AddMEAIProviders(f => f
        .RegisterOpenAI("deepseek", modelName, apiKey!, baseUrl: "https://api.deepseek.com/v1")
        .SetDefault("deepseek"));
}
else
{
    services.AddMEAIProviders(f => f
        .RegisterOpenAI(providerName, modelName, apiKey!)
        .SetDefault(providerName));
}

var sp = services.BuildServiceProvider();
var runtime = sp.GetRequiredService<IActorRuntime>();
var streams = sp.GetRequiredService<IStreamProvider>();
var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Maker");
var connectorLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Maker.Connectors");
var connectorBuilders = sp.GetServices<IConnectorBuilder>();

// ─── Register named connectors from ~/.aevatar/connectors.json ───
var connectorRegistry = sp.GetRequiredService<IConnectorRegistry>();
var loadedConnectorCount = ConnectorRegistration.RegisterConnectors(connectorRegistry, connectorBuilders, connectorLogger);
if (loadedConnectorCount == 0)
{
    var localConnectorPath = Path.Combine(AppContext.BaseDirectory, "connectors", "maker.connectors.json");
    if (!File.Exists(localConnectorPath))
        localConnectorPath = Path.Combine(Directory.GetCurrentDirectory(), "connectors", "maker.connectors.json");
    if (File.Exists(localConnectorPath))
    {
        connectorLogger.LogInformation("Loading demo connectors from {Path}", localConnectorPath);
        ConnectorRegistration.RegisterConnectors(connectorRegistry, connectorBuilders, connectorLogger, localConnectorPath);
    }
}

var registeredConnectorNames = connectorRegistry.ListNames();
logger.LogInformation("Connector registry loaded: {Count} connector(s) [{Names}]",
    registeredConnectorNames.Count,
    registeredConnectorNames.Count == 0 ? "-" : string.Join(", ", registeredConnectorNames));

if (deterministicMode)
{
    logger.LogInformation("LLM mode={Mode}, provider={Provider}, model={Model}",
        runMode, providerName, modelName);
}
else
{
    logger.LogInformation("LLM mode={Mode}, provider={Provider}, model={Model}, key={KeyPreview}...",
        runMode, providerName, modelName, apiKey!.Length > 8 ? apiKey[..8] : "***");
}

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
await actor.HandleEventAsync(new EventEnvelope
{
    Id = Guid.NewGuid().ToString("N"),
    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
    Payload = Any.Pack(new BindWorkflowDefinitionEvent
    {
        WorkflowYaml = workflowYaml,
        WorkflowName = workflowName,
    }),
    PublisherId = "maker.demo",
    Direction = EventDirection.Self,
    CorrelationId = Guid.NewGuid().ToString("N"),
});

logger.LogInformation("WorkflowGAgent created: {Id}", actor.Id);

// ─── Subscribe to stream ───

var recorder = new MakerRunProjectionAccumulator(actor.Id);
var stream = streams.GetStream(actor.Id);
var tcs = new TaskCompletionSource<string>();
var timeoutMinutes = 10;
if (int.TryParse(config["Maker:TimeoutMinutes"], out var cfgTimeoutMinutes) && cfgTimeoutMinutes > 0)
    timeoutMinutes = cfgTimeoutMinutes;
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
logger.LogInformation("Run timeout: {TimeoutMinutes} minutes", timeoutMinutes);

await using var sub = await stream.SubscribeAsync<EventEnvelope>(envelope =>
{
    var payload = envelope.Payload;
    if (payload == null) return Task.CompletedTask;
    recorder.RecordEnvelope(envelope);

    if (payload.Is(TextMessageStartEvent.Descriptor))
    {
        var evt = payload.Unpack<TextMessageStartEvent>();
        logger.LogInformation("Role stream started: agent={AgentId}, session={SessionId}",
            string.IsNullOrWhiteSpace(evt.AgentId) ? envelope.PublisherId : evt.AgentId,
            evt.SessionId);
    }

    if (payload.Is(TextMessageEndEvent.Descriptor))
    {
        var evt = payload.Unpack<TextMessageEndEvent>();
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

    if (payload.Is(WorkflowCompletedEvent.Descriptor))
    {
        var evt = payload.Unpack<WorkflowCompletedEvent>();
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

    if (payload.Is(StepCompletedEvent.Descriptor))
    {
        var evt = payload.Unpack<StepCompletedEvent>();
        var preview = evt.Output.Length > 100 ? evt.Output[..100] + "..." : evt.Output;
        logger.LogInformation("Step {StepId}: success={Success} | {Preview}",
            evt.StepId, evt.Success, preview);
    }

    return Task.CompletedTask;
});

// ─── Input text ───

var inputText = !string.IsNullOrWhiteSpace(options.InputText)
    ? options.InputText
    : MakerCliOptions.DefaultInputText;

logger.LogInformation("Run mode: {Mode}", runMode);
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
var visited = new HashSet<string>(StringComparer.Ordinal);
var queue = new Queue<string>();
queue.Enqueue(actor.Id);
while (queue.Count > 0)
{
    var parentId = queue.Dequeue();
    if (!visited.Add(parentId))
        continue;

    var parentActor = await runtime.GetAsync(parentId);
    if (parentActor == null)
        continue;

    var children = await parentActor.GetChildrenIdsAsync();
    foreach (var childId in children)
    {
        topology.Add(new MakerTopologyEdge(parentId, childId));
        queue.Enqueue(childId);
    }
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
logger.LogInformation("Verification: fullFlowPassed={Passed}, failedChecks={FailedCount}",
    report.Verification.FullFlowPassed,
    report.Verification.FailedChecks.Count);
Console.WriteLine($"Execution JSON: {jsonPath}");
Console.WriteLine($"Execution HTML: {htmlPath}");
Console.WriteLine($"Verification FullFlowPassed: {report.Verification.FullFlowPassed}");
if (report.Verification.FailedChecks.Count > 0)
    Console.WriteLine($"Verification FailedChecks: {string.Join(", ", report.Verification.FailedChecks)}");

// ─── Cleanup ───

await runtime.DestroyAsync(actor.Id);
(sp as IDisposable)?.Dispose();
