using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Scripts.Contracts;
using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Aevatar.Studio.Infrastructure.Storage;
using Aevatar.Scripting.Hosting.CapabilityApi;
using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json;
namespace Aevatar.Studio.Hosting.Endpoints;

internal static class StudioEndpoints
{
    public static void Map(IEndpointRouteBuilder app, bool embeddedWorkflowMode)
    {
        app.MapGet("/api/auth/me", HandleGetAuthMe);
        app.MapGet("/api/app/context", (HttpContext http, IServiceProvider services) =>
            HandleGetContext(http, services, embeddedWorkflowMode));
        app.MapPost("/api/app/workflow-generator", (
            HttpContext http,
            AppWorkflowGenerateRequest request,
            IServiceProvider services,
            CancellationToken ct) =>
            HandleGenerateWorkflowAsync(http, request, services, embeddedWorkflowMode, ct));
        app.MapPost("/api/app/scripts/generator", (
            HttpContext http,
            AppScriptGenerateRequest request,
            IServiceProvider services,
            CancellationToken ct) =>
            HandleGenerateScriptAsync(http, request, services, embeddedWorkflowMode, ct));
        app.MapPost("/api/app/scripts/validate", (
            AppScriptValidateRequest request,
            IServiceProvider services) =>
            HandleValidateScript(request, services));
        app.MapGet("/api/app/scripts", (
            HttpContext http,
            IServiceProvider services,
            bool includeSource,
            CancellationToken ct) =>
            HandleListScopedScriptsAsync(http, services, includeSource, ct));
        app.MapGet("/api/app/scripts/{scriptId}", (
            HttpContext http,
            string scriptId,
            IServiceProvider services,
            CancellationToken ct) =>
            HandleGetScopedScriptAsync(http, scriptId, services, ct));
        app.MapGet("/api/app/scripts/{scriptId}/catalog", (
            HttpContext http,
            string scriptId,
            IServiceProvider services,
            CancellationToken ct) =>
            HandleGetScopedScriptCatalogAsync(http, scriptId, services, ct));
        app.MapPost("/api/app/scripts", (
            HttpContext http,
            AppScopeScriptSaveRequest request,
            IServiceProvider services,
            CancellationToken ct) =>
            HandleSaveScopedScriptAsync(http, request, services, ct));
        app.MapGet("/api/app/scripts/runtimes", (
            int take,
            IServiceProvider services,
            CancellationToken ct) =>
            HandleListAppScriptRuntimesAsync(take, services, ct));
        app.MapPost("/api/app/scripts/evolutions/proposals", (
            HttpContext http,
            AppScopeScriptEvolutionRequest request,
            IServiceProvider services,
            CancellationToken ct) =>
            HandleProposeScopedScriptEvolutionAsync(http, request, services, ct));
        app.MapGet("/api/app/scripts/evolutions/{proposalId}", (
            string proposalId,
            IServiceProvider services,
            CancellationToken ct) =>
            HandleGetAppScriptEvolutionDecisionAsync(proposalId, services, ct));
        app.MapGet("/api/app/scripts/runtimes/{actorId}/readmodel", (
            string actorId,
            IServiceProvider services,
            CancellationToken ct) =>
            HandleGetAppScriptReadModelAsync(actorId, services, ct));

        app.MapPost("/api/app/scripts/draft-run", (
            HttpContext http,
            AppScriptDraftRunRequest request,
            IServiceProvider services,
            CancellationToken ct) =>
            HandleRunDraftScriptAsync(http, request, services, embeddedWorkflowMode, ct));
    }

    internal static string NormalizeStudioDocumentId(string? rawValue, string fallbackPrefix)
    {
        var trimmed = string.IsNullOrWhiteSpace(rawValue)
            ? string.Empty
            : rawValue.Trim().ToLowerInvariant();
        var builder = new StringBuilder(trimmed.Length);
        var lastWasDash = false;
        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasDash = false;
                continue;
            }

