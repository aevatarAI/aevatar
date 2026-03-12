using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.Agents;
using Aevatar.AI.Core.LLMProviders;
using Aevatar.AI.LLMProviders.MEAI;
using Aevatar.AI.LLMProviders.Tornado;
using Aevatar.Bootstrap;
using Aevatar.Bootstrap.Connectors;
using Aevatar.Configuration;
using Aevatar.Demos.Workflow.Web;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.Workflows;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;

var port = 5280;
const int AutoResumeDelayMs = 50;
const int WorkflowRunTimeoutMinutes = 2;
const int FinalSseFlushDelayMs = 500;
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

var primaryYamlDir = ResolveYamlDir();
var turingYamlDir = ResolveTuringYamlDir();
var workflowSources = BuildWorkflowSources(primaryYamlDir, turingYamlDir);

builder.Services.AddAevatarRuntime();
builder.Services.AddAevatarConfig();
builder.Services.AddWorkflowProjectionReadModelProviders(builder.Configuration);
builder.Services.AddWorkflowCapability(builder.Configuration);
builder.Services.AddWorkflowDefinitionFileSource(options =>
{
    options.WorkflowDirectories.Add(primaryYamlDir);
    if (!string.IsNullOrWhiteSpace(turingYamlDir))
        options.WorkflowDirectories.Add(turingYamlDir);
    options.DuplicatePolicy = WorkflowDefinitionDuplicatePolicy.Override;
});
builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectorBuilder, HttpConnectorBuilder>());
builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectorBuilder, CliConnectorBuilder>());
builder.Services.AddSingleton<IWorkflowModulePack, DemoWorkflowModulePack>();
builder.Services.AddSingleton<DemoWorkflowModuleFactory>();
builder.Services.Replace(ServiceDescriptor.Singleton<IEventModuleFactory<IWorkflowExecutionContext>>(sp =>
    sp.GetRequiredService<DemoWorkflowModuleFactory>()));
builder.Services.AddSingleton<IEventModuleFactory<IEventHandlerContext>>(sp =>
    sp.GetRequiredService<DemoWorkflowModuleFactory>());
builder.Services.AddSingleton<IRoleAgentTypeResolver, RoleGAgentTypeResolver>();

var config = new ConfigurationBuilder()
    .AddAevatarConfig()
    .AddEnvironmentVariables()
    .Build();

var providerName = string.Empty;
var modelName = string.Empty;
var availableProviderNames = new List<string>();
var llmAvailable = false;

var secrets = new AevatarSecretsStore();
var configuredProviders = ResolveConfiguredProvidersFromSecrets(secrets, config);
if (configuredProviders.Count == 0)
{
    var fallbackProvider = ResolveFallbackProviderFromEnvironmentAndSecrets(secrets, config);
    if (fallbackProvider != null)
        configuredProviders.Add(fallbackProvider);
}

