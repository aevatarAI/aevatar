using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.Agents;
using Aevatar.AI.LLMProviders.MEAI;
using Aevatar.Bootstrap;
using Aevatar.Configuration;
using Aevatar.Demos.Workflow.Web;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ChatMessage = Aevatar.AI.Abstractions.LLMProviders.ChatMessage;

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
builder.Services.AddSingleton<IWorkflowModulePack, DemoWorkflowModulePack>();
builder.Services.Replace(ServiceDescriptor.Singleton<IEventModuleFactory, DemoWorkflowModuleFactory>());
builder.Services.AddSingleton<IRoleAgentTypeResolver, RoleGAgentTypeResolver>();

var primaryYamlDir = ResolveYamlDir();
var turingYamlDir = ResolveTuringYamlDir();
var workflowSources = BuildWorkflowSources(primaryYamlDir, turingYamlDir);

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
Console.WriteLine($"║  YAML:   {primaryYamlDir,-48}║");
Console.WriteLine($"║  LLM:    {(llmAvailable ? $"{providerName}/{modelName}" : "not configured"),-48}║");
Console.WriteLine("║  Press Ctrl+C to stop                                  ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
Console.WriteLine();
if (!string.IsNullOrWhiteSpace(turingYamlDir))
    Console.WriteLine($"[Turing demos] {turingYamlDir}");
app.Lifetime.ApplicationStarted.Register(() => { if (!noBrowser) OpenBrowser(url); });

app.UseDefaultFiles();
app.UseStaticFiles();

var deterministicWorkflows = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "01_transform", "02_guard", "03_conditional", "04_switch",
    "05_assign", "06_retrieve_facts", "07_pipeline",
    "17_demo_template", "18_demo_csv_markdown", "19_demo_json_pick",
    "20_role_event_module_template", "21_role_event_module_csv_markdown", "22_role_event_module_json_pick",
    "23_role_event_module_multiplex_template", "24_role_event_module_multiplex_csv", "25_role_event_module_multiplex_json",
    "26_role_event_module_multi_role_chain",
    "27_role_event_module_extensions_template", "28_role_event_module_extensions_csv",
    "29_role_event_module_top_level_overrides_extensions",
    "30_role_event_module_extensions_multi_role_chain",
    "31_role_event_module_extensions_multiplex_json",
    "32_role_event_module_top_level_overrides_extensions_multiplex",
    "33_role_event_module_no_routes_template",
    "34_role_event_module_route_dsl_csv",
    "35_role_event_module_unknown_ignored_template",
    "36_mixed_step_json_pick_then_role_template",
    "37_mixed_step_csv_markdown_then_role_template",
    "38_mixed_step_template_then_role_csv_markdown",
    "39_human_input_basic_auto_resume",
    "40_human_approval_approved_auto_resume",
    "41_human_approval_rejected_fail_auto_resume",
    "42_human_approval_rejected_skip_auto_resume",
    "43_human_input_manual_triage",
    "44_wait_signal_manual_success",
    "45_wait_signal_timeout_failure",
    "46_human_approval_release_gate",
    "47_mixed_human_approval_wait_signal",
};
var turingWorkflows = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "counter-addition",
    "minsky-inc-dec-jz",
    "counter_addition",
    "minsky_inc_dec_jz",
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
    ["17_demo_template"] = "payment_api_timeout",
    ["18_demo_csv_markdown"] = "service,error_rate,latency_ms\ngateway,1.2,210\ncheckout,0.3,120",
    ["19_demo_json_pick"] = """{"incident":{"id":"INC-2026-001","owner":{"team":"sre","user":"alice"}},"severity":"high"}""",
    ["20_role_event_module_template"] = "checkout_db_latency_spike",
    ["21_role_event_module_csv_markdown"] = "service,error_rate,latency_ms\nauth,0.8,180\nbilling,1.4,260",
    ["22_role_event_module_json_pick"] = """{"incident":{"id":"INC-2026-007","owner":{"team":"platform","user":"bob"}},"severity":"critical"}""",
    ["23_role_event_module_multiplex_template"] = "order_service_high_latency",
    ["24_role_event_module_multiplex_csv"] = "service,error_rate,latency_ms\napi,1.1,240\nworker,0.5,170",
    ["25_role_event_module_multiplex_json"] = """{"incident":{"id":"INC-2026-011","owner":{"team":"infra","user":"charlie"}},"severity":"high"}""",
    ["26_role_event_module_multi_role_chain"] = "checkout_timeout_spike",
    ["27_role_event_module_extensions_template"] = "payments_retry_exhausted",
    ["28_role_event_module_extensions_csv"] = "service,error_rate,latency_ms\nsearch,0.6,150\nrecommendation,1.3,280",
    ["29_role_event_module_top_level_overrides_extensions"] = """{"incident":{"id":"INC-2026-023","owner":{"team":"runtime","user":"eve"}},"severity":"critical"}""",
    ["30_role_event_module_extensions_multi_role_chain"] = "payment_timeout_burst",
    ["31_role_event_module_extensions_multiplex_json"] = """{"incident":{"id":"INC-2026-033","owner":{"team":"gateway","user":"gina"}},"severity":"high"}""",
    ["32_role_event_module_top_level_overrides_extensions_multiplex"] = "service,error_rate,latency_ms\nedge,1.0,210\npayment,1.8,320",
    ["33_role_event_module_no_routes_template"] = "inventory_sync_lag",
    ["34_role_event_module_route_dsl_csv"] = "service,error_rate,latency_ms\ncatalog,0.4,140\ncheckout,1.6,300",
    ["35_role_event_module_unknown_ignored_template"] = "payments_duplicate_callback",
    ["36_mixed_step_json_pick_then_role_template"] = """{"incident":{"id":"INC-2026-041","owner":{"team":"data","user":"harry"}},"severity":"high"}""",
    ["37_mixed_step_csv_markdown_then_role_template"] = "service,error_rate,latency_ms\nsearch,0.7,160\nfeed,1.4,295",
    ["38_mixed_step_template_then_role_csv_markdown"] = "1.3",
    ["39_human_input_basic_auto_resume"] = "checkout request missing approver and rollback plan",
    ["40_human_approval_approved_auto_resume"] = "deploy release v1.2.3 to production",
    ["41_human_approval_rejected_fail_auto_resume"] = "delete production database",
    ["42_human_approval_rejected_skip_auto_resume"] = "restart read replica cluster",
    ["43_human_input_manual_triage"] = "api gateway latency spikes in us-east-1",
    ["44_wait_signal_manual_success"] = "release candidate v2.4.0 passed smoke checks",
    ["45_wait_signal_timeout_failure"] = "database migration waiting for DBA ack",
    ["46_human_approval_release_gate"] = "change request CR-2026-021",
    ["47_mixed_human_approval_wait_signal"] = "deploy cache cluster patch-7",
    ["counter-addition"] = "Run the closed-world two-counter addition demo.",
    ["minsky-inc-dec-jz"] = "Run the closed-world INC/DEC/JZ transfer demo.",
    ["counter_addition"] = "Run the closed-world two-counter addition demo.",
    ["minsky_inc_dec_jz"] = "Run the closed-world INC/DEC/JZ transfer demo.",
};

var parser = new WorkflowParser();
var runContexts = new ConcurrentDictionary<string, DemoRunContext>(StringComparer.Ordinal);

