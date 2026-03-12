// ─────────────────────────────────────────────────────────────
// Workflow Primitives Demo
//
// Showcases every built-in workflow event module (primitive)
// through focused YAML workflows.
//
// Usage:
//   dotnet run                          # list all demos
//   dotnet run -- transform          # run a specific demo
//   dotnet run -- --deterministic       # run all no-LLM demos
//   dotnet run -- --all                 # run everything (needs LLM key)
//
//   # LLM key (for LLM-powered demos):
//   export DEEPSEEK_API_KEY="sk-..."
//   dotnet run -- llm_call
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Core;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Core.Agents;
using Aevatar.AI.LLMProviders.MEAI;
using Aevatar.Bootstrap;
using Aevatar.Bootstrap.Connectors;
using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Workflow.Core;
using Aevatar.Configuration;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Workflows;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Aevatar.Workflow.Infrastructure.Workflows;

// ─── Deterministic demo inputs (no LLM needed) ───

var demoInputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["transform"] = """
        Line one: hello world
        Line two: foo bar
        Line three: baz qux
        Line four: the quick brown fox
        Line five: jumps over the lazy dog
        """,

    ["guard"] = """{"name": "Alice", "email": "alice@example.com", "age": 30}""",

    ["conditional"] = "URGENT: Server is down, all requests failing with 502 errors.",

    ["switch"] = "bug: Login button does not respond on mobile Safari",

    ["assign"] = "The answer to the ultimate question is 42.",

    ["retrieve_facts"] = """
        Earth orbits the Sun at about 150 million km
        Water boils at 100 degrees Celsius at sea level
        The speed of light is approximately 300000 km per second
        Mount Everest is 8849 meters tall
        The human body contains about 60 percent water
        Python was created by Guido van Rossum in 1991
        Rust language focuses on memory safety without garbage collection
        TCP uses a three-way handshake to establish connections
        """,

    ["pipeline"] = """
        Earth orbits the Sun at about 150 million km
        The speed of light is approximately 300000 km per second
        Water boils at 100 degrees Celsius at sea level
        Python was created by Guido van Rossum
        Light travels faster than sound
        """,

    ["llm_call"] = "Explain the concept of event sourcing in 3 sentences.",

    ["llm_chain"] = "Distributed systems face challenges in maintaining consistency across nodes.",

    ["parallel"] = "What are the benefits of using microservices architecture?",

    ["race"] = "Give a one-sentence definition of functional programming.",

    ["map_reduce"] = """
        Topic: Benefits of remote work
        ---
        Topic: Challenges of remote work
        ---
        Topic: Future of remote work
        """,

    ["foreach"] = """
        Kubernetes
        ---
        Docker
        ---
        Terraform
        """,

    ["evaluate"] = "Write a haiku about programming.",

    ["reflect"] = "Write a concise explanation of the CAP theorem suitable for a junior developer.",

    ["cache"] = "What is the difference between SQL and NoSQL databases?",

    ["workflow_call_multilevel"] = """
          apple  
        banana
          apple
        carrot  
        """,
};

var deterministicWorkflows = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "transform", "guard", "conditional", "switch",
    "assign", "retrieve_facts", "pipeline",
    "workflow_call_multilevel",
};

// ─── Parse CLI ───

var workflowsToRun = new List<string>();
var showList = false;

if (args.Length == 0)
{
    showList = true;
}
else
{
    foreach (var arg in args)
    {
        switch (arg.ToLowerInvariant())
        {
            case "--deterministic":
                workflowsToRun.AddRange(deterministicWorkflows.Order());
                break;
            case "--all":
                workflowsToRun.AddRange(demoInputs.Keys.Order());
                break;
            case "--list":
                showList = true;
                break;
            default:
                var name = arg.Replace(".yaml", "");
                if (demoInputs.ContainsKey(name))
                    workflowsToRun.Add(name);
                else
                    Console.WriteLine($"Unknown workflow: {arg}");
                break;
        }
    }
}

if (showList || workflowsToRun.Count == 0)
{
    Console.WriteLine();
    Console.WriteLine("Aevatar Workflow Primitives Demo");
    Console.WriteLine("================================");
    Console.WriteLine();
    Console.WriteLine("Deterministic (no LLM needed):");
    foreach (var name in deterministicWorkflows.Order())
        Console.WriteLine($"  {name}");
    Console.WriteLine();
    Console.WriteLine("LLM-powered (requires API key):");
    foreach (var name in demoInputs.Keys.Order().Where(k => !deterministicWorkflows.Contains(k)))
        Console.WriteLine($"  {name}");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run -- <name>              Run a single demo");
    Console.WriteLine("  dotnet run -- --deterministic     Run all deterministic demos");
    Console.WriteLine("  dotnet run -- --all               Run all demos");
    Console.WriteLine("  dotnet run -- --list              Show this list");
    Console.WriteLine();
    if (workflowsToRun.Count == 0) return;
}

