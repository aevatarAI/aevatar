using System.Text;
using System.Text.Json;
using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Demos.Cli.Messages;
using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Hooks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Demos.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "list" => HandleList(),
                "run" => await HandleRunAsync(args.Skip(1).ToArray()),
                "web" => await HandleWebAsync(args.Skip(1).ToArray()),
                _ => Fail("Unknown command. Use `list`, `run`, or `web`."),
            };
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[demo:error] {ex.Message}");
            return 1;
        }
    }

    private static int HandleList()
    {
        Console.WriteLine("Available scenarios:");
        foreach (var key in DemoScenarioRunner.ScenarioNames)
        {
            Console.WriteLine($"- {key}");
        }

        return 0;
    }

    private static async Task<int> HandleRunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return Fail("Missing scenario. Example: `run hierarchy`");
        }

        var scenario = args[0].ToLowerInvariant();
        string? jsonPath = null;
        string? webPath = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--json" && i + 1 < args.Length) jsonPath = args[++i];
            else if (args[i] == "--web" && i + 1 < args.Length) webPath = args[++i];
        }

        var result = await DemoScenarioRunner.RunAsync(scenario);
        RenderCli(result);

        jsonPath ??= BuildDefaultPath("artifacts/demo", $"{scenario}.json");
        await WriteJsonAsync(jsonPath, result);
        Console.WriteLine($"\nJSON report: {jsonPath}");

        if (!string.IsNullOrWhiteSpace(webPath))
        {
            await WriteHtmlAsync(result, webPath);
            Console.WriteLine($"HTML report: {webPath}");
        }

        return 0;
    }

    private static async Task<int> HandleWebAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return Fail("Missing json path. Example: `web artifacts/demo/hierarchy.json`");
        }

        var jsonPath = args[0];
        var outPath = args.Length >= 3 && args[1] == "--out"
            ? args[2]
            : BuildDefaultPath("artifacts/demo", "report.html");
        var json = await File.ReadAllTextAsync(jsonPath);
        var result = JsonSerializer.Deserialize<DemoRunResult>(json, JsonOptions.Default)
            ?? throw new InvalidOperationException("Invalid demo JSON report.");
        await WriteHtmlAsync(result, outPath);
        Console.WriteLine($"HTML report: {outPath}");
        return 0;
    }

    private static async Task WriteJsonAsync(string path, DemoRunResult result)
    {
        EnsureParentDirectory(path);
        var json = JsonSerializer.Serialize(result, JsonOptions.Default);
        await File.WriteAllTextAsync(path, json);
    }

    private static async Task WriteHtmlAsync(DemoRunResult result, string path)
    {
        EnsureParentDirectory(path);
        var html = DemoHtmlReportWriter.Write(result);
        await File.WriteAllTextAsync(path, html);
    }

    private static string BuildDefaultPath(string dir, string fileName)
    {
        Directory.CreateDirectory(dir);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var ext = Path.GetExtension(fileName);
        var name = Path.GetFileNameWithoutExtension(fileName);
        return Path.Combine(dir, $"{name}-{stamp}{ext}");
    }

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void RenderCli(DemoRunResult result)
    {
        Console.WriteLine($"Scenario: {result.Scenario}");
        Console.WriteLine($"Description: {result.Description}");
        Console.WriteLine("\nTopology:");
        foreach (var edge in result.Topology.Edges)
        {
            Console.WriteLine($"- {edge.Parent} -> {edge.Child}");
        }

        if (result.Topology.Edges.Count == 0)
        {
            Console.WriteLine("- (single node or no links)");
        }

        Console.WriteLine("\nTimeline:");
        foreach (var evt in result.Events)
        {
            var text = $"{evt.Timestamp:HH:mm:ss.fff} [{evt.Stage}] {evt.Message}";
            if (evt.Targets.Count > 0)
            {
                text += $" targets=[{string.Join(",", evt.Targets)}]";
            }

            if (evt.DurationMs is > 0)
            {
                text += $" duration={evt.DurationMs:F2}ms";
            }

            Console.WriteLine($"- {text}");
        }

        Console.WriteLine("\nSummary:");
        foreach (var item in result.Summary)
        {
            Console.WriteLine($"- {item.Key}: {item.Value}");
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Aevatar demo CLI");
        Console.WriteLine("Commands:");
        Console.WriteLine("  list");
        Console.WriteLine("  run <scenario> [--json <path>] [--web <path>]");
        Console.WriteLine("  web <jsonPath> [--out <htmlPath>]");
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project demos/Aevatar.Demos.Cli -- list");
        Console.WriteLine("  dotnet run --project demos/Aevatar.Demos.Cli -- run hierarchy --web artifacts/demo/hierarchy.html");
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

internal sealed record DemoTopologyEdge(string Parent, string Child);

internal sealed record DemoTopology(List<DemoTopologyEdge> Edges);

internal sealed record DemoEventLog(
    DateTimeOffset Timestamp,
    string Stage,
    string Message,
    string? AgentId,
    string? EventType,
    string? Direction,
    List<string> Targets,
    double? DurationMs,
    Dictionary<string, string> Data);

internal sealed record DemoRunResult(
    string Scenario,
    string Description,
    DemoTopology Topology,
    List<DemoEventLog> Events,
    Dictionary<string, string> Summary);

internal sealed class DemoTraceStore
{
    private readonly object _lock = new();
    private readonly List<DemoEventLog> _events = [];
    private readonly Dictionary<string, string> _summary = [];

    public void Add(
        string stage,
        string message,
        string? agentId = null,
        string? eventType = null,
        string? direction = null,
        IEnumerable<string>? targets = null,
        double? durationMs = null,
        Dictionary<string, string>? data = null)
    {
        lock (_lock)
        {
            _events.Add(new DemoEventLog(
                DateTimeOffset.UtcNow,
                stage,
                message,
                agentId,
                eventType,
                direction,
                targets?.ToList() ?? [],
                durationMs,
                data ?? []));
        }
    }

    public void SetSummary(string key, object value)
    {
        lock (_lock)
        {
            _summary[key] = value.ToString() ?? string.Empty;
        }
    }

    public List<DemoEventLog> SnapshotEvents()
    {
        lock (_lock)
        {
            return _events.OrderBy(x => x.Timestamp).ToList();
        }
    }

    public Dictionary<string, string> SnapshotSummary()
    {
        lock (_lock)
        {
            return new Dictionary<string, string>(_summary);
        }
    }
}

internal sealed class DemoHook(DemoTraceStore traceStore) : IGAgentExecutionHook
{
    public string Name => "demo_hook";
    public int Priority => 0;

    public Task OnEventHandlerStartAsync(GAgentExecutionHookContext ctx, CancellationToken ct)
    {
        traceStore.Add("hook:start", $"handler={ctx.HandlerName}", ctx.AgentId, ctx.EventType);
        return Task.CompletedTask;
    }

    public Task OnEventHandlerEndAsync(GAgentExecutionHookContext ctx, CancellationToken ct)
    {
        traceStore.Add(
            "hook:end",
            $"handler={ctx.HandlerName}",
            ctx.AgentId,
            ctx.EventType,
            durationMs: ctx.Duration?.TotalMilliseconds);
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(GAgentExecutionHookContext ctx, Exception ex, CancellationToken ct)
    {
        traceStore.Add("hook:error", $"{ctx.HandlerName}:{ex.Message}", ctx.AgentId, ctx.EventType);
        return Task.CompletedTask;
    }
}

internal sealed class TracingPublisher(
    string actorId,
    IEventPublisher inner,
    DemoTraceStore traceStore,
    Func<EventDirection, Task<IReadOnlyList<string>>> resolveTargets) : IEventPublisher
{
    public async Task PublishAsync<TEvent>(TEvent evt, EventDirection direction, CancellationToken ct) where TEvent : IMessage
    {
        var targets = await resolveTargets(direction);
        traceStore.Add(
            "publish",
            $"{evt.Descriptor.Name} published",
            actorId,
            evt.Descriptor.Name,
            direction.ToString(),
            targets);
        await inner.PublishAsync(evt, direction, ct);
    }

    public Task SendToAsync<TEvent>(string targetActorId, TEvent evt, CancellationToken ct) where TEvent : IMessage
    {
        traceStore.Add(
            "send",
            $"{evt.Descriptor.Name} sent",
            actorId,
            evt.Descriptor.Name,
            "Direct",
            [targetActorId]);
        return inner.SendToAsync(targetActorId, evt, ct);
    }
}

internal static class DemoScenarioRunner
{
    public static readonly IReadOnlyList<string> ScenarioNames =
    [
        "hierarchy",
        "fanout",
        "pipeline",
        "hooks",
        "lifecycle",
    ];

    public static async Task<DemoRunResult> RunAsync(string scenario)
    {
        var traceStore = new DemoTraceStore();
        DemoAgentBase.TraceStore = traceStore;

        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddSingleton(traceStore);
        services.AddSingleton<IGAgentExecutionHook, DemoHook>();
        services.AddTransient<DemoCollectorAgent>();
        services.AddTransient<DemoTransformerAgent>();
        services.AddTransient<DemoFaultyAgent>();
        services.AddTransient<DemoCounterAgent>();
        var sp = services.BuildServiceProvider();
        var runtime = (LocalActorRuntime)sp.GetRequiredService<IActorRuntime>();

        try
        {
            return scenario switch
            {
                "hierarchy" => await RunHierarchyAsync(runtime, traceStore),
                "fanout" => await RunFanoutAsync(runtime, traceStore),
                "pipeline" => await RunPipelineAsync(runtime, traceStore),
                "hooks" => await RunHooksAsync(runtime, traceStore),
                "lifecycle" => await RunLifecycleAsync(runtime, traceStore),
                _ => throw new InvalidOperationException($"Unknown scenario: {scenario}"),
            };
        }
        finally
        {
            var all = await runtime.GetAllAsync();
            foreach (var actor in all)
            {
                await runtime.DestroyAsync(actor.Id);
            }

            sp.Dispose();
        }
    }

    private static async Task<DemoRunResult> RunHierarchyAsync(LocalActorRuntime runtime, DemoTraceStore traceStore)
    {
        var parent = await runtime.CreateAsync<DemoTransformerAgent>("parent");
        var child = await runtime.CreateAsync<DemoCollectorAgent>("child");
        await runtime.LinkAsync(parent.Id, child.Id);
        await AttachTracingPublisherAsync(runtime, parent, traceStore);
        await AttachTracingPublisherAsync(runtime, child, traceStore);

        await ((GAgentBase)parent.Agent).EventPublisher.PublishAsync(new PingEvent { Message = "hello-child" }, EventDirection.Down);
        await WaitForAsync(() => ((DemoCollectorAgent)child.Agent).Received.Count > 0);

        traceStore.SetSummary("childReceived", string.Join(",", ((DemoCollectorAgent)child.Agent).Received));
        return await BuildResultAsync(runtime, "hierarchy", "Parent publishes Down event to child.", traceStore);
    }

    private static async Task<DemoRunResult> RunFanoutAsync(LocalActorRuntime runtime, DemoTraceStore traceStore)
    {
        var coord = await runtime.CreateAsync<DemoTransformerAgent>("coord");
        var w1 = await runtime.CreateAsync<DemoCollectorAgent>("w1");
        var w2 = await runtime.CreateAsync<DemoCollectorAgent>("w2");
        var w3 = await runtime.CreateAsync<DemoCollectorAgent>("w3");

        await runtime.LinkAsync(coord.Id, w1.Id);
        await runtime.LinkAsync(coord.Id, w2.Id);
        await runtime.LinkAsync(coord.Id, w3.Id);

        await AttachTracingPublisherAsync(runtime, coord, traceStore);
        await AttachTracingPublisherAsync(runtime, w1, traceStore);
        await AttachTracingPublisherAsync(runtime, w2, traceStore);
        await AttachTracingPublisherAsync(runtime, w3, traceStore);

        await ((GAgentBase)coord.Agent).EventPublisher.PublishAsync(new PingEvent { Message = "task" }, EventDirection.Down);
        await WaitForAsync(() =>
            ((DemoCollectorAgent)w1.Agent).Received.Count > 0 &&
            ((DemoCollectorAgent)w2.Agent).Received.Count > 0 &&
            ((DemoCollectorAgent)w3.Agent).Received.Count > 0);

        traceStore.SetSummary("w1", string.Join(",", ((DemoCollectorAgent)w1.Agent).Received));
        traceStore.SetSummary("w2", string.Join(",", ((DemoCollectorAgent)w2.Agent).Received));
        traceStore.SetSummary("w3", string.Join(",", ((DemoCollectorAgent)w3.Agent).Received));
        return await BuildResultAsync(runtime, "fanout", "Coordinator broadcasts task to three workers.", traceStore);
    }

    private static async Task<DemoRunResult> RunPipelineAsync(LocalActorRuntime runtime, DemoTraceStore traceStore)
    {
        var transformer = await runtime.CreateAsync<DemoTransformerAgent>("transformer");
        var collector = await runtime.CreateAsync<DemoCollectorAgent>("sink");
        await runtime.LinkAsync(transformer.Id, collector.Id);
        await AttachTracingPublisherAsync(runtime, transformer, traceStore);
        await AttachTracingPublisherAsync(runtime, collector, traceStore);

        var moduleA = new ReplyModule("module_a", "A");
        var moduleB = new ReplyModule("module_b", "B");
        var agent = (DemoTransformerAgent)transformer.Agent;
        agent.SetModules([moduleA]);
        await ((GAgentBase)transformer.Agent).EventPublisher.PublishAsync(new PingEvent { Message = "pipeline" }, EventDirection.Self);
        await WaitForAsync(() => ((DemoCollectorAgent)collector.Agent).Received.Count > 0);

        agent.SetModules([moduleB]);
        await ((GAgentBase)transformer.Agent).EventPublisher.PublishAsync(new PingEvent { Message = "pipeline" }, EventDirection.Self);
        await WaitForAsync(() => ((DemoCollectorAgent)collector.Agent).Received.Count > 1);

        traceStore.SetSummary("pipelineReplies", string.Join(",", ((DemoCollectorAgent)collector.Agent).Received));
        return await BuildResultAsync(runtime, "pipeline", "Swap module to change pipeline behavior.", traceStore);
    }

    private static async Task<DemoRunResult> RunHooksAsync(LocalActorRuntime runtime, DemoTraceStore traceStore)
    {
        var faulty = await runtime.CreateAsync<DemoFaultyAgent>("faulty");
        await AttachTracingPublisherAsync(runtime, faulty, traceStore);

        await faulty.HandleEventAsync(CreateEnvelope(new PingEvent { Message = "boom" }));
        await Task.Delay(80);
        traceStore.SetSummary("expected", "hook:start + hook:error + hook:end");
        return await BuildResultAsync(runtime, "hooks", "Hook lifecycle around failing handler.", traceStore);
    }

    private static async Task<DemoRunResult> RunLifecycleAsync(LocalActorRuntime runtime, DemoTraceStore traceStore)
    {
        var actor = await runtime.CreateAsync<DemoCounterAgent>("stateful");
        await AttachTracingPublisherAsync(runtime, actor, traceStore);

        await actor.HandleEventAsync(CreateEnvelope(new IncrementEvent { Amount = 7 }));
        await WaitForAsync(() => ((DemoCounterAgent)actor.Agent).State.Count == 7);

        await runtime.DestroyAsync(actor.Id);
        var restored = await runtime.CreateAsync<DemoCounterAgent>("stateful");
        var state = ((DemoCounterAgent)restored.Agent).State.Count;
        traceStore.SetSummary("restoredCount", state);
        return await BuildResultAsync(runtime, "lifecycle", "Deactivate/save and reactivate/load state.", traceStore);
    }

    private static async Task AttachTracingPublisherAsync(LocalActorRuntime runtime, IActor actor, DemoTraceStore traceStore)
    {
        if (actor.Agent is not GAgentBase gab) return;
        var inner = gab.EventPublisher;
        var wrapped = new TracingPublisher(
            actor.Id,
            inner,
            traceStore,
            async direction =>
            {
                var a = await runtime.GetAsync(actor.Id);
                if (a == null) return Array.Empty<string>();
                var children = await a.GetChildrenIdsAsync();
                var parent = await a.GetParentIdAsync();
                return direction switch
                {
                    EventDirection.Self => [actor.Id],
                    EventDirection.Down => children.ToList(),
                    EventDirection.Up => parent != null ? [parent] : [],
                    EventDirection.Both => children.Concat(parent != null ? [parent] : []).ToList(),
                    _ => [],
                };
            });
        gab.EventPublisher = wrapped;
    }

    private static async Task<DemoRunResult> BuildResultAsync(
        LocalActorRuntime runtime,
        string scenario,
        string description,
        DemoTraceStore traceStore)
    {
        var topology = await BuildTopologyAsync(runtime);
        return new DemoRunResult(
            scenario,
            description,
            topology,
            traceStore.SnapshotEvents(),
            traceStore.SnapshotSummary());
    }

    private static async Task<DemoTopology> BuildTopologyAsync(IActorRuntime runtime)
    {
        var edges = new List<DemoTopologyEdge>();
        var all = await runtime.GetAllAsync();
        foreach (var actor in all)
        {
            var parent = await actor.GetParentIdAsync();
            if (!string.IsNullOrWhiteSpace(parent))
            {
                edges.Add(new DemoTopologyEdge(parent, actor.Id));
            }
        }

        return new DemoTopology(edges);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        if (!condition()) throw new TimeoutException("Condition not met.");
    }

    private static EventEnvelope CreateEnvelope<T>(T evt, string publisherId = "demo-user")
        where T : IMessage =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = publisherId,
            Direction = EventDirection.Down,
        };
}

internal static class DemoAgentBase
{
    public static DemoTraceStore TraceStore { get; set; } = new();

    public static void RecordState(string agentId, string field, string before, string after)
    {
        TraceStore.Add(
            "state",
            $"{field}:{before}->{after}",
            agentId,
            data: new Dictionary<string, string>
            {
                ["field"] = field,
                ["before"] = before,
                ["after"] = after,
            });
    }
}

internal sealed class ReplyModule(string name, string reply) : IEventModule
{
    public string Name { get; } = name;
    public int Priority => 0;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(PingEvent.Descriptor) == true;

    public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        DemoAgentBase.TraceStore.Add("module", $"module={Name}");
        return ctx.PublishAsync(new PongEvent { Reply = reply }, EventDirection.Down, ct);
    }
}

internal static class DemoHtmlReportWriter
{
    public static string Write(DemoRunResult result)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions.Default).Replace("</script>", "<\\/script>");
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html><head><meta charset=\"utf-8\"/>");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>");
        sb.AppendLine("<title>Aevatar Demo Report</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:ui-sans-serif,system-ui;margin:24px;background:#0f172a;color:#e2e8f0}");
        sb.AppendLine("h1,h2{margin:0 0 12px} .card{background:#111827;border:1px solid #334155;border-radius:8px;padding:12px;margin:12px 0}");
        sb.AppendLine("table{width:100%;border-collapse:collapse} th,td{border-bottom:1px solid #334155;padding:6px 8px;text-align:left;font-size:13px}");
        sb.AppendLine(".muted{color:#94a3b8} code{color:#93c5fd}");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine("<h1>Aevatar Demo Report</h1>");
        sb.AppendLine("<div id=\"root\"></div>");
        sb.AppendLine($"<script>const data={json};</script>");
        sb.AppendLine("<script>");
        sb.AppendLine("const root=document.getElementById('root');");
        sb.AppendLine("const topologyRows=(data.topology.edges||[]).map(e=>`<tr><td>${e.parent}</td><td>${e.child}</td></tr>`).join('');");
        sb.AppendLine("const eventRows=(data.events||[]).map(e=>`<tr><td>${new Date(e.timestamp).toLocaleTimeString()}</td><td>${e.stage}</td><td>${e.message}</td><td>${e.agentId||''}</td><td>${e.eventType||''}</td><td>${(e.targets||[]).join(',')}</td><td>${e.durationMs?e.durationMs.toFixed(2):''}</td></tr>`).join('');");
        sb.AppendLine("const summaryRows=Object.entries(data.summary||{}).map(([k,v])=>`<tr><td>${k}</td><td>${v}</td></tr>`).join('');");
        sb.AppendLine("root.innerHTML=`");
        sb.AppendLine("<div class='card'><h2>Scenario</h2><div><code>${data.scenario}</code> <span class='muted'>${data.description}</span></div></div>");
        sb.AppendLine("<div class='card'><h2>Topology</h2><table><thead><tr><th>Parent</th><th>Child</th></tr></thead><tbody>${topologyRows || \"<tr><td colspan='2'>(none)</td></tr>\"}</tbody></table></div>");
        sb.AppendLine("<div class='card'><h2>Timeline</h2><table><thead><tr><th>Time</th><th>Stage</th><th>Message</th><th>Agent</th><th>EventType</th><th>Targets</th><th>DurationMs</th></tr></thead><tbody>${eventRows}</tbody></table></div>");
        sb.AppendLine("<div class='card'><h2>Summary</h2><table><thead><tr><th>Key</th><th>Value</th></tr></thead><tbody>${summaryRows}</tbody></table></div>`;");
        sb.AppendLine("</script></body></html>");
        return sb.ToString();
    }
}