void TrackRunContext(
    string runId,
    string actorId,
    string channel,
    string workflowName,
    Action<DemoRunContext>? mutate = null)
{
    if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(actorId))
        return;

    var normalizedRunId = runId.Trim();
    var normalizedActorId = actorId.Trim();
    var now = DateTimeOffset.UtcNow;

    var context = runContexts.AddOrUpdate(
        normalizedRunId,
        _ => new DemoRunContext
        {
            RunId = normalizedRunId,
            ActorId = normalizedActorId,
            Channel = channel,
            WorkflowName = workflowName,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        },
        (_, existing) =>
        {
            existing.ActorId = normalizedActorId;
            existing.Channel = channel;
            existing.WorkflowName = workflowName;
            existing.UpdatedAtUtc = now;
            return existing;
        });

    mutate?.Invoke(context);
    context.UpdatedAtUtc = DateTimeOffset.UtcNow;
}

void CleanupRunContext(string runId)
{
    if (string.IsNullOrWhiteSpace(runId))
        return;

    runContexts.TryRemove(runId.Trim(), out _);
}

void CleanupRunContextsForActor(string actorId)
{
    if (string.IsNullOrWhiteSpace(actorId))
        return;

    var normalized = actorId.Trim();
    foreach (var (runId, context) in runContexts)
    {
        if (string.Equals(context.ActorId, normalized, StringComparison.Ordinal))
            runContexts.TryRemove(runId, out _);
    }
}

// GET /api/workflows — list all workflows
app.MapGet("/api/workflows", () =>
{
    var workflowFiles = DiscoverWorkflowFiles(workflowSources);
    var workflows = new List<object>();
    foreach (var workflowFile in workflowFiles.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
    {
        var name = workflowFile.Name;
        var listMeta = ClassifyWorkflowForList(name, workflowFile.SourceKind, deterministicWorkflows, turingWorkflows);
        try
        {
            var yaml = File.ReadAllText(workflowFile.FilePath);
            var def = parser.Parse(yaml);
            var primitives = def.Steps.Select(s => s.Type).Distinct().ToList();
            workflows.Add(new
            {
                name,
                description = def.Description,
                category = listMeta.Category,
                group = listMeta.Group,
                groupLabel = listMeta.GroupLabel,
                sortOrder = listMeta.SortOrder,
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
                category = listMeta.Category,
                group = listMeta.Group,
                groupLabel = listMeta.GroupLabel,
                sortOrder = listMeta.SortOrder,
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
    if (!TryResolveWorkflowFile(name, workflowSources, out var workflowFile))
        return Results.NotFound(new { error = $"Workflow '{name}' not found" });

    var yaml = File.ReadAllText(workflowFile.FilePath);
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
            configuration = new
            {
                closedWorldMode = def.Configuration.ClosedWorldMode,
            },
            roles = def.Roles.Select(BuildRoleDto),
            steps,
        },
        edges,
    });
});

// GET /api/workflows/{name}/run — SSE execution endpoint
app.MapGet("/api/workflows/{name}/run", async (string name, string? input, bool? autoResume, HttpContext ctx, CancellationToken ct) =>
{
    if (!TryResolveWorkflowFile(name, workflowSources, out var workflowFile))
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync($"Workflow '{name}' not found");
        return;
    }

    var workflowCategory = ClassifyWorkflowForList(name, workflowFile.SourceKind, deterministicWorkflows, turingWorkflows).Category;

    if (string.Equals(workflowCategory, "llm", StringComparison.OrdinalIgnoreCase) && !llmAvailable)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("LLM not configured. Set DEEPSEEK_API_KEY or OPENAI_API_KEY.");
        return;
    }

    ctx.Response.Headers["Content-Type"] = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["Connection"] = "keep-alive";

    var yaml = File.ReadAllText(workflowFile.FilePath);
    WorkflowDefinition parsedDefinition;
    try
    {
        parsedDefinition = parser.Parse(yaml);
    }
    catch (Exception ex)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync($"Invalid workflow YAML: {ex.Message}");
        return;
    }

    var validationErrors = ValidateWorkflowDefinitionForRuntime(parsedDefinition, ctx.RequestServices);
    if (validationErrors.Count > 0)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync($"Workflow validation failed: {string.Join("; ", validationErrors)}");
        return;
    }

    var actualInput = input ?? demoInputs.GetValueOrDefault(name, "Hello, world!");
    var shouldAutoResume = autoResume == true;

    var runtime = ctx.RequestServices.GetRequiredService<IActorRuntime>();
    var streams = ctx.RequestServices.GetRequiredService<IStreamProvider>();
    var actorId = $"web-{Guid.NewGuid():N}"[..32];

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
    string? activeRunId = null;

    async Task WriteSse(string eventType, object data)
    {
        var json = JsonSerializer.Serialize(data, jsonOpts);
        await ctx.Response.WriteAsync($"event: {eventType}\ndata: {json}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    void RememberRunId(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return;

        activeRunId = runId.Trim();
        TrackRunContext(activeRunId, actor.Id, "workflow", name);
    }

    void ScheduleAutoResume(WorkflowSuspendedEvent suspended)
    {
        var resumed = BuildAutoResumedEvent(suspended, actualInput);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50, CancellationToken.None);
                await actor.HandleEventAsync(new EventEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Payload = Any.Pack(resumed),
                    PublisherId = "web.demo.auto-human",
                    Direction = EventDirection.Self,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                });
            }
            catch (Exception ex)
            {
                try
                {
                    await WriteSse("workflow.error", new { error = $"Auto resume failed: {ex.Message}" });
                }
                catch
                {
                    // ignore write failures after client disconnect
                }
            }
        }, CancellationToken.None);
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
                RememberRunId(evt.RunId);
                if (!string.IsNullOrWhiteSpace(activeRunId))
                {
                    TrackRunContext(activeRunId, actor.Id, "workflow", name, tracked =>
                    {
                        tracked.LastStepId = evt.StepId;
                        tracked.LastEventType = "step.request";
                    });
                }

                await WriteSse("step.request", new
                {
                    runId = string.IsNullOrWhiteSpace(evt.RunId) ? activeRunId : evt.RunId,
                    stepId = evt.StepId,
                    stepType = evt.StepType,
                    input = Truncate(evt.Input, 500),
                });
            }

            if (payload.Is(StepCompletedEvent.Descriptor))
            {
                var evt = payload.Unpack<StepCompletedEvent>();
                RememberRunId(evt.RunId);
                if (!string.IsNullOrWhiteSpace(activeRunId))
                {
                    TrackRunContext(activeRunId, actor.Id, "workflow", name, tracked =>
                    {
                        tracked.LastStepId = evt.StepId;
                        tracked.LastEventType = "step.completed";
                    });
                }

                var meta = new Dictionary<string, string>();
                foreach (var kv in evt.Metadata)
                    meta[kv.Key] = kv.Value;
                await WriteSse("step.completed", new
                {
                    runId = string.IsNullOrWhiteSpace(evt.RunId) ? activeRunId : evt.RunId,
                    stepId = evt.StepId,
                    success = evt.Success,
                    output = Truncate(evt.Output, 1000),
                    error = string.IsNullOrEmpty(evt.Error) ? null : evt.Error,
                    metadata = meta.Count > 0 ? meta : null,
                });
            }

            if (payload.Is(WorkflowSuspendedEvent.Descriptor))
            {
                var evt = payload.Unpack<WorkflowSuspendedEvent>();
                RememberRunId(evt.RunId);
                if (!string.IsNullOrWhiteSpace(activeRunId))
                {
                    TrackRunContext(activeRunId, actor.Id, "workflow", name, tracked =>
                    {
                        tracked.LastStepId = evt.StepId;
                        tracked.LastSuspensionType = evt.SuspensionType;
                        tracked.LastEventType = "workflow.suspended";
                    });
                }

                var meta = new Dictionary<string, string>();
                foreach (var kv in evt.Metadata)
                    meta[kv.Key] = kv.Value;

                await WriteSse("workflow.suspended", new
                {
                    runId = evt.RunId,
                    stepId = evt.StepId,
                    suspensionType = evt.SuspensionType,
                    prompt = evt.Prompt,
                    timeoutSeconds = evt.TimeoutSeconds,
                    metadata = meta.Count > 0 ? meta : null,
                });

                if (shouldAutoResume)
                    ScheduleAutoResume(evt);
            }

            if (payload.Is(WaitingForSignalEvent.Descriptor))
            {
                var evt = payload.Unpack<WaitingForSignalEvent>();
                if (!string.IsNullOrWhiteSpace(activeRunId))
                {
                    TrackRunContext(activeRunId, actor.Id, "workflow", name, tracked =>
                    {
                        tracked.LastStepId = evt.StepId;
                        tracked.LastSignalName = evt.SignalName;
                        tracked.LastEventType = "workflow.waiting_signal";
                    });
                }

                await WriteSse("workflow.waiting_signal", new
                {
                    runId = activeRunId,
                    stepId = evt.StepId,
                    signalName = evt.SignalName,
                    prompt = evt.Prompt,
                    timeoutMs = evt.TimeoutMs,
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

            if (payload.Is(ChatResponseEvent.Descriptor))
            {
                var evt = payload.Unpack<ChatResponseEvent>();
                if (string.Equals(envelope.PublisherId, actor.Id, StringComparison.Ordinal))
                {
                    var error = string.IsNullOrWhiteSpace(evt.Content) ? "Workflow run failed." : evt.Content;
                    await WriteSse("workflow.error", new { error });
                    if (!string.IsNullOrWhiteSpace(activeRunId))
                        CleanupRunContext(activeRunId);
                    tcs.TrySetResult(false);
                }
            }

            if (payload.Is(WorkflowCompletedEvent.Descriptor))
            {
                var evt = payload.Unpack<WorkflowCompletedEvent>();
                RememberRunId(evt.RunId);
                await WriteSse("workflow.completed", new
                {
                    runId = string.IsNullOrWhiteSpace(evt.RunId) ? activeRunId : evt.RunId,
                    success = evt.Success,
                    output = Truncate(evt.Output, 2000),
                    error = string.IsNullOrEmpty(evt.Error) ? null : evt.Error,
                });
                if (!string.IsNullOrWhiteSpace(evt.RunId))
                    CleanupRunContext(evt.RunId);
                else if (!string.IsNullOrWhiteSpace(activeRunId))
                    CleanupRunContext(activeRunId);
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
    CleanupRunContextsForActor(actor.Id);
});

// POST /api/workflows/resume — resume a suspended human_input/human_approval step
app.MapPost("/api/workflows/resume", async (WorkflowResumeRequest? body, HttpContext ctx) =>
{
    if (body == null)
        return Results.BadRequest(new { error = "Request body is required." });

    var runId = (body.RunId ?? string.Empty).Trim();
    var stepId = (body.StepId ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(stepId))
        return Results.BadRequest(new { error = "runId and stepId are required." });

    if (!runContexts.TryGetValue(runId, out var runContext))
        return Results.NotFound(new { error = $"Run context '{runId}' not found or already finished." });

    var runtime = ctx.RequestServices.GetRequiredService<IActorRuntime>();
    var actor = await runtime.GetAsync(runContext.ActorId);
    if (actor == null)
    {
        CleanupRunContext(runId);
        return Results.NotFound(new { error = $"Actor '{runContext.ActorId}' is no longer active for run '{runId}'." });
    }

    var resumed = new WorkflowResumedEvent
    {
        RunId = runId,
        StepId = stepId,
        Approved = body.Approved,
        UserInput = body.UserInput ?? string.Empty,
    };
    if (body.Metadata is { Count: > 0 })
    {
        foreach (var (key, value) in body.Metadata)
            resumed.Metadata[key] = value;
    }

    await actor.HandleEventAsync(new EventEnvelope
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(resumed),
        PublisherId = "web.demo.resume",
        Direction = EventDirection.Self,
        CorrelationId = Guid.NewGuid().ToString("N"),
    });

    TrackRunContext(runId, runContext.ActorId, runContext.Channel, runContext.WorkflowName, tracked =>
    {
        tracked.LastStepId = stepId;
        tracked.LastEventType = "api.resume";
    });

    return Results.Json(new
    {
        accepted = true,
        runId,
        stepId,
        actorId = runContext.ActorId,
    });
});

// POST /api/workflows/signal — deliver signal to wait_signal step
app.MapPost("/api/workflows/signal", async (WorkflowSignalRequest? body, HttpContext ctx) =>
{
    if (body == null)
        return Results.BadRequest(new { error = "Request body is required." });

    var runId = (body.RunId ?? string.Empty).Trim();
    var signalName = (body.SignalName ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(signalName))
        return Results.BadRequest(new { error = "runId and signalName are required." });

    if (!runContexts.TryGetValue(runId, out var runContext))
        return Results.NotFound(new { error = $"Run context '{runId}' not found or already finished." });

    var runtime = ctx.RequestServices.GetRequiredService<IActorRuntime>();
    var actor = await runtime.GetAsync(runContext.ActorId);
    if (actor == null)
    {
        CleanupRunContext(runId);
        return Results.NotFound(new { error = $"Actor '{runContext.ActorId}' is no longer active for run '{runId}'." });
    }

    await actor.HandleEventAsync(new EventEnvelope
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(new SignalReceivedEvent
        {
            RunId = runId,
            SignalName = signalName,
            Payload = body.Payload ?? string.Empty,
        }),
        PublisherId = "web.demo.signal",
        Direction = EventDirection.Self,
        CorrelationId = Guid.NewGuid().ToString("N"),
    });

    TrackRunContext(runId, runContext.ActorId, runContext.Channel, runContext.WorkflowName, tracked =>
    {
        tracked.LastSignalName = signalName;
        tracked.LastEventType = "api.signal";
    });

    return Results.Json(new
    {
        accepted = true,
        runId,
        signalName,
        actorId = runContext.ActorId,
    });
});