// ─── Determine if LLM is needed ───

var needsLlm = workflowsToRun.Any(w => !deterministicWorkflows.Contains(w));

// ─── Load config + resolve LLM key ───

var config = new ConfigurationBuilder()
    .AddAevatarConfig()
    .AddEnvironmentVariables()
    .Build();

string? apiKey = null;
var providerName = "deepseek";
var modelName = "deepseek-chat";

if (needsLlm)
{
    var secrets = new AevatarSecretsStore();

    apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
          ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
          ?? Environment.GetEnvironmentVariable("AEVATAR_LLM_API_KEY");

    if (!string.IsNullOrEmpty(apiKey))
    {
        providerName = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") != null ? "deepseek" : "openai";
        modelName = providerName == "deepseek" ? "deepseek-chat" : "gpt-4o-mini";
    }
    else
    {
        var defaultProv = secrets.GetDefaultProvider();
        providerName = defaultProv ?? config["Models:DefaultProvider"] ?? "deepseek";
        modelName = config["Models:DefaultModel"] ?? "deepseek-chat";
        apiKey = secrets.GetApiKey(providerName);

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
        Console.WriteLine("No LLM API key found. LLM demos will be skipped.");
        Console.WriteLine("Set DEEPSEEK_API_KEY or OPENAI_API_KEY to enable them.");
        Console.WriteLine();
        workflowsToRun.RemoveAll(w => !deterministicWorkflows.Contains(w));
        needsLlm = false;
        if (workflowsToRun.Count == 0) return;
    }
}

var workflowRootCandidates = new[]
{
    Path.Combine(AppContext.BaseDirectory, "workflows"),
    Path.Combine(Directory.GetCurrentDirectory(), "workflows"),
};
var workflowRoot = workflowRootCandidates.FirstOrDefault(Directory.Exists) ?? workflowRootCandidates[0];
var workflowDefinitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
if (Directory.Exists(workflowRoot))
{
    foreach (var pattern in new[] { "*.yaml", "*.yml" })
    {
        foreach (var workflowPath in Directory.EnumerateFiles(workflowRoot, pattern, SearchOption.AllDirectories))
        {
            var workflowKey = Path.GetFileNameWithoutExtension(workflowPath);
            workflowDefinitions[workflowKey] = File.ReadAllText(workflowPath);
        }
    }
}
var workflowRegistry = new WorkflowDefinitionRegistry();
foreach (var (workflowName, workflowYaml) in workflowDefinitions)
    workflowRegistry.Register(workflowName, workflowYaml);

// ─── Build services ───

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
services.AddAevatarBootstrap(config);
services.AddAevatarAIFeatures(config, options =>
{
    options.EnableMCPTools = false;
    options.EnableMEAIProviders = false;
    options.EnableMEAIToTornadoFailover = false;
    options.EnableSkills = false;
});
services.AddAevatarWorkflow();
services.AddSingleton<IRoleAgentTypeResolver, RoleGAgentTypeResolver>();
services.AddSingleton<IWorkflowDefinitionRegistry>(workflowRegistry);
services.AddSingleton<IWorkflowDefinitionResolver, RegistryWorkflowDefinitionResolver>();

