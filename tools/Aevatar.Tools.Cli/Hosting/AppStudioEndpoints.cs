using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Tools.Cli.Hosting;

internal static class AppStudioEndpoints
{
    public static void Map(IEndpointRouteBuilder app, bool embeddedWorkflowMode)
    {
        app.MapGet("/api/app/context", (HttpContext http, IServiceProvider services) =>
            HandleGetContext(http, services, embeddedWorkflowMode));

        app.MapPost("/api/app/scripts/draft-run", (
            AppScriptDraftRunRequest request,
            IServiceProvider services,
            CancellationToken ct) =>
            HandleRunDraftScriptAsync(request, services, embeddedWorkflowMode, ct));
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

    private static IResult HandleGetContext(HttpContext http, IServiceProvider services, bool embeddedWorkflowMode)
    {
        var publishedWorkflows = !embeddedWorkflowMode || services.GetService<IScopeWorkflowQueryPort>() != null;
        var scripts = embeddedWorkflowMode &&
                      services.GetService<IScriptDefinitionCommandPort>() != null &&
                      services.GetService<IScriptRuntimeProvisioningPort>() != null &&
                      services.GetService<IScriptRuntimeCommandPort>() != null;
        var scopeContext = services.GetService<IAppScopeResolver>()?.Resolve(http);

        return Results.Json(new
        {
            mode = embeddedWorkflowMode ? "embedded" : "proxy",
            scopeId = scopeContext?.ScopeId,
            scopeResolved = scopeContext != null,
            scopeSource = scopeContext?.Source,
            workflowStorageMode = scopeContext == null ? "workspace" : "scope",
            features = new
            {
                publishedWorkflows,
                scripts,
            },
            scriptContract = new
            {
                inputType = Any.Pack(new StringValue()).TypeUrl,
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

        var source = string.IsNullOrWhiteSpace(request.Source)
            ? string.Empty
            : request.Source.Trim();
        if (source.Length == 0)
        {
            return Results.BadRequest(new
            {
                code = "SCRIPT_SOURCE_REQUIRED",
                message = "Script source is required.",
            });
        }

        var scriptId = NormalizeStudioDocumentId(request.ScriptId, "script");
        var revision = NormalizeStudioDocumentId(request.ScriptRevision, "draft");
        var definitionActorId = string.IsNullOrWhiteSpace(request.DefinitionActorId)
            ? $"app-script-definition:{scriptId}:{revision}"
            : request.DefinitionActorId.Trim();
        var runtimeActorId = string.IsNullOrWhiteSpace(request.RuntimeActorId)
            ? $"app-script-runtime:{scriptId}:{revision}"
            : request.RuntimeActorId.Trim();
        var sourceHash = ComputeSha256(source);

        try
        {
            var upsert = await definitionPort.UpsertDefinitionWithSnapshotAsync(
                scriptId,
                revision,
                source,
                sourceHash,
                definitionActorId,
                ct);

            var resolvedRuntimeActorId = await runtimeProvisioningPort.EnsureRuntimeAsync(
                upsert.ActorId,
                revision,
                runtimeActorId,
                upsert.Snapshot,
                ct);

            var runId = Guid.NewGuid().ToString("N");
            var payload = Any.Pack(new StringValue
            {
                Value = request.Input ?? string.Empty,
            });

            await runtimeCommandPort.RunRuntimeAsync(
                resolvedRuntimeActorId,
                runId,
                payload,
                revision,
                upsert.ActorId,
                payload.TypeUrl,
                ct);

            return Results.Ok(new
            {
                accepted = true,
                scriptId,
                scriptRevision = revision,
                definitionActorId = upsert.ActorId,
                runtimeActorId = resolvedRuntimeActorId,
                runId,
                sourceHash,
                commandTypeUrl = payload.TypeUrl,
                readModelUrl = $"/api/scripts/runtimes/{Uri.EscapeDataString(resolvedRuntimeActorId)}/readmodel",
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

    private static string ComputeSha256(string source)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal sealed record AppScriptDraftRunRequest(
        string? ScriptId,
        string? ScriptRevision,
        string? Source,
        string? Input,
        string? DefinitionActorId,
        string? RuntimeActorId);
}
