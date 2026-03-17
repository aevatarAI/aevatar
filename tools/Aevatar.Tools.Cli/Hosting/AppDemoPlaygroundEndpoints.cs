using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Infrastructure.Workflows;
using Aevatar.Workflow.Sdk;
using Aevatar.Workflow.Sdk.Contracts;
using QueryWorkflowCatalogItem = Aevatar.Workflow.Application.Abstractions.Queries.WorkflowCatalogItem;
using QueryWorkflowCatalogItemDetail = Aevatar.Workflow.Application.Abstractions.Queries.WorkflowCatalogItemDetail;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Tools.Cli.Hosting;

internal static class AppDemoPlaygroundEndpoints
{
    private const int AutoResumeDelayMs = 50;
    internal const string AppConfigOpenRoute = "/api/app/config/open";
    private const int DefaultConfigUiPort = 6677;
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly JsonSerializerOptions CapabilityDocumentJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HashSet<string> LlmLikeStepTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "llm_call",
        "evaluate",
        "reflect",
        "tool_call",
        "human_input",
        "human_approval",
        "wait_signal",
        "connector_call",
    };

    private static readonly IReadOnlyDictionary<string, string[]> CuratedPrimitiveExamples =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["transform"] = ["transform", "pipeline"],
            ["guard"] = ["guard", "pipeline"],
            ["conditional"] = ["conditional"],
            ["switch"] = ["switch"],
            ["assign"] = ["assign", "pipeline"],
            ["retrieve_facts"] = ["retrieve_facts", "pipeline"],
            ["llm_call"] = ["llm_call", "llm_chain"],
            ["parallel"] = ["parallel"],
            ["race"] = ["race"],
            ["map_reduce"] = ["map_reduce", "map_reduce_llm_alias"],
            ["foreach"] = ["foreach"],
            ["evaluate"] = ["evaluate"],
            ["reflect"] = ["reflect"],
            ["cache"] = ["cache"],
            ["human_input"] = ["human_input_manual_triage", "human_input_basic_auto_resume"],
            ["human_approval"] = ["human_approval_release_gate", "mixed_human_approval_wait_signal"],
            ["wait_signal"] = ["wait_signal_manual_success", "mixed_human_approval_wait_signal"],
            ["workflow_call"] = ["workflow_call_multilevel"],
            ["connector_call"] = ["connector_cli_demo", "cli_call_alias"],
            ["emit"] = ["emit_publish_demo"],
            ["tool_call"] = ["tool_call_fallback_demo"],
            ["checkpoint"] = ["delay_checkpoint_demo"],
            ["delay"] = ["delay_checkpoint_demo"],
        };

    private static readonly IReadOnlyDictionary<string, string[]> PrimitiveAliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["parallel"] = ["parallel", "parallel_fanout", "fan_out"],
            ["foreach"] = ["foreach", "for_each", "foreach_llm"],
            ["map_reduce"] = ["map_reduce", "mapreduce", "map_reduce_llm"],
            ["evaluate"] = ["evaluate", "judge"],
            ["race"] = ["race", "select"],
            ["guard"] = ["guard", "assert"],
            ["delay"] = ["delay", "sleep"],
            ["emit"] = ["emit", "publish"],
            ["wait_signal"] = ["wait_signal", "wait"],
            ["connector_call"] = ["connector_call", "bridge_call", "cli_call", "mcp_call", "http_get", "http_post", "http_put", "http_delete"],
            ["vote"] = ["vote", "vote_consensus"],
            ["workflow_call"] = ["workflow_call", "sub_workflow"],
            ["while"] = ["while", "loop"],
        };

    private static readonly IReadOnlyDictionary<string, PrimitiveMetadata> PrimitiveMetadataCatalog =
        new Dictionary<string, PrimitiveMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["transform"] = new(
                "Applies deterministic transformations to the current input or intermediate state, such as uppercase, trim, count, or structural reshaping.",
                [
                    new PrimitiveParameter("op", "Transformation operation to apply.", null, "uppercase, trim, count"),
                ]),
            ["assign"] = new(
                "Writes a value into workflow state so later steps can reuse it as named context.",
                [
                    new PrimitiveParameter("target", "State field or variable name to update.", null, null),
                    new PrimitiveParameter("value", "Literal value or expression to store.", null, null),
                ]),
            ["retrieve_facts"] = new(
                "Looks up deterministic reference facts and injects them into the workflow without calling an LLM.",
                [
                    new PrimitiveParameter("query", "Fact lookup query.", null, null),
                    new PrimitiveParameter("top_k", "Maximum number of facts to return.", "3", null),
                ]),
            ["cache"] = new(
                "Reuses a cached result when available, or computes and stores it on a cache miss.",
                [
                    new PrimitiveParameter("key", "Cache key used to read or write the result.", null, null),
                    new PrimitiveParameter("step", "Child step type to execute on cache miss.", null, null),
                ]),
            ["guard"] = new(
                "Validates a precondition before the workflow continues and can stop execution early on invalid input.",
                [
                    new PrimitiveParameter("check", "Predicate or built-in validation rule.", null, "not_empty"),
                    new PrimitiveParameter("on_fail", "Failure behavior when the guard does not pass.", "fail", "fail, skip"),
                ]),
            ["conditional"] = new(
                "Evaluates a boolean condition and routes execution to explicit true and false branches.",
                [
                    new PrimitiveParameter("condition", "Boolean expression to evaluate.", null, null),
                ]),
            ["switch"] = new(
                "Selects one branch from several named routes and falls back to `_default` when no case matches.",
                [
                    new PrimitiveParameter("expression", "Expression whose result selects the branch.", null, null),
                ]),
            ["while"] = new(
                "Repeats a nested step while a condition stays true or until a max iteration limit is reached.",
                [
                    new PrimitiveParameter("condition", "Loop continuation condition.", null, null),
                    new PrimitiveParameter("max_iterations", "Safety cap for loop iterations.", null, null),
                    new PrimitiveParameter("step", "Nested step type to execute each iteration.", null, null),
                ]),
            ["llm_call"] = new(
                "Sends the current context to an LLM-backed role and streams the generated response into the workflow.",
                [
                    new PrimitiveParameter("prompt", "Prompt template or override for the model call.", null, null),
                ]),
            ["evaluate"] = new(
                "Uses a model as a judge to score, classify, or decide based on explicit criteria.",
                [
                    new PrimitiveParameter("criteria", "Evaluation rubric or scoring criteria.", null, null),
                ]),
            ["reflect"] = new(
                "Runs a second-pass critique or refinement step over earlier output to improve quality before continuing.",
                [
                    new PrimitiveParameter("prompt", "Reflection or critique instruction.", null, null),
                ]),
            ["parallel"] = new(
                "Runs multiple branches concurrently and combines their outputs when all work completes.",
                [
                    new PrimitiveParameter("branches", "Named branch definitions executed in parallel.", null, null),
                ]),
            ["race"] = new(
                "Starts competing branches and keeps the first successful result.",
                [
                    new PrimitiveParameter("branches", "Competing branch definitions.", null, null),
                ]),
            ["map_reduce"] = new(
                "Maps a step across a collection of items, then reduces the aggregated outputs into a final result.",
                [
                    new PrimitiveParameter("map_step_type", "Step type used for each mapped item.", null, null),
                    new PrimitiveParameter("reduce_step_type", "Step type used to combine mapped results.", null, null),
                ]),
            ["foreach"] = new(
                "Iterates over a list and runs the same child step for every item.",
                [
                    new PrimitiveParameter("items", "Collection to iterate over.", null, null),
                    new PrimitiveParameter("sub_step_type", "Step type executed for each item.", null, null),
                ]),
            ["vote"] = new(
                "Aggregates multiple candidate outputs and selects the strongest one through consensus or ranking.",
                [
                    new PrimitiveParameter("strategy", "Vote or consensus strategy.", null, null),
                ]),
            ["human_input"] = new(
                "Pauses the workflow and waits for a person to provide structured input before continuing.",
                [
                    new PrimitiveParameter("prompt", "Question shown to the human participant.", null, null),
                    new PrimitiveParameter("timeout_seconds", "Optional timeout before the step expires.", null, null),
                ]),
            ["human_approval"] = new(
                "Pauses the workflow until a human explicitly approves or rejects the next action.",
                [
                    new PrimitiveParameter("prompt", "Approval request shown to the reviewer.", null, null),
                    new PrimitiveParameter("timeout_seconds", "Optional timeout for the approval gate.", null, null),
                ]),
            ["wait_signal"] = new(
                "Suspends execution until an external signal arrives or a timeout is reached.",
                [
                    new PrimitiveParameter("signal_name", "Signal name that resumes the workflow.", null, null),
                    new PrimitiveParameter("timeout_ms", "Maximum wait time in milliseconds.", null, null),
                ]),
            ["workflow_call"] = new(
                "Calls another workflow YAML as a subworkflow so larger systems can be built from smaller reusable flows.",
                [
                    new PrimitiveParameter("workflow", "Referenced workflow name.", null, null),
                    new PrimitiveParameter("lifecycle", "How the parent waits for the child workflow.", null, "sync, async"),
                ]),
            ["connector_call"] = new(
                "Invokes an external connector, CLI bridge, MCP server, or HTTP capability from within the workflow.",
                [
                    new PrimitiveParameter("connector", "Connector or capability name to invoke.", null, null),
                    new PrimitiveParameter("operation", "Connector-specific operation or method.", null, null),
                ]),
            ["tool_call"] = new(
                "Calls a tool made available to the workflow runtime and returns the tool result to later steps.",
                [
                    new PrimitiveParameter("tool", "Tool name to execute.", null, null),
                ]),
            ["emit"] = new(
                "Publishes an event or message to another part of the system so downstream consumers can react asynchronously.",
                [
                    new PrimitiveParameter("event_name", "Logical event name to publish.", null, null),
                ]),
            ["delay"] = new(
                "Waits for a bounded amount of time before resuming the workflow.",
                [
                    new PrimitiveParameter("duration_ms", "Delay duration in milliseconds.", null, null),
                ]),
            ["checkpoint"] = new(
                "Persists a named checkpoint so long-running flows can recover or audit their intermediate progress.",
                [
                    new PrimitiveParameter("name", "Checkpoint identifier.", null, null),
                ]),
            ["workflow_yaml_validate"] = new(
                "Parses and validates workflow YAML so authoring flows can check generated definitions before saving or running them.",
                [
                    new PrimitiveParameter("yaml", "Workflow YAML content to validate.", null, null),
                ]),
            ["dynamic_workflow"] = new(
                "Creates or reconfigures workflow definitions dynamically at runtime based on generated YAML.",
                [
                    new PrimitiveParameter("workflow_yaml", "Dynamic workflow YAML to apply.", null, null),
                ]),
            ["workflow_loop"] = new(
                "Internal orchestration primitive used by the workflow runtime to advance serialized loop execution.",
                Array.Empty<PrimitiveParameter>()),
        };

    public static void Map(IEndpointRouteBuilder app, bool embeddedWorkflowMode)
    {
        app.MapGet("/api/workflows", (IAevatarWorkflowClient client, IServiceProvider services, CancellationToken ct) =>
            HandleListWorkflows(client, services, embeddedWorkflowMode, ct));
        app.MapGet("/api/workflow-catalog", (IAevatarWorkflowClient client, IServiceProvider services, CancellationToken ct) =>
            HandleListWorkflows(client, services, embeddedWorkflowMode, ct));
        app.MapGet("/api/workflows/{name}", (string name, IAevatarWorkflowClient client, IServiceProvider services, CancellationToken ct) =>
            HandleGetWorkflow(name, client, services, embeddedWorkflowMode, ct));
        app.MapGet("/api/capabilities", (IAevatarWorkflowClient client, IServiceProvider services, CancellationToken ct) =>
            HandleCapabilitiesAsync(client, services, embeddedWorkflowMode, ct));
        app.MapGet("/api/workflows/{name}/run", (string name, string? input, bool? autoResume, HttpContext ctx, IAevatarWorkflowClient client, CancellationToken ct) =>
            HandleRunWorkflowAsync(name, input, autoResume, ctx, client, embeddedWorkflowMode, ct));
        app.MapGet("/api/llm/status", (IServiceProvider services) => HandleLlmStatus(services, embeddedWorkflowMode));
        app.MapGet("/api/primitives", (IAevatarWorkflowClient client, IServiceProvider services, CancellationToken ct) =>
            HandlePrimitivesAsync(client, services, embeddedWorkflowMode, ct));
        app.MapPost("/api/playground/parse", HandlePlaygroundParseAsync);
        app.MapPost("/api/playground/workflows", HandleSavePlaygroundWorkflowAsync);
        app.MapPost(AppConfigOpenRoute, (AppConfigOpenRequest? input, CancellationToken ct) =>
            HandleOpenConfigAsync(input, embeddedWorkflowMode, ct));
    }

    private static async Task<IResult> HandleListWorkflows(
        IAevatarWorkflowClient client,
        IServiceProvider services,
        bool embeddedWorkflowMode,
        CancellationToken ct)
    {
        if (!embeddedWorkflowMode)
        {
            var catalog = await client.GetWorkflowCatalogAsync(ct);
            return Results.Json(catalog);
        }

        var queryCatalog = TryListWorkflowCatalogFromQueryService(services);
        if (queryCatalog != null)
            return Results.Json(queryCatalog.Select(MapCatalogItemDto));

        var localCatalog = BuildWorkflowCatalog();
        return Results.Json(localCatalog.Select(MapCatalogItemDto));
    }

    private static async Task<IResult> HandleGetWorkflow(
        string name,
        IAevatarWorkflowClient client,
        IServiceProvider services,
        bool embeddedWorkflowMode,
        CancellationToken ct)
    {
        if (!embeddedWorkflowMode)
        {
            var detail = await client.GetWorkflowDetailAsync(name, ct);
            return detail == null
                ? Results.NotFound(new { error = $"Workflow '{name}' not found" })
                : Results.Json(detail.Value);
        }

        var queryDetail = TryGetWorkflowDetailFromQueryService(services, name);
        if (queryDetail != null)
            return Results.Json(MapWorkflowDetailDto(queryDetail));

        if (!TryResolveWorkflowFile(name, out var workflowFile))
            return Results.NotFound(new { error = $"Workflow '{name}' not found" });

        var parser = new WorkflowParser();
        var yaml = File.ReadAllText(workflowFile.FilePath);
        var def = parser.Parse(yaml);
        var catalogItem = BuildWorkflowCatalog()
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        var steps = def.Steps.Select(step => new
        {
            id = step.Id,
            type = step.Type,
            targetRole = step.TargetRole,
            parameters = step.Parameters,
            next = step.Next,
            branches = step.Branches,
            children = step.Children?.Select(child => new { id = child.Id, type = child.Type, targetRole = child.TargetRole }).ToList(),
        }).ToList();

        return Results.Json(new
        {
            catalog = catalogItem == null
                ? null
                : new
                {
                    name = catalogItem.Name,
                    description = catalogItem.Description,
                    category = catalogItem.Category,
                    group = catalogItem.Group,
                    groupLabel = catalogItem.GroupLabel,
                    sortOrder = catalogItem.SortOrder,
                    source = catalogItem.Source,
                    sourceLabel = catalogItem.SourceLabel,
                    showInLibrary = catalogItem.ShowInLibrary,
                    isPrimitiveExample = catalogItem.IsPrimitiveExample,
                    primitives = catalogItem.Primitives,
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
            edges = ComputeEdges(def),
        });
    }

    private static async Task HandleRunWorkflowAsync(
        string name,
        string? input,
        bool? autoResume,
        HttpContext ctx,
        IAevatarWorkflowClient client,
        bool embeddedWorkflowMode,
        CancellationToken ct)
    {
        var prompt = string.IsNullOrWhiteSpace(input) ? "Hello, world!" : input.Trim();

        if (!embeddedWorkflowMode)
        {
            await StreamWorkflowRunAsync(
                ctx,
                client,
                new ChatRunRequest
                {
                    Prompt = prompt,
                    Workflow = name,
                },
                autoResume == true,
                ct);
            return;
        }

        if (!TryResolveWorkflowFile(name, out var workflowFile))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsync($"Workflow '{name}' not found", ct);
            return;
        }

        var yaml = await File.ReadAllTextAsync(workflowFile.FilePath, ct);
        await StreamWorkflowRunAsync(
            ctx,
            client,
            new ChatRunRequest
            {
                Prompt = prompt,
                WorkflowYamls = [yaml],
            },
            autoResume == true,
            ct);
    }

    private static async Task StreamWorkflowRunAsync(
        HttpContext ctx,
        IAevatarWorkflowClient client,
        ChatRunRequest request,
        bool shouldAutoResume,
        CancellationToken ct)
    {
        ctx.Response.Headers["Content-Type"] = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["Connection"] = "keep-alive";

        async Task WriteSseAsync(string eventType, object payload, CancellationToken token)
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            await ctx.Response.WriteAsync($"event: {eventType}\ndata: {json}\n\n", token);
            await ctx.Response.Body.FlushAsync(token);
        }

        var messageBuffers = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var actorId = string.Empty;
        var runId = string.Empty;

        try
        {
            await foreach (var evt in client.StartRunStreamAsync(request, ct))
            {
                var frame = evt.Frame;
                var type = frame.Type ?? string.Empty;
                if (string.IsNullOrWhiteSpace(type))
                    continue;

                if (string.Equals(type, WorkflowEventTypes.RunStarted, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(frame.ThreadId))
                        actorId = frame.ThreadId;
                    continue;
                }

                if (string.Equals(type, WorkflowEventTypes.TextMessageStart, StringComparison.Ordinal))
                {
                    var messageId = string.IsNullOrWhiteSpace(frame.MessageId) ? "__default__" : frame.MessageId!;
                    messageBuffers[messageId] = new StringBuilder();
                    continue;
                }

                if (string.Equals(type, WorkflowEventTypes.TextMessageContent, StringComparison.Ordinal))
                {
                    var messageId = string.IsNullOrWhiteSpace(frame.MessageId) ? "__default__" : frame.MessageId!;
                    if (!messageBuffers.TryGetValue(messageId, out var builder))
                    {
                        builder = new StringBuilder();
                        messageBuffers[messageId] = builder;
                    }

                    if (!string.IsNullOrEmpty(frame.Delta))
                        builder.Append(frame.Delta);
                    continue;
                }

                if (string.Equals(type, WorkflowEventTypes.TextMessageEnd, StringComparison.Ordinal))
                {
                    var messageId = string.IsNullOrWhiteSpace(frame.MessageId) ? "__default__" : frame.MessageId!;
                    var content = messageBuffers.TryGetValue(messageId, out var builder)
                        ? builder.ToString()
                        : string.Empty;
                    messageBuffers.Remove(messageId);

                    if (string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(frame.Delta))
                        content = frame.Delta!;
                    if (string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(frame.Message))
                        content = frame.Message!;

                    await WriteSseAsync(
                        "llm.response",
                        new
                        {
                            role = string.IsNullOrWhiteSpace(frame.Role) ? "assistant" : frame.Role,
                            content,
                        },
                        ct);
                    continue;
                }

                if (string.Equals(type, WorkflowEventTypes.Custom, StringComparison.Ordinal))
                {
                    var mapped = MapCustomFrame(frame, ref actorId, ref runId);
                    if (mapped == null)
                        continue;

                    if (!mapped.Data.ContainsKey("actorId") && !string.IsNullOrWhiteSpace(actorId))
                        mapped.Data["actorId"] = actorId;
                    if (!mapped.Data.ContainsKey("runId") && !string.IsNullOrWhiteSpace(runId))
                        mapped.Data["runId"] = runId;

                    await WriteSseAsync(mapped.EventType, mapped.Data, ct);

                    if (shouldAutoResume &&
                        string.Equals(mapped.EventType, "workflow.suspended", StringComparison.Ordinal))
                    {
                        _ = TryAutoResumeAsync(mapped.Data, request.Prompt, client, ct);
                    }

                    continue;
                }

                if (string.Equals(type, WorkflowEventTypes.RunFinished, StringComparison.Ordinal))
                {
                    await WriteSseAsync("workflow.completed", new
                    {
                        runId,
                        success = true,
                        output = ExtractRunOutput(frame.Result),
                    }, ct);
                    continue;
                }

                if (string.Equals(type, WorkflowEventTypes.RunError, StringComparison.Ordinal))
                {
                    await WriteSseAsync("workflow.error", new
                    {
                        runId,
                        error = frame.Message ?? "Workflow run failed.",
                    }, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                await WriteSseAsync("workflow.error", new
                {
                    runId,
                    error = ex.Message,
                }, CancellationToken.None);
            }
        }
    }

    private static IResult HandleLlmStatus(IServiceProvider services, bool embeddedWorkflowMode)
    {
        if (!embeddedWorkflowMode)
        {
            return Results.Json(new
            {
                available = false,
                provider = (string?)null,
                model = (string?)null,
                providers = Array.Empty<string>(),
            });
        }

        var factory = services.GetService<ILLMProviderFactory>();
        if (factory == null)
        {
            return Results.Json(new
            {
                available = false,
                provider = (string?)null,
                model = (string?)null,
                providers = Array.Empty<string>(),
            });
        }

        try
        {
            var providers = factory.GetAvailableProviders();
            if (providers.Count == 0)
            {
                return Results.Json(new
                {
                    available = false,
                    provider = (string?)null,
                    model = (string?)null,
                    providers = Array.Empty<string>(),
                });
            }

            var provider = factory.GetDefault();
            var config = services.GetService<IConfiguration>();
            var model = ResolveDefaultProviderModel(provider.Name, config);
            return Results.Json(new
            {
                available = true,
                provider = provider.Name,
                model,
                providers = providers.ToArray(),
            });
        }
        catch
        {
            return Results.Json(new
            {
                available = false,
                provider = (string?)null,
                model = (string?)null,
                providers = Array.Empty<string>(),
            });
        }
    }

    private static string? ResolveDefaultProviderModel(string providerName, IConfiguration? config)
    {
        try
        {
            var secrets = new AevatarSecretsStore();
            var configuredModel = secrets.Get($"LLMProviders:Providers:{providerName}:Model");
            if (!string.IsNullOrWhiteSpace(configuredModel))
                return configuredModel.Trim();
        }
        catch
        {
            // Best effort fallback.
        }

        return config?["Models:DefaultModel"];
    }

    private static async Task<IResult> HandleCapabilitiesAsync(
        IAevatarWorkflowClient client,
        IServiceProvider services,
        bool embeddedWorkflowMode,
        CancellationToken ct)
    {
        WorkflowCapabilitiesDocument? capabilities = embeddedWorkflowMode
            ? TryGetCapabilitiesFromQueryService(services)
            : await TryLoadCapabilitiesDocumentAsync(client, ct);

        if (capabilities == null && embeddedWorkflowMode)
            capabilities = BuildLocalCapabilitiesDocumentFallback();

        if (capabilities != null)
            return Results.Json(capabilities);

        return Results.Json(new WorkflowCapabilitiesDocument
        {
            SchemaVersion = "capabilities.v1",
        });
    }

    private static async Task<IResult> HandlePrimitivesAsync(
        IAevatarWorkflowClient client,
        IServiceProvider services,
        bool embeddedWorkflowMode,
        CancellationToken ct)
    {
        var workflows = await LoadPlaygroundCatalogAsync(client, services, embeddedWorkflowMode, ct);
        WorkflowCapabilitiesDocument? capabilities = embeddedWorkflowMode
            ? TryGetCapabilitiesFromQueryService(services)
            : await TryLoadCapabilitiesDocumentAsync(client, ct);
        if (capabilities == null && embeddedWorkflowMode)
            capabilities = BuildLocalCapabilitiesDocumentFallback();
        var descriptors = BuildPrimitiveDescriptors(capabilities);

        var primitives = descriptors
            .OrderBy(descriptor => GetPrimitiveCategorySortOrder(descriptor.Category))
            .ThenBy(descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)
            .Select(descriptor => new
            {
                name = descriptor.Name,
                aliases = descriptor.Aliases,
                category = descriptor.Category,
                description = descriptor.Description,
                parameters = descriptor.Parameters,
                exampleWorkflows = BuildPrimitiveExamples(descriptor.Name, workflows),
            })
            .ToArray();
        return Results.Json(primitives);
    }

    private static IWorkflowExecutionQueryApplicationService? TryResolveQueryService(IServiceProvider services)
    {
        try
        {
            return services.GetService<IWorkflowExecutionQueryApplicationService>();
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<QueryWorkflowCatalogItem>? TryListWorkflowCatalogFromQueryService(IServiceProvider services)
    {
        try
        {
            return TryResolveQueryService(services)?.ListWorkflowCatalog();
        }
        catch
        {
            return null;
        }
    }

    private static QueryWorkflowCatalogItemDetail? TryGetWorkflowDetailFromQueryService(
        IServiceProvider services,
        string workflowName)
    {
        try
        {
            return TryResolveQueryService(services)?.GetWorkflowDetail(workflowName);
        }
        catch
        {
            return null;
        }
    }

    private static WorkflowCapabilitiesDocument? TryGetCapabilitiesFromQueryService(IServiceProvider services)
    {
        try
        {
            return TryResolveQueryService(services)?.GetCapabilities();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<QueryWorkflowCatalogItem>?> TryLoadWorkflowCatalogFromClientAsync(
        IAevatarWorkflowClient client,
        CancellationToken ct)
    {
        try
        {
            var payload = await client.GetWorkflowCatalogAsync(ct);
            var items = new List<QueryWorkflowCatalogItem>(payload.Count);
            foreach (var entry in payload)
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                var item = JsonSerializer.Deserialize<QueryWorkflowCatalogItem>(
                    entry.GetRawText(),
                    CapabilityDocumentJsonOptions);
                if (item != null)
                    items.Add(item);
            }

            return items;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<WorkflowCatalogItem>> LoadPlaygroundCatalogAsync(
        IAevatarWorkflowClient client,
        IServiceProvider services,
        bool embeddedWorkflowMode,
        CancellationToken ct)
    {
        if (embeddedWorkflowMode)
        {
            var queryCatalog = TryListWorkflowCatalogFromQueryService(services);
            if (queryCatalog != null)
                return queryCatalog.Select(ToPlaygroundCatalogItem).ToArray();

            return BuildWorkflowCatalog();
        }

        var remoteCatalog = await TryLoadWorkflowCatalogFromClientAsync(client, ct);
        if (remoteCatalog != null && remoteCatalog.Count > 0)
            return remoteCatalog.Select(ToPlaygroundCatalogItem).ToArray();

        return BuildWorkflowCatalog();
    }

    private static WorkflowCatalogItem ToPlaygroundCatalogItem(QueryWorkflowCatalogItem workflow)
    {
        var normalizedSource = string.IsNullOrWhiteSpace(workflow.Source)
            ? "file"
            : workflow.Source;
        var normalizedCategory = string.IsNullOrWhiteSpace(workflow.Category)
            ? "deterministic"
            : workflow.Category;
        var classification = WorkflowLibraryClassifier.Classify(
            workflow.Name,
            normalizedSource,
            normalizedCategory);
        return new WorkflowCatalogItem(
            Name: workflow.Name,
            Description: workflow.Description ?? string.Empty,
            Category: normalizedCategory,
            Group: string.IsNullOrWhiteSpace(workflow.Group) ? classification.Group : workflow.Group,
            GroupLabel: string.IsNullOrWhiteSpace(workflow.GroupLabel) ? classification.GroupLabel : workflow.GroupLabel,
            SortOrder: workflow.SortOrder == 0 ? classification.SortOrder : workflow.SortOrder,
            Source: normalizedSource,
            Primitives: workflow.Primitives?.ToArray() ?? [],
            DefaultInput: "Hello, world!",
            ShowInLibrary: workflow.ShowInLibrary || classification.ShowInLibrary,
            IsPrimitiveExample: workflow.IsPrimitiveExample || classification.IsPrimitiveExample,
            RequiresLlmProvider: workflow.RequiresLlmProvider,
            SourceLabel: string.IsNullOrWhiteSpace(workflow.SourceLabel) ? classification.SourceLabel : workflow.SourceLabel);
    }

    private static object MapCatalogItemDto(WorkflowCatalogItem workflow) => new
    {
        name = workflow.Name,
        description = workflow.Description,
        category = workflow.Category,
        group = workflow.Group,
        groupLabel = workflow.GroupLabel,
        sortOrder = workflow.SortOrder,
        source = workflow.Source,
        primitives = workflow.Primitives,
        defaultInput = workflow.DefaultInput,
        showInLibrary = workflow.ShowInLibrary,
        isPrimitiveExample = workflow.IsPrimitiveExample,
        sourceLabel = workflow.SourceLabel,
    };

    private static object MapCatalogItemDto(QueryWorkflowCatalogItem workflow) =>
        MapCatalogItemDto(ToPlaygroundCatalogItem(workflow));

    private static object MapWorkflowDetailDto(QueryWorkflowCatalogItemDetail detail) => new
    {
        catalog = MapCatalogItemDto(detail.Catalog),
        yaml = detail.Yaml,
        definition = new
        {
            name = detail.Definition.Name,
            description = detail.Definition.Description,
            configuration = new
            {
                closedWorldMode = detail.Definition.ClosedWorldMode,
            },
            roles = detail.Definition.Roles.Select(role => new
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
            }),
            steps = detail.Definition.Steps.Select(step => new
            {
                id = step.Id,
                type = step.Type,
                targetRole = step.TargetRole,
                parameters = step.Parameters,
                next = step.Next,
                branches = step.Branches,
                children = step.Children.Select(child => new
                {
                    id = child.Id,
                    type = child.Type,
                    targetRole = child.TargetRole,
                }),
            }),
        },
        edges = detail.Edges.Select(edge => new
        {
            from = edge.From,
            to = edge.To,
            label = edge.Label,
        }),
    };

    private static WorkflowCapabilitiesDocument BuildLocalCapabilitiesDocumentFallback()
    {
        var descriptors = BuildPrimitiveDescriptors(capabilities: null);
        var workflows = BuildWorkflowCatalog();
        return new WorkflowCapabilitiesDocument
        {
            SchemaVersion = "capabilities.v1",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Primitives = descriptors.Select(descriptor => new WorkflowPrimitiveCapability
            {
                Name = descriptor.Name,
                Aliases = descriptor.Aliases.ToList(),
                Category = descriptor.Category,
                Description = descriptor.Description,
                Parameters = descriptor.Parameters.Select(parameter => new WorkflowPrimitiveParameterCapability
                {
                    Name = parameter.Name,
                    Type = "string",
                    Required = false,
                    Description = parameter.Description,
                    Default = parameter.Default ?? string.Empty,
                    Enum = string.IsNullOrWhiteSpace(parameter.Values)
                        ? []
                        : parameter.Values
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToList(),
                }).ToList(),
            }).ToList(),
            Workflows = workflows.Select(workflow => new WorkflowCapabilityWorkflow
            {
                Name = workflow.Name,
                Description = workflow.Description,
                Source = workflow.Source,
                RequiresLlmProvider = workflow.RequiresLlmProvider,
                Primitives = workflow.Primitives.ToList(),
            }).ToList(),
        };
    }

    private static async Task HandlePlaygroundParseAsync(HttpContext ctx)
    {
        string yaml;
        using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
            yaml = await reader.ReadToEndAsync();

        var validation = ValidatePlaygroundWorkflow(yaml, ctx.RequestServices);
        if (!validation.Valid || validation.Definition == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new
            {
                valid = false,
                error = validation.Error ?? "Invalid YAML",
                errors = validation.Errors,
            });
            return;
        }

        var definition = validation.Definition;
        var steps = definition.Steps.Select(step => new
        {
            id = step.Id,
            type = step.Type,
            targetRole = step.TargetRole,
            parameters = step.Parameters,
            next = step.Next,
            branches = step.Branches,
            children = step.Children?.Select(child => new { id = child.Id, type = child.Type, targetRole = child.TargetRole }).ToList(),
        }).ToList();

        await ctx.Response.WriteAsJsonAsync(new
        {
            valid = true,
            definition = new
            {
                name = definition.Name,
                description = definition.Description,
                configuration = new
                {
                    closedWorldMode = definition.Configuration.ClosedWorldMode,
                },
                roles = definition.Roles.Select(BuildRoleDto),
                steps,
            },
            edges = ComputeEdges(definition),
        });
    }

    private static async Task HandleSavePlaygroundWorkflowAsync(HttpContext ctx)
    {
        PlaygroundWorkflowSaveRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<PlaygroundWorkflowSaveRequest>(
                ctx.Request.Body,
                JsonOptions,
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

        var validation = ValidatePlaygroundWorkflow(request.Yaml, ctx.RequestServices);
        if (!validation.Valid || validation.Definition == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = validation.Error ?? "workflow yaml validation failed",
                errors = validation.Errors,
            });
            return;
        }

        string filename;
        try
        {
            filename = NormalizeWorkflowSaveFilename(request.Filename, validation.Definition.Name);
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
        await File.WriteAllTextAsync(path, content, Utf8NoBom, ctx.RequestAborted);

        await ctx.Response.WriteAsJsonAsync(new
        {
            saved = true,
            filename,
            path,
            workflowName = validation.Definition.Name,
            overwritten = existed,
        });
    }

    internal static async Task<IResult> HandleOpenConfigAsync(
        AppConfigOpenRequest? input,
        bool embeddedWorkflowMode,
        CancellationToken ct)
    {
        var normalizedPort = NormalizeConfigUiPort(input?.Port);
        var fallbackConfigUrl = BuildConfigUiUrl(normalizedPort);
        if (!embeddedWorkflowMode)
        {
            return Results.BadRequest(new
            {
                ok = false,
                code = "APP_CONFIG_OPEN_UNSUPPORTED_MODE",
                message = "Config open workflow is only available in embedded mode.",
                configUrl = fallbackConfigUrl,
            });
        }

        try
        {
            var ensureResult = await ConfigCommandHandler.EnsureUiAsync(
                normalizedPort,
                noBrowser: true,
                ct);

            return Results.Json(new
            {
                ok = true,
                configUrl = ensureResult.Url,
                port = ensureResult.Port,
                started = ensureResult.Started,
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                ok = false,
                code = "APP_CONFIG_OPEN_FAILED",
                message = ex.Message,
                configUrl = fallbackConfigUrl,
            });
        }
        catch (TimeoutException ex)
        {
            return Results.BadRequest(new
            {
                ok = false,
                code = "APP_CONFIG_OPEN_TIMEOUT",
                message = ex.Message,
                configUrl = fallbackConfigUrl,
            });
        }
        catch (Exception ex)
        {
            return Results.Json(
                new
                {
                    ok = false,
                    code = "APP_CONFIG_OPEN_FAILED",
                    message = ex.Message,
                    configUrl = fallbackConfigUrl,
                },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static int NormalizeConfigUiPort(int? inputPort)
    {
        if (inputPort is > 0 and <= 65535)
            return inputPort.Value;
        return DefaultConfigUiPort;
    }

    private static string BuildConfigUiUrl(int port) => $"http://localhost:{port}";

    private static async Task TryAutoResumeAsync(
        IReadOnlyDictionary<string, object?> suspendedData,
        string originalInput,
        IAevatarWorkflowClient client,
        CancellationToken ct)
    {
        var actorId = ReadDictionaryValue(suspendedData, "actorId");
        var runId = ReadDictionaryValue(suspendedData, "runId");
        var stepId = ReadDictionaryValue(suspendedData, "stepId");
        if (string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(runId) ||
            string.IsNullOrWhiteSpace(stepId))
        {
            return;
        }

        try
        {
            await Task.Delay(AutoResumeDelayMs, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var metadata = suspendedData.TryGetValue("metadata", out var metadataValue) &&
                       metadataValue is IReadOnlyDictionary<string, string> m
            ? m
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var suspensionType = ReadDictionaryValue(suspendedData, "suspensionType");
        var prompt = ReadDictionaryValue(suspendedData, "prompt");
        var approved = true;
        var userInput = string.Empty;

        if (string.Equals(suspensionType, "human_approval", StringComparison.OrdinalIgnoreCase))
        {
            var shouldReject = (!string.IsNullOrWhiteSpace(prompt) &&
                                prompt.Contains("AUTO_REJECT", StringComparison.OrdinalIgnoreCase)) ||
                               (metadata.TryGetValue("auto_reject", out var marker) &&
                                string.Equals(marker, "true", StringComparison.OrdinalIgnoreCase));
            approved = !shouldReject;
        }
        else
        {
            var variable = metadata.TryGetValue("variable", out var variableName) &&
                           !string.IsNullOrWhiteSpace(variableName)
                ? variableName.Trim()
                : "user_input";
            var source = (originalInput ?? string.Empty).ReplaceLineEndings(" ").Trim();
            if (source.Length > 80)
                source = source[..80];
            if (string.IsNullOrWhiteSpace(source))
                source = "empty";
            userInput = $"{variable}=AUTO<{source}>";
        }

        try
        {
            await client.ResumeAsync(
                new WorkflowResumeRequest
                {
                    ActorId = actorId,
                    RunId = runId,
                    StepId = stepId,
                    Approved = approved,
                    UserInput = userInput,
                },
                ct);
        }
        catch
        {
            // auto resume best-effort
        }
    }

    private static MappedSseEvent? MapCustomFrame(WorkflowOutputFrame frame, ref string actorId, ref string runId)
    {
        var name = frame.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var value = NormalizeCustomPayload(frame.Value);
        if (string.Equals(name, "aevatar.run.context", StringComparison.Ordinal))
        {
            var contextActor = ReadJsonValue(value, "actorId", "ActorId");
            if (!string.IsNullOrWhiteSpace(contextActor))
                actorId = contextActor;

            return null;
        }

        if (string.Equals(name, "aevatar.step.request", StringComparison.Ordinal))
        {
            var currentRunId = ReadJsonValue(value, "runId", "RunId");
            if (!string.IsNullOrWhiteSpace(currentRunId))
                runId = currentRunId;

            return new MappedSseEvent(
                "step.request",
                new Dictionary<string, object?>
                {
                    ["runId"] = runId,
                    ["stepId"] = ReadJsonValue(value, "stepId", "StepId"),
                    ["stepType"] = ReadJsonValue(value, "stepType", "StepType"),
                    ["input"] = ReadJsonValue(value, "input", "Input"),
                    ["targetRole"] = ReadJsonValue(value, "targetRole", "TargetRole"),
                });
        }

        if (string.Equals(name, "aevatar.step.completed", StringComparison.Ordinal))
        {
            var currentRunId = ReadJsonValue(value, "runId", "RunId");
            if (!string.IsNullOrWhiteSpace(currentRunId))
                runId = currentRunId;

            return new MappedSseEvent(
                "step.completed",
                new Dictionary<string, object?>
                {
                    ["runId"] = runId,
                    ["stepId"] = ReadJsonValue(value, "stepId", "StepId"),
                    ["success"] = ReadJsonBool(value, "success", "Success"),
                    ["output"] = ReadJsonValue(value, "output", "Output"),
                    ["error"] = ReadJsonValue(value, "error", "Error"),
                    ["metadata"] = ReadMetadata(value),
                });
        }

        if (string.Equals(name, "aevatar.human_input.request", StringComparison.Ordinal))
        {
            var currentRunId = ReadJsonValue(value, "runId", "RunId");
            if (!string.IsNullOrWhiteSpace(currentRunId))
                runId = currentRunId;

            return new MappedSseEvent(
                "workflow.suspended",
                new Dictionary<string, object?>
                {
                    ["runId"] = runId,
                    ["stepId"] = ReadJsonValue(value, "stepId", "StepId"),
                    ["suspensionType"] = Coalesce(
                        ReadJsonValue(value, "suspensionType", "SuspensionType"),
                        "human_input"),
                    ["prompt"] = ReadJsonValue(value, "prompt", "Prompt"),
                    ["timeoutSeconds"] = ReadJsonInt(value, "timeoutSeconds", "TimeoutSeconds"),
                    ["metadata"] = ReadMetadata(value),
                });
        }

        if (string.Equals(name, "aevatar.workflow.waiting_signal", StringComparison.Ordinal))
        {
            var currentRunId = ReadJsonValue(value, "runId", "RunId");
            if (!string.IsNullOrWhiteSpace(currentRunId))
                runId = currentRunId;

            return new MappedSseEvent(
                "workflow.waiting_signal",
                new Dictionary<string, object?>
                {
                    ["runId"] = runId,
                    ["stepId"] = ReadJsonValue(value, "stepId", "StepId"),
                    ["signalName"] = ReadJsonValue(value, "signalName", "SignalName"),
                    ["prompt"] = ReadJsonValue(value, "prompt", "Prompt"),
                    ["timeoutMs"] = ReadJsonInt(value, "timeoutMs", "TimeoutMs"),
                });
        }

        if (string.Equals(name, "aevatar.llm.reasoning", StringComparison.Ordinal))
        {
            return new MappedSseEvent(
                "llm.thinking",
                new Dictionary<string, object?>
                {
                    ["role"] = Coalesce(ReadJsonValue(value, "role", "Role"), "assistant"),
                    ["content"] = ReadJsonValue(value, "delta", "Delta"),
                });
        }

        return null;
    }

    private static JsonElement NormalizeCustomPayload(JsonElement? payload)
    {
        if (payload is not { } raw)
            return EmptyObjectElement();

        var value = raw;
        if (value.ValueKind == JsonValueKind.Object)
            return value;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        return doc.RootElement.Clone();
                }
                catch
                {
                    return EmptyObjectElement();
                }
            }
        }

        return EmptyObjectElement();
    }

    private static JsonElement EmptyObjectElement()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private static string ReadJsonValue(JsonElement element, string camelKey, string pascalKey)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return string.Empty;

        if (element.TryGetProperty(camelKey, out var value) ||
            element.TryGetProperty(pascalKey, out value))
        {
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? string.Empty;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean() ? "true" : "false";
            if (value.ValueKind == JsonValueKind.Number)
                return value.GetRawText();
            if (value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined)
                return value.GetRawText();
        }

        return string.Empty;
    }

    private static bool ReadJsonBool(JsonElement element, string camelKey, string pascalKey)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (element.TryGetProperty(camelKey, out var value) ||
            element.TryGetProperty(pascalKey, out value))
        {
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();
            if (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return false;
    }

    private static int ReadJsonInt(JsonElement element, string camelKey, string pascalKey)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return 0;

        if (element.TryGetProperty(camelKey, out var value) ||
            element.TryGetProperty(pascalKey, out value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    private static IReadOnlyDictionary<string, string> ReadMetadata(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!element.TryGetProperty("metadata", out var metadataElement) &&
            !element.TryGetProperty("Metadata", out metadataElement))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        if (metadataElement.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in metadataElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                result[property.Name] = property.Value.GetString() ?? string.Empty;
            else if (property.Value.ValueKind != JsonValueKind.Null &&
                     property.Value.ValueKind != JsonValueKind.Undefined)
            {
                result[property.Name] = property.Value.GetRawText();
            }
        }

        return result;
    }

    private static string ExtractRunOutput(JsonElement? result)
    {
        if (result is not { } raw)
            return string.Empty;

        var value = raw;
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? string.Empty;

        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("output", out var output))
        {
            return output.ValueKind == JsonValueKind.String
                ? output.GetString() ?? string.Empty
                : output.GetRawText();
        }

        return value.GetRawText();
    }

    private static IReadOnlyDictionary<string, WorkflowFileEntry> DiscoverWorkflowFiles()
    {
        var files = new Dictionary<string, WorkflowFileEntry>(StringComparer.OrdinalIgnoreCase);
        var parser = new WorkflowParser();
        foreach (var source in BuildWorkflowSources())
        {
            if (!Directory.Exists(source.DirectoryPath))
                continue;

            foreach (var file in Directory.EnumerateFiles(source.DirectoryPath, "*.*")
                         .Where(path => path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                                     || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name) || files.ContainsKey(name))
                    continue;

                if (!CanParseWorkflowFile(file, parser))
                    continue;

                files[name] = new WorkflowFileEntry(name, file, source.Kind);
            }
        }

        return files;
    }

    private static bool TryResolveWorkflowFile(string workflowName, out WorkflowFileEntry workflowFile) =>
        DiscoverWorkflowFiles().TryGetValue(workflowName, out workflowFile!);

    private static IReadOnlyList<WorkflowCatalogItem> BuildWorkflowCatalog()
    {
        var parser = new WorkflowParser();
        var items = new List<WorkflowCatalogItem>();

        foreach (var workflowFile in DiscoverWorkflowFiles().Values
                     .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var yaml = File.ReadAllText(workflowFile.FilePath);
                var definition = parser.Parse(yaml);
                var primitives = definition.Steps
                    .Select(step => WorkflowPrimitiveCatalog.ToCanonicalType(step.Type))
                    .Where(type => !string.IsNullOrWhiteSpace(type))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var category = primitives.Any(p => LlmLikeStepTypes.Contains(p)) ? "llm" : "deterministic";
                var requiresLlmProvider = WorkflowLlmRuntimePolicy.RequiresLlmProvider(definition);
                var classification = ClassifyWorkflowForLibrary(
                    workflowFile.Name,
                    workflowFile.SourceKind,
                    category);

                items.Add(new WorkflowCatalogItem(
                    Name: workflowFile.Name,
                    Description: definition.Description ?? string.Empty,
                    Category: category,
                    Group: classification.Group,
                    GroupLabel: classification.GroupLabel,
                    SortOrder: classification.SortOrder,
                    Source: workflowFile.SourceKind,
                    Primitives: primitives,
                    DefaultInput: "Hello, world!",
                    ShowInLibrary: classification.ShowInLibrary,
                    IsPrimitiveExample: classification.IsPrimitiveExample,
                    RequiresLlmProvider: requiresLlmProvider,
                    SourceLabel: classification.SourceLabel));
            }
            catch
            {
                // Ignore legacy/invalid definitions in list view and fall back to valid sources.
            }
        }

        return items;
    }

    private static IReadOnlyList<WorkflowYamlSource> BuildWorkflowSources()
    {
        var sources = new List<WorkflowYamlSource>();
        void AddIfExists(string kind, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return;

            if (sources.Any(source =>
                    string.Equals(source.DirectoryPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            sources.Add(new WorkflowYamlSource(kind, path));
        }

        AddIfExists("app", Path.Combine(AppContext.BaseDirectory, "workflows"));
        AddIfExists("app", Path.Combine(AevatarPaths.RepoRoot, "tools", "Aevatar.Tools.Cli", "workflows"));
        AddIfExists("app", Path.Combine(Environment.CurrentDirectory, "tools", "Aevatar.Tools.Cli", "workflows"));
        AddIfExists("demo", Path.Combine(AevatarPaths.RepoRoot, "demos", "Aevatar.Demos.Workflow", "workflows"));
        AddIfExists("turing", Path.Combine(AevatarPaths.RepoRoot, "workflows", "turing-completeness"));
        AddIfExists("repo", AevatarPaths.RepoRootWorkflows);
        AddIfExists("home", AevatarPaths.Workflows);
        AddIfExists("cwd", Path.Combine(Directory.GetCurrentDirectory(), "workflows"));
        return sources;
    }

    private static bool CanParseWorkflowFile(string filePath, WorkflowParser parser)
    {
        try
        {
            var yaml = File.ReadAllText(filePath);
            _ = parser.Parse(yaml);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static WorkflowLibraryClassification ClassifyWorkflowForLibrary(
        string workflowName,
        string sourceKind,
        string category) =>
        WorkflowLibraryClassifier.Classify(workflowName, sourceKind, category);

    private static object[] BuildPrimitiveExamples(
        string primitiveName,
        IReadOnlyList<WorkflowCatalogItem> workflows)
    {
        if (!CuratedPrimitiveExamples.TryGetValue(primitiveName, out var curatedWorkflowNames))
            return Array.Empty<object>();

        var workflowLookup = workflows.ToDictionary(workflow => workflow.Name, StringComparer.OrdinalIgnoreCase);
        return curatedWorkflowNames
            .Where(name => workflowLookup.ContainsKey(name))
            .Select(name => workflowLookup[name])
            .Select(workflow => (object)new
            {
                name = workflow.Name,
                description = workflow.Description,
                kindLabel = workflow.IsPrimitiveExample ? "Mini Example" : "Workflow",
            })
            .ToArray();
    }

    private static async Task<WorkflowCapabilitiesDocument?> TryLoadCapabilitiesDocumentAsync(
        IAevatarWorkflowClient client,
        CancellationToken ct)
    {
        try
        {
            var payload = await client.GetCapabilitiesAsync(ct);
            if (payload is not { ValueKind: JsonValueKind.Object } documentJson)
                return null;

            var document = JsonSerializer.Deserialize<WorkflowCapabilitiesDocument>(
                documentJson.GetRawText(),
                CapabilityDocumentJsonOptions);
            if (document == null)
                return null;

            if (string.IsNullOrWhiteSpace(document.SchemaVersion))
                document.SchemaVersion = "capabilities.v1";
            return document;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<PrimitiveDescriptor> BuildPrimitiveDescriptors(
        WorkflowCapabilitiesDocument? capabilities)
    {
        if (capabilities?.Primitives is not { Count: > 0 })
        {
            return WorkflowPrimitiveCatalog.BuiltInCanonicalTypes
                .Select(ResolvePrimitiveDescriptor)
                .ToArray();
        }

        var connectorNames = JoinCapabilityValues(capabilities.Connectors.Select(connector => connector.Name));
        var connectorOperations = JoinCapabilityValues(
            capabilities.Connectors.SelectMany(connector => connector.AllowedOperations));
        var resolved = new Dictionary<string, PrimitiveDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var primitive in capabilities.Primitives)
        {
            if (string.IsNullOrWhiteSpace(primitive.Name))
                continue;

            var descriptor = ResolvePrimitiveDescriptor(primitive, connectorNames, connectorOperations);
            resolved[descriptor.Name] = descriptor;
        }

        foreach (var builtInPrimitive in WorkflowPrimitiveCatalog.BuiltInCanonicalTypes)
        {
            var canonical = WorkflowPrimitiveCatalog.ToCanonicalType(builtInPrimitive);
            if (!resolved.ContainsKey(canonical))
                resolved[canonical] = ResolvePrimitiveDescriptor(canonical);
        }

        return resolved.Values.ToArray();
    }

    private static PrimitiveDescriptor ResolvePrimitiveDescriptor(
        WorkflowPrimitiveCapability capability,
        string connectorNames,
        string connectorOperations)
    {
        var canonicalName = WorkflowPrimitiveCatalog.ToCanonicalType(capability.Name);
        var fallback = ResolvePrimitiveDescriptor(canonicalName);
        IEnumerable<string> aliases = capability.Aliases is { Count: > 0 }
            ? capability.Aliases
            : fallback.Aliases;
        var normalizedAliases = aliases
            .Prepend(canonicalName)
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var parameters = capability.Parameters is { Count: > 0 }
            ? capability.Parameters
                .Select(parameter => new PrimitiveParameter(
                    parameter.Name,
                    parameter.Description,
                    string.IsNullOrWhiteSpace(parameter.Default) ? null : parameter.Default,
                    parameter.Enum is { Count: > 0 } ? string.Join(", ", parameter.Enum) : null))
                .ToList()
            : fallback.Parameters.ToList();

        if (string.Equals(canonicalName, "connector_call", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(canonicalName, "secure_connector_call", StringComparison.OrdinalIgnoreCase))
        {
            EnsurePrimitiveParameter(
                parameters,
                new PrimitiveParameter(
                    "connector",
                    "Connector or capability name to invoke.",
                    null,
                    connectorNames));
            EnsurePrimitiveParameter(
                parameters,
                new PrimitiveParameter(
                    "operation",
                    "Connector-specific operation or method.",
                    null,
                    connectorOperations));
        }

        return new PrimitiveDescriptor(
            Name: canonicalName,
            Aliases: normalizedAliases,
            Category: string.IsNullOrWhiteSpace(capability.Category)
                ? fallback.Category
                : capability.Category,
            Description: string.IsNullOrWhiteSpace(capability.Description)
                ? fallback.Description
                : capability.Description,
            Parameters: parameters.ToArray());
    }

    private static string JoinCapabilityValues(IEnumerable<string> values)
    {
        var normalized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalized.Length == 0 ? string.Empty : string.Join(", ", normalized);
    }

    private static void EnsurePrimitiveParameter(
        ICollection<PrimitiveParameter> parameters,
        PrimitiveParameter parameter)
    {
        if (parameters.Any(existing => string.Equals(existing.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)))
            return;

        parameters.Add(parameter);
    }

    internal static PrimitiveDescriptor ResolvePrimitiveDescriptor(string primitiveName)
    {
        var canonicalName = WorkflowPrimitiveCatalog.ToCanonicalType(primitiveName);
        var aliases = PrimitiveAliases.TryGetValue(canonicalName, out var aliasList)
            ? aliasList
            : [canonicalName];

        var normalizedAliases = aliases
            .Prepend(canonicalName)
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var metadata = PrimitiveMetadataCatalog.TryGetValue(canonicalName, out var entry)
            ? entry
            : new PrimitiveMetadata(
                $"Core workflow primitive `{canonicalName}` used by the runtime.",
                Array.Empty<PrimitiveParameter>());

        return new PrimitiveDescriptor(
            Name: canonicalName,
            Aliases: normalizedAliases,
            Category: InferPrimitiveCategory(canonicalName),
            Description: metadata.Description,
            Parameters: metadata.Parameters.ToArray());
    }

    private static string InferPrimitiveCategory(string name) =>
        name switch
        {
            "transform" or "assign" or "retrieve_facts" or "cache" => "data",
            "guard" or "conditional" or "switch" or "while" or "delay" or "wait_signal" or "checkpoint" => "control",
            "foreach" or "parallel" or "race" or "map_reduce" or "workflow_call" or "vote" => "composition",
            "llm_call" or "tool_call" or "evaluate" or "reflect" => "ai",
            "connector_call" or "emit" => "integration",
            "human_input" or "human_approval" => "human",
            _ => "general",
        };

    private static int GetPrimitiveCategorySortOrder(string category) =>
        category switch
        {
            "data" => 0,
            "control" => 1,
            "composition" => 2,
            "ai" => 3,
            "human" => 4,
            "integration" => 5,
            _ => 6,
        };

    private static List<object> ComputeEdges(WorkflowDefinition definition)
    {
        var edges = new List<object>();
        for (var i = 0; i < definition.Steps.Count; i++)
        {
            var step = definition.Steps[i];
            if (step.Branches is { Count: > 0 })
            {
                foreach (var (label, targetId) in step.Branches)
                {
                    if (definition.GetStep(targetId) != null)
                        edges.Add(new { from = step.Id, to = targetId, label });
                }
            }
            else if (!string.IsNullOrWhiteSpace(step.Next))
            {
                if (definition.GetStep(step.Next) != null)
                    edges.Add(new { from = step.Id, to = step.Next });
            }
            else if (i + 1 < definition.Steps.Count)
            {
                edges.Add(new { from = step.Id, to = definition.Steps[i + 1].Id });
            }

            if (step.Children is { Count: > 0 })
            {
                foreach (var child in step.Children)
                    edges.Add(new { from = step.Id, to = child.Id, label = "child" });
            }
        }

        return edges;
    }

    internal static PlaygroundWorkflowValidationResult ValidatePlaygroundWorkflow(
        string yaml,
        IServiceProvider services)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new PlaygroundWorkflowValidationResult(
                false,
                null,
                ["Empty YAML"],
                "Empty YAML");
        }

        try
        {
            var parser = new WorkflowParser();
            var definition = parser.Parse(yaml);
            var errors = ValidateWorkflowDefinitionForRuntime(definition, services);
            if (errors.Count > 0)
            {
                return new PlaygroundWorkflowValidationResult(
                    false,
                    definition,
                    errors,
                    string.Join("; ", errors));
            }

            return new PlaygroundWorkflowValidationResult(
                true,
                definition,
                Array.Empty<string>(),
                null);
        }
        catch (Exception ex)
        {
            return new PlaygroundWorkflowValidationResult(
                false,
                null,
                [ex.Message],
                ex.Message);
        }
    }

    internal static List<string> ValidateWorkflowDefinitionForRuntime(
        WorkflowDefinition definition,
        IServiceProvider services)
    {
        var modulePacks = services.GetServices<IWorkflowModulePack>().ToList();
        var knownStepTypes = modulePacks.Count > 0
            ? WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(
                modulePacks.SelectMany(pack => pack.Modules).SelectMany(module => module.Names))
            : WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(
                WorkflowPrimitiveCatalog.BuiltInCanonicalTypes);

        var moduleFactory = services.GetService<IEventModuleFactory<IWorkflowExecutionContext>>();
        if (moduleFactory != null)
        {
            foreach (var stepType in EnumerateReferencedStepTypes(definition.Steps))
            {
                var canonical = WorkflowPrimitiveCatalog.ToCanonicalType(stepType);
                if (string.IsNullOrWhiteSpace(canonical) || knownStepTypes.Contains(canonical))
                    continue;

                if (moduleFactory.TryCreate(canonical, out _))
                    knownStepTypes.Add(canonical);
            }
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

    internal static string NormalizeWorkflowSaveFilename(string? requestedFilename, string workflowName)
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

    internal static string NormalizeWorkflowContentForSave(string yaml) =>
        (yaml ?? string.Empty).Trim() + Environment.NewLine;

    private static IEnumerable<string> EnumerateReferencedStepTypes(IEnumerable<StepDefinition> steps)
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

    private static object BuildRoleDto(RoleDefinition role) => new
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

    private static string Coalesce(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string ReadDictionaryValue(IReadOnlyDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value == null)
            return string.Empty;
        return value is string text ? text : value.ToString() ?? string.Empty;
    }

    private sealed record WorkflowYamlSource(string Kind, string DirectoryPath);
    private sealed record WorkflowFileEntry(string Name, string FilePath, string SourceKind);
    internal sealed record PrimitiveDescriptor(
        string Name,
        string[] Aliases,
        string Category,
        string Description,
        PrimitiveParameter[] Parameters);
    private sealed record PrimitiveMetadata(
        string Description,
        IReadOnlyList<PrimitiveParameter> Parameters);
    internal sealed record PrimitiveParameter(
        string Name,
        string Description,
        string? Default,
        string? Values);
    private sealed record WorkflowCatalogItem(
        string Name,
        string Description,
        string Category,
        string Group,
        string GroupLabel,
        int SortOrder,
        string Source,
        string[] Primitives,
        string DefaultInput,
        bool ShowInLibrary,
        bool IsPrimitiveExample,
        bool RequiresLlmProvider,
        string SourceLabel);
    private sealed record PlaygroundWorkflowSaveRequest(string Yaml, string? Filename, bool Overwrite);
    internal sealed record AppConfigOpenRequest(int? Port);
    internal sealed record PlaygroundWorkflowValidationResult(
        bool Valid,
        WorkflowDefinition? Definition,
        IReadOnlyList<string> Errors,
        string? Error);
    private sealed record MappedSseEvent(string EventType, Dictionary<string, object?> Data);
}
