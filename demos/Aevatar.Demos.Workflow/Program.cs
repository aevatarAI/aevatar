// ─────────────────────────────────────────────────────────────
// Workflow Primitives Demo
//
// Showcases every built-in workflow event module (primitive)
// through focused YAML workflows.
//
// Usage:
//   dotnet run                          # list all demos
//   dotnet run -- 01_transform          # run a specific demo
//   dotnet run -- --deterministic       # run all no-LLM demos
//   dotnet run -- --all                 # run everything (needs LLM key)
//
//   # LLM key (for demos 08+):
//   export DEEPSEEK_API_KEY="sk-..."
//   dotnet run -- 08_llm_call
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Core.Agents;
using Aevatar.AI.LLMProviders.MEAI;
using Aevatar.Bootstrap;
using Aevatar.Workflow.Core;
using Aevatar.Configuration;
using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ─── Deterministic demo inputs (no LLM needed) ───

var demoInputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["01_transform"] = """
        Line one: hello world
        Line two: foo bar
        Line three: baz qux
        Line four: the quick brown fox
        Line five: jumps over the lazy dog
        """,

    ["02_guard"] = """{"name": "Alice", "email": "alice@example.com", "age": 30}""",

    ["03_conditional"] = "URGENT: Server is down, all requests failing with 502 errors.",

    ["04_switch"] = "bug: Login button does not respond on mobile Safari",

    ["05_assign"] = "The answer to the ultimate question is 42.",

    ["06_retrieve_facts"] = """
        Earth orbits the Sun at about 150 million km
        Water boils at 100 degrees Celsius at sea level
        The speed of light is approximately 300000 km per second
        Mount Everest is 8849 meters tall
        The human body contains about 60 percent water
        Python was created by Guido van Rossum in 1991
        Rust language focuses on memory safety without garbage collection
        TCP uses a three-way handshake to establish connections
        """,

    ["07_pipeline"] = """
        Earth orbits the Sun at about 150 million km
        The speed of light is approximately 300000 km per second
        Water boils at 100 degrees Celsius at sea level
        Python was created by Guido van Rossum
        Light travels faster than sound
        """,

    ["08_llm_call"] = "Explain the concept of event sourcing in 3 sentences.",

    ["09_llm_chain"] = "Distributed systems face challenges in maintaining consistency across nodes.",

    ["10_parallel"] = "What are the benefits of using microservices architecture?",

    ["11_race"] = "Give a one-sentence definition of functional programming.",

    ["12_map_reduce"] = """
        Topic: Benefits of remote work
        ---
        Topic: Challenges of remote work
        ---
        Topic: Future of remote work
        """,

    ["13_foreach"] = """
        Kubernetes
        ---
        Docker
        ---
        Terraform
        """,

    ["14_evaluate"] = "Write a haiku about programming.",

    ["15_reflect"] = "Write a concise explanation of the CAP theorem suitable for a junior developer.",

    ["16_cache"] = "What is the difference between SQL and NoSQL databases?",
};

var deterministicWorkflows = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "01_transform", "02_guard", "03_conditional", "04_switch",
    "05_assign", "06_retrieve_facts", "07_pipeline",
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

// ─── Build services ───

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
services.AddAevatarRuntime();
services.AddAevatarConfig();
services.AddAevatarWorkflow();
services.AddSingleton<IRoleAgentTypeResolver, RoleGAgentTypeResolver>();

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
var runtime = sp.GetRequiredService<IActorRuntime>();
var streams = sp.GetRequiredService<IStreamProvider>();

// ─── Run workflows ───

foreach (var workflowName in workflowsToRun)
{
    Console.WriteLine();
    Console.WriteLine($"╔══════════════════════════════════════════════════════════╗");
    Console.WriteLine($"║  Demo: {workflowName,-49}║");
    Console.WriteLine($"╚══════════════════════════════════════════════════════════╝");

    var yamlPath = Path.Combine(AppContext.BaseDirectory, "workflows", $"{workflowName}.yaml");
    if (!File.Exists(yamlPath))
        yamlPath = Path.Combine(Directory.GetCurrentDirectory(), "workflows", $"{workflowName}.yaml");

    if (!File.Exists(yamlPath))
    {
        Console.WriteLine($"  YAML not found: {workflowName}.yaml — skipping");
        continue;
    }

    var yaml = File.ReadAllText(yamlPath);
    var actorId = $"demo-{workflowName}-{Guid.NewGuid():N}"[..32];
    var actor = await runtime.CreateAsync<WorkflowGAgent>(actorId);

    await actor.HandleEventAsync(new EventEnvelope
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(new ConfigureWorkflowEvent
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
            tcs.TrySetResult((evt.Success, evt.Output, evt.Error));
        }

        if (payload.Is(TextMessageEndEvent.Descriptor))
        {
            var evt = payload.Unpack<TextMessageEndEvent>();
            var publisher = envelope.PublisherId ?? "";
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
