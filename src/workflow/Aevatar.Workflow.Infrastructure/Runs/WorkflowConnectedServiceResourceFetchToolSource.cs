using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Modules;

namespace Aevatar.Workflow.Infrastructure.Runs;

public sealed class WorkflowConnectedServiceResourceFetchToolSource(
    IEnumerable<IWorkflowConnectedServiceResourceFetchAdapter> adapters,
    IFileArtifactIngressPort fileIngress) : IWorkflowToolSource
{
    private readonly IReadOnlyList<IWorkflowConnectedServiceResourceFetchAdapter> _adapters =
        adapters?.ToArray() ?? throw new ArgumentNullException(nameof(adapters));
    private readonly IFileArtifactIngressPort _fileIngress =
        fileIngress ?? throw new ArgumentNullException(nameof(fileIngress));

    public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IWorkflowTool>>([new ConnectedServiceResourceFetchTool(_adapters, _fileIngress)]);

    private sealed class ConnectedServiceResourceFetchTool(
        IReadOnlyList<IWorkflowConnectedServiceResourceFetchAdapter> adapters,
        IFileArtifactIngressPort fileIngress) : IWorkflowTool
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly IReadOnlyList<IWorkflowConnectedServiceResourceFetchAdapter> _adapters = adapters;
        private readonly IFileArtifactIngressPort _fileIngress = fileIngress;

        public string Name => "workflow_connected_service_resource_fetch";

        public async Task<WorkflowToolExecutionResult> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken ct = default)
        {
            WorkflowConnectedServiceResourceFetchArguments arguments;
            try
            {
                arguments = ParseArguments(request.ArgumentsJson);
            }
            catch (JsonException ex)
            {
                return Error("invalid_arguments", ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Error("invalid_arguments", ex.Message);
            }

            var route = new WorkflowConnectedServiceResourceFetchRoute(
                arguments.Provider,
                arguments.Operation,
                arguments.ResourceKind);
            var adapter = ResolveAdapter(route);
            if (adapter == null)
                return Error(
                    "unsupported_resource_route",
                    "workflow_connected_service_resource_fetch only supports registered connected-service resource routes.");

            var token = Normalize(request.CallerCredential.BearerToken);
            if (token == null)
                return Error("missing_bearer", "workflow_connected_service_resource_fetch requires a workflow caller bearer token.");

            WorkflowConnectedServiceResourceFetchResult fetchResult;
            try
            {
                fetchResult = await adapter.FetchAsync(
                    new WorkflowConnectedServiceResourceFetchRequest(
                        route,
                        arguments.MessageId,
                        arguments.ResourceKey,
                        new WorkflowCallerCredential(token)),
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Error("provider_call_failed", "Connected-service resource fetch failed.");
            }

            if (!fetchResult.Succeeded)
                return Error(
                    "provider_resource_unavailable",
                    "Connected-service resource fetch did not return content.");
            if (fetchResult.Content.IsEmpty)
                return Error("empty_resource", "Connected-service resource fetch returned empty content.");

            FileArtifactIngressResult ingressResult;
            try
            {
                ingressResult = await _fileIngress.IngestAsync(new FileArtifactIngressRequest(
                    fetchResult.Content,
                    FileArtifactSourceKind.ConnectedServiceResource,
                    SourceMessageId: arguments.MessageId,
                    SourceResourceKey: arguments.ResourceKey,
                    FileName: fetchResult.FileName,
                    MediaType: fetchResult.MediaType,
                    OwnerRunId: Normalize(request.RunId),
                    OwnerScopeId: Normalize(request.ScopeId)), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Error("artifact_ingress_failed", "Connected-service resource could not be stored.");
            }

            return WorkflowToolExecutionResult.Success(JsonSerializer.Serialize(
                new WorkflowConnectedServiceResourceFetchOutput(
                    Success: true,
                    Provider: route.Provider,
                    Operation: route.Operation,
                    ResourceKind: route.ResourceKind,
                    FileRef: ToOutputFileRef(ingressResult.FileRef)),
                JsonOptions));
        }

        private IWorkflowConnectedServiceResourceFetchAdapter? ResolveAdapter(
            WorkflowConnectedServiceResourceFetchRoute route)
        {
            foreach (var adapter in _adapters)
            {
                if (adapter.Routes.Any(adapterRoute => RoutesEqual(adapterRoute, route)))
                    return adapter;
            }

            return null;
        }

        private static WorkflowConnectedServiceResourceFetchArguments ParseArguments(string? argumentsJson)
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson)
                ? "{}"
                : argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("workflow_connected_service_resource_fetch arguments must be a JSON object.");

            var provider = RequiredString(root, "provider");
            var operation = RequiredString(root, "operation");
            var resourceKind = RequiredString(root, "resource_kind", "resourceKind");
            var messageId = RequiredString(root, "message_id", "messageId");
            var resourceKey = RequiredString(root, "resource_key", "resourceKey");

            return new WorkflowConnectedServiceResourceFetchArguments(
                provider,
                operation,
                resourceKind,
                messageId,
                resourceKey);
        }

        private static string RequiredString(JsonElement root, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                if (!root.TryGetProperty(propertyName, out var property))
                    continue;
                if (property.ValueKind != JsonValueKind.String)
                    throw new ArgumentException(
                        $"workflow_connected_service_resource_fetch {propertyName} must be a string.");
                var value = Normalize(property.GetString());
                if (value != null)
                    return value;
            }

            throw new ArgumentException(
                $"workflow_connected_service_resource_fetch {propertyNames[0]} is required.");
        }

        private static bool RoutesEqual(
            WorkflowConnectedServiceResourceFetchRoute left,
            WorkflowConnectedServiceResourceFetchRoute right) =>
            string.Equals(left.Provider, right.Provider, StringComparison.Ordinal) &&
            string.Equals(left.Operation, right.Operation, StringComparison.Ordinal) &&
            string.Equals(left.ResourceKind, right.ResourceKind, StringComparison.Ordinal);

        private static WorkflowConnectedServiceResourceFetchFileRef ToOutputFileRef(FileArtifactRef fileRef) =>
            new(
                fileRef.FileId,
                fileRef.ArtifactId,
                fileRef.SourceKind.ToString(),
                fileRef.SourceMessageId,
                fileRef.SourceResourceKey,
                fileRef.FileName,
                fileRef.MediaType,
                fileRef.SizeBytes,
                fileRef.Sha256,
                fileRef.CreatedAtUnixMs,
                fileRef.ExpiresAtUnixMs,
                fileRef.OwnerRunId,
                fileRef.OwnerScopeId);

        private static WorkflowToolExecutionResult Error(string error, string detail)
        {
            var resultJson = JsonSerializer.Serialize(
                new WorkflowConnectedServiceResourceFetchError(false, error, detail),
                JsonOptions);
            return WorkflowToolExecutionResult.Failed(resultJson, error, detail);
        }

        private static string? Normalize(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record WorkflowConnectedServiceResourceFetchArguments(
        string Provider,
        string Operation,
        string ResourceKind,
        string MessageId,
        string ResourceKey);

    private sealed record WorkflowConnectedServiceResourceFetchOutput(
        bool Success,
        string Provider,
        string Operation,
        string ResourceKind,
        WorkflowConnectedServiceResourceFetchFileRef FileRef);

    private sealed record WorkflowConnectedServiceResourceFetchFileRef(
        string? FileId,
        string? ArtifactId,
        string SourceKind,
        string? SourceMessageId,
        string? SourceResourceKey,
        string? FileName,
        string? MediaType,
        long SizeBytes,
        string? Sha256,
        long CreatedAtUnixMs,
        long ExpiresAtUnixMs,
        string? OwnerRunId,
        string? OwnerScopeId);

    private sealed record WorkflowConnectedServiceResourceFetchError(
        bool Success,
        string Error,
        string Detail);
}
