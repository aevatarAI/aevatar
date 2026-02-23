using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Core.Agents;
using Aevatar.AI.LLMProviders.MEAI;
using Aevatar.Bootstrap;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http.Json;

var port = 5280;
var noBrowser = args.Contains("--no-browser");
var portArg = Array.IndexOf(args, "--port");
if (portArg >= 0 && portArg + 1 < args.Length && int.TryParse(args[portArg + 1], out var customPort))
    port = customPort;

var builder = WebApplication.CreateBuilder(args);

var url = $"http://localhost:{port}";
builder.WebHost.UseUrls(url);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddAevatarRuntime();
builder.Services.AddAevatarConfig();
builder.Services.AddAevatarWorkflow();
builder.Services.AddSingleton<IRoleAgentTypeResolver, RoleGAgentTypeResolver>();

var yamlDir = ResolveYamlDir();

var config = new ConfigurationBuilder()
    .AddAevatarConfig()
    .AddEnvironmentVariables()
    .Build();

string? apiKey = null;
var providerName = "deepseek";
var modelName = "deepseek-chat";

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

var llmAvailable = !string.IsNullOrEmpty(apiKey);
if (llmAvailable)
{
    var isDeepSeek = providerName.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
    if (isDeepSeek)
    {
        providerName = "deepseek";
        if (!modelName.Contains("deepseek")) modelName = "deepseek-chat";
        builder.Services.AddMEAIProviders(f => f
            .RegisterOpenAI("deepseek", modelName, apiKey!, baseUrl: "https://api.deepseek.com/v1")
            .SetDefault("deepseek"));
    }
    else
    {
        builder.Services.AddMEAIProviders(f => f
            .RegisterOpenAI(providerName, modelName, apiKey!)
            .SetDefault(providerName));
    }
}

var app = builder.Build();

Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║           Workflow Primitives Web UI                    ║");
Console.WriteLine("╠══════════════════════════════════════════════════════════╣");
Console.WriteLine($"║  Web UI: {url,-48}║");
Console.WriteLine($"║  YAML:   {yamlDir,-48}║");
Console.WriteLine($"║  LLM:    {(llmAvailable ? $"{providerName}/{modelName}" : "not configured"),-48}║");
Console.WriteLine("║  Press Ctrl+C to stop                                  ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
Console.WriteLine();
app.Lifetime.ApplicationStarted.Register(() => { if (!noBrowser) OpenBrowser(url); });

app.UseDefaultFiles();
app.UseStaticFiles();

var deterministicWorkflows = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "01_transform", "02_guard", "03_conditional", "04_switch",
    "05_assign", "06_retrieve_facts", "07_pipeline",
};

var demoInputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["01_transform"] = "Line one: hello world\nLine two: foo bar\nLine three: baz qux\nLine four: the quick brown fox\nLine five: jumps over the lazy dog",
    ["02_guard"] = """{"name": "Alice", "email": "alice@example.com", "age": 30}""",
    ["03_conditional"] = "URGENT: Server is down, all requests failing with 502 errors.",
    ["04_switch"] = "bug: Login button does not respond on mobile Safari",
    ["05_assign"] = "The answer to the ultimate question is 42.",
    ["06_retrieve_facts"] = "Earth orbits the Sun at about 150 million km\nWater boils at 100 degrees Celsius at sea level\nThe speed of light is approximately 300000 km per second\nMount Everest is 8849 meters tall",
    ["07_pipeline"] = "Earth orbits the Sun at about 150 million km\nThe speed of light is approximately 300000 km per second\nWater boils at 100 degrees Celsius at sea level\nPython was created by Guido van Rossum\nLight travels faster than sound",
    ["08_llm_call"] = "Explain the concept of event sourcing in 3 sentences.",
    ["09_llm_chain"] = "Distributed systems face challenges in maintaining consistency across nodes.",
    ["10_parallel"] = "What are the benefits of using microservices architecture?",
    ["11_race"] = "Give a one-sentence definition of functional programming.",
    ["12_map_reduce"] = "Topic: Benefits of remote work\n---\nTopic: Challenges of remote work\n---\nTopic: Future of remote work",
    ["13_foreach"] = "Kubernetes\n---\nDocker\n---\nTerraform",
    ["14_evaluate"] = "Write a haiku about programming.",
    ["15_reflect"] = "Write a concise explanation of the CAP theorem suitable for a junior developer.",
    ["16_cache"] = "What is the difference between SQL and NoSQL databases?",
};