// GET /api/llm/status — LLM availability
app.MapGet("/api/llm/status", () => Results.Json(new
{
    available = llmAvailable,
    provider = llmAvailable ? providerName : null,
    model = llmAvailable ? modelName : null,
}));

// GET /api/primitives — module catalog with parameter docs
app.MapGet("/api/primitives", () => Results.Json(BuildPrimitivesCatalog()));

static object[] BuildPrimitivesCatalog()
{
    object P(string name, string desc, string def = "", string? values = null) =>
        new { name, description = desc, @default = def, values };

    return
    [
        new {
            name = "transform", aliases = new[] { "transform" }, category = "data",
            description = "Pure text transformation. Applies an operation to the input and returns the result.",
            parameters = new object[] {
                P("op", "Operation to apply", "identity",
                    "identity, uppercase, lowercase, trim, count, count_words, take, take_last, join, split, distinct, reverse_lines"),
                P("n", "Number of lines for take/take_last", "5"),
                P("separator", "Delimiter for join/split", "\\n"),
            },
        },
        new {
            name = "demo_template", aliases = new[] { "demo_template", "demo_format" }, category = "data",
            description = "Demo custom module. Renders a text template for StepRequest, and can short-circuit role ChatRequest when attached via roles.event_modules.",
            parameters = new object[] {
                P("template", "Template text. Supports {{input}} and {{param.<key>}} placeholders", "Incident {{input}} owned by {{param.owner}}"),
                P("prefix", "Optional prefix to prepend to rendered output"),
                P("suffix", "Optional suffix to append to rendered output"),
                P("uppercase", "Uppercase final output", "false", "true, false"),
            },
        },
        new {
            name = "demo_csv_markdown", aliases = new[] { "demo_csv_markdown", "demo_table" }, category = "data",
            description = "Demo custom module. Converts CSV to markdown table for StepRequest, and can respond to role ChatRequest with deterministic table output.",
            parameters = new object[] {
                P("delimiter", "CSV delimiter", ","),
                P("has_header", "Treat first line as header", "true", "true, false"),
            },
        },
        new {
            name = "demo_json_pick", aliases = new[] { "demo_json_pick", "demo_json_path" }, category = "data",
            description = "Demo custom module. Extracts a nested JSON field for StepRequest, and can process role ChatRequest JSON payload deterministically.",
            parameters = new object[] {
                P("path", "Dot path to extract", "incident.owner.team"),
            },
        },
        new {
            name = "guard", aliases = new[] { "guard", "assert" }, category = "control",
            description = "Input validation gate. Runs a check on the input; fails or branches if the check is not met.",
            parameters = new object[] {
                P("check", "Validation check to perform", "not_empty",
                    "not_empty, json_valid, regex, max_length, contains"),
                P("on_fail", "Action when check fails", "fail", "fail, skip, branch"),
                P("pattern", "Regex pattern (required when check=regex)"),
                P("max", "Maximum length (required when check=max_length)"),
                P("keyword", "Substring to find (required when check=contains)"),
                P("branch_target", "Step ID to jump to (required when on_fail=branch)"),
            },
        },
        new {
            name = "conditional", aliases = new[] { "conditional" }, category = "control",
            description = "Binary branching. Checks if input contains a keyword, sets metadata[\"branch\"] to \"true\" or \"false\".",
            parameters = new object[] {
                P("condition", "Keyword to search for in input (case-insensitive contains)", "default"),
            },
        },
        new {
            name = "switch", aliases = new[] { "switch" }, category = "control",
            description = "Multi-way branching. Matches input against branch keys using case-insensitive contains, routes to the matched branch target.",
            parameters = new object[] {
                P("on", "Value to match against (defaults to step input)"),
                P("branch.{key}", "Maps a match key to a target step ID. E.g. branch.bug: handle_bug"),
            },
        },
        new {
            name = "while", aliases = new[] { "while", "loop" }, category = "control",
            description = "Loop that repeats a sub-step until max iterations. Each iteration passes previous output as input.",
            parameters = new object[] {
                P("max_iterations", "Maximum number of loop iterations", "10"),
                P("step", "Sub-step type to execute each iteration", "llm_call"),
            },
        },
        new {
            name = "foreach", aliases = new[] { "foreach", "for_each" }, category = "composition",
            description = "Splits input by delimiter and executes a sub-step for each chunk in parallel. Merges all results.",
            parameters = new object[] {
                P("delimiter", "Separator to split input into items", "\\n---\\n"),
                P("sub_step_type", "Step type to run for each item", "parallel"),
                P("sub_target_role", "Target role for sub-steps (defaults to step's target_role)"),
                P("sub_param_{key}", "Additional parameters forwarded to each sub-step"),
            },
        },
        new {
            name = "parallel", aliases = new[] { "parallel_fanout", "parallel", "fan_out" }, category = "composition",
            description = "Fan-out: sends the same input to multiple worker roles in parallel, then merges all responses.",
            parameters = new object[] {
                P("workers", "Comma-separated list of role IDs to fan out to"),
                P("parallel_count", "Number of workers if workers not specified", "3"),
                P("vote_step_type", "Optional follow-up vote/consensus step type"),
                P("vote_param_{key}", "Parameters forwarded to the vote step"),
            },
        },
        new {
            name = "race", aliases = new[] { "race", "select" }, category = "composition",
            description = "Sends input to multiple workers; returns the first response received, discarding the rest.",
            parameters = new object[] {
                P("workers", "Comma-separated list of role IDs"),
                P("count", "Number of workers if workers not specified", "2"),
            },
        },
        new {
            name = "map_reduce", aliases = new[] { "map_reduce", "mapreduce" }, category = "composition",
            description = "Splits input into chunks (map phase), processes each in parallel, then reduces all results into one.",
            parameters = new object[] {
                P("delimiter", "Separator to split input", "\\n---\\n"),
                P("map_step_type", "Step type for the map phase", "llm_call"),
                P("map_target_role", "Target role for map workers"),
                P("reduce_step_type", "Step type for the reduce phase", "llm_call"),
                P("reduce_target_role", "Target role for the reducer"),
                P("reduce_prompt_prefix", "Text prepended to the reduce prompt"),
            },
        },
        new {
            name = "llm_call", aliases = new[] { "llm_call" }, category = "ai",
            description = "Sends a prompt to the target role's LLM and returns the response. The role's system_prompt provides persona.",
            parameters = new object[] {
                P("prompt_prefix", "Text prepended to the input before sending to LLM"),
            },
        },
        new {
            name = "tool_call", aliases = new[] { "tool_call" }, category = "ai",
            description = "Invokes a registered tool/function by name, passing the step input as arguments.",
            parameters = new object[] {
                P("tool", "Name of the tool to invoke (required)"),
            },
        },
        new {
            name = "connector_call", aliases = new[] { "connector_call", "bridge_call" }, category = "integration",
            description = "Calls an external connector/bridge service. Supports retry, timeout, and graceful degradation.",
            parameters = new object[] {
                P("connector", "Connector name to invoke (required)"),
                P("operation", "Operation/method on the connector"),
                P("retry", "Number of retry attempts on failure", "0"),
                P("timeout_ms", "Timeout in milliseconds", "30000"),
                P("optional", "If true, missing connector is non-fatal", "false", "true, false"),
                P("on_missing", "Action when connector not found", "fail", "fail, skip"),
                P("on_error", "Action on connector error", "fail", "fail, continue"),
            },
        },
        new {
            name = "evaluate", aliases = new[] { "evaluate", "judge" }, category = "ai",
            description = "LLM-as-judge: sends content to a judge role for scoring on a numeric scale. Supports threshold-based branching.",
            parameters = new object[] {
                P("criteria", "Evaluation criteria description", "quality"),
                P("scale", "Numeric scale for scoring", "1-5"),
                P("threshold", "Minimum passing score", "3"),
                P("on_below", "Branch key when score is below threshold"),
            },
        },
        new {
            name = "reflect", aliases = new[] { "reflect" }, category = "ai",
            description = "Self-reflection loop: sends content for critique, improves based on feedback, repeats until \"PASS\" or max rounds.",
            parameters = new object[] {
                P("max_rounds", "Maximum critique-improve cycles (1\u201310)", "3"),
                P("criteria", "Criteria for the critique evaluation", "quality and correctness"),
            },
        },
        new {
            name = "assign", aliases = new[] { "assign" }, category = "data",
            description = "Assigns a value to a named variable in the workflow context. Use \"$input\" as value to capture the current input.",
            parameters = new object[] {
                P("target", "Variable name to assign to"),
                P("value", "Value to assign (\"$input\" = current step input)"),
            },
        },
        new {
            name = "retrieve_facts", aliases = new[] { "retrieve_facts" }, category = "data",
            description = "Searches the input text for lines matching a query using keyword overlap scoring. Returns the top-k most relevant lines.",
            parameters = new object[] {
                P("query", "Keywords to search for in the input"),
                P("top_k", "Number of top results to return", "5"),
            },
        },
        new {
            name = "cache", aliases = new[] { "cache" }, category = "data",
            description = "Caches LLM responses by key. On cache hit, returns cached result; on miss, delegates to a child step and caches the output.",
            parameters = new object[] {
                P("cache_key", "Cache key (defaults to step input)"),
                P("ttl_seconds", "Cache time-to-live in seconds (1\u201386400)", "3600"),
                P("child_step_type", "Step type to execute on cache miss", "llm_call"),
                P("child_target_role", "Target role for the child step"),
            },
        },
        new {
            name = "emit", aliases = new[] { "emit", "publish" }, category = "integration",
            description = "Publishes an event to external listeners (Up + Down direction). The event carries a custom type and payload.",
            parameters = new object[] {
                P("event_type", "Custom event type identifier", "custom"),
                P("payload", "Event payload (defaults to step input)"),
            },
        },
        new {
            name = "delay", aliases = new[] { "delay", "sleep" }, category = "control",
            description = "Pauses workflow execution for a specified duration before proceeding to the next step.",
            parameters = new object[] {
                P("duration_ms", "Pause duration in milliseconds (0\u2013300000)", "1000"),
            },
        },
        new {
            name = "wait_signal", aliases = new[] { "wait_signal", "wait" }, category = "control",
            description = "Blocks execution until an external signal with the matching name is received, or times out.",
            parameters = new object[] {
                P("signal_name", "Name of the signal to wait for", "default"),
                P("prompt", "Message to display while waiting"),
                P("timeout_ms", "Timeout in milliseconds (0 = no timeout, max 3600000)", "0"),
            },
        },
        new {
            name = "checkpoint", aliases = new[] { "checkpoint" }, category = "control",
            description = "Saves workflow state at this point. Can be used for recovery or auditing.",
            parameters = new object[] {
                P("name", "Checkpoint label (defaults to step ID)"),
            },
        },
        new {
            name = "human_approval", aliases = new[] { "human_approval" }, category = "human",
            description = "Pauses the workflow and waits for a human to approve or reject before continuing.",
            parameters = new object[] {
                P("prompt", "Message shown to the human reviewer", "Approve this step?"),
                P("timeout", "Timeout in seconds", "3600"),
                P("on_reject", "Action if rejected", "fail", "fail, skip, branch"),
            },
        },
        new {
            name = "human_input", aliases = new[] { "human_input" }, category = "human",
            description = "Pauses the workflow and waits for a human to provide freeform text input.",
            parameters = new object[] {
                P("prompt", "Message shown to the human", "Please provide input:"),
                P("variable", "Variable name to store the input", "user_input"),
                P("timeout", "Timeout in seconds", "1800"),
                P("on_timeout", "Action on timeout", "fail", "fail, skip"),
            },
        },
        new {
            name = "workflow_call", aliases = new[] { "workflow_call", "sub_workflow" }, category = "composition",
            description = "Invokes another workflow definition as a sub-workflow, passing the current input.",
            parameters = new object[] {
                P("workflow", "Name of the workflow to invoke (required)"),
            },
        },
        new {
            name = "vote_consensus", aliases = new[] { "vote_consensus", "vote" }, category = "composition",
            description = "Collects responses from multiple agents and determines consensus. Typically used after a parallel fan-out.",
            parameters = Array.Empty<object>(),
        },
        new {
            name = "workflow_loop", aliases = new[] { "workflow_loop" }, category = "control",
            description = "Internal orchestrator module. Drives step-by-step execution of the workflow definition. Not used directly in YAML.",
            parameters = Array.Empty<object>(),
        },
    ];
}

