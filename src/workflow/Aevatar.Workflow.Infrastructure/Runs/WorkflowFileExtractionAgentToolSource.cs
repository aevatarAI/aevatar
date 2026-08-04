using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Modules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Infrastructure.Runs;

public sealed class WorkflowFileExtractionAgentToolSource(
    WorkflowDocumentExtractToolSource documentExtractToolSource,
    WorkflowSpreadsheetExtractToolSource spreadsheetExtractToolSource,
    ILogger<WorkflowFileExtractionAgentToolSource>? logger = null) : IAgentToolSource
{
    private readonly WorkflowDocumentExtractToolSource _documentExtractToolSource = documentExtractToolSource;
    private readonly WorkflowSpreadsheetExtractToolSource _spreadsheetExtractToolSource = spreadsheetExtractToolSource;
    private readonly ILogger<WorkflowFileExtractionAgentToolSource>? _logger = logger;

    public async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        var tools = new List<IAgentTool>();
        var documentTools = await _documentExtractToolSource.GetToolsAsync(ct).ConfigureAwait(false);
        tools.AddRange(documentTools.Select(tool => new WorkflowFileExtractionAgentTool(tool, _logger)));
        var spreadsheetTools = await _spreadsheetExtractToolSource.GetToolsAsync(ct).ConfigureAwait(false);
        tools.AddRange(spreadsheetTools.Select(tool => new WorkflowFileExtractionAgentTool(tool, _logger)));
        _logger?.LogInformation(
            "Workflow file extraction agent tools discovered. documentToolCount={DocumentToolCount} spreadsheetToolCount={SpreadsheetToolCount} toolNames={ToolNames}",
            documentTools.Count,
            spreadsheetTools.Count,
            string.Join(',', tools.Select(static tool => tool.Name)));
        return tools;
    }

    private sealed class WorkflowFileExtractionAgentTool(
        IWorkflowTool tool,
        ILogger<WorkflowFileExtractionAgentToolSource>? logger) : IAgentTool
    {
        private readonly IWorkflowTool _tool = tool;
        private readonly ILogger<WorkflowFileExtractionAgentToolSource>? _logger = logger;

        public string Name => _tool.Name;

        public string Description => _tool.Name switch
        {
            "document_extract" => "Extract text or schema-bound JSON from a workflow input document file reference.",
            "spreadsheet_extract" => "Extract workbook data from a workflow input spreadsheet file reference.",
            _ => "Read a workflow input file reference.",
        };

        public string ParametersSchema => _tool.Name switch
        {
            "document_extract" =>
                "{\"type\":\"object\",\"properties\":{\"fileRef\":{\"type\":\"object\"},\"extraction_kind\":{\"type\":\"string\",\"enum\":[\"text\",\"schema_bound_json\"]},\"maxChars\":{\"type\":\"integer\"},\"schema_contract\":{\"type\":\"object\"}},\"additionalProperties\":true}",
            "spreadsheet_extract" =>
                "{\"type\":\"object\",\"properties\":{\"fileRef\":{\"type\":\"object\"}},\"additionalProperties\":true}",
            _ => "{\"type\":\"object\"}",
        };

        public bool IsReadOnly => true;

        public async Task<AgentToolTerminalOutcome> ExecuteWithOutcomeAsync(
            string callId,
            string toolName,
            string argumentsJson,
            CancellationToken ct = default)
        {
            var result = await ExecuteWorkflowToolAsync(argumentsJson, ct).ConfigureAwait(false);
            var resultJson = ToResultJson(result);
            return new AgentToolTerminalOutcome(
                resultJson,
                ToReceipt(callId, toolName, resultJson, result.Failure));
        }

        public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            var result = await ExecuteWorkflowToolAsync(argumentsJson, ct).ConfigureAwait(false);
            return ToResultJson(result);
        }

        private async Task<WorkflowToolExecutionResult> ExecuteWorkflowToolAsync(
            string argumentsJson,
            CancellationToken ct)
        {
            var context = AgentToolRequestContext.Current ?? AgentToolExecutionContext.Empty;
            var inputFileRefs = context.InputFileRefs.Select(ToWorkflowFileRef).ToArray();
            var request = new WorkflowToolExecutionRequest(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
                Normalize(context.WorkflowRuntime.ParentRunId) ?? Normalize(context.Request.RequestId) ?? string.Empty,
                Normalize(context.WorkflowRuntime.ParentStepId) ?? string.Empty,
                Normalize(context.Request.IdempotencyKey) ?? Normalize(context.Request.CallId) ?? string.Empty,
                Normalize(context.Request.CallId) ?? string.Empty,
                Normalize(context.Caller.ScopeId) ?? Normalize(context.Caller.OwnerScopeId) ?? string.Empty,
                ToWorkflowCallerCredential(context),
                ToWorkflowRuntimeContext(context.WorkflowRuntime),
                InputFileRefs: inputFileRefs,
                IdempotencyKey: Normalize(context.Request.IdempotencyKey) ?? string.Empty,
                ScheduleId: Normalize(context.Schedule.ScheduleId) ?? string.Empty);
            var firstFileRef = inputFileRefs.FirstOrDefault();
            _logger?.LogInformation(
                "Workflow file extraction agent tool executing. toolName={ToolName} runId={RunId} stepId={StepId} callId={CallId} scopeId={ScopeId} inputFileRefCount={InputFileRefCount} firstFileId={FirstFileId} firstArtifactId={FirstArtifactId} firstMediaType={FirstMediaType}",
                Name,
                request.RunId,
                request.StepId,
                request.CallId,
                request.ScopeId,
                inputFileRefs.Length,
                firstFileRef?.FileId ?? string.Empty,
                firstFileRef?.ArtifactId ?? string.Empty,
                firstFileRef?.MediaType ?? string.Empty);

            var result = await _tool.ExecuteAsync(request, ct).ConfigureAwait(false);
            _logger?.LogInformation(
                "Workflow file extraction agent tool completed. toolName={ToolName} runId={RunId} stepId={StepId} callId={CallId} resultJsonLength={ResultJsonLength} failureCode={FailureCode} failureMessage={FailureMessage}",
                Name,
                request.RunId,
                request.StepId,
                request.CallId,
                result.ResultJson?.Length ?? 0,
                result.Failure?.ErrorCode ?? string.Empty,
                result.Failure?.ErrorMessage ?? string.Empty);
            return result;
        }

        private AgentToolReceipt ToReceipt(
            string callId,
            string toolName,
            string resultJson,
            WorkflowToolExecutionFailure? failure) =>
            new()
            {
                CallId = callId ?? string.Empty,
                ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
                Status = failure == null ? AgentToolReceiptStatus.Success : AgentToolReceiptStatus.Error,
                ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
                ResultJson = resultJson ?? string.Empty,
                ErrorCode = failure?.ErrorCode ?? string.Empty,
                ErrorMessage = failure?.ErrorMessage ?? string.Empty,
            };

        private static string ToResultJson(WorkflowToolExecutionResult result)
        {
            if (result.Failure == null || !string.IsNullOrWhiteSpace(result.ResultJson))
                return result.ResultJson;

            return JsonSerializer.Serialize(new
            {
                error = new
                {
                    code = result.Failure.ErrorCode,
                    message = result.Failure.ErrorMessage,
                },
            });
        }

        private static WorkflowCallerCredential ToWorkflowCallerCredential(AgentToolExecutionContext context) =>
            new()
            {
                BearerToken = Normalize(context.Credentials.NyxIdAccessToken) ??
                              Normalize(context.Credentials.SenderNyxIdAccessToken) ??
                              string.Empty,
                NyxIdAuthority = new WorkflowCallerNyxIdAuthority
                {
                    Platform = Normalize(context.NyxIdAuthority.Platform) ?? string.Empty,
                    Tenant = Normalize(context.NyxIdAuthority.Tenant) ?? string.Empty,
                    ExternalUserId = Normalize(context.NyxIdAuthority.ExternalUserId) ?? string.Empty,
                    Scope = Normalize(context.NyxIdAuthority.Scope) ?? string.Empty,
                    BindingId = Normalize(context.SenderBinding.BindingId) ?? string.Empty,
                },
            };

        private static WorkflowToolRuntimeContext ToWorkflowRuntimeContext(AgentWorkflowRuntimeContext context) =>
            new(
                Normalize(context.ParentActorId) ?? string.Empty,
                Normalize(context.ParentRunId) ?? string.Empty,
                Normalize(context.ParentStepId) ?? string.Empty,
                Normalize(context.RootRunId) ?? string.Empty,
                Math.Max(0, context.Depth));

        private static WorkflowFileRef ToWorkflowFileRef(ChatFileRef source) =>
            new()
            {
                FileId = Normalize(source.FileId) ?? string.Empty,
                ArtifactId = Normalize(source.ArtifactId) ?? string.Empty,
                SourceKind = source.SourceKind switch
                {
                    ChatFileSourceKind.ChatInput => WorkflowFileSourceKind.ChatInput,
                    ChatFileSourceKind.FormUpload => WorkflowFileSourceKind.FormUpload,
                    ChatFileSourceKind.ConnectedServiceResource => WorkflowFileSourceKind.ConnectedServiceResource,
                    ChatFileSourceKind.ExternalResource => WorkflowFileSourceKind.ExternalResource,
                    ChatFileSourceKind.Generated => WorkflowFileSourceKind.Generated,
                    _ => WorkflowFileSourceKind.Unspecified,
                },
                SourceMessageId = Normalize(source.SourceMessageId) ?? string.Empty,
                SourceResourceKey = Normalize(source.SourceResourceKey) ?? string.Empty,
                FileName = Normalize(source.FileName) ?? string.Empty,
                MediaType = Normalize(source.MediaType) ?? string.Empty,
                SizeBytes = source.SizeBytes,
                Sha256 = Normalize(source.Sha256) ?? string.Empty,
                CreatedAtUnixMs = source.CreatedAtUnixMs,
                ExpiresAtUnixMs = source.ExpiresAtUnixMs,
                OwnerRunId = Normalize(source.OwnerRunId) ?? string.Empty,
                OwnerScopeId = Normalize(source.OwnerScopeId) ?? string.Empty,
            };

        private static string? Normalize(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