var parser = new WorkflowParser();

// GET /api/workflows — list all workflows
app.MapGet("/api/workflows", () =>
{
    var yamlFiles = Directory.GetFiles(yamlDir, "*.yaml").OrderBy(f => f).ToList();
    var workflows = new List<object>();
    foreach (var file in yamlFiles)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        try
        {
            var yaml = File.ReadAllText(file);
            var def = parser.Parse(yaml);
            var primitives = def.Steps.Select(s => s.Type).Distinct().ToList();
            workflows.Add(new
            {
                name,
                description = def.Description,
                category = deterministicWorkflows.Contains(name) ? "deterministic" : "llm",
                primitives,
                defaultInput = demoInputs.GetValueOrDefault(name, "Hello, world!"),
            });
        }
        catch
        {
            workflows.Add(new
            {
                name,
                description = "Failed to parse",
                category = "unknown",
                primitives = new List<string>(),
                defaultInput = "",
            });
        }
    }
    return Results.Json(workflows);
});

// GET /api/workflows/{name} — workflow definition with edges
app.MapGet("/api/workflows/{name}", (string name) =>
{
    var file = Path.Combine(yamlDir, $"{name}.yaml");
    if (!File.Exists(file)) return Results.NotFound(new { error = $"Workflow '{name}' not found" });

    var yaml = File.ReadAllText(file);
    var def = parser.Parse(yaml);

    var steps = def.Steps.Select(s => new
    {
        id = s.Id,
        type = s.Type,
        targetRole = s.TargetRole,
        parameters = s.Parameters,
        next = s.Next,
        branches = s.Branches,
        children = s.Children?.Select(c => new { id = c.Id, type = c.Type, targetRole = c.TargetRole }).ToList(),
    }).ToList();

    var edges = ComputeEdges(def);

    return Results.Json(new
    {
        yaml,
        definition = new
        {
            name = def.Name,
            description = def.Description,
            roles = def.Roles.Select(r => new { id = r.Id, name = r.Name, systemPrompt = r.SystemPrompt }),
            steps,
        },
        edges,
    });
});

