using System.Net;
using Microsoft.AspNetCore.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Aevatar.Configuration;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Domain.Studio.Models;

using Aevatar.Studio.Application.Studio;
namespace Aevatar.Studio.Application;

public sealed class AppScopedWorkflowService
{
    private const string BackendClientName = "AppBridgeBackend";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IScopeWorkflowQueryPort? _workflowQueryPort;
    private readonly IScopeWorkflowCommandPort? _workflowCommandPort;
    private readonly IWorkflowActorBindingReader? _workflowActorBindingReader;
    private readonly IServiceRevisionArtifactStore? _artifactStore;
    private readonly IServiceLifecycleQueryPort? _serviceLifecycleQueryPort;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWorkflowYamlDocumentService _yamlDocumentService;
    private readonly IWorkflowStoragePort? _workflowStoragePort;

    public AppScopedWorkflowService(
        IHttpClientFactory httpClientFactory,
        IWorkflowYamlDocumentService yamlDocumentService,
        IScopeWorkflowQueryPort? workflowQueryPort = null,
        IScopeWorkflowCommandPort? workflowCommandPort = null,
        IWorkflowActorBindingReader? workflowActorBindingReader = null,
        IServiceRevisionArtifactStore? artifactStore = null,
        IServiceLifecycleQueryPort? serviceLifecycleQueryPort = null,
        IWorkflowStoragePort? workflowStoragePort = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _yamlDocumentService = yamlDocumentService ?? throw new ArgumentNullException(nameof(yamlDocumentService));
        _workflowQueryPort = workflowQueryPort;
        _workflowCommandPort = workflowCommandPort;
        _workflowActorBindingReader = workflowActorBindingReader;
        _artifactStore = artifactStore;
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort;
        _workflowStoragePort = workflowStoragePort;
    }

    public async Task<IReadOnlyList<WorkflowSummary>> ListAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var workflows = _workflowQueryPort != null
            ? await _workflowQueryPort.ListAsync(normalizedScopeId, ct)
            : await SendAsync<List<ScopeWorkflowSummary>>(
                HttpMethod.Get,
                $"/api/scopes/{Uri.EscapeDataString(normalizedScopeId)}/workflows",
                body: null,
                ct) ?? [];

        var summaries = workflows
            .OrderByDescending(static item => item.UpdatedAt)
            .Select(workflow => ToWorkflowSummary(normalizedScopeId, workflow))
            .ToList();