if (configuredProviders.Count > 0)
{
    var preferredDefault = secrets.GetDefaultProvider() ?? config["Models:DefaultProvider"];
    var defaultProvider = configuredProviders.FirstOrDefault(p =>
        string.Equals(p.Name, preferredDefault, StringComparison.OrdinalIgnoreCase))
        ?? configuredProviders[0];
    var tornadoDefaultProvider = configuredProviders.FirstOrDefault(p =>
        p.Name.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
        ?? defaultProvider;

    providerName = defaultProvider.Name;
    modelName = defaultProvider.Model;
    availableProviderNames = configuredProviders.Select(p => p.Name).ToList();

    var meaiFactory = new MEAILLMProviderFactory();
    var tornadoFactory = new TornadoLLMProviderFactory();
    foreach (var provider in configuredProviders)
    {
        meaiFactory.RegisterOpenAI(
            provider.Name,
            provider.Model,
            provider.ApiKey,
            string.IsNullOrWhiteSpace(provider.Endpoint) ? null : provider.Endpoint);
        tornadoFactory.RegisterOpenAICompatible(
            provider.Name,
            provider.ApiKey,
            provider.Model,
            string.IsNullOrWhiteSpace(provider.Endpoint) ? null : provider.Endpoint);
    }

    meaiFactory.SetDefault(providerName);
    tornadoFactory.SetDefault(tornadoDefaultProvider.Name);
    var failoverFactory = new FailoverLLMProviderFactory(
        meaiFactory,
        tornadoFactory,
        new LLMProviderFailoverOptions
        {
            PreferFallbackDefaultProvider = true,
            FallbackToDefaultProviderWhenNamedProviderMissing = true,
        });
    builder.Services.Replace(ServiceDescriptor.Singleton<ILLMProviderFactory>(failoverFactory));

    llmAvailable = true;
}

var app = builder.Build();
var loadedConnectorNames = LoadNamedConnectors(app.Services);

Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║           Workflow Primitives Web UI                    ║");
Console.WriteLine("╠══════════════════════════════════════════════════════════╣");
Console.WriteLine($"║  Web UI: {url,-48}║");
Console.WriteLine($"║  YAML:   {primaryYamlDir,-48}║");
var llmSummary = llmAvailable
    ? $"{providerName}/{modelName}" + (availableProviderNames.Count > 1 ? $" (+{availableProviderNames.Count} providers)" : string.Empty)
    : "not configured";
Console.WriteLine($"║  LLM:    {llmSummary,-48}║");
var connectorSummary = loadedConnectorNames.Count == 0 ? "-" : string.Join(", ", loadedConnectorNames);
if (connectorSummary.Length > 48)
    connectorSummary = connectorSummary[..45] + "...";
Console.WriteLine($"║  Conn:   {connectorSummary,-48}║");
Console.WriteLine("║  Press Ctrl+C to stop                                  ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
Console.WriteLine();
if (!string.IsNullOrWhiteSpace(turingYamlDir))
    Console.WriteLine($"[Turing demos] {turingYamlDir}");
app.Lifetime.ApplicationStarted.Register(() => { if (!noBrowser) OpenBrowser(url); });

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapWorkflowChatInteractionEndpoints();

var deterministicWorkflows = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "transform", "guard", "conditional", "switch",
    "assign", "retrieve_facts", "pipeline",
    "demo_template", "demo_csv_markdown", "demo_json_pick",
    "role_event_module_template", "role_event_module_csv_markdown", "role_event_module_json_pick",
    "role_event_module_multiplex_template", "role_event_module_multiplex_csv", "role_event_module_multiplex_json",
    "role_event_module_multi_role_chain",
    "role_event_module_extensions_template", "role_event_module_extensions_csv",
    "role_event_module_top_level_overrides_extensions",
    "role_event_module_extensions_multi_role_chain",
    "role_event_module_extensions_multiplex_json",
    "role_event_module_top_level_overrides_extensions_multiplex",
    "role_event_module_no_routes_template",
    "role_event_module_route_dsl_csv",
    "role_event_module_unknown_ignored_template",
    "mixed_step_json_pick_then_role_template",
    "mixed_step_csv_markdown_then_role_template",
    "mixed_step_template_then_role_csv_markdown",
    "human_input_basic_auto_resume",
    "human_approval_approved_auto_resume",
    "human_approval_rejected_fail_auto_resume",
    "human_approval_rejected_skip_auto_resume",
    "human_input_manual_triage",
    "wait_signal_manual_success",
    "wait_signal_timeout_failure",
    "human_approval_release_gate",
    "mixed_human_approval_wait_signal",
    "connector_cli_demo",
    "cli_call_alias",
    "emit_publish_demo",
    "tool_call_fallback_demo",
    "delay_checkpoint_demo",
    "workflow_call_multilevel",
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
    ["transform"] = "Line one: hello world\nLine two: foo bar\nLine three: baz qux\nLine four: the quick brown fox\nLine five: jumps over the lazy dog",
    ["guard"] = """{"name": "Alice", "email": "alice@example.com", "age": 30}""",
    ["conditional"] = "URGENT: Server is down, all requests failing with 502 errors.",
    ["switch"] = "bug: Login button does not respond on mobile Safari",
    ["assign"] = "The answer to the ultimate question is 42.",
    ["retrieve_facts"] = "Earth orbits the Sun at about 150 million km\nWater boils at 100 degrees Celsius at sea level\nThe speed of light is approximately 300000 km per second\nMount Everest is 8849 meters tall",
    ["pipeline"] = "Earth orbits the Sun at about 150 million km\nThe speed of light is approximately 300000 km per second\nWater boils at 100 degrees Celsius at sea level\nPython was created by Guido van Rossum\nLight travels faster than sound",
    ["llm_call"] = "Explain the concept of event sourcing in 3 sentences.",
    ["llm_chain"] = "Distributed systems face challenges in maintaining consistency across nodes.",
    ["parallel"] = "What are the benefits of using microservices architecture?",
    ["race"] = "Give a one-sentence definition of functional programming.",
    ["map_reduce"] = "Topic: Benefits of remote work\n---\nTopic: Challenges of remote work\n---\nTopic: Future of remote work",
    ["foreach"] = "Kubernetes\n---\nDocker\n---\nTerraform",
    ["evaluate"] = "Write a haiku about programming.",
    ["reflect"] = "Write a concise explanation of the CAP theorem suitable for a junior developer.",
    ["cache"] = "What is the difference between SQL and NoSQL databases?",
    ["demo_template"] = "payment_api_timeout",
    ["demo_csv_markdown"] = "service,error_rate,latency_ms\ngateway,1.2,210\ncheckout,0.3,120",
    ["demo_json_pick"] = """{"incident":{"id":"INC-2026-001","owner":{"team":"sre","user":"alice"}},"severity":"high"}""",
    ["role_event_module_template"] = "checkout_db_latency_spike",
    ["role_event_module_csv_markdown"] = "service,error_rate,latency_ms\nauth,0.8,180\nbilling,1.4,260",
    ["role_event_module_json_pick"] = """{"incident":{"id":"INC-2026-007","owner":{"team":"platform","user":"bob"}},"severity":"critical"}""",
    ["role_event_module_multiplex_template"] = "order_service_high_latency",
    ["role_event_module_multiplex_csv"] = "service,error_rate,latency_ms\napi,1.1,240\nworker,0.5,170",
    ["role_event_module_multiplex_json"] = """{"incident":{"id":"INC-2026-011","owner":{"team":"infra","user":"charlie"}},"severity":"high"}""",
    ["role_event_module_multi_role_chain"] = "checkout_timeout_spike",
    ["role_event_module_extensions_template"] = "payments_retry_exhausted",
    ["role_event_module_extensions_csv"] = "service,error_rate,latency_ms\nsearch,0.6,150\nrecommendation,1.3,280",
    ["role_event_module_top_level_overrides_extensions"] = """{"incident":{"id":"INC-2026-023","owner":{"team":"runtime","user":"eve"}},"severity":"critical"}""",
    ["role_event_module_extensions_multi_role_chain"] = "payment_timeout_burst",
    ["role_event_module_extensions_multiplex_json"] = """{"incident":{"id":"INC-2026-033","owner":{"team":"gateway","user":"gina"}},"severity":"high"}""",
    ["role_event_module_top_level_overrides_extensions_multiplex"] = "service,error_rate,latency_ms\nedge,1.0,210\npayment,1.8,320",
    ["role_event_module_no_routes_template"] = "inventory_sync_lag",
    ["role_event_module_route_dsl_csv"] = "service,error_rate,latency_ms\ncatalog,0.4,140\ncheckout,1.6,300",
    ["role_event_module_unknown_ignored_template"] = "payments_duplicate_callback",
    ["mixed_step_json_pick_then_role_template"] = """{"incident":{"id":"INC-2026-041","owner":{"team":"data","user":"harry"}},"severity":"high"}""",
    ["mixed_step_csv_markdown_then_role_template"] = "service,error_rate,latency_ms\nsearch,0.7,160\nfeed,1.4,295",
    ["mixed_step_template_then_role_csv_markdown"] = "1.3",
    ["human_input_basic_auto_resume"] = "checkout request missing approver and rollback plan",
    ["human_approval_approved_auto_resume"] = "deploy release v1.2.3 to production",
    ["human_approval_rejected_fail_auto_resume"] = "delete production database",
    ["human_approval_rejected_skip_auto_resume"] = "restart read replica cluster",
    ["human_input_manual_triage"] = "api gateway latency spikes in us-east-1",
    ["wait_signal_manual_success"] = "release candidate v2.4.0 passed smoke checks",
    ["wait_signal_timeout_failure"] = "database migration waiting for DBA ack",
    ["human_approval_release_gate"] = "change request CR-2026-021",
    ["mixed_human_approval_wait_signal"] = "deploy cache cluster patch-7",
    ["workflow_call_multilevel"] = "apple\nbanana\napple\ncarrot",
    ["connector_cli_demo"] = "Run connector demo (local CLI): execute dotnet --version through connector_call.",
    ["cli_call_alias"] = "Run cli_call alias demo using local dotnet connector.",
    ["foreach_llm_alias"] = "Kubernetes\n---\nDocker\n---\nTerraform",
    ["map_reduce_llm_alias"] = "Topic: Caching strategy\n---\nTopic: Retry policy\n---\nTopic: Observability basics",
    ["emit_publish_demo"] = "workflow event payload demo",
    ["tool_call_fallback_demo"] = "tool_call fallback demo input",
    ["delay_checkpoint_demo"] = "delay + checkpoint demo input",
    ["counter-addition"] = "Run the closed-world two-counter addition demo.",
    ["minsky-inc-dec-jz"] = "Run the closed-world INC/DEC/JZ transfer demo.",
    ["counter_addition"] = "Run the closed-world two-counter addition demo.",
    ["minsky_inc_dec_jz"] = "Run the closed-world INC/DEC/JZ transfer demo.",
};

var parser = new WorkflowParser();

IResult BuildWorkflowCatalogResponse()
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
                source = workflowFile.SourceKind,
                sourceLabel = DescribeWorkflowSource(workflowFile.SourceKind),
                requiresLlmProvider = WorkflowLlmRuntimePolicy.RequiresLlmProvider(def),
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
                source = workflowFile.SourceKind,
                sourceLabel = DescribeWorkflowSource(workflowFile.SourceKind),
                requiresLlmProvider = false,
                primitives = new List<string>(),
                defaultInput = "",
            });
        }
    }
    return Results.Json(workflows);
}