// GET /api/workflows/{name}/run — SSE execution endpoint
app.MapGet("/api/workflows/{name}/run", async (string name, string? input, HttpContext ctx, CancellationToken ct) =>
{
    var file = Path.Combine(yamlDir, $"{name}.yaml");
    if (!File.Exists(file))
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync($"Workflow '{name}' not found");
        return;
    }

    if (!deterministicWorkflows.Contains(name) && !llmAvailable)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("LLM not configured. Set DEEPSEEK_API_KEY or OPENAI_API_KEY.");
        return;
    }

    ctx.Response.Headers["Content-Type"] = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["Connection"] = "keep-alive";

    var yaml = File.ReadAllText(file);
    var actualInput = input ?? demoInputs.GetValueOrDefault(name, "Hello, world!");

    var runtime = ctx.RequestServices.GetRequiredService<IActorRuntime>();
    var streams = ctx.RequestServices.GetRequiredService<IStreamProvider>();
    var actorId = $"web-{name}-{Guid.NewGuid():N}"[..32];

    var actor = await runtime.CreateAsync<WorkflowGAgent>(actorId);

    await actor.HandleEventAsync(new EventEnvelope
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(new ConfigureWorkflowEvent
        {
            WorkflowYaml = yaml,
            WorkflowName = name,
        }),
        PublisherId = "web.demo",
        Direction = EventDirection.Self,
        CorrelationId = Guid.NewGuid().ToString("N"),
    });

    var tcs = new TaskCompletionSource<bool>();
    var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    async Task WriteSse(string eventType, object data)
    {
        var json = JsonSerializer.Serialize(data, jsonOpts);
        await ctx.Response.WriteAsync($"event: {eventType}\ndata: {json}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    var stream = streams.GetStream(actor.Id);
    await using var sub = await stream.SubscribeAsync<EventEnvelope>(async envelope =>
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        try
        {
            if (payload.Is(StepRequestEvent.Descriptor))
            {
                var evt = payload.Unpack<StepRequestEvent>();
                await WriteSse("step.request", new { stepId = evt.StepId, stepType = evt.StepType, input = Truncate(evt.Input, 500) });
            }

            if (payload.Is(StepCompletedEvent.Descriptor))
            {
                var evt = payload.Unpack<StepCompletedEvent>();
                var meta = new Dictionary<string, string>();
                foreach (var kv in evt.Metadata)
                    meta[kv.Key] = kv.Value;
                await WriteSse("step.completed", new
                {
                    stepId = evt.StepId,
                    success = evt.Success,
                    output = Truncate(evt.Output, 1000),
                    error = string.IsNullOrEmpty(evt.Error) ? null : evt.Error,
                    metadata = meta.Count > 0 ? meta : null,
                });
            }

            if (payload.Is(TextMessageEndEvent.Descriptor))
            {
                var evt = payload.Unpack<TextMessageEndEvent>();
                var publisher = envelope.PublisherId ?? "";
                if (!string.Equals(publisher, actor.Id, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(evt.Content))
                {
                    var role = publisher.Contains(':') ? publisher[(publisher.LastIndexOf(':') + 1)..] : publisher;
                    await WriteSse("llm.response", new { role, content = Truncate(evt.Content, 2000) });
                }
            }

            if (payload.Is(WorkflowCompletedEvent.Descriptor))
            {
                var evt = payload.Unpack<WorkflowCompletedEvent>();
                await WriteSse("workflow.completed", new
                {
                    success = evt.Success,
                    output = Truncate(evt.Output, 2000),
                    error = string.IsNullOrEmpty(evt.Error) ? null : evt.Error,
                });
                tcs.TrySetResult(evt.Success);
            }
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    });

    await actor.HandleEventAsync(new EventEnvelope
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(new ChatRequestEvent { Prompt = actualInput, SessionId = $"web-{name}" }),
        PublisherId = "web.demo",
        Direction = EventDirection.Self,
    });

    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        await tcs.Task.WaitAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        await WriteSse("workflow.error", new { error = "Timeout (2 min)" });
    }
    catch (Exception ex)
    {
        await WriteSse("workflow.error", new { error = ex.Message });
    }

    // Keep connection open briefly so the browser's EventSource processes the final event
    // before the TCP close triggers onerror/reconnect.
    await Task.Delay(500, CancellationToken.None);

    await runtime.DestroyAsync(actor.Id);
});

// GET /api/llm/status — LLM availability
app.MapGet("/api/llm/status", () => Results.Json(new
{
    available = llmAvailable,
    provider = llmAvailable ? providerName : null,
    model = llmAvailable ? modelName : null,
}));