// POST /api/playground/chat — streaming LLM chat for workflow authoring
var playgroundSystemPrompt = BuildPlaygroundSystemPrompt();
app.MapPost("/api/playground/chat", async (HttpContext ctx, CancellationToken ct) =>
{
    if (!llmAvailable)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("LLM not configured");
        return;
    }

    var body = await JsonSerializer.DeserializeAsync<PlaygroundChatRequest>(ctx.Request.Body, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    }, ct);

    if (body?.Messages is not { Count: > 0 })
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("messages required");
        return;
    }

    ctx.Response.Headers["Content-Type"] = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["Connection"] = "keep-alive";

    var factory = ctx.RequestServices.GetRequiredService<ILLMProviderFactory>();
    var provider = factory.GetDefault();

    var llmMessages = new List<ChatMessage>
    {
        ChatMessage.System(playgroundSystemPrompt),
    };
    foreach (var m in body.Messages)
        llmMessages.Add(new ChatMessage { Role = m.Role, Content = m.Content });

    var request = new LLMRequest { Messages = llmMessages, Temperature = 0.3 };
    var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    try
    {
        await foreach (var chunk in provider.ChatStreamAsync(request, ct))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
            {
                var json = JsonSerializer.Serialize(new { delta = chunk.DeltaContent }, jsonOpts);
                await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        var json = JsonSerializer.Serialize(new { error = ex.Message }, jsonOpts);
        await ctx.Response.WriteAsync($"data: {json}\n\n", CancellationToken.None);
        await ctx.Response.Body.FlushAsync(CancellationToken.None);
    }

    await ctx.Response.WriteAsync("data: [DONE]\n\n", CancellationToken.None);
    await ctx.Response.Body.FlushAsync(CancellationToken.None);
});