        return await MergeStoredWorkflowSummariesAsync(normalizedScopeId, summaries, ct);
    }

    public async Task<WorkflowFileResponse?> GetAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedWorkflowId = NormalizeRequired(workflowId, nameof(workflowId));

        if (_workflowQueryPort != null && _workflowActorBindingReader != null)
        {
            var workflow = await _workflowQueryPort.GetByWorkflowIdAsync(normalizedScopeId, normalizedWorkflowId, ct);
            if (workflow != null)
            {
                var binding = string.IsNullOrWhiteSpace(workflow.ActorId)
                    ? null
                    : await _workflowActorBindingReader.GetAsync(workflow.ActorId, ct);

                var yaml = binding?.WorkflowYaml ?? string.Empty;

                // Fallback: if binding projection hasn't materialized the YAML yet,
                // try the artifact store which is written synchronously during save.
                if (string.IsNullOrWhiteSpace(yaml) &&
                    _artifactStore != null &&
                    !string.IsNullOrWhiteSpace(workflow.ServiceKey))
                {
                    // Try with known revision ID.
                    if (!string.IsNullOrWhiteSpace(workflow.ActiveRevisionId))
                    {
                        var artifact = await _artifactStore.GetAsync(workflow.ServiceKey, workflow.ActiveRevisionId, ct);
                        yaml = artifact?.DeploymentPlan?.WorkflowPlan?.WorkflowYaml ?? string.Empty;
                    }

                    // If revision ID is empty (deployment snapshot not ready yet),
                    // scan for the latest revision via the service lifecycle query.
                    if (string.IsNullOrWhiteSpace(yaml) &&
                        _workflowQueryPort != null &&
                        _serviceLifecycleQueryPort != null)
                    {
                        var identity = new ServiceIdentity
                        {
                            TenantId = normalizedScopeId,
                            AppId = "default",
                            Namespace = "default",
                            ServiceId = normalizedWorkflowId,
                        };
                        var svc = await _serviceLifecycleQueryPort.GetServiceAsync(identity, ct);
                        var revId = svc?.ActiveServingRevisionId;
                        if (string.IsNullOrWhiteSpace(revId))
                            revId = svc?.DefaultServingRevisionId;
                        if (!string.IsNullOrWhiteSpace(revId))
                        {
                            var artifact = await _artifactStore.GetAsync(workflow.ServiceKey, revId, ct);
                            yaml = artifact?.DeploymentPlan?.WorkflowPlan?.WorkflowYaml ?? string.Empty;
                        }
                    }
                }

                return ToWorkflowFileResponse(
                    normalizedScopeId,
                    workflow,
                    yaml,
                    layout: TryReadPersistedLayout(normalizedScopeId, normalizedWorkflowId),
                    findingsFallbackMessage: "Workflow YAML is not available yet.");
            }

            var storedWorkflow = await TryGetStoredWorkflowAsync(normalizedWorkflowId, ct);
            if (storedWorkflow != null)
            {
                return ToStoredWorkflowFileResponse(
                    normalizedScopeId,
                    storedWorkflow,
                    TryReadPersistedLayout(normalizedScopeId, normalizedWorkflowId));
            }

            return null;
        }

        var detail = await SendAsync<ScopeWorkflowDetail>(
            HttpMethod.Get,
            $"/api/scopes/{Uri.EscapeDataString(normalizedScopeId)}/workflows/{Uri.EscapeDataString(normalizedWorkflowId)}",
            body: null,
            ct,
            allowNotFound: true);

        if (detail == null || detail.Workflow == null)
        {
            var storedWorkflow = await TryGetStoredWorkflowAsync(normalizedWorkflowId, ct);
            return storedWorkflow == null
                ? null
                : ToStoredWorkflowFileResponse(
                    normalizedScopeId,
                    storedWorkflow,
                    TryReadPersistedLayout(normalizedScopeId, normalizedWorkflowId));
        }

        return ToWorkflowFileResponse(
            normalizedScopeId,
            detail.Workflow,
            detail.Source?.WorkflowYaml ?? string.Empty,
            layout: TryReadPersistedLayout(normalizedScopeId, normalizedWorkflowId),
            findingsFallbackMessage: "Workflow YAML is not available yet.");
    }

    public async Task<WorkflowFileResponse> SaveAsync(
        string scopeId,
        SaveWorkflowFileRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var requestedWorkflowName = string.IsNullOrWhiteSpace(request.WorkflowName)
            ? string.Empty
            : request.WorkflowName.Trim();
        var normalizedYaml = NormalizeRequired(request.Yaml, nameof(request.Yaml));
        if (!string.IsNullOrWhiteSpace(requestedWorkflowName))
        {
            normalizedYaml = AlignWorkflowYamlName(normalizedYaml, requestedWorkflowName);
        }

        var parsed = _yamlDocumentService.Parse(normalizedYaml);
        var workflowName = !string.IsNullOrWhiteSpace(requestedWorkflowName)
            ? requestedWorkflowName
            : !string.IsNullOrWhiteSpace(parsed.Document?.Name)
            ? parsed.Document.Name.Trim()
            : NormalizeRequired(request.WorkflowName, nameof(request.WorkflowName));
        var workflowId = string.IsNullOrWhiteSpace(request.WorkflowId)
            ? StudioDocumentIdNormalizer.Normalize(workflowName, "workflow")
            : NormalizeRequired(request.WorkflowId, nameof(request.WorkflowId));
        var displayName = string.IsNullOrWhiteSpace(requestedWorkflowName)
            ? workflowId
            : requestedWorkflowName;

        ScopeWorkflowUpsertResult upsert;
        if (_workflowCommandPort != null)
        {
            upsert = await _workflowCommandPort.UpsertAsync(
                new ScopeWorkflowUpsertRequest(
                    normalizedScopeId,
                    workflowId,
                    normalizedYaml,
                    workflowName,
                    displayName),
                ct);
        }
        else
        {
            upsert = await SendAsync<ScopeWorkflowUpsertResult>(
                HttpMethod.Put,
                $"/api/scopes/{Uri.EscapeDataString(normalizedScopeId)}/workflows/{Uri.EscapeDataString(workflowId)}",
                new RemoteUpsertRequest(
                    normalizedYaml,
                    workflowName,
                    displayName,
                    InlineWorkflowYamls: null,
                    RevisionId: null),
                ct) ?? throw new InvalidOperationException("Workflow save returned an empty response.");
        }

        PersistLayout(normalizedScopeId, workflowId, request.Layout);

        try
        {
            if (_workflowStoragePort != null)
            {
                await _workflowStoragePort.UploadWorkflowYamlAsync(workflowId, workflowName, normalizedYaml, ct);
            }
        }
        catch
        {
            // Don't fail the save if chrono-storage upload fails
        }

        return ToWorkflowFileResponse(
            normalizedScopeId,
            upsert.Workflow,
            normalizedYaml,
            request.Layout,
            parsed);
    }

    public async Task DeleteDraftAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedWorkflowId = NormalizeRequired(workflowId, nameof(workflowId));

        try
        {
            if (_workflowStoragePort != null)
            {
                await _workflowStoragePort.DeleteWorkflowYamlAsync(normalizedWorkflowId, ct);
            }
        }
        catch
        {
        }

        DeletePersistedLayout(normalizedScopeId, normalizedWorkflowId);
    }

    private string AlignWorkflowYamlName(string yaml, string workflowName)
    {
        if (string.IsNullOrWhiteSpace(yaml) || string.IsNullOrWhiteSpace(workflowName))
            return yaml;

        var parsed = _yamlDocumentService.Parse(yaml);
        if (parsed.Document == null)
            return yaml;

        if (string.Equals(parsed.Document.Name?.Trim(), workflowName, StringComparison.Ordinal))
            return yaml;

        return _yamlDocumentService.Serialize(parsed.Document with
        {
            Name = workflowName,
        });
    }

    public static WorkflowDirectorySummary CreateScopeDirectory(string scopeId) =>
        new(
            BuildScopeDirectoryId(scopeId),
            scopeId,
            $"scope://{scopeId}",
            true);

    public static string BuildScopeDirectoryId(string scopeId) =>
        $"scope:{NormalizeRequired(scopeId, nameof(scopeId))}";

    private WorkflowFileResponse ToWorkflowFileResponse(
        string scopeId,
        ScopeWorkflowSummary workflow,
        string yaml,
        WorkflowLayoutDocument? layout,
        WorkflowParseResult? parseResult = null,
        string? findingsFallbackMessage = null)
    {
        var parse = parseResult ?? _yamlDocumentService.Parse(yaml);
        var findings = parse.Findings;
        if (parse.Document == null &&
            findings.Count == 0 &&
            !string.IsNullOrWhiteSpace(findingsFallbackMessage))
        {
            findings =
            [
                new ValidationFinding(
                    ValidationLevel.Error,
                    "/",
                    findingsFallbackMessage),
            ];
        }

        var scopeDirectory = CreateScopeDirectory(scopeId);
        return new WorkflowFileResponse(
            workflow.WorkflowId,
            !string.IsNullOrWhiteSpace(parse.Document?.Name) ? parse.Document.Name : ResolveWorkflowDisplayName(workflow),
            $"{workflow.WorkflowId}.yaml",
            $"{scopeDirectory.Path}/{workflow.WorkflowId}.yaml",
            scopeDirectory.DirectoryId,
            scopeDirectory.Label,
            yaml,
            parse.Document,
            layout,
            findings,
            workflow.UpdatedAt);
    }

    private static WorkflowSummary ToWorkflowSummary(string scopeId, ScopeWorkflowSummary workflow)
    {
        var scopeDirectory = CreateScopeDirectory(scopeId);
        return new WorkflowSummary(
            workflow.WorkflowId,
            ResolveWorkflowDisplayName(workflow),
            string.Empty,
            $"{workflow.WorkflowId}.yaml",
            $"{scopeDirectory.Path}/{workflow.WorkflowId}.yaml",
            scopeDirectory.DirectoryId,
            scopeDirectory.Label,
            0,
            false,
            workflow.UpdatedAt);
    }

    private static string ResolveWorkflowDisplayName(ScopeWorkflowSummary workflow)
    {
        if (!string.IsNullOrWhiteSpace(workflow.DisplayName))
            return workflow.DisplayName;
        if (!string.IsNullOrWhiteSpace(workflow.WorkflowName))
            return workflow.WorkflowName;

        return workflow.WorkflowId;
    }

    private async Task<IReadOnlyList<WorkflowSummary>> MergeStoredWorkflowSummariesAsync(
        string scopeId,
        IReadOnlyList<WorkflowSummary> runtimeSummaries,
        CancellationToken ct)
    {
        if (_workflowStoragePort == null)
            return runtimeSummaries;

        IReadOnlyList<StoredWorkflowYaml> storedWorkflows;
        try
        {
            storedWorkflows = await _workflowStoragePort.ListWorkflowYamlsAsync(ct);
        }
        catch
        {
            return runtimeSummaries;
        }

        if (storedWorkflows.Count == 0)
            return runtimeSummaries;

        var merged = runtimeSummaries.ToDictionary(summary => summary.WorkflowId, StringComparer.Ordinal);
        foreach (var storedWorkflow in storedWorkflows)
        {
            if (merged.ContainsKey(storedWorkflow.WorkflowId))
                continue;

            merged[storedWorkflow.WorkflowId] = ToStoredWorkflowSummary(scopeId, storedWorkflow);
        }

        return merged.Values
            .OrderByDescending(static item => item.UpdatedAtUtc)
            .ToList();
    }

    private async Task<StoredWorkflowYaml?> TryGetStoredWorkflowAsync(string workflowId, CancellationToken ct)
    {
        if (_workflowStoragePort == null)
            return null;

        try
        {
            return await _workflowStoragePort.GetWorkflowYamlAsync(workflowId, ct);
        }
        catch
        {
            return null;
        }
    }

    private WorkflowSummary ToStoredWorkflowSummary(
        string scopeId,
        StoredWorkflowYaml storedWorkflow)
    {
        var parse = _yamlDocumentService.Parse(storedWorkflow.Yaml);
        var scopeDirectory = CreateScopeDirectory(scopeId);
        return new WorkflowSummary(
            storedWorkflow.WorkflowId,
            ResolveStoredWorkflowName(storedWorkflow, parse),
            parse.Document?.Description ?? string.Empty,
            $"{storedWorkflow.WorkflowId}.yaml",
            $"{scopeDirectory.Path}/{storedWorkflow.WorkflowId}.yaml",
            scopeDirectory.DirectoryId,
            scopeDirectory.Label,
            parse.Document?.Steps.Count ?? 0,
            TryReadPersistedLayout(scopeId, storedWorkflow.WorkflowId) != null,
            storedWorkflow.UpdatedAtUtc ?? DateTimeOffset.UtcNow);
    }

    private WorkflowFileResponse ToStoredWorkflowFileResponse(
        string scopeId,
        StoredWorkflowYaml storedWorkflow,
        WorkflowLayoutDocument? layout)
    {
        var parse = _yamlDocumentService.Parse(storedWorkflow.Yaml);
        var scopeDirectory = CreateScopeDirectory(scopeId);
        return new WorkflowFileResponse(
            storedWorkflow.WorkflowId,
            ResolveStoredWorkflowName(storedWorkflow, parse),
            $"{storedWorkflow.WorkflowId}.yaml",
            $"{scopeDirectory.Path}/{storedWorkflow.WorkflowId}.yaml",
            scopeDirectory.DirectoryId,
            scopeDirectory.Label,
            storedWorkflow.Yaml,
            parse.Document,
            layout,
            parse.Findings,
            storedWorkflow.UpdatedAtUtc);
    }

    private static string ResolveStoredWorkflowName(
        StoredWorkflowYaml storedWorkflow,
        WorkflowParseResult parseResult)
    {
        var parsedName = parseResult.Document?.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(parsedName))
            return parsedName;

        var storedName = storedWorkflow.WorkflowName?.Trim();
        if (!string.IsNullOrWhiteSpace(storedName))
            return storedName;

        return storedWorkflow.WorkflowId;
    }

    private async Task<T?> SendAsync<T>(
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken ct,
        bool allowNotFound = false)
    {
        var client = _httpClientFactory.CreateClient(BackendClientName);
        using var request = new HttpRequestMessage(method, relativePath);
        if (body != null)
            request.Content = JsonContent.Create(body);

        using var response = await client.SendAsync(request, ct);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            return default;

        if (!response.IsSuccessStatusCode)
        {
            throw await BuildApiExceptionAsync(response, ct);
        }

        if (response.Content == null)
            return default;

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!IsJsonContentType(mediaType))
        {
            throw new AppApiException(
                StatusCodes.Status502BadGateway,
                AppApiErrors.BackendInvalidResponseCode,
                "Workflow backend returned a non-JSON response.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        if (stream == Stream.Null)
            return default;

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
        }
        catch (JsonException ex)
        {
            throw new AppApiException(
                StatusCodes.Status502BadGateway,
                AppApiErrors.BackendInvalidResponseCode,
                "Workflow backend returned invalid JSON.",
                innerException: ex);
        }
    }

    private static async Task<AppApiException> BuildApiExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var content = response.Content;
        var mediaType = response.Content?.Headers.ContentType?.MediaType;
        var redirectUrl = ResolveRedirectUrl(response);
        if (redirectUrl != null &&
            response.StatusCode is HttpStatusCode.Moved or
                HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or
                HttpStatusCode.TemporaryRedirect or
                HttpStatusCode.PermanentRedirect)
        {
            return new AppApiException(
                StatusCodes.Status401Unauthorized,
                AppApiErrors.BackendAuthRequiredCode,
                "Backend authentication required.",
                redirectUrl);
        }

        if (content == null)
        {
            return new AppApiException(
                (int)response.StatusCode,
                "WORKFLOW_REQUEST_FAILED",
                $"Workflow request failed with status {(int)response.StatusCode}.",
                redirectUrl);
        }

        try
        {
            var payload = await content.ReadFromJsonAsync<RemoteErrorResponse>(JsonOptions, ct);
            if (!string.IsNullOrWhiteSpace(payload?.Message))
            {
                return new AppApiException(
                    (int)response.StatusCode,
                    string.IsNullOrWhiteSpace(payload.Code) ? "WORKFLOW_REQUEST_FAILED" : payload.Code.Trim(),
                    payload.Message.Trim(),
                    redirectUrl);
            }
        }
        catch
        {
            // Ignore body parse failures and fall through to status-based message.
        }

        if (IsHtmlContentType(mediaType))
        {
            return new AppApiException(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status502BadGateway,
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? AppApiErrors.BackendAuthRequiredCode
                    : AppApiErrors.BackendInvalidResponseCode,
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "Backend authentication required."
                    : "Workflow backend returned HTML for an API request.",
                redirectUrl);
        }

        return new AppApiException(
            (int)response.StatusCode,
            "WORKFLOW_REQUEST_FAILED",
            $"Workflow request failed with status {(int)response.StatusCode}.",
            redirectUrl);
    }

    private static WorkflowLayoutDocument? TryReadPersistedLayout(string scopeId, string workflowId)
    {
        var path = BuildLayoutCachePath(scopeId, workflowId);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<WorkflowLayoutDocument>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void PersistLayout(string scopeId, string workflowId, WorkflowLayoutDocument? layout)
    {
        var path = BuildLayoutCachePath(scopeId, workflowId);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        if (layout == null)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        var json = JsonSerializer.Serialize(layout, JsonOptions);
        File.WriteAllText(path, json);
    }

    private static void DeletePersistedLayout(string scopeId, string workflowId)
    {
        var path = BuildLayoutCachePath(scopeId, workflowId);
        if (File.Exists(path))
            File.Delete(path);
    }

    private static string BuildLayoutCachePath(string scopeId, string workflowId) =>
        Path.Combine(
            AevatarPaths.Root,
            "app",
            "scope-workflow-layouts",
            $"{StudioDocumentIdNormalizer.Normalize(scopeId, "scope")}--{StudioDocumentIdNormalizer.Normalize(workflowId, "workflow")}.json");

    private static string NormalizeRequired(string value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");

        return normalized;
    }

    private static string? ResolveRedirectUrl(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location == null)
            return null;

        if (location.IsAbsoluteUri)
            return location.ToString();

        var requestUri = response.RequestMessage?.RequestUri;
        return requestUri == null
            ? location.ToString()
            : new Uri(requestUri, location).ToString();
    }

    private static bool IsJsonContentType(string? mediaType) =>
        !string.IsNullOrWhiteSpace(mediaType) &&
        (mediaType.Contains("application/json", StringComparison.OrdinalIgnoreCase) ||
         mediaType.Contains("+json", StringComparison.OrdinalIgnoreCase));

    private static bool IsHtmlContentType(string? mediaType) =>
        !string.IsNullOrWhiteSpace(mediaType) &&
        (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase) ||
         mediaType.Contains("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));

    private sealed record RemoteUpsertRequest(
        string WorkflowYaml,
        string? WorkflowName,
        string? DisplayName,
        Dictionary<string, string>? InlineWorkflowYamls,
        string? RevisionId);

    private sealed record RemoteErrorResponse(string? Code, string? Message);
}