// GET /api/workflows — list all workflows
app.MapGet("/api/workflows", BuildWorkflowCatalogResponse);

// GET /api/workflow-catalog — richer workflow catalog for the app UI
app.MapGet("/api/workflow-catalog", BuildWorkflowCatalogResponse);

// GET /api/workflows/{name} — workflow definition with edges
app.MapGet("/api/workflows/{name}", (string name) =>
{
    if (!TryResolveWorkflowFile(name, workflowSources, out var workflowFile))
        return Results.NotFound(new { error = $"Workflow '{name}' not found" });

    var yaml = File.ReadAllText(workflowFile.FilePath);
    var def = parser.Parse(yaml);
    var listMeta = ClassifyWorkflowForList(name, workflowFile.SourceKind, deterministicWorkflows, turingWorkflows);

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
        catalog = new
        {
            name,
            description = def.Description,
            category = listMeta.Category,
            group = listMeta.Group,
            groupLabel = listMeta.GroupLabel,
            sortOrder = listMeta.SortOrder,
            source = workflowFile.SourceKind,
            sourceLabel = DescribeWorkflowSource(workflowFile.SourceKind),
            requiresLlmProvider = WorkflowLlmRuntimePolicy.RequiresLlmProvider(def),
            primitives = def.Steps.Select(s => s.Type).Distinct().ToList(),
        },
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

    if (WorkflowLlmRuntimePolicy.RequiresLlmProvider(parsedDefinition) && !llmAvailable)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("LLM not configured. Set DEEPSEEK_API_KEY or OPENAI_API_KEY.");
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
        Payload = Any.Pack(new BindWorkflowDefinitionEvent
        {
            WorkflowYaml = yaml,
            WorkflowName = name,
        }),
        Route = new EnvelopeRoute
        {
            PublisherActorId = "web.demo",
            Direction = EventDirection.Self,
        },
        Propagation = new EnvelopePropagation
        {
            CorrelationId = Guid.NewGuid().ToString("N"),
        },
    });

    var tcs = new TaskCompletionSource<bool>();
    var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    async Task WriteSse(string eventType, object data)
    {
        var json = JsonSerializer.Serialize(data, jsonOpts);
        await ctx.Response.WriteAsync($"event: {eventType}\ndata: {json}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    void ScheduleAutoResume(WorkflowSuspendedEvent suspended)
    {
        var resumed = BuildAutoResumedEvent(suspended, actualInput);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(AutoResumeDelayMs, ct);
                await actor.HandleEventAsync(new EventEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Payload = Any.Pack(resumed),
                    Route = new EnvelopeRoute
                    {
                        PublisherActorId = "web.demo.auto-human",
                        Direction = EventDirection.Self,
                    },
                    Propagation = new EnvelopePropagation
                    {
                        CorrelationId = Guid.NewGuid().ToString("N"),
                    },
                });
            }
            catch (OperationCanceledException)
            {
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
        }, ct);
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
                await WriteSse("step.request", new
                {
                    runId = evt.RunId,
                    stepId = evt.StepId,
                    stepType = evt.StepType,
                    input = evt.Input,
                });
            }

            if (payload.Is(StepCompletedEvent.Descriptor))
            {
                var evt = payload.Unpack<StepCompletedEvent>();
                var annotations = new Dictionary<string, string>();
                foreach (var kv in evt.Annotations)
                    annotations[kv.Key] = kv.Value;
                await WriteSse("step.completed", new
                {
                    runId = evt.RunId,
                    stepId = evt.StepId,
                    success = evt.Success,
                    output = evt.Output,
                    error = string.IsNullOrEmpty(evt.Error) ? null : evt.Error,
                    annotations = annotations.Count > 0 ? annotations : null,
                });
            }

            if (payload.Is(WorkflowSuspendedEvent.Descriptor))
            {
                var evt = payload.Unpack<WorkflowSuspendedEvent>();
                await WriteSse("workflow.suspended", new
                {
                    actorId = actor.Id,
                    runId = evt.RunId,
                    stepId = evt.StepId,
                    suspensionType = evt.SuspensionType,
                    prompt = evt.Prompt,
                    timeoutSeconds = evt.TimeoutSeconds,
                    variableName = string.IsNullOrWhiteSpace(evt.VariableName) ? null : evt.VariableName,
                });

                if (shouldAutoResume)
                    ScheduleAutoResume(evt);
            }

            if (payload.Is(WaitingForSignalEvent.Descriptor))
            {
                var evt = payload.Unpack<WaitingForSignalEvent>();
                await WriteSse("workflow.waiting_signal", new
                {
                    actorId = actor.Id,
                    runId = evt.RunId,
                    stepId = evt.StepId,
                    signalName = evt.SignalName,
                    prompt = evt.Prompt,
                    timeoutMs = evt.TimeoutMs,
                });
            }

            if (payload.Is(TextMessageEndEvent.Descriptor))
            {
                var evt = payload.Unpack<TextMessageEndEvent>();
                var publisher = envelope.Route?.PublisherActorId ?? "";
                if (!string.Equals(publisher, actor.Id, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(evt.Content))
                {
                    var role = publisher.Contains(':') ? publisher[(publisher.LastIndexOf(':') + 1)..] : publisher;
                    await WriteSse("llm.response", new { role, content = evt.Content });
                }
            }

            if (payload.Is(TextMessageReasoningEvent.Descriptor))
            {
                var evt = payload.Unpack<TextMessageReasoningEvent>();
                var publisher = envelope.Route?.PublisherActorId ?? "";
                if (!string.Equals(publisher, actor.Id, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(evt.Delta))
                {
                    var role = publisher.Contains(':') ? publisher[(publisher.LastIndexOf(':') + 1)..] : publisher;
                    await WriteSse("llm.thinking", new { role, content = evt.Delta });
                }
            }

            if (payload.Is(ChatResponseEvent.Descriptor))
            {
                var evt = payload.Unpack<ChatResponseEvent>();
                if (string.Equals(envelope.Route?.PublisherActorId, actor.Id, StringComparison.Ordinal))
                {
                    var error = string.IsNullOrWhiteSpace(evt.Content) ? "Workflow run failed." : evt.Content;
                    await WriteSse("workflow.error", new { error });
                    tcs.TrySetResult(false);
                }
            }

            if (payload.Is(WorkflowCompletedEvent.Descriptor))
            {
                var evt = payload.Unpack<WorkflowCompletedEvent>();
                await WriteSse("workflow.completed", new
                {
                    runId = evt.RunId,
                    success = evt.Success,
                    output = evt.Output,
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
        Route = new EnvelopeRoute
        {
            PublisherActorId = "web.demo",
            Direction = EventDirection.Self,
        },
    });

    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(WorkflowRunTimeoutMinutes));
        await tcs.Task.WaitAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        await WriteSse("workflow.error", new { error = $"Timeout ({WorkflowRunTimeoutMinutes} min)" });
    }
    catch (Exception ex)
    {
        await WriteSse("workflow.error", new { error = ex.Message });
    }

    // Keep connection open briefly so the browser's EventSource processes the final event
    // before the TCP close triggers onerror/reconnect.
    if (!ct.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(FinalSseFlushDelayMs, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    await runtime.DestroyAsync(actor.Id);
});

// GET /api/llm/status — LLM availability
app.MapGet("/api/llm/status", () => Results.Json(new
{
    available = llmAvailable,
    provider = llmAvailable ? providerName : null,
    model = llmAvailable ? modelName : null,
    providers = llmAvailable ? availableProviderNames.ToArray() : Array.Empty<string>(),
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
            description = "Binary branching. Checks if input contains a keyword, sets StepCompletedEvent.BranchKey to \"true\" or \"false\".",
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
        var validationErrors = ValidateWorkflowDefinitionForRuntime(def, ctx.RequestServices);
        if (validationErrors.Count > 0)
        {
            await ctx.Response.WriteAsJsonAsync(new
            {
                valid = false,
                error = string.Join("; ", validationErrors),
                errors = validationErrors,
            });
            return;
        }

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

// POST /api/playground/workflows — validate and save YAML to ~/.aevatar/workflows
app.MapPost("/api/playground/workflows", async (HttpContext ctx) =>
{
    PlaygroundWorkflowSaveRequest? request;
    try
    {
        request = await JsonSerializer.DeserializeAsync<PlaygroundWorkflowSaveRequest>(
            ctx.Request.Body,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
            ctx.RequestAborted);
    }
    catch (JsonException)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = "invalid json body" });
        return;
    }

    if (request == null)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = "request body is required" });
        return;
    }

    if (string.IsNullOrWhiteSpace(request.Yaml))
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = "workflow yaml is required" });
        return;
    }

    WorkflowDefinition parsedDefinition;
    List<string> validationErrors;
    try
    {
        parsedDefinition = parser.Parse(request.Yaml);
        validationErrors = ValidateWorkflowDefinitionForRuntime(parsedDefinition, ctx.RequestServices);
    }
    catch (Exception ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
        return;
    }

    if (validationErrors.Count > 0)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new
        {
            error = string.Join("; ", validationErrors),
            errors = validationErrors,
        });
        return;
    }

    string filename;
    try
    {
        filename = NormalizeWorkflowSaveFilename(request.Filename, parsedDefinition.Name);
    }
    catch (Exception ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
        return;
    }

    Directory.CreateDirectory(AevatarPaths.Workflows);
    var path = Path.Combine(AevatarPaths.Workflows, filename);
    var existed = File.Exists(path);
    if (existed && !request.Overwrite)
    {
        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
        await ctx.Response.WriteAsJsonAsync(new
        {
            error = $"Workflow '{filename}' already exists.",
            filename,
            path,
        });
        return;
    }

    var content = NormalizeWorkflowContentForSave(request.Yaml);
    await File.WriteAllTextAsync(path, content, Encoding.UTF8, ctx.RequestAborted);

    await ctx.Response.WriteAsJsonAsync(new
    {
        saved = true,
        filename,
        path,
        workflowName = parsedDefinition.Name,
        overwritten = existed,
    });
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
        var shouldReject = (suspended.Prompt ?? string.Empty).Contains("AUTO_REJECT", StringComparison.OrdinalIgnoreCase);

        return new WorkflowResumedEvent
        {
            RunId = suspended.RunId,
            StepId = suspended.StepId,
            Approved = !shouldReject,
            UserInput = string.Empty,
        };
    }

    var variable = !string.IsNullOrWhiteSpace(suspended.VariableName)
        ? suspended.VariableName.Trim()
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
    if (index is >= 51 and <= 53)
    {
        return new WorkflowListClassification(
            Category: isDeterministic ? "deterministic" : "llm",
            Group: "ergonomic-aliases",
            GroupLabel: "Ergonomic Aliases",
            SortOrder: index.Value);
    }

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

    if (index == 50)
    {
        return new WorkflowListClassification(
            Category: "deterministic",
            Group: "connector-integration",
            GroupLabel: "Connector Integration",
            SortOrder: index.Value);
    }

    if (index is >= 54 and <= 56)
    {
        return new WorkflowListClassification(
            Category: "deterministic",
            Group: "integration-utility",
            GroupLabel: "Integration Utility",
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

    if (index is >= 39 and <= 42)
    {
        return new WorkflowListClassification(
            Category: "deterministic",
            Group: "human-interaction-legacy",
            GroupLabel: "Human Interaction (Legacy Auto)",
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

    var moduleFactory = services.GetRequiredService<IEventModuleFactory<IWorkflowExecutionContext>>();
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

static string NormalizeWorkflowSaveFilename(string? requestedFilename, string workflowName)
{
    var candidate = string.IsNullOrWhiteSpace(requestedFilename)
        ? workflowName
        : requestedFilename.Trim();
    if (string.IsNullOrWhiteSpace(candidate))
        throw new InvalidOperationException("workflow filename is required");

    var fileNameOnly = Path.GetFileName(candidate);
    if (!string.Equals(fileNameOnly, candidate, StringComparison.Ordinal))
        throw new InvalidOperationException("workflow filename must not include directory segments");

    var stem = Path.GetFileNameWithoutExtension(fileNameOnly);
    if (string.IsNullOrWhiteSpace(stem))
        throw new InvalidOperationException("workflow filename is invalid");

    var sanitizedChars = stem
        .Trim()
        .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')
        .ToArray();
    var sanitizedStem = new string(sanitizedChars)
        .Trim('_');
    while (sanitizedStem.Contains("__", StringComparison.Ordinal))
        sanitizedStem = sanitizedStem.Replace("__", "_", StringComparison.Ordinal);

    if (string.IsNullOrWhiteSpace(sanitizedStem))
        throw new InvalidOperationException("workflow filename must contain letters or digits");

    return sanitizedStem + ".yaml";
}

static string NormalizeWorkflowContentForSave(string yaml) =>
    (yaml ?? string.Empty).Trim() + Environment.NewLine;

static int? TryParseWorkflowIndex(string workflowName)
{
    return WorkflowLibraryClassifier.TryGetWorkflowIndex(workflowName);
}

static string DescribeWorkflowSource(string sourceKind) =>
    (sourceKind ?? string.Empty).ToLowerInvariant() switch
    {
        "home" => "Saved",
        "cwd" => "Workspace",
        "repo" => "Starter",
        "demo" => "Demo",
        "turing" => "Advanced",
        _ => "Workflow",
    };

static IReadOnlyList<string> LoadNamedConnectors(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var scoped = scope.ServiceProvider;
    var logger = scoped.GetRequiredService<ILoggerFactory>().CreateLogger("Workflow.Web.Connectors");
    var registry = scoped.GetService<IConnectorRegistry>();
    if (registry == null)
    {
        logger.LogWarning("IConnectorRegistry is not registered. Skip connector loading.");
        return [];
    }

    var connectorBuilders = scoped.GetServices<IConnectorBuilder>().ToList();
    if (connectorBuilders.Count == 0)
    {
        logger.LogWarning("No IConnectorBuilder registered. Skip connector loading.");
        return registry.ListNames();
    }

    var loadedCount = ConnectorRegistration.RegisterConnectors(registry, connectorBuilders, logger);
    if (loadedCount == 0)
    {
        var localConnectorPath = ResolveLocalConnectorConfigPath();
        if (!string.IsNullOrWhiteSpace(localConnectorPath) && File.Exists(localConnectorPath))
        {
            logger.LogInformation("Loading demo connectors from {Path}", localConnectorPath);
            ConnectorRegistration.RegisterConnectors(registry, connectorBuilders, logger, localConnectorPath);
        }
    }

    var names = registry.ListNames();
    logger.LogInformation(
        "Connector registry loaded: {Count} connector(s) [{Names}]",
        names.Count,
        names.Count == 0 ? "-" : string.Join(", ", names));
    return names;
}

static string? ResolveLocalConnectorConfigPath()
{
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "connectors", "workflow_web.connectors.json"),
        Path.Combine(Directory.GetCurrentDirectory(), "connectors", "workflow_web.connectors.json"),
    };

    return candidates.FirstOrDefault(File.Exists);
}

static List<LlmProviderRegistration> ResolveConfiguredProvidersFromSecrets(
    IAevatarSecretsStore secrets,
    IConfiguration config)
{
    const string prefix = "LLMProviders:Providers:";
    var all = secrets.GetAll();
    var names = all.Keys
        .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .Select(key => key[prefix.Length..])
        .Select(rest =>
        {
            var splitIndex = rest.IndexOf(':');
            return splitIndex <= 0 ? string.Empty : rest[..splitIndex];
        })
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    var registrations = new List<LlmProviderRegistration>(names.Count);
    foreach (var rawName in names)
    {
        var name = rawName.Trim();
        var apiKey = secrets.GetApiKey(name);
        if (string.IsNullOrWhiteSpace(apiKey))
            continue;

        all.TryGetValue($"LLMProviders:Providers:{name}:ProviderType", out var providerType);
        all.TryGetValue($"LLMProviders:Providers:{name}:Model", out var model);
        all.TryGetValue($"LLMProviders:Providers:{name}:Endpoint", out var endpoint);

        var defaults = InferProviderDefaults(providerType ?? name, config);
        var resolvedModel = string.IsNullOrWhiteSpace(model) ? defaults.Model : model.Trim();
        var resolvedEndpoint = string.IsNullOrWhiteSpace(endpoint) ? defaults.Endpoint : endpoint.Trim();

        registrations.Add(new LlmProviderRegistration(name, resolvedModel, resolvedEndpoint, apiKey.Trim()));
    }

    return registrations;
}

static LlmProviderRegistration? ResolveFallbackProviderFromEnvironmentAndSecrets(
    IAevatarSecretsStore secrets,
    IConfiguration config)
{
    var deepSeekApiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
    if (!string.IsNullOrWhiteSpace(deepSeekApiKey))
    {
        var defaults = InferProviderDefaults("deepseek", config);
        return new LlmProviderRegistration("deepseek", defaults.Model, defaults.Endpoint, deepSeekApiKey.Trim());
    }

    var openAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (!string.IsNullOrWhiteSpace(openAiApiKey))
    {
        var defaults = InferProviderDefaults("openai", config);
        return new LlmProviderRegistration("openai", defaults.Model, defaults.Endpoint, openAiApiKey.Trim());
    }

    var genericApiKey = Environment.GetEnvironmentVariable("AEVATAR_LLM_API_KEY");
    if (!string.IsNullOrWhiteSpace(genericApiKey))
    {
        var preferredName = secrets.GetDefaultProvider() ?? config["Models:DefaultProvider"] ?? "openai";
        var defaults = InferProviderDefaults(preferredName, config);
        return new LlmProviderRegistration(preferredName.Trim(), defaults.Model, defaults.Endpoint, genericApiKey.Trim());
    }

    var candidates = new List<string>();
    var preferredDefault = secrets.GetDefaultProvider() ?? config["Models:DefaultProvider"];
    if (!string.IsNullOrWhiteSpace(preferredDefault))
        candidates.Add(preferredDefault.Trim());
    candidates.AddRange(["deepseek", "openai", "deepseek-deepseek-chat"]);

    foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (string.IsNullOrWhiteSpace(candidate))
            continue;

        var apiKey = secrets.GetApiKey(candidate);
        if (string.IsNullOrWhiteSpace(apiKey))
            continue;

        var normalizedName = candidate.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            ? "deepseek"
            : candidate.Trim();
        var defaults = InferProviderDefaults(normalizedName, config);
        return new LlmProviderRegistration(normalizedName, defaults.Model, defaults.Endpoint, apiKey.Trim());
    }

    return null;
}

static (string Model, string? Endpoint) InferProviderDefaults(string? providerHint, IConfiguration config)
{
    var isDeepSeek = !string.IsNullOrWhiteSpace(providerHint) &&
        providerHint.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
    if (isDeepSeek)
        return (config["Models:DeepSeekModel"] ?? config["Models:DefaultModel"] ?? "deepseek-chat", "https://api.deepseek.com/v1");

    return (config["Models:OpenAIModel"] ?? config["Models:DefaultModel"] ?? "gpt-4o-mini", null);
}

sealed record PlaygroundWorkflowSaveRequest(string Yaml, string? Filename, bool Overwrite);
sealed record WorkflowYamlSource(string Kind, string DirectoryPath);
sealed record WorkflowFileEntry(string Name, string FilePath, string SourceKind);
sealed record WorkflowListClassification(string Category, string Group, string GroupLabel, int SortOrder);
sealed record LlmProviderRegistration(string Name, string Model, string? Endpoint, string ApiKey);