// POST /api/playground/parse — parse YAML and return steps + edges for graph
app.MapPost("/api/playground/parse", async (HttpContext ctx) =>
{
    string yaml;
    using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
        yaml = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(yaml))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { valid = false, error = "Empty YAML" });
        return;
    }

    try
    {
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
        var roles = def.Roles.Select(BuildRoleDto);

        await ctx.Response.WriteAsJsonAsync(new
        {
            valid = true,
            definition = new
            {
                name = def.Name,
                description = def.Description,
                configuration = new
                {
                    closedWorldMode = def.Configuration.ClosedWorldMode,
                },
                roles,
                steps,
            },
            edges,
        });
    }
    catch (Exception ex)
    {
        await ctx.Response.WriteAsJsonAsync(new { valid = false, error = ex.Message });
    }
});

// POST /api/playground/run — run arbitrary YAML workflow via SSE
app.MapPost("/api/playground/run", async (HttpContext ctx, CancellationToken ct) =>
{
    var body = await JsonSerializer.DeserializeAsync<PlaygroundRunRequest>(ctx.Request.Body,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }, ct);

    if (string.IsNullOrWhiteSpace(body?.Yaml))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("yaml required");
        return;
    }

    WorkflowDefinition parsedDefinition;
    try
    {
        parsedDefinition = parser.Parse(body.Yaml);
    }
    catch (Exception ex)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync($"Invalid YAML: {ex.Message}");
        return;
    }

    var validationErrors = ValidateWorkflowDefinitionForRuntime(parsedDefinition, ctx.RequestServices);
    if (validationErrors.Count > 0)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync($"Invalid YAML: {string.Join("; ", validationErrors)}");
        return;
    }

    var needsLlm = parsedDefinition.Roles.Count > 0 || parsedDefinition.Steps.Any(s =>
        s.Type is "llm_call" or "evaluate" or "reflect" or "map_reduce" or "race" or "parallel" or "parallel_fanout");

    if (needsLlm && !llmAvailable)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("LLM not configured. Set DEEPSEEK_API_KEY or OPENAI_API_KEY.");
        return;
    }

    ctx.Response.Headers["Content-Type"] = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["Connection"] = "keep-alive";

    var actualInput = body.Input ?? "Hello, world!";
    var shouldAutoResume = body.AutoResume == true;
    var runtime = ctx.RequestServices.GetRequiredService<IActorRuntime>();
    var streams = ctx.RequestServices.GetRequiredService<IStreamProvider>();
    var actorId = $"pg-{Guid.NewGuid():N}"[..24];

    var actor = await runtime.CreateAsync<WorkflowGAgent>(actorId);
    await actor.HandleEventAsync(new EventEnvelope
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(new ConfigureWorkflowEvent { WorkflowYaml = body.Yaml, WorkflowName = "playground" }),
        PublisherId = "playground",
        Direction = EventDirection.Self,
        CorrelationId = Guid.NewGuid().ToString("N"),
    });

    var tcs = new TaskCompletionSource<bool>();
    var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    string? activeRunId = null;

    async Task WriteSse(string eventType, object data)
    {
        var json = JsonSerializer.Serialize(data, jsonOpts);
        await ctx.Response.WriteAsync($"event: {eventType}\ndata: {json}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    void RememberRunId(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return;

        activeRunId = runId.Trim();
        TrackRunContext(activeRunId, actor.Id, "playground", "playground");
    }

    void ScheduleAutoResume(WorkflowSuspendedEvent suspended)
    {
        var resumed = BuildAutoResumedEvent(suspended, actualInput);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50, CancellationToken.None);
                await actor.HandleEventAsync(new EventEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Payload = Any.Pack(resumed),
                    PublisherId = "playground.auto-human",
                    Direction = EventDirection.Self,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                });
            }
            catch (Exception ex)
            {
                try
                {
                    await WriteSse("workflow.error", new { error = $"Auto resume failed: {ex.Message}" });
                }
                catch
                {
                    // ignore write failures after client disconnect
                }
            }
        }, CancellationToken.None);
    }

    var stream = streams.GetStream(actor.Id);
    await using var sub = await stream.SubscribeAsync<EventEnvelope>(async envelope =>
    {
        if (envelope.Payload == null) return;
        try
        {
            var payload = envelope.Payload;
            if (payload.Is(StepRequestEvent.Descriptor))
            {
                var evt = payload.Unpack<StepRequestEvent>();
                RememberRunId(evt.RunId);
                if (!string.IsNullOrWhiteSpace(activeRunId))
                {
                    TrackRunContext(activeRunId, actor.Id, "playground", "playground", tracked =>
                    {
                        tracked.LastStepId = evt.StepId;
                        tracked.LastEventType = "step.request";
                    });
                }

                await WriteSse("step.request", new
                {
                    runId = string.IsNullOrWhiteSpace(evt.RunId) ? activeRunId : evt.RunId,
                    stepId = evt.StepId,
                    stepType = evt.StepType,
                    input = Truncate(evt.Input, 500),
                });
            }
            if (payload.Is(StepCompletedEvent.Descriptor))
            {
                var evt = payload.Unpack<StepCompletedEvent>();
                RememberRunId(evt.RunId);
                if (!string.IsNullOrWhiteSpace(activeRunId))
                {
                    TrackRunContext(activeRunId, actor.Id, "playground", "playground", tracked =>
                    {
                        tracked.LastStepId = evt.StepId;
                        tracked.LastEventType = "step.completed";
                    });
                }

                var meta = new Dictionary<string, string>();
                foreach (var kv in evt.Metadata) meta[kv.Key] = kv.Value;
                await WriteSse("step.completed", new
                {
                    runId = string.IsNullOrWhiteSpace(evt.RunId) ? activeRunId : evt.RunId,
                    stepId = evt.StepId, success = evt.Success,
                    output = Truncate(evt.Output, 1000),
                    error = string.IsNullOrEmpty(evt.Error) ? null : evt.Error,
                    metadata = meta.Count > 0 ? meta : null,
                });
            }
            if (payload.Is(WorkflowSuspendedEvent.Descriptor))
            {
                var evt = payload.Unpack<WorkflowSuspendedEvent>();
                RememberRunId(evt.RunId);
                if (!string.IsNullOrWhiteSpace(activeRunId))
                {
                    TrackRunContext(activeRunId, actor.Id, "playground", "playground", tracked =>
                    {
                        tracked.LastStepId = evt.StepId;
                        tracked.LastSuspensionType = evt.SuspensionType;
                        tracked.LastEventType = "workflow.suspended";
                    });
                }

                var meta = new Dictionary<string, string>();
                foreach (var kv in evt.Metadata)
                    meta[kv.Key] = kv.Value;

                await WriteSse("workflow.suspended", new
                {
                    runId = evt.RunId,
                    stepId = evt.StepId,
                    suspensionType = evt.SuspensionType,
                    prompt = evt.Prompt,
                    timeoutSeconds = evt.TimeoutSeconds,
                    metadata = meta.Count > 0 ? meta : null,
                });

                if (shouldAutoResume)
                    ScheduleAutoResume(evt);
            }
            if (payload.Is(WaitingForSignalEvent.Descriptor))
            {
                var evt = payload.Unpack<WaitingForSignalEvent>();
                if (!string.IsNullOrWhiteSpace(activeRunId))
                {
                    TrackRunContext(activeRunId, actor.Id, "playground", "playground", tracked =>
                    {
                        tracked.LastStepId = evt.StepId;
                        tracked.LastSignalName = evt.SignalName;
                        tracked.LastEventType = "workflow.waiting_signal";
                    });
                }

                await WriteSse("workflow.waiting_signal", new
                {
                    runId = activeRunId,
                    stepId = evt.StepId,
                    signalName = evt.SignalName,
                    prompt = evt.Prompt,
                    timeoutMs = evt.TimeoutMs,
                });
            }
            if (payload.Is(TextMessageEndEvent.Descriptor))
            {
                var evt = payload.Unpack<TextMessageEndEvent>();
                var pub = envelope.PublisherId ?? "";
                if (!string.Equals(pub, actor.Id, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(evt.Content))
                {
                    var role = pub.Contains(':') ? pub[(pub.LastIndexOf(':') + 1)..] : pub;
                    await WriteSse("llm.response", new { role, content = Truncate(evt.Content, 2000) });
                }
            }
            if (payload.Is(ChatResponseEvent.Descriptor))
            {
                var evt = payload.Unpack<ChatResponseEvent>();
                if (string.Equals(envelope.PublisherId, actor.Id, StringComparison.Ordinal))
                {
                    var error = string.IsNullOrWhiteSpace(evt.Content) ? "Workflow run failed." : evt.Content;
                    await WriteSse("workflow.error", new { error });
                    if (!string.IsNullOrWhiteSpace(activeRunId))
                        CleanupRunContext(activeRunId);
                    tcs.TrySetResult(false);
                }
            }
            if (payload.Is(WorkflowCompletedEvent.Descriptor))
            {
                var evt = payload.Unpack<WorkflowCompletedEvent>();
                RememberRunId(evt.RunId);
                await WriteSse("workflow.completed", new
                {
                    runId = string.IsNullOrWhiteSpace(evt.RunId) ? activeRunId : evt.RunId,
                    success = evt.Success, output = Truncate(evt.Output, 2000),
                    error = string.IsNullOrEmpty(evt.Error) ? null : evt.Error,
                });
                if (!string.IsNullOrWhiteSpace(evt.RunId))
                    CleanupRunContext(evt.RunId);
                else if (!string.IsNullOrWhiteSpace(activeRunId))
                    CleanupRunContext(activeRunId);
                tcs.TrySetResult(evt.Success);
            }
        }
        catch (Exception ex) { tcs.TrySetException(ex); }
    });

    await actor.HandleEventAsync(new EventEnvelope
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(new ChatRequestEvent { Prompt = actualInput, SessionId = "playground" }),
        PublisherId = "playground",
        Direction = EventDirection.Self,
    });

    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        await tcs.Task.WaitAsync(cts.Token);
    }
    catch (OperationCanceledException) { await WriteSse("workflow.error", new { error = "Timeout (2 min)" }); }
    catch (Exception ex) { await WriteSse("workflow.error", new { error = ex.Message }); }

    await Task.Delay(500, CancellationToken.None);
    await runtime.DestroyAsync(actor.Id);
    CleanupRunContextsForActor(actor.Id);
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

