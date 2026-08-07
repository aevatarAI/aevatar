using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

public sealed class PreviewWorkflowExplicitRequestsTool : IAgentTool
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IWorkflowExplicitRequestPreviewService _previewService;

    public PreviewWorkflowExplicitRequestsTool(
        IWorkflowExplicitRequestPreviewService previewService)
    {
        _previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));
    }

    public string Name => "preview_workflow_explicit_requests";

    public string Description =>
        "Preview canonical NyxID authored requests for one exact workflow revision before binding. " +
        "Returns server-derived risk, execution modes, plain disclosure data, and technical confirmations " +
        "that must be passed unchanged to the bind mutation. This tool does not approve or mutate resources.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "workflow_yaml": {
              "type": "string",
              "description": "Complete workflow YAML containing canonical capability.nyxid_request selectors."
            },
            "workflow_id": {
              "type": "string",
              "description": "Exact Studio workflow draft identity."
            },
            "revision_id": {
              "type": "string",
              "description": "Optional exact immutable workflow revision identity. Omit for a new draft so the server allocates one."
            },
            "execution_mode": {
              "type": "string",
              "enum": ["interactive", "durable"]
            }
          },
          "required": ["workflow_yaml", "workflow_id", "execution_mode"]
        }
        """;

    public bool IsReadOnly => true;

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson) =>
        BindingToolResultReceipts.CreateExplicitRequestPreview(Name, callId, toolName, resultJson);

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            var unknownArgument = FindUnknownArgument(argumentsJson);
            if (unknownArgument is not null)
                return JsonDefaults.Error($"Unknown argument: {unknownArgument}");

            var args = JsonSerializer.Deserialize<PreviewArguments>(argumentsJson, s_jsonOptions);
            if (args is null)
                return JsonDefaults.Error("tool arguments are required");

            var workflowYaml = Normalize(args.WorkflowYaml);
            var workflowId = Normalize(args.WorkflowId);
            var revisionId = Normalize(args.RevisionId);
            if (workflowYaml is null)
                return JsonDefaults.Error("workflow_yaml is required");
            if (workflowId is null)
                return JsonDefaults.Error("workflow_id is required");
            if (!TryParseExecutionMode(args.ExecutionMode, out var executionMode))
                return JsonDefaults.Error("execution_mode must be interactive or durable");
            if (!ExternalWorkflowCapabilityToolSupport.TryResolveAccess(out var access, out var error))
                return JsonDefaults.Error(error!);

            var result = await _previewService.PreviewAsync(
                new WorkflowExplicitRequestPreviewRequest(
                    access!,
                    workflowYaml,
                    InlineWorkflowYamls: null,
                    executionMode,
                    workflowId,
                    revisionId),
                ct);
            var confirmations = result.Items.Select(item => new PreviewConfirmationJson(
                item.CallSiteId,
                item.RequestContractDigest,
                FormatRisk(item.EffectiveRisk),
                result.WorkflowId,
                result.RevisionId));
            var requests = result.Items.Select(item => new PreviewRequestJson(
                item.CallSiteId,
                item.RequestContractDigest,
                item.UserServiceId,
                FormatMethod(item.Method),
                item.PathTemplate,
                FormatBodyMode(item.BodyMode),
                item.BodyRequired,
                FormatResponseMode(item.ResponseMode),
                FormatRisk(item.EffectiveRisk),
                item.AllowedExecutionModes.Select(FormatExecutionMode).ToArray(),
                new PreviewDisclosureJson(
                    item.UserServiceId,
                    ResolveActionKind(item.Method),
                    FormatRisk(item.EffectiveRisk),
                    executionMode == ExternalCapabilityExecutionMode.Durable,
                    AevatarUserApprovalRequired: false,
                    NyxIdApprovalMayBeRequired: true)));

            return JsonSerializer.Serialize(
                new PreviewResultJson(
                    result.WorkflowId,
                    result.RevisionId,
                    FormatExecutionMode(executionMode),
                    confirmations,
                    requests),
                s_jsonOptions);
        }
        catch (JsonException exception)
        {
            return JsonDefaults.Error($"Could not parse tool arguments: {exception.Message}");
        }
        catch (WorkflowExternalCapabilityAdmissionException exception)
        {
            return ExternalWorkflowCapabilityToolSupport.ProtoJsonFormatter.Format(exception.Readiness);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return JsonDefaults.Error($"Explicit request preview failed: {exception.GetType().Name}");
        }
    }

    private static string? FindUnknownArgument(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name is not "workflow_yaml" and
                not "workflow_id" and
                not "revision_id" and
                not "execution_mode")
            {
                return property.Name;
            }
        }

        return null;
    }

    private static bool TryParseExecutionMode(
        string? value,
        out ExternalCapabilityExecutionMode executionMode)
    {
        executionMode = value?.Trim().ToLowerInvariant() switch
        {
            "interactive" => ExternalCapabilityExecutionMode.Interactive,
            "durable" => ExternalCapabilityExecutionMode.Durable,
            _ => ExternalCapabilityExecutionMode.Unspecified,
        };
        return executionMode != ExternalCapabilityExecutionMode.Unspecified;
    }

    private static string FormatExecutionMode(ExternalCapabilityExecutionMode mode) => mode switch
    {
        ExternalCapabilityExecutionMode.Interactive => "interactive",
        ExternalCapabilityExecutionMode.Durable => "durable",
        _ => "unspecified",
    };

    private static string FormatMethod(NyxIdRequestMethod method) => method switch
    {
        NyxIdRequestMethod.Get => "GET",
        NyxIdRequestMethod.Head => "HEAD",
        NyxIdRequestMethod.Options => "OPTIONS",
        NyxIdRequestMethod.Post => "POST",
        NyxIdRequestMethod.Put => "PUT",
        NyxIdRequestMethod.Patch => "PATCH",
        NyxIdRequestMethod.Delete => "DELETE",
        _ => "UNSPECIFIED",
    };

    private static string FormatBodyMode(NyxIdRequestBodyMode mode) => mode switch
    {
        NyxIdRequestBodyMode.None => "none",
        NyxIdRequestBodyMode.Json => "json",
        _ => "unspecified",
    };

    private static string FormatResponseMode(NyxIdRequestResponseMode mode) => mode switch
    {
        NyxIdRequestResponseMode.Text => "text",
        NyxIdRequestResponseMode.FileArtifact => "file_artifact",
        _ => "unspecified",
    };

    private static string FormatRisk(NyxIdOperationRisk risk) => risk switch
    {
        NyxIdOperationRisk.ReadOnly => "read_only",
        NyxIdOperationRisk.Write => "write",
        NyxIdOperationRisk.Destructive => "destructive",
        _ => "unspecified",
    };

    private static string ResolveActionKind(NyxIdRequestMethod method) => method switch
    {
        NyxIdRequestMethod.Get or NyxIdRequestMethod.Head or NyxIdRequestMethod.Options => "read",
        NyxIdRequestMethod.Post => "create_or_trigger",
        NyxIdRequestMethod.Put or NyxIdRequestMethod.Patch => "update",
        NyxIdRequestMethod.Delete => "delete",
        _ => "unknown",
    };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PreviewArguments(
        [property: JsonPropertyName("workflow_yaml")] string? WorkflowYaml,
        [property: JsonPropertyName("workflow_id")] string? WorkflowId,
        [property: JsonPropertyName("revision_id")] string? RevisionId,
        [property: JsonPropertyName("execution_mode")] string? ExecutionMode);

    private sealed record PreviewResultJson(
        string WorkflowId,
        string RevisionId,
        string ExecutionMode,
        IEnumerable<PreviewConfirmationJson> Confirmations,
        IEnumerable<PreviewRequestJson> Requests);

    private sealed record PreviewConfirmationJson(
        string CallSiteId,
        string RequestContractDigest,
        string AttestedRisk,
        string WorkflowId,
        string RevisionId);

    private sealed record PreviewRequestJson(
        string CallSiteId,
        string RequestContractDigest,
        string UserServiceId,
        string Method,
        string PathTemplate,
        string BodyMode,
        bool BodyRequired,
        string ResponseMode,
        string EffectiveRisk,
        IReadOnlyList<string> AllowedExecutionModes,
        PreviewDisclosureJson Disclosure);

    private sealed record PreviewDisclosureJson(
        string ConnectionUserServiceId,
        string ActionKind,
        string Risk,
        bool DurableAutomation,
        bool AevatarUserApprovalRequired,
        bool NyxIdApprovalMayBeRequired);
}