            if (ch is '-' or '_' or ' ' or '.')
            {
                if (lastWasDash)
                    continue;

                builder.Append('-');
                lastWasDash = true;
            }
        }

        var normalized = builder
            .ToString()
            .Trim('-');
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        var prefix = string.IsNullOrWhiteSpace(fallbackPrefix)
            ? "studio"
            : fallbackPrefix.Trim().ToLowerInvariant();
        return $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
    }

    private static IResult HandleGetAuthMe(HttpContext http)
    {
        var user = http.User;
        var isAuthenticated = user?.Identity?.IsAuthenticated == true;
        var scopeResolver = http.RequestServices.GetService<IAppScopeResolver>();
        var scope = scopeResolver?.Resolve(http);

        return Results.Json(new
        {
            enabled = true,
            authenticated = isAuthenticated,
            name = user?.Identity?.Name,
            email = user?.FindFirst("email")?.Value,
            scopeId = scope?.ScopeId,
            scopeSource = scope?.Source,
        });
    }

    private static IResult HandleGetContext(HttpContext http, IServiceProvider services, bool embeddedWorkflowMode)
    {
        var publishedWorkflows = !embeddedWorkflowMode || services.GetService<IScopeWorkflowQueryPort>() != null;
        var scripts = services.GetService<AppScopedScriptService>() != null ||
                      (embeddedWorkflowMode &&
                       services.GetService<IScriptDefinitionCommandPort>() != null &&
                       services.GetService<IScriptRuntimeProvisioningPort>() != null &&
                       services.GetService<IScriptRuntimeCommandPort>() != null);
        var scopeContext = services.GetService<IAppScopeResolver>()?.Resolve(http);

        return Results.Json(new
        {
            mode = embeddedWorkflowMode ? "embedded" : "proxy",
            scopeId = scopeContext?.ScopeId,
            scopeResolved = scopeContext != null,
            scopeSource = scopeContext?.Source,
            workflowStorageMode = scopeContext == null ? "workspace" : "scope",
            scriptStorageMode = scopeContext == null ? "draft" : "scope",
            features = new
            {
                publishedWorkflows,
                scripts,
            },
            scriptContract = new
            {
                inputType = Any.Pack(new AppScriptCommand()).TypeUrl,
                readModelFields = new[]
                {
                    AppScriptProtocol.InputField,
                    AppScriptProtocol.OutputField,
                    AppScriptProtocol.StatusField,
                    AppScriptProtocol.LastCommandIdField,
                    AppScriptProtocol.NotesField,
                },
            },
        });
    }

    private static async Task<IResult> HandleRunDraftScriptAsync(
        HttpContext http,
        AppScriptDraftRunRequest request,
        IServiceProvider services,
        bool embeddedWorkflowMode,
        CancellationToken ct)
    {
        if (!embeddedWorkflowMode)
        {
            return Results.BadRequest(new
            {
                code = "SCRIPT_DRAFT_RUN_UNAVAILABLE",
                message = "Script draft run is only available in embedded mode.",
            });
        }

        var definitionPort = services.GetService<IScriptDefinitionCommandPort>();
        var runtimeProvisioningPort = services.GetService<IScriptRuntimeProvisioningPort>();
        var runtimeCommandPort = services.GetService<IScriptRuntimeCommandPort>();
        if (definitionPort == null || runtimeProvisioningPort == null || runtimeCommandPort == null)
        {
            return Results.BadRequest(new
            {
                code = "SCRIPT_RUNTIME_UNAVAILABLE",
                message = "Script runtime services are not available in the current host.",
            });
        }

        var source = AppScriptPackagePayloads.ResolvePersistedSource(request.Package, request.Source);
        if (string.IsNullOrWhiteSpace(source))
        {
            return Results.BadRequest(new
            {
                code = "SCRIPT_SOURCE_REQUIRED",
                message = "Script source is required.",
            });
        }

        var scriptId = NormalizeStudioDocumentId(request.ScriptId, "script");
        var revision = NormalizeStudioDocumentId(request.ScriptRevision, "draft");
        var scopeId = services.GetService<IAppScopeResolver>()?.Resolve(http)?.ScopeId;
        var scopeToken = string.IsNullOrWhiteSpace(scopeId)
            ? "local"
            : NormalizeStudioDocumentId(scopeId, "scope");
        var definitionActorId = string.IsNullOrWhiteSpace(request.DefinitionActorId)
            ? $"app-script-definition:{scopeToken}:{scriptId}:{revision}"
            : request.DefinitionActorId.Trim();
        var runtimeActorId = string.IsNullOrWhiteSpace(request.RuntimeActorId)
            ? $"app-script-runtime:{scopeToken}:{scriptId}:{revision}"
            : request.RuntimeActorId.Trim();
        var sourceHash = AppScriptPackagePayloads.ComputeSourceHash(request.Package, source);

        try
        {
            var upsert = await definitionPort.UpsertDefinitionWithSnapshotAsync(
                scriptId,
                revision,
                source,
                sourceHash,
                definitionActorId,
                scopeId,
                ct);

            var resolvedRuntimeActorId = await runtimeProvisioningPort.EnsureRuntimeAsync(
                upsert.ActorId,
                revision,
                runtimeActorId,
                upsert.Snapshot,
                scopeId,
                ct);

            var runId = Guid.NewGuid().ToString("N");
            var payload = Any.Pack(AppScriptProtocol.CreateCommand(
                request.Input ?? string.Empty,
                runId));

            await runtimeCommandPort.RunRuntimeAsync(
                resolvedRuntimeActorId,
                runId,
                payload,
                revision,
                upsert.ActorId,
                payload.TypeUrl,
                scopeId,
                ct);

            return Results.Ok(new
            {
                accepted = true,
                scopeId,
                scriptId,
                scriptRevision = revision,
                definitionActorId = upsert.ActorId,
                runtimeActorId = resolvedRuntimeActorId,
                runId,
                sourceHash,
                commandTypeUrl = payload.TypeUrl,
                readModelUrl = $"/api/app/scripts/runtimes/{Uri.EscapeDataString(resolvedRuntimeActorId)}/readmodel",
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "SCRIPT_DRAFT_RUN_FAILED",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleListScopedScriptsAsync(
        HttpContext http,
        IServiceProvider services,
        bool includeSource,
        CancellationToken ct)
    {
        var scopeContext = services.GetService<IAppScopeResolver>()?.Resolve(http);
        if (scopeContext == null)
        {
            return Results.BadRequest(new
            {
                code = "APP_SCOPE_REQUIRED",
                message = "Script management requires a resolved scope id.",
            });
        }

        var service = services.GetService<AppScopedScriptService>();
        if (service == null)
        {
            return Results.BadRequest(new
            {
                code = "SCRIPT_SCOPE_SERVICE_UNAVAILABLE",
                message = "Scoped script services are not available in the current host.",
            });
        }

        try
        {
            if (!includeSource)
                return Results.Ok(await service.ListAsync(scopeContext.ScopeId, ct));

            var summaries = await service.ListAsync(scopeContext.ScopeId, ct);
            var details = new List<ScopeScriptDetail>(summaries.Count);
            foreach (var summary in summaries)
            {
                var detail = await service.GetAsync(scopeContext.ScopeId, summary.ScriptId, ct);
                if (detail != null)
                    details.Add(detail);
            }

            return Results.Ok(details);
        }
        catch (AppApiException ex)
        {
            return AppApiErrors.ToResult(ex);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_SCOPE_SCRIPT_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleGetScopedScriptAsync(
        HttpContext http,
        string scriptId,
        IServiceProvider services,
        CancellationToken ct)
    {
        var scopeContext = services.GetService<IAppScopeResolver>()?.Resolve(http);
        if (scopeContext == null)
        {
            return Results.BadRequest(new
            {
                code = "APP_SCOPE_REQUIRED",
                message = "Script management requires a resolved scope id.",
            });
        }

        var service = services.GetService<AppScopedScriptService>();
        if (service == null)
        {
            return Results.BadRequest(new
            {
                code = "SCRIPT_SCOPE_SERVICE_UNAVAILABLE",
                message = "Scoped script services are not available in the current host.",
            });
        }

        try
        {
            var detail = await service.GetAsync(scopeContext.ScopeId, scriptId, ct);
            return detail == null ? Results.NotFound() : Results.Ok(detail);
        }
        catch (AppApiException ex)
        {
            return AppApiErrors.ToResult(ex);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_SCOPE_SCRIPT_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleGetScopedScriptCatalogAsync(
        HttpContext http,
        string scriptId,
        IServiceProvider services,
        CancellationToken ct)
    {
        var scopeContext = services.GetService<IAppScopeResolver>()?.Resolve(http);
        if (scopeContext == null)
        {
            return Results.BadRequest(new
            {
                code = "APP_SCOPE_REQUIRED",
                message = "Script catalog browsing requires a resolved scope id.",
            });
        }

        var service = services.GetService<AppScopedScriptService>();
        if (service == null)
        {
            return Results.BadRequest(new
            {
                code = "SCRIPT_SCOPE_SERVICE_UNAVAILABLE",
                message = "Scoped script services are not available in the current host.",
            });
        }

        try
        {
            var catalog = await service.GetCatalogAsync(scopeContext.ScopeId, scriptId, ct);
            return catalog == null ? Results.NotFound() : Results.Ok(catalog);
        }
        catch (AppApiException ex)
        {
            return AppApiErrors.ToResult(ex);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_SCOPE_SCRIPT_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleProposeScopedScriptEvolutionAsync(
        HttpContext http,
        AppScopeScriptEvolutionRequest request,
        IServiceProvider services,
        CancellationToken ct)
    {
        var scopeContext = services.GetService<IAppScopeResolver>()?.Resolve(http);
        if (scopeContext == null)
        {
            return Results.BadRequest(new
            {
                code = "APP_SCOPE_REQUIRED",
                message = "Script governance requires a resolved scope id.",
            });
        }

        var service = services.GetService<AppScopedScriptService>();
        if (service == null)
        {
            return Results.BadRequest(new
            {
                code = "SCRIPT_SCOPE_SERVICE_UNAVAILABLE",
                message = "Scoped script services are not available in the current host.",
            });
        }

        try
        {
            return Results.Ok(await service.ProposeEvolutionAsync(scopeContext.ScopeId, request, ct));
        }
        catch (AppApiException ex)
        {
            return AppApiErrors.ToResult(ex);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_SCOPE_SCRIPT_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleSaveScopedScriptAsync(
        HttpContext http,
        AppScopeScriptSaveRequest request,
        IServiceProvider services,
        CancellationToken ct)
    {
        var scopeContext = services.GetService<IAppScopeResolver>()?.Resolve(http);
        if (scopeContext == null)
        {
            return Results.BadRequest(new
            {
                code = "APP_SCOPE_REQUIRED",
                message = "Script management requires a resolved scope id.",
            });
        }

        var service = services.GetService<AppScopedScriptService>();
        if (service == null)
        {
            return Results.BadRequest(new
            {
                code = "SCRIPT_SCOPE_SERVICE_UNAVAILABLE",
                message = "Scoped script services are not available in the current host.",
            });
        }

        try
        {
            return Results.Ok(await service.SaveAsync(scopeContext.ScopeId, request, ct));
        }
        catch (AppApiException ex)
        {
            return AppApiErrors.ToResult(ex);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_SCOPE_SCRIPT_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleListAppScriptRuntimesAsync(
        int take,
        IServiceProvider services,
        CancellationToken ct)
    {
        var service = services.GetService<AppScopedScriptService>();
        if (service == null)
        {
            return Results.BadRequest(new
            {
                code = "SCRIPT_READMODEL_UNAVAILABLE",
                message = "Script read model queries are not available in the current host.",
            });
        }

        try
        {
            return Results.Ok(await service.ListRuntimeSnapshotsAsync(take, ct));
        }
        catch (AppApiException ex)
        {
            return AppApiErrors.ToResult(ex);
        }
    }

    private static IResult HandleValidateScript(
        AppScriptValidateRequest request,
        IServiceProvider services)
    {
        var validator = services.GetService<ScriptEditorValidationService>();
        if (validator == null)
        {
            return Results.BadRequest(new
            {
                code = "SCRIPT_VALIDATION_UNAVAILABLE",
                message = "Script validation services are not available in the current host.",
            });
        }

        var scriptId = NormalizeStudioDocumentId(request.ScriptId, "script");
        var revision = NormalizeStudioDocumentId(request.ScriptRevision, "draft");
        var result = validator.Validate(
            scriptId,
            revision,
            request.Package,
            request.Source);
        return Results.Ok(result);
    }

    private static async Task<IResult> HandleGetAppScriptReadModelAsync(
        string actorId,
        IServiceProvider services,
        CancellationToken ct)
    {
        var service = services.GetService<AppScopedScriptService>();
        if (service == null)
        {
            return Results.BadRequest(new
            {
                code = "SCRIPT_READMODEL_UNAVAILABLE",
                message = "Script read model queries are not available in the current host.",
            });
        }

        ScriptReadModelSnapshotHttpResponse? snapshot;
        try
        {
            snapshot = await service.GetRuntimeSnapshotAsync(actorId, ct);
        }
        catch (AppApiException ex)
        {
            return AppApiErrors.ToResult(ex);
        }

        if (snapshot == null)
            return Results.NotFound();

        return Results.Ok(snapshot);
    }

    private static async Task<IResult> HandleGetAppScriptEvolutionDecisionAsync(
        string proposalId,
        IServiceProvider services,
        CancellationToken ct)
    {
        var service = services.GetService<AppScopedScriptService>();
        if (service == null)
        {
            return Results.BadRequest(new
            {
                code = "SCRIPT_SCOPE_SERVICE_UNAVAILABLE",
                message = "Scoped script services are not available in the current host.",
            });
        }

        try
        {
            var decision = await service.GetEvolutionDecisionAsync(proposalId, ct);
            return decision == null ? Results.NotFound() : Results.Ok(decision);
        }
        catch (AppApiException ex)
        {
            return AppApiErrors.ToResult(ex);
        }
    }

    private static async Task HandleGenerateWorkflowAsync(
        HttpContext http,
        AppWorkflowGenerateRequest request,
        IServiceProvider services,
        bool embeddedWorkflowMode,
        CancellationToken ct)
    {
        if (!embeddedWorkflowMode)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new
            {
                code = "WORKFLOW_GENERATOR_UNAVAILABLE",
                message = "Ask AI workflow generation is only available in embedded mode.",
            }, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new
            {
                code = "WORKFLOW_GENERATOR_PROMPT_REQUIRED",
                message = "Workflow authoring prompt is required.",
            }, ct);
            return;
        }

        var generator = services.GetService<WorkflowGenerateActorService>();
        if (generator == null)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new
            {
                code = "WORKFLOW_GENERATOR_MISSING",
                message = "Workflow generator services are not available in the current host.",
            }, ct);
            return;
        }

        try
        {
            await StartSseAsync(http.Response, ct);
            var result = await generator.GenerateAsync(
                new WorkflowGenerateRequest(
                    request.Prompt.Trim(),
                    request.CurrentYaml,
                    request.AvailableWorkflowNames,
                    request.Metadata),
                (delta, token) => WriteSseFrameAsync(http.Response, new
                {
                    type = "TEXT_MESSAGE_REASONING",
                    delta,
                }, token),
                (progress, token) => WriteSseFrameAsync(http.Response, new
                {
                    type = "TEXT_MESSAGE_REASONING",
                    delta = progress.Message.EndsWith('\n') ? progress.Message : $"{progress.Message}\n",
                }, token),
                ct);

            foreach (var chunk in ChunkText(result.Yaml, 320))
            {
                await WriteSseFrameAsync(http.Response, new
                {
                    type = "TEXT_MESSAGE_CONTENT",
                    delta = chunk,
                }, ct);
            }

            await WriteSseFrameAsync(http.Response, new
            {
                type = "TEXT_MESSAGE_END",
                message = result.Yaml,
                delta = string.Empty,
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException ex)
        {
            if (!http.Response.HasStarted)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsJsonAsync(new
                {
                    code = "WORKFLOW_GENERATOR_FAILED",
                    message = ex.Message,
                }, ct);
                return;
            }

            await WriteSseFrameAsync(http.Response, new
            {
                type = "RUN_ERROR",
                message = ex.Message,
            }, ct);
        }
        catch (Exception ex)
        {
            if (!http.Response.HasStarted)
            {
                http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await http.Response.WriteAsJsonAsync(new
                {
                    code = "WORKFLOW_GENERATOR_UNEXPECTED",
                    message = ex.Message,
                }, ct);
                return;
            }

            await WriteSseFrameAsync(http.Response, new
            {
                type = "RUN_ERROR",
                message = ex.Message,
            }, ct);
        }
    }

    private static async Task HandleGenerateScriptAsync(
        HttpContext http,
        AppScriptGenerateRequest request,
        IServiceProvider services,
        bool embeddedWorkflowMode,
        CancellationToken ct)
    {
        if (!embeddedWorkflowMode)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new
            {
                code = "SCRIPT_GENERATOR_UNAVAILABLE",
                message = "Ask AI script generation is only available in embedded mode.",
            }, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new
            {
                code = "SCRIPT_GENERATOR_PROMPT_REQUIRED",
                message = "Script authoring prompt is required.",
            }, ct);
            return;
        }

        var generator = services.GetService<ScriptGenerateActorService>();
        if (generator == null)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new
            {
                code = "SCRIPT_GENERATOR_MISSING",
                message = "Script generator services are not available in the current host.",
            }, ct);
            return;
        }

        try
        {
            await StartSseAsync(http.Response, ct);
            var result = await generator.GenerateAsync(
                new ScriptGenerateRequest(
                    request.Prompt.Trim(),
                    request.CurrentSource,
                    request.Metadata,
                    request.CurrentPackage,
                    request.CurrentFilePath),
                (delta, token) => WriteSseFrameAsync(http.Response, new
                {
                    type = "TEXT_MESSAGE_REASONING",
                    delta,
                }, token),
                (progress, token) => WriteSseFrameAsync(http.Response, new
                {
                    type = "TEXT_MESSAGE_REASONING",
                    delta = progress.Message.EndsWith('\n') ? progress.Message : $"{progress.Message}\n",
                }, token),
                ct);

            foreach (var chunk in ChunkText(result.Source, 320))
            {
                await WriteSseFrameAsync(http.Response, new
                {
                    type = "TEXT_MESSAGE_CONTENT",
                    delta = chunk,
                }, ct);
            }

            await WriteSseFrameAsync(http.Response, new
            {
                type = "TEXT_MESSAGE_END",
                message = result.Source,
                delta = string.Empty,
                currentFilePath = result.CurrentFilePath ?? string.Empty,
                scriptPackage = result.Package == null
                    ? null
                    : new
                    {
                        csharpSources = (result.Package.CsharpSources ?? Array.Empty<AppScriptPackageFile>()).Select(static file => new
                        {
                            path = file.Path,
                            content = file.Content,
                        }),
                        protoFiles = (result.Package.ProtoFiles ?? Array.Empty<AppScriptPackageFile>()).Select(static file => new
                        {
                            path = file.Path,
                            content = file.Content,
                        }),
                        entryBehaviorTypeName = result.Package.EntryBehaviorTypeName,
                        entrySourcePath = result.Package.EntrySourcePath,
                    },
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException ex)
        {
            if (!http.Response.HasStarted)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsJsonAsync(new
                {
                    code = "SCRIPT_GENERATOR_FAILED",
                    message = ex.Message,
                }, ct);
                return;
            }

            await WriteSseFrameAsync(http.Response, new
            {
                type = "RUN_ERROR",
                message = ex.Message,
            }, ct);
        }
        catch (Exception ex)
        {
            if (!http.Response.HasStarted)
            {
                http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await http.Response.WriteAsJsonAsync(new
                {
                    code = "SCRIPT_GENERATOR_UNEXPECTED",
                    message = ex.Message,
                }, ct);
                return;
            }

            await WriteSseFrameAsync(http.Response, new
            {
                type = "RUN_ERROR",
                message = ex.Message,
            }, ct);
        }
    }

    private static string ComputeSha256(string source)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static ValueTask StartSseAsync(HttpResponse response, CancellationToken ct)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.Headers.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        return new ValueTask(response.StartAsync(ct));
    }

    private static async Task WriteSseFrameAsync(HttpResponse response, object frame, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(frame);
        var bytes = Encoding.UTF8.GetBytes($"data: {payload}\n\n");
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }

    private static IEnumerable<string> ChunkText(string text, int chunkSize)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        var size = chunkSize > 0 ? chunkSize : 320;
        for (var index = 0; index < text.Length; index += size)
        {
            var length = Math.Min(size, text.Length - index);
            yield return text.Substring(index, length);
        }
    }

    internal sealed record AppScriptDraftRunRequest(
        string? ScriptId,
        string? ScriptRevision,
        string? Source,
        AppScriptPackage? Package,
        string? Input,
        string? DefinitionActorId,
        string? RuntimeActorId);

    internal sealed record AppScriptValidateRequest(
        string? ScriptId,
        string? ScriptRevision,
        string? Source,
        AppScriptPackage? Package);

    internal sealed record AppWorkflowGenerateRequest(
        string? Prompt,
        string? CurrentYaml,
        IReadOnlyCollection<string>? AvailableWorkflowNames,
        IReadOnlyDictionary<string, string>? Metadata);

    internal sealed record AppScriptGenerateRequest(
        string? Prompt,
        string? CurrentSource,
        AppScriptPackage? CurrentPackage,
        string? CurrentFilePath,
        IReadOnlyDictionary<string, string>? Metadata);
}