static object BuildRoleDto(RoleDefinition role) => new
{
    id = role.Id,
    name = role.Name,
    systemPrompt = role.SystemPrompt,
    provider = role.Provider,
    model = role.Model,
    temperature = role.Temperature,
    maxTokens = role.MaxTokens,
    maxToolRounds = role.MaxToolRounds,
    maxHistoryMessages = role.MaxHistoryMessages,
    streamBufferCapacity = role.StreamBufferCapacity,
    eventModules = role.EventModules,
    eventRoutes = role.EventRoutes,
    connectors = role.Connectors,
};

static WorkflowResumedEvent BuildAutoResumedEvent(WorkflowSuspendedEvent suspended, string originalInput)
{
    var suspensionType = suspended.SuspensionType ?? string.Empty;
    if (string.Equals(suspensionType, "human_approval", StringComparison.OrdinalIgnoreCase))
    {
        var shouldReject = (suspended.Prompt ?? string.Empty).Contains("AUTO_REJECT", StringComparison.OrdinalIgnoreCase) ||
            (suspended.Metadata.TryGetValue("auto_reject", out var marker) &&
             string.Equals(marker, "true", StringComparison.OrdinalIgnoreCase));

        return new WorkflowResumedEvent
        {
            RunId = suspended.RunId,
            StepId = suspended.StepId,
            Approved = !shouldReject,
            UserInput = string.Empty,
        };
    }

    var variable = suspended.Metadata.TryGetValue("variable", out var v) && !string.IsNullOrWhiteSpace(v)
        ? v.Trim()
        : "user_input";
    var source = (originalInput ?? string.Empty).ReplaceLineEndings(" ").Trim();
    if (source.Length > 80)
        source = source[..80];
    if (string.IsNullOrWhiteSpace(source))
        source = "empty";

    return new WorkflowResumedEvent
    {
        RunId = suspended.RunId,
        StepId = suspended.StepId,
        Approved = true,
        UserInput = $"{variable}=AUTO<{source}>",
    };
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

static string? ResolveTuringYamlDir()
{
    var envDir = Environment.GetEnvironmentVariable("WORKFLOW_TURING_YAML_DIR");
    if (!string.IsNullOrWhiteSpace(envDir) && Directory.Exists(envDir)) return envDir;

    var projectDir = Directory.GetCurrentDirectory();
    var repoRelative = Path.GetFullPath(Path.Combine(projectDir, "..", "..", "workflows", "turing-completeness"));
    if (Directory.Exists(repoRelative)) return repoRelative;

    var fromBin = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? projectDir,
        "..", "..", "..", "..", "..", "workflows", "turing-completeness"));
    if (Directory.Exists(fromBin)) return fromBin;

    return null;
}