// GET /api/primitives — module catalog
app.MapGet("/api/primitives", () =>
{
    var catalog = new[]
    {
        new { name = "transform", aliases = new[] { "transform" }, category = "data", description = "Text transformation: uppercase, lowercase, count words, trim, etc." },
        new { name = "guard", aliases = new[] { "guard", "assert" }, category = "control", description = "Input validation gate. Fails the step if the check is not met." },
        new { name = "conditional", aliases = new[] { "conditional" }, category = "control", description = "Binary branching: evaluates a condition and routes to true/false paths." },
        new { name = "switch", aliases = new[] { "switch" }, category = "control", description = "Multi-way branching: matches input against patterns to select a branch." },
        new { name = "while", aliases = new[] { "while", "loop" }, category = "control", description = "Loop that repeats child steps while a condition holds." },
        new { name = "foreach", aliases = new[] { "foreach", "for_each" }, category = "composition", description = "Iterates over a delimited list, executing child steps for each item." },
        new { name = "parallel", aliases = new[] { "parallel_fanout", "parallel", "fan_out" }, category = "composition", description = "Fan-out: sends input to multiple workers in parallel and merges results." },
        new { name = "race", aliases = new[] { "race", "select" }, category = "composition", description = "Sends input to multiple workers; returns the first response received." },
        new { name = "map_reduce", aliases = new[] { "map_reduce", "mapreduce" }, category = "composition", description = "Splits input into chunks, processes in parallel, then reduces results." },
        new { name = "llm_call", aliases = new[] { "llm_call" }, category = "ai", description = "Sends a prompt to an LLM and returns the response." },
        new { name = "tool_call", aliases = new[] { "tool_call" }, category = "ai", description = "Invokes a registered tool/function with the given parameters." },
        new { name = "connector_call", aliases = new[] { "connector_call", "bridge_call" }, category = "integration", description = "Calls an external connector/bridge service." },
        new { name = "evaluate", aliases = new[] { "evaluate", "judge" }, category = "ai", description = "LLM-as-judge: evaluates content against criteria and returns a score." },
        new { name = "reflect", aliases = new[] { "reflect" }, category = "ai", description = "Self-reflection loop: generates, critiques, and refines output." },
        new { name = "assign", aliases = new[] { "assign" }, category = "data", description = "Assigns a value to a named variable in the workflow context." },
        new { name = "retrieve_facts", aliases = new[] { "retrieve_facts" }, category = "data", description = "Retrieves relevant facts from input text matching a query." },
        new { name = "cache", aliases = new[] { "cache" }, category = "data", description = "Caches LLM responses to avoid redundant calls." },
        new { name = "emit", aliases = new[] { "emit", "publish" }, category = "integration", description = "Publishes an event to external listeners." },
        new { name = "delay", aliases = new[] { "delay", "sleep" }, category = "control", description = "Pauses execution for a specified duration." },
        new { name = "wait_signal", aliases = new[] { "wait_signal", "wait" }, category = "control", description = "Blocks until an external signal is received." },
        new { name = "checkpoint", aliases = new[] { "checkpoint" }, category = "control", description = "Saves workflow state for recovery/resume." },
        new { name = "human_approval", aliases = new[] { "human_approval" }, category = "human", description = "Pauses workflow until a human approves or rejects." },
        new { name = "human_input", aliases = new[] { "human_input" }, category = "human", description = "Pauses workflow until a human provides input." },
        new { name = "workflow_call", aliases = new[] { "workflow_call", "sub_workflow" }, category = "composition", description = "Invokes another workflow as a sub-workflow." },
        new { name = "vote_consensus", aliases = new[] { "vote_consensus", "vote" }, category = "composition", description = "Collects votes from multiple agents and determines consensus." },
        new { name = "workflow_loop", aliases = new[] { "workflow_loop" }, category = "control", description = "Internal orchestrator that drives step-by-step execution." },
    };
    return Results.Json(catalog);
});

app.MapFallbackToFile("index.html");

app.Run();

static List<object> ComputeEdges(WorkflowDefinition def)
{
    var edges = new List<object>();
    for (var i = 0; i < def.Steps.Count; i++)
    {
        var step = def.Steps[i];

        if (step.Branches is { Count: > 0 })
        {
            foreach (var (label, targetId) in step.Branches)
            {
                if (def.GetStep(targetId) != null)
                    edges.Add(new { from = step.Id, to = targetId, label });
            }
        }
        else if (step.Next != null)
        {
            if (def.GetStep(step.Next) != null)
                edges.Add(new { from = step.Id, to = step.Next });
        }
        else if (i + 1 < def.Steps.Count)
        {
            edges.Add(new { from = step.Id, to = def.Steps[i + 1].Id });
        }

        if (step.Children is { Count: > 0 })
        {
            foreach (var child in step.Children)
                edges.Add(new { from = step.Id, to = child.Id, label = "child" });
        }
    }
    return edges;
}

static string Truncate(string s, int max) => s.Length > max ? s[..max] + "..." : s;

static string ResolveYamlDir()
{
    var envDir = Environment.GetEnvironmentVariable("WORKFLOW_YAML_DIR");
    if (!string.IsNullOrEmpty(envDir) && Directory.Exists(envDir)) return envDir;

    var projectDir = Directory.GetCurrentDirectory();
    var sibling = Path.GetFullPath(Path.Combine(projectDir, "..", "Aevatar.Demos.Workflow", "workflows"));
    if (Directory.Exists(sibling)) return sibling;

    var fromBin = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? projectDir,
        "..", "..", "..", "..", "Aevatar.Demos.Workflow", "workflows"));
    if (Directory.Exists(fromBin)) return fromBin;

    throw new DirectoryNotFoundException(
        $"Cannot find workflow YAML files. Tried:\n  {sibling}\n  {fromBin}\nSet WORKFLOW_YAML_DIR to override.");
}

static void OpenBrowser(string url)
{
    try
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start("open", url);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Process.Start("xdg-open", url);
    }
    catch { }
}
