using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Configuration;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Sdk;
using Aevatar.Workflow.Sdk.Contracts;
using ChatMessage = Aevatar.AI.Abstractions.LLMProviders.ChatMessage;

namespace Aevatar.Tools.Cli.Hosting;

internal static class AppDemoPlaygroundEndpoints
{
    private const int AutoResumeDelayMs = 50;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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
        "openclaw_call",
    };

    public static void Map(IEndpointRouteBuilder app, bool embeddedWorkflowMode)
    {
        app.MapGet("/api/workflows", HandleListWorkflows);
        app.MapGet("/api/workflows/{name}", HandleGetWorkflow);
        app.MapGet("/api/workflows/{name}/run", HandleRunWorkflowAsync);
        app.MapGet("/api/llm/status", (IServiceProvider services) => HandleLlmStatus(services, embeddedWorkflowMode));
        app.MapGet("/api/primitives", HandlePrimitives);
        app.MapPost("/api/playground/parse", HandlePlaygroundParseAsync);
        app.MapPost("/api/playground/chat", (HttpContext ctx, CancellationToken ct) =>
            HandlePlaygroundChatAsync(ctx, embeddedWorkflowMode, ct));
    }

    private static IResult HandleListWorkflows()
    {
        var parser = new WorkflowParser();
        var workflows = new List<object>();
        foreach (var workflowFile in DiscoverWorkflowFiles().Values
                     .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var sortOrder = TryParseWorkflowIndex(workflowFile.Name) ?? 10_000;
            var (group, groupLabel) = InferGroup(workflowFile.SourceKind);

            try
            {
                var yaml = File.ReadAllText(workflowFile.FilePath);
                var def = parser.Parse(yaml);
                var primitives = def.Steps
                    .Select(step => WorkflowPrimitiveCatalog.ToCanonicalType(step.Type))
                    .Where(type => !string.IsNullOrWhiteSpace(type))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var category = primitives.Any(p => LlmLikeStepTypes.Contains(p)) ? "llm" : "deterministic";

                workflows.Add(new
                {
                    name = workflowFile.Name,
                    description = def.Description,
                    category,
                    group,
                    groupLabel,
                    sortOrder,
                    primitives,
                    defaultInput = "Hello, world!",
                });
            }
            catch
            {
                // Ignore legacy/invalid definitions in list view and fall back to valid sources.
                continue;
            }
        }

        return Results.Json(workflows);
    }

    private static IResult HandleGetWorkflow(string name)
    {
        if (!TryResolveWorkflowFile(name, out var workflowFile))
            return Results.NotFound(new { error = $"Workflow '{name}' not found" });

        var parser = new WorkflowParser();
        var yaml = File.ReadAllText(workflowFile.FilePath);
        var def = parser.Parse(yaml);
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
        CancellationToken ct)
    {
        if (!TryResolveWorkflowFile(name, out var workflowFile))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsync($"Workflow '{name}' not found", ct);
            return;
        }

        var yaml = await File.ReadAllTextAsync(workflowFile.FilePath, ct);
        var prompt = string.IsNullOrWhiteSpace(input) ? "Hello, world!" : input.Trim();

        ctx.Response.Headers["Content-Type"] = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["Connection"] = "keep-alive";

        async Task WriteSseAsync(string eventType, object payload, CancellationToken token)
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            await ctx.Response.WriteAsync($"event: {eventType}\ndata: {json}\n\n", token);
            await ctx.Response.Body.FlushAsync(token);
        }

        var shouldAutoResume = autoResume == true;
        var messageBuffers = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var actorId = string.Empty;
        var runId = string.Empty;

        try
        {
            await foreach (var evt in client.StartRunStreamAsync(
                               new ChatRunRequest
                               {
                                   Prompt = prompt,
                                   WorkflowYamls = [yaml],
                               },
                               ct))
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
                        _ = TryAutoResumeAsync(mapped.Data, prompt, client, ct);
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
            var model = config?["Models:DefaultModel"];
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

    private static IResult HandlePrimitives()
    {
        var primitives = WorkflowPrimitiveCatalog.BuiltInCanonicalTypes
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new
            {
                name,
                aliases = new[] { name },
                category = InferPrimitiveCategory(name),
                description = string.Empty,
                parameters = Array.Empty<object>(),
            })
            .ToArray();
        return Results.Json(primitives);
    }

    private static async Task HandlePlaygroundParseAsync(HttpContext ctx)
    {
        string yaml;
        using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
            yaml = await reader.ReadToEndAsync();

        if (string.IsNullOrWhiteSpace(yaml))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { valid = false, error = "Empty YAML" });
            return;
        }

        try
        {
            var parser = new WorkflowParser();
            var def = parser.Parse(yaml);
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
                    roles = def.Roles.Select(BuildRoleDto),
                    steps,
                },
                edges = ComputeEdges(def),
            });
        }
        catch (Exception ex)
        {
            await ctx.Response.WriteAsJsonAsync(new { valid = false, error = ex.Message });
        }
    }

    private static async Task HandlePlaygroundChatAsync(HttpContext ctx, bool embeddedWorkflowMode, CancellationToken ct)
    {
        if (!embeddedWorkflowMode)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("Playground chat requires embedded workflow mode.", ct);
            return;
        }

        var request = await JsonSerializer.DeserializeAsync<PlaygroundChatRequest>(ctx.Request.Body, JsonOptions, ct);
        if (request?.Messages is not { Count: > 0 })
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("messages required", ct);
            return;
        }

        var factory = ctx.RequestServices.GetService<ILLMProviderFactory>();
        if (factory == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("LLM provider not configured", ct);
            return;
        }

        ILLMProvider provider;
        IReadOnlyList<string> availableProviders;
        try
        {
            provider = factory.GetDefault();
            availableProviders = factory.GetAvailableProviders();
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync($"LLM provider unavailable: {ex.Message}", ct);
            return;
        }

        var llmMessages = new List<ChatMessage>
        {
            ChatMessage.System(BuildPlaygroundSystemPrompt(availableProviders, provider.Name)),
        };
        foreach (var message in request.Messages.Where(m => !string.IsNullOrWhiteSpace(m.Content)))
        {
            llmMessages.Add(new ChatMessage
            {
                Role = string.IsNullOrWhiteSpace(message.Role) ? "user" : message.Role,
                Content = message.Content.Trim(),
            });
        }

        ctx.Response.Headers["Content-Type"] = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["Connection"] = "keep-alive";

        try
        {
            await foreach (var chunk in provider.ChatStreamAsync(new LLMRequest
                           {
                               Messages = llmMessages,
                               Temperature = 0.3,
                           }, ct))
            {
                if (string.IsNullOrEmpty(chunk.DeltaContent))
                    continue;

                var json = JsonSerializer.Serialize(new { delta = chunk.DeltaContent }, JsonOptions);
                await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                var json = JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
                await ctx.Response.WriteAsync($"data: {json}\n\n", CancellationToken.None);
                await ctx.Response.Body.FlushAsync(CancellationToken.None);
            }
        }

        if (!ct.IsCancellationRequested)
        {
            await ctx.Response.WriteAsync("data: [DONE]\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }

    private static string BuildPlaygroundSystemPrompt(IReadOnlyList<string> availableProviders, string defaultProvider)
    {
        var providerList = availableProviders.Count == 0 ? "<none>" : string.Join(", ", availableProviders);
        var defaultLabel = string.IsNullOrWhiteSpace(defaultProvider) ? "not-set" : defaultProvider.Trim();

        return $"""
You are an expert Aevatar workflow author.
Return valid workflow YAML only in a fenced ```yaml block.

Rules:
- Use snake_case fields.
- Always provide: name, description, steps.
- Add roles when using llm_call/evaluate/reflect.
- Keep parameter values as strings.
- Make ids stable and unique.

Runtime providers:
- available: {providerList}
- default: {defaultLabel}
- Prefer omitting provider/model unless user explicitly asks.
""";
    }

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

            foreach (var file in Directory.GetFiles(source.DirectoryPath, "*.yaml")
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

    private static (string Group, string GroupLabel) InferGroup(string sourceKind) =>
        sourceKind switch
        {
            "home" => ("home-workflows", "Home Workflows"),
            "demo" => ("demo-workflows", "Demo Workflows"),
            "turing" => ("turing-completeness", "Turing Completeness"),
            "repo" => ("repo-workflows", "Repo Workflows"),
            _ => ("other-workflows", "Other Workflows"),
        };

    private static int? TryParseWorkflowIndex(string workflowName)
    {
        if (string.IsNullOrWhiteSpace(workflowName))
            return null;

        var span = workflowName.AsSpan().Trim();
        var index = 0;
        while (index < span.Length && char.IsDigit(span[index]))
            index++;

        if (index == 0 || index >= span.Length || span[index] != '_')
            return null;

        return int.TryParse(span[..index], out var value) ? value : null;
    }

    private static string InferPrimitiveCategory(string name) =>
        name switch
        {
            "transform" or "assign" or "retrieve_facts" or "cache" => "data",
            "guard" or "conditional" or "switch" or "while" or "delay" or "wait_signal" or "checkpoint" => "control",
            "foreach" or "parallel" or "race" or "map_reduce" or "workflow_call" or "vote" => "composition",
            "llm_call" or "tool_call" or "evaluate" or "reflect" => "ai",
            "connector_call" or "emit" or "openclaw_call" => "integration",
            "human_input" or "human_approval" => "human",
            _ => "general",
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
    private sealed record PlaygroundChatMessage(string Role, string Content);
    private sealed record PlaygroundChatRequest(List<PlaygroundChatMessage> Messages);
    private sealed record MappedSseEvent(string EventType, Dictionary<string, object?> Data);
}