static List<WorkflowYamlSource> BuildWorkflowSources(string primaryYamlDir, string? turingYamlDir)
{
    var sources = new List<WorkflowYamlSource>
    {
        new("default", primaryYamlDir),
    };

    if (!string.IsNullOrWhiteSpace(turingYamlDir) &&
        !string.Equals(primaryYamlDir, turingYamlDir, StringComparison.OrdinalIgnoreCase))
    {
        sources.Add(new("turing", turingYamlDir));
    }

    return sources;
}

static Dictionary<string, WorkflowFileEntry> DiscoverWorkflowFiles(IEnumerable<WorkflowYamlSource> sources)
{
    var workflowFiles = new Dictionary<string, WorkflowFileEntry>(StringComparer.OrdinalIgnoreCase);
    foreach (var source in sources)
    {
        if (!Directory.Exists(source.DirectoryPath))
            continue;

        foreach (var file in Directory.GetFiles(source.DirectoryPath, "*.yaml")
                     .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase))
        {
            var workflowName = Path.GetFileNameWithoutExtension(file);
            if (workflowFiles.ContainsKey(workflowName))
                continue;

            workflowFiles[workflowName] = new WorkflowFileEntry(workflowName, file, source.Kind);
        }
    }

    return workflowFiles;
}

static bool TryResolveWorkflowFile(
    string workflowName,
    IReadOnlyCollection<WorkflowYamlSource> sources,
    out WorkflowFileEntry workflowFile)
{
    var workflowFiles = DiscoverWorkflowFiles(sources);
    return workflowFiles.TryGetValue(workflowName, out workflowFile!);
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

static WorkflowListClassification ClassifyWorkflowForList(
    string workflowName,
    string sourceKind,
    ISet<string> deterministicWorkflows,
    ISet<string> turingWorkflows)
{
    var name = workflowName ?? string.Empty;
    var normalizedSource = sourceKind ?? string.Empty;
    var index = TryParseWorkflowIndex(name);

    var isTuring =
        string.Equals(normalizedSource, "turing", StringComparison.OrdinalIgnoreCase) ||
        turingWorkflows.Contains(name);
    if (isTuring)
    {
        var turingOrder = name.Contains("counter", StringComparison.OrdinalIgnoreCase) ? 1
            : name.Contains("minsky", StringComparison.OrdinalIgnoreCase) ? 2
            : 1000;
        return new WorkflowListClassification(
            Category: "turing",
            Group: "turing-completeness",
            GroupLabel: "Turing Completeness",
            SortOrder: turingOrder);
    }

    var isDeterministic = deterministicWorkflows.Contains(name);
    if (!isDeterministic)
    {
        return new WorkflowListClassification(
            Category: "llm",
            Group: "llm-workflows",
            GroupLabel: "LLM Workflows",
            SortOrder: index ?? 10_000);
    }

    if (index is >= 1 and <= 7)
    {
        return new WorkflowListClassification(
            Category: "deterministic",
            Group: "start-here",
            GroupLabel: "Start Here (Deterministic Basics)",
            SortOrder: index.Value);
    }

    if (index is >= 17 and <= 19)
    {
        return new WorkflowListClassification(
            Category: "deterministic",
            Group: "custom-step-modules",
            GroupLabel: "Custom Step Modules",
            SortOrder: index.Value);
    }

    if (index is >= 20 and <= 38)
    {
        return new WorkflowListClassification(
            Category: "deterministic",
            Group: "role-event-modules",
            GroupLabel: "Role Event Modules",
            SortOrder: index.Value);
    }

    if (index is >= 43 and <= 47)
    {
        return new WorkflowListClassification(
            Category: "deterministic",
            Group: "human-interaction-manual",
            GroupLabel: "Human Interaction (Manual)",
            SortOrder: index.Value);
    }

    if (index is >= 39 and <= 42)
    {
        return new WorkflowListClassification(
            Category: "deterministic",
            Group: "human-interaction-legacy",
            GroupLabel: "Human Interaction (Legacy Auto)",
            SortOrder: index.Value);
    }

    return new WorkflowListClassification(
        Category: "deterministic",
        Group: "deterministic-other",
        GroupLabel: "Other Deterministic Demos",
        SortOrder: index ?? 20_000);
}

static List<string> ValidateWorkflowDefinitionForRuntime(WorkflowDefinition definition, IServiceProvider services)
{
    var modulePacks = services.GetServices<IWorkflowModulePack>();
    var knownStepTypes = WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(
        modulePacks.SelectMany(pack => pack.Modules).SelectMany(module => module.Names));

    var moduleFactory = services.GetRequiredService<IEventModuleFactory>();
    foreach (var stepType in EnumerateReferencedStepTypes(definition.Steps))
    {
        var canonical = WorkflowPrimitiveCatalog.ToCanonicalType(stepType);
        if (string.IsNullOrWhiteSpace(canonical) || knownStepTypes.Contains(canonical))
            continue;

        if (moduleFactory.TryCreate(canonical, out _))
            knownStepTypes.Add(canonical);
    }

    return WorkflowValidator.Validate(
        definition,
        new WorkflowValidator.WorkflowValidationOptions
        {
            RequireKnownStepTypes = true,
            KnownStepTypes = knownStepTypes,
        },
        availableWorkflowNames: null);
}

static IEnumerable<string> EnumerateReferencedStepTypes(IEnumerable<StepDefinition> steps)
{
    foreach (var step in steps)
    {
        yield return step.Type;

        foreach (var (key, value) in step.Parameters)
        {
            if (WorkflowPrimitiveCatalog.IsStepTypeParameterKey(key) &&
                !string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }

        if (step.Children is { Count: > 0 })
        {
            foreach (var childType in EnumerateReferencedStepTypes(step.Children))
                yield return childType;
        }
    }
}

static int? TryParseWorkflowIndex(string workflowName)
{
    if (string.IsNullOrWhiteSpace(workflowName))
        return null;

    var span = workflowName.AsSpan().Trim();
    var i = 0;
    while (i < span.Length && char.IsDigit(span[i]))
        i++;

    if (i == 0 || i >= span.Length || span[i] != '_')
        return null;

    return int.TryParse(span[..i], out var value) ? value : null;
}

static string BuildPlaygroundSystemPrompt() => """
You are an expert Aevatar Workflow YAML author. You help users design workflows by writing valid YAML.

## YAML Schema (snake_case)
```
name: string            # required
description: string     # optional
configuration:          # optional
  closed_world_mode: bool # optional, default false
roles:                  # optional — LLM persona definitions
  - id: string          # required (or name)
    name: string        # required (or id)
    system_prompt: |    # optional
      ...
    provider: string    # optional
    model: string       # optional
    temperature: number # optional
    max_tokens: int     # optional
    max_tool_rounds: int # optional
    max_history_messages: int # optional
    stream_buffer_capacity: int # optional
    event_modules: string # optional, comma-separated module names
    event_routes: string  # optional, route DSL/YAML list
    connectors: [string]  # optional
    extensions:           # optional compatibility container
      event_modules: string
      event_routes: string
steps:                  # ordered step list
  - id: string          # required — unique
    type: string        # default "llm_call"
    target_role: string # optional (alias: role)
    parameters: {}      # Dict<string, string>
    next: string        # explicit next step id
    branches: {}        # key → step_id ("_default" for fallback)
    children: []        # nested sub-steps (recursive)
    retry: { max_attempts: 3, backoff: "fixed"|"exponential", delay_ms: 1000 }
    on_error: { strategy: "fail"|"skip"|"fallback", fallback_step: "...", default_output: "..." }
    timeout_ms: int
```

## Role Customization Guidance
- You can and should design custom roles based on the user's domain and task.
- Each role should have a stable `id` (snake_case) and a clear `system_prompt`.
- For multi-stage workflows, prefer specialized roles (e.g. researcher, reviewer, writer) over one generic role.
- All role-referenced steps must point to existing role ids (`target_role` or `role`).
- When user asks runtime behavior, include role runtime fields:
  `provider`, `model`, `temperature`, `max_tokens`, `max_tool_rounds`,
  `max_history_messages`, `stream_buffer_capacity`, `event_modules`,
  `event_routes`, `connectors`.
- If user explicitly wants a single-role workflow, keep exactly one role.
- If workflow is purely deterministic and has no role-driven AI steps, roles can be omitted.

## Step Types
| Type | Category | Purpose |
|------|----------|---------|
| transform | data | Text ops: op= identity/uppercase/lowercase/trim/count/count_words/take/take_last/join/split/distinct/reverse_lines; n, separator |
| assign | data | Set variable: target, value ("$input" = current input) |
| retrieve_facts | data | Keyword search: query, top_k |
| cache | data | Cache child step: cache_key, ttl_seconds, child_step_type, child_target_role |
| guard | control | Validation gate: check= not_empty/json_valid/regex/max_length/contains; on_fail= fail/skip/branch; pattern, max, keyword, branch_target |
| conditional | control | Binary branch: condition (keyword to search in input) |
| switch | control | Multi-way branch: branch.{key} in parameters + branches in step definition |
| while | control | Loop: max_iterations, step (sub-step type) |
| delay | control | Pause: duration_ms (0–300000) |
| wait_signal | control | Block: signal_name, timeout_ms |
| checkpoint | control | Save state: name |
| llm_call | ai | LLM prompt: prompt_prefix. Requires target_role with system_prompt |
| tool_call | ai | Invoke tool: tool (name) |
| evaluate | ai | LLM-as-judge: criteria, scale, threshold, on_below. Requires judge role |
| reflect | ai | Self-improvement loop: max_rounds (1–10), criteria |
| foreach | composition | Iterate: delimiter, sub_step_type, sub_target_role |
| parallel | composition | Fan-out: workers (comma-separated role IDs), parallel_count |
| race | composition | First-wins: workers, count |
| map_reduce | composition | Split→map→reduce: delimiter, map_step_type, map_target_role, reduce_step_type, reduce_target_role, reduce_prompt_prefix |
| workflow_call | composition | Sub-workflow: workflow (name) |
| vote_consensus | composition | Aggregate votes (no params) |
| connector_call | integration | External call: connector, operation, retry, timeout_ms, on_error= fail/continue |
| emit | integration | Publish event: event_type, payload |
| human_input | human | Wait for input: prompt, variable, timeout, on_timeout |
| human_approval | human | Wait for approval: prompt, timeout, on_reject |

## Rules
- All parameter values are strings (even numbers: "3" not 3).
- type defaults to "llm_call" when omitted.
- target_role and role are aliases; target_role takes precedence.
- Role fields `event_modules/event_routes` support both top-level and `extensions.*`; top-level has higher priority.
- For workflows using `llm_call` / `evaluate` / `reflect`, always provide matching roles.
- When user customizes roles, preserve requested role names/ids and wire all related steps correctly.
- Steps flow: next → explicit jump; branches → conditional routing; neither → sequential (list order).
- For switch: both parameters.branch.* AND branches: must be set.
- Each branch target should have next: pointing to a merge step.
- "_default" is the reserved fallback branch key.

## Response Format
- Always wrap workflow YAML in a ```yaml code block.
- Explain your design choices briefly.
- If the user's request is ambiguous, ask clarifying questions.
- Generate complete, valid, parseable YAML.
- Return a full workflow YAML (including roles) unless the user explicitly asks for a partial snippet.
""";

sealed record PlaygroundChatMessage(string Role, string Content);
sealed record PlaygroundChatRequest(List<PlaygroundChatMessage> Messages);
sealed record PlaygroundRunRequest(string Yaml, string? Input, bool? AutoResume = null);
sealed record WorkflowResumeRequest(
    string RunId,
    string StepId,
    bool Approved = true,
    string? UserInput = null,
    Dictionary<string, string>? Metadata = null);
sealed record WorkflowSignalRequest(string RunId, string SignalName, string? Payload = null);
sealed record WorkflowYamlSource(string Kind, string DirectoryPath);
sealed record WorkflowFileEntry(string Name, string FilePath, string SourceKind);
sealed record WorkflowListClassification(string Category, string Group, string GroupLabel, int SortOrder);

sealed class DemoRunContext
{
    public string RunId { get; init; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string? LastStepId { get; set; }
    public string? LastSuspensionType { get; set; }
    public string? LastSignalName { get; set; }
    public string? LastEventType { get; set; }
}