if (needsLlm && !string.IsNullOrEmpty(apiKey))
{
    var isDeepSeek = providerName.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
    if (isDeepSeek)
    {
        providerName = "deepseek";
        if (!modelName.Contains("deepseek")) modelName = "deepseek-chat";
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
}

var sp = services.BuildServiceProvider();
var connectorRegistry = sp.GetService<IConnectorRegistry>();
if (connectorRegistry != null)
{
    var connectorBuilders = sp.GetServices<IConnectorBuilder>();
    var connectorLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("WorkflowDemoConnectorBootstrap");
    ConnectorRegistration.RegisterConnectors(connectorRegistry, connectorBuilders, connectorLogger);
}
var runtime = sp.GetRequiredService<IActorRuntime>();
var streams = sp.GetRequiredService<IStreamProvider>();

// ─── Run workflows ───

foreach (var workflowName in workflowsToRun)
{
    Console.WriteLine();
    Console.WriteLine($"╔══════════════════════════════════════════════════════════╗");
    Console.WriteLine($"║  Demo: {workflowName,-49}║");
    Console.WriteLine($"╚══════════════════════════════════════════════════════════╝");

    if (!workflowDefinitions.TryGetValue(workflowName, out var yaml))
    {
        Console.WriteLine($"  YAML not found: {workflowName}.yaml/.yml — skipping");
        continue;
    }
    var actorId = $"demo-{workflowName}-{Guid.NewGuid():N}"[..32];
    var actor = await runtime.CreateAsync<WorkflowGAgent>(actorId);

    await actor.HandleEventAsync(new EventEnvelope
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(new BindWorkflowDefinitionEvent
        {
            WorkflowYaml = yaml,
            WorkflowName = workflowName,
        }),
        PublisherId = "primitives.demo",
        Direction = EventDirection.Self,
        CorrelationId = Guid.NewGuid().ToString("N"),
    });

    var tcs = new TaskCompletionSource<(bool Success, string Output, string Error)>();
    var stepLog = new List<string>();

    var stream = streams.GetStream(actor.Id);
    await using var sub = await stream.SubscribeAsync<EventEnvelope>(envelope =>
    {
        var payload = envelope.Payload;
        if (payload == null) return Task.CompletedTask;

        if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            var preview = evt.Output.Length > 120 ? evt.Output[..120] + "..." : evt.Output;
            preview = preview.ReplaceLineEndings(" ");
            stepLog.Add($"  [{(evt.Success ? "OK" : "FAIL")}] {evt.StepId}: {preview}");
        }

        if (payload.Is(WorkflowCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<WorkflowCompletedEvent>();
            if (string.Equals(envelope.PublisherId, actor.Id, StringComparison.Ordinal))
                tcs.TrySetResult((evt.Success, evt.Output, evt.Error));
        }

        if (payload.Is(ChatResponseEvent.Descriptor))
        {
            var evt = payload.Unpack<ChatResponseEvent>();
            if (string.Equals(envelope.PublisherId, actor.Id, StringComparison.Ordinal))
                tcs.TrySetResult((false, string.Empty, evt.Content));
        }

        if (payload.Is(TextMessageEndEvent.Descriptor))
        {
            var evt = payload.Unpack<TextMessageEndEvent>();
            var publisher = envelope.PublisherId ?? "";
            if (string.Equals(publisher, actor.Id, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(evt.Content))
            {
                var isFailure = evt.Content.Contains("失败", StringComparison.OrdinalIgnoreCase);
                tcs.TrySetResult((!isFailure, evt.Content, isFailure ? evt.Content : string.Empty));
            }

            if (!string.Equals(publisher, actor.Id, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(evt.Content))
            {
                var role = publisher.Contains(':') ? publisher[(publisher.LastIndexOf(':') + 1)..] : publisher;
                var contentPreview = evt.Content.Length > 200 ? evt.Content[..200] + "..." : evt.Content;
                stepLog.Add($"  [LLM:{role}] {contentPreview.ReplaceLineEndings(" ")}");
            }
        }

        return Task.CompletedTask;
    });

    var input = demoInputs.GetValueOrDefault(workflowName, "Hello, world!");
    Console.WriteLine($"  Input: {(input.Length > 80 ? input[..80].ReplaceLineEndings(" ") + "..." : input.ReplaceLineEndings(" "))}");
    Console.WriteLine();

    await actor.HandleEventAsync(new EventEnvelope
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(new ChatRequestEvent { Prompt = input, SessionId = $"demo-{workflowName}" }),
        PublisherId = "primitives.demo",
        Direction = EventDirection.Self,
    });

    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    try
    {
        var (success, output, error) = await tcs.Task.WaitAsync(cts.Token);

        foreach (var line in stepLog)
            Console.WriteLine(line);

        Console.WriteLine();
        if (success)
        {
            var display = output.Length > 500 ? output[..500] + "\n  ... (truncated)" : output;
            Console.WriteLine($"  Result: {display}");
        }
        else
        {
            Console.WriteLine($"  FAILED: {error}");
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("  TIMEOUT (2 min)");
    }

    await runtime.DestroyAsync(actor.Id);
    Console.WriteLine();
}

// ─── Cleanup ───

(sp as IDisposable)?.Dispose();
Console.WriteLine("Done.");
