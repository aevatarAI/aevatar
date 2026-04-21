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
using Microsoft.Extensions.Logging;

using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Application.Studio.Services;
namespace Aevatar.Studio.Application;

public sealed class AppScopedWorkflowService
{
    private const string BackendClientName = "AppBridgeBackend";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IScopeWorkflowQueryPort? _workflowQueryPort;
    private readonly IWorkflowActorBindingReader? _workflowActorBindingReader;
    private readonly IServiceRevisionArtifactStore? _artifactStore;
    private readonly IServiceLifecycleQueryPort? _serviceLifecycleQueryPort;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWorkflowYamlDocumentService _yamlDocumentService;
    private readonly IWorkflowDraftStore? _workflowDraftStore;
    private readonly ILogger<AppScopedWorkflowService>? _logger;

    public AppScopedWorkflowService(
        IHttpClientFactory httpClientFactory,
        IWorkflowYamlDocumentService yamlDocumentService,
        IScopeWorkflowQueryPort? workflowQueryPort = null,
        IWorkflowActorBindingReader? workflowActorBindingReader = null,
        IServiceRevisionArtifactStore? artifactStore = null,
        IServiceLifecycleQueryPort? serviceLifecycleQueryPort = null,
        IWorkflowDraftStore? workflowDraftStore = null,
        ILogger<AppScopedWorkflowService>? logger = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _yamlDocumentService = yamlDocumentService ?? throw new ArgumentNullException(nameof(yamlDocumentService));
        _workflowQueryPort = workflowQueryPort;
        _workflowActorBindingReader = workflowActorBindingReader;
        _artifactStore = artifactStore;
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort;
        _workflowDraftStore = workflowDraftStore;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WorkflowDraftSummary>> ListDraftsAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var persistedLayoutWorkflowIds = ListPersistedLayoutWorkflowIds(normalizedScopeId);
        var draftsById = await ListDraftsByIdAsync(normalizedScopeId, ct);
        return draftsById.Values
            .Select(draft => ToDraftWorkflowSummary(
                normalizedScopeId,
                draft,
                persistedLayoutWorkflowIds))
            .OrderByDescending(static item => item.UpdatedAtUtc)
            .ToList();
    }

    public async Task<WorkflowDraftResponse?> GetDraftAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedWorkflowId = NormalizeRequired(workflowId, nameof(workflowId));
        var draft = await TryGetDraftAsync(normalizedScopeId, normalizedWorkflowId, ct);
        return draft == null
            ? null
            : ToDraftWorkflowResponse(
                normalizedScopeId,
                draft,
                TryReadPersistedLayout(normalizedScopeId, normalizedWorkflowId));
    }

    public Task<WorkflowDraftResponse> CreateDraftAsync(
        string scopeId,
        SaveWorkflowDraftRequest request,
        CancellationToken ct = default)
        => SaveDraftAsync(scopeId, workflowId: null, request, ct);

    public Task<WorkflowDraftResponse> UpdateDraftAsync(
        string scopeId,
        string workflowId,
        SaveWorkflowDraftRequest request,
        CancellationToken ct = default)
        => SaveDraftAsync(scopeId, NormalizeRequired(workflowId, nameof(workflowId)), request, ct);

    private async Task<WorkflowDraftResponse> SaveDraftAsync(
        string scopeId,
        string? workflowId,
        SaveWorkflowDraftRequest request,
        CancellationToken ct)
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
        var draftStore = _workflowDraftStore
            ?? throw new InvalidOperationException("Scoped workflow draft storage is not configured.");
        var savedAtUtc = DateTimeOffset.UtcNow;
        var normalizedWorkflowId = string.IsNullOrWhiteSpace(workflowId)
            ? await CreateScopedWorkflowIdAsync(normalizedScopeId, workflowName, ct)
            : workflowId;

        if (!string.IsNullOrWhiteSpace(workflowId))
        {
            var existingDraft = await draftStore.GetDraftAsync(normalizedScopeId, normalizedWorkflowId, ct);
            if (existingDraft == null)
            {
                throw new WorkflowDraftNotFoundException(normalizedWorkflowId);
            }
        }

        // Scoped workspace save persists an editor draft; publish stays on the scope-binding flow.
        await draftStore.SaveDraftAsync(normalizedScopeId, normalizedWorkflowId, workflowName, normalizedYaml, ct);

        PersistLayout(normalizedScopeId, normalizedWorkflowId, request.Layout);

        return ToDraftWorkflowResponse(
            normalizedScopeId,
            new WorkflowDraft(
                normalizedWorkflowId,
                workflowName,
                normalizedYaml,
                savedAtUtc),
            request.Layout);
    }

    public async Task DeleteDraftAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedWorkflowId = NormalizeRequired(workflowId, nameof(workflowId));
        var draftStore = _workflowDraftStore
            ?? throw new InvalidOperationException("Scoped workflow draft storage is not configured.");
        var existingDraft = await draftStore.GetDraftAsync(normalizedScopeId, normalizedWorkflowId, ct);
        if (existingDraft == null)
        {
            throw new WorkflowDraftNotFoundException(normalizedWorkflowId);
        }

        await draftStore.DeleteDraftAsync(normalizedScopeId, normalizedWorkflowId, ct);

        DeletePersistedLayout(normalizedScopeId, normalizedWorkflowId);
    }

    #pragma warning disable CS0618
    [Obsolete("Use ListDraftsAsync.")]
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

        var draftsById = await ListDraftsByIdAsync(normalizedScopeId, ct);
        var persistedLayoutWorkflowIds = ListPersistedLayoutWorkflowIds(normalizedScopeId);
        var summaries = workflows
            .OrderByDescending(static item => item.UpdatedAt)
            .Select(workflow => ToLegacyWorkflowSummary(
                normalizedScopeId,
                workflow,
                persistedLayoutWorkflowIds,
                draftsById.TryGetValue(workflow.WorkflowId, out var draft)
                    ? draft
                    : null))
            .ToList();

        return MergeLegacyDraftSummaries(normalizedScopeId, summaries, draftsById, persistedLayoutWorkflowIds);
    }

    [Obsolete("Use GetDraftAsync.")]
    public async Task<WorkflowFileResponse?> GetAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedWorkflowId = NormalizeRequired(workflowId, nameof(workflowId));
        var draft = await TryGetDraftAsync(normalizedScopeId, normalizedWorkflowId, ct);

        if (draft != null)
        {
            return ToLegacyDraftWorkflowFileResponse(
                normalizedScopeId,
                draft,
                TryReadPersistedLayout(normalizedScopeId, normalizedWorkflowId));
        }

        if (_workflowQueryPort != null && _workflowActorBindingReader != null)
        {
            var workflow = await _workflowQueryPort.GetByWorkflowIdAsync(normalizedScopeId, normalizedWorkflowId, ct);
            if (workflow != null)
            {
                var binding = string.IsNullOrWhiteSpace(workflow.ActorId)
                    ? null
                    : await _workflowActorBindingReader.GetAsync(workflow.ActorId, ct);

                var yaml = binding?.WorkflowYaml ?? string.Empty;
                if (string.IsNullOrWhiteSpace(yaml) &&
                    _artifactStore != null &&
                    !string.IsNullOrWhiteSpace(workflow.ServiceKey))
                {
                    if (!string.IsNullOrWhiteSpace(workflow.ActiveRevisionId))
                    {
                        var artifact = await _artifactStore.GetAsync(workflow.ServiceKey, workflow.ActiveRevisionId, ct);
                        yaml = artifact?.DeploymentPlan?.WorkflowPlan?.WorkflowYaml ?? string.Empty;
                    }

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

                return ToLegacyCommittedWorkflowFileResponse(
                    normalizedScopeId,
                    workflow,
                    yaml,
                    TryReadPersistedLayout(normalizedScopeId, normalizedWorkflowId),
                    findingsFallbackMessage: "Workflow YAML is not available yet.");
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
            return null;

        return ToLegacyCommittedWorkflowFileResponse(
            normalizedScopeId,
            detail.Workflow,
            detail.Source?.WorkflowYaml ?? string.Empty,
            TryReadPersistedLayout(normalizedScopeId, normalizedWorkflowId),
            findingsFallbackMessage: "Workflow YAML is not available yet.");
    }

    [Obsolete("Use CreateDraftAsync or UpdateDraftAsync.")]
    public async Task<WorkflowFileResponse> SaveDraftAsync(
        string scopeId,
        SaveWorkflowFileRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var nextRequest = new SaveWorkflowDraftRequest(
            request.DirectoryId,
            request.WorkflowName,
            request.FileName,
            request.Yaml,
            request.Layout);
        var saved = string.IsNullOrWhiteSpace(request.WorkflowId)
            ? await CreateDraftAsync(scopeId, nextRequest, ct)
            : await UpdateDraftAsync(scopeId, request.WorkflowId, nextRequest, ct);
        return ToLegacyWorkflowFileResponse(saved);
    }
    #pragma warning restore CS0618

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

    private WorkflowCommittedResponse ToWorkflowCommittedResponse(
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

        return new WorkflowCommittedResponse(
            workflow.WorkflowId,
            !string.IsNullOrWhiteSpace(parse.Document?.Name) ? parse.Document.Name : ResolveWorkflowDisplayName(workflow),
            yaml,
            parse.Document,
            findings,
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

    private async Task<IReadOnlyDictionary<string, WorkflowDraft>> ListDraftsByIdAsync(
        string scopeId,
        CancellationToken ct)
    {
        if (_workflowDraftStore == null)
            return new Dictionary<string, WorkflowDraft>(StringComparer.Ordinal);

        try
        {
            return (await _workflowDraftStore.ListDraftsAsync(scopeId, ct))
                .GroupBy(static workflow => workflow.WorkflowId, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group
                        .OrderByDescending(static workflow => workflow.UpdatedAtUtc ?? DateTimeOffset.MinValue)
                        .First(),
                    StringComparer.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                exception,
                "Failed to list stored scoped workflow drafts for scope {ScopeId}. Falling back to runtime workflows only.",
                scopeId);
            return new Dictionary<string, WorkflowDraft>(StringComparer.Ordinal);
        }
    }

    private async Task<WorkflowDraft?> TryGetDraftAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct)
    {
        if (_workflowDraftStore == null)
            return null;

        try
        {
            return await _workflowDraftStore.GetDraftAsync(scopeId, workflowId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                exception,
                "Failed to load stored scoped workflow draft {WorkflowId} for scope {ScopeId}. Falling back to runtime workflow content.",
                workflowId,
                scopeId);
            return null;
        }
    }

    private WorkflowDraftSummary ToDraftWorkflowSummary(
        string scopeId,
        WorkflowDraft draft,
        IReadOnlySet<string> persistedLayoutWorkflowIds)
    {
        var parse = _yamlDocumentService.Parse(draft.Yaml);
        var scopeDirectory = CreateScopeDirectory(scopeId);
        return new WorkflowDraftSummary(
            draft.WorkflowId,
            ResolveDraftWorkflowName(draft, parse),
            parse.Document?.Description ?? string.Empty,
            $"{draft.WorkflowId}.yaml",
            $"{scopeDirectory.Path}/{draft.WorkflowId}.yaml",
            scopeDirectory.DirectoryId,
            scopeDirectory.Label,
            parse.Document?.Steps.Count ?? 0,
            HasPersistedLayout(persistedLayoutWorkflowIds, draft.WorkflowId),
            draft.UpdatedAtUtc ?? DateTimeOffset.UtcNow);
    }

    private WorkflowSummary ToLegacyWorkflowSummary(
        string scopeId,
        ScopeWorkflowSummary workflow,
        IReadOnlySet<string> persistedLayoutWorkflowIds,
        WorkflowDraft? draft)
    {
        var parse = !string.IsNullOrWhiteSpace(draft?.Yaml)
            ? _yamlDocumentService.Parse(draft.Yaml)
            : null;
        var scopeDirectory = CreateScopeDirectory(scopeId);
        return new WorkflowSummary(
            workflow.WorkflowId,
            ResolveWorkflowSummaryName(workflow, draft, parse),
            parse?.Document?.Description ?? string.Empty,
            $"{workflow.WorkflowId}.yaml",
            $"{scopeDirectory.Path}/{workflow.WorkflowId}.yaml",
            scopeDirectory.DirectoryId,
            scopeDirectory.Label,
            parse?.Document?.Steps.Count ?? 0,
            HasPersistedLayout(persistedLayoutWorkflowIds, workflow.WorkflowId),
            ResolveWorkflowSummaryUpdatedAt(workflow, draft));
    }

    private IReadOnlyList<WorkflowSummary> MergeLegacyDraftSummaries(
        string scopeId,
        IReadOnlyList<WorkflowSummary> runtimeSummaries,
        IReadOnlyDictionary<string, WorkflowDraft> draftsById,
        IReadOnlySet<string> persistedLayoutWorkflowIds)
    {
        if (draftsById.Count == 0)
            return runtimeSummaries;

        var merged = runtimeSummaries.ToDictionary(summary => summary.WorkflowId, StringComparer.Ordinal);
        foreach (var draft in draftsById.Values)
        {
            if (merged.ContainsKey(draft.WorkflowId))
                continue;

            var nextDraftSummary = ToDraftWorkflowSummary(scopeId, draft, persistedLayoutWorkflowIds);
            merged[draft.WorkflowId] = new WorkflowSummary(
                nextDraftSummary.WorkflowId,
                nextDraftSummary.Name,
                nextDraftSummary.Description,
                nextDraftSummary.FileName,
                nextDraftSummary.FilePath,
                nextDraftSummary.DirectoryId,
                nextDraftSummary.DirectoryLabel,
                nextDraftSummary.StepCount,
                nextDraftSummary.HasLayout,
                nextDraftSummary.UpdatedAtUtc);
        }

        return merged.Values
            .OrderByDescending(static item => item.UpdatedAtUtc)
            .ToList();
    }

    private IReadOnlySet<string> ListPersistedLayoutWorkflowIds(string scopeId)
    {
        var directory = BuildLayoutCacheDirectoryPath();
        if (!Directory.Exists(directory))
            return new HashSet<string>(StringComparer.Ordinal);

        var normalizedScopeId = StudioDocumentIdNormalizer.Normalize(scopeId, "scope");
        var prefix = $"{normalizedScopeId}--";
        var workflowIds = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, $"{prefix}*.json"))
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(fileName) ||
                    !fileName.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var normalizedWorkflowId = fileName[prefix.Length..];
                if (normalizedWorkflowId.Length > 0)
                {
                    workflowIds.Add(normalizedWorkflowId);
                }
            }
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                exception,
                "Failed to enumerate scoped workflow layout sidecars for scope {ScopeId}. Layout availability will be omitted from workflow summaries.",
                scopeId);
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return workflowIds;
    }

    private static bool HasPersistedLayout(
        IReadOnlySet<string> persistedLayoutWorkflowIds,
        string workflowId) =>
        persistedLayoutWorkflowIds.Contains(StudioDocumentIdNormalizer.Normalize(workflowId, "workflow"));

    private static string ResolveWorkflowSummaryName(
        ScopeWorkflowSummary workflow,
        WorkflowDraft? draft,
        WorkflowParseResult? parseResult)
    {
        var parsedName = parseResult?.Document?.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(parsedName))
            return parsedName;

        var storedName = draft?.WorkflowName?.Trim();
        if (!string.IsNullOrWhiteSpace(storedName))
            return storedName;

        return ResolveWorkflowDisplayName(workflow);
    }

    private static DateTimeOffset ResolveWorkflowSummaryUpdatedAt(
        ScopeWorkflowSummary workflow,
        WorkflowDraft? draft)
    {
        if (draft?.UpdatedAtUtc is { } storedUpdatedAtUtc &&
            storedUpdatedAtUtc > workflow.UpdatedAt)
        {
            return storedUpdatedAtUtc;
        }

        return workflow.UpdatedAt;
    }

    private WorkflowDraftResponse ToDraftWorkflowResponse(
        string scopeId,
        WorkflowDraft draft,
        WorkflowLayoutDocument? layout)
    {
        var scopeDirectory = CreateScopeDirectory(scopeId);
        return new WorkflowDraftResponse(
            draft.WorkflowId,
            ResolveDraftWorkflowName(draft, _yamlDocumentService.Parse(draft.Yaml)),
            $"{draft.WorkflowId}.yaml",
            $"{scopeDirectory.Path}/{draft.WorkflowId}.yaml",
            scopeDirectory.DirectoryId,
            scopeDirectory.Label,
            draft.Yaml,
            layout,
            draft.UpdatedAtUtc ?? DateTimeOffset.UtcNow);
    }

    private WorkflowFileResponse ToLegacyDraftWorkflowFileResponse(
        string scopeId,
        WorkflowDraft draft,
        WorkflowLayoutDocument? layout)
    {
        var parse = _yamlDocumentService.Parse(draft.Yaml);
        var scopeDirectory = CreateScopeDirectory(scopeId);
        return new WorkflowFileResponse(
            draft.WorkflowId,
            ResolveDraftWorkflowName(draft, parse),
            $"{draft.WorkflowId}.yaml",
            $"{scopeDirectory.Path}/{draft.WorkflowId}.yaml",
            scopeDirectory.DirectoryId,
            scopeDirectory.Label,
            draft.Yaml,
            parse.Document,
            layout,
            parse.Findings,
            draft.UpdatedAtUtc);
    }

    private WorkflowFileResponse ToLegacyCommittedWorkflowFileResponse(
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

    private WorkflowFileResponse ToLegacyWorkflowFileResponse(WorkflowDraftResponse draftResponse)
    {
        var parse = _yamlDocumentService.Parse(draftResponse.Yaml);
        return new WorkflowFileResponse(
            draftResponse.WorkflowId,
            draftResponse.Name,
            draftResponse.FileName,
            draftResponse.FilePath,
            draftResponse.DirectoryId,
            draftResponse.DirectoryLabel,
            draftResponse.Yaml,
            parse.Document,
            draftResponse.Layout,
            parse.Findings,
            draftResponse.UpdatedAtUtc);
    }

    private static string ResolveDraftWorkflowName(
        WorkflowDraft draft,
        WorkflowParseResult parseResult)
    {
        var parsedName = parseResult.Document?.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(parsedName))
            return parsedName;

        var storedName = draft.WorkflowName?.Trim();
        if (!string.IsNullOrWhiteSpace(storedName))
            return storedName;

        return draft.WorkflowId;
    }

    private async Task<string> CreateScopedWorkflowIdAsync(
        string scopeId,
        string workflowName,
        CancellationToken ct)
    {
        var baseWorkflowId = StudioDocumentIdNormalizer.Normalize(workflowName, "workflow");
        var draftStore = _workflowDraftStore
            ?? throw new InvalidOperationException("Scoped workflow draft storage is not configured.");
        var existingIds = (await draftStore.ListDraftsAsync(scopeId, ct))
            .Select(static draft => draft.WorkflowId)
            .ToHashSet(StringComparer.Ordinal);
        if (!existingIds.Contains(baseWorkflowId))
        {
            return baseWorkflowId;
        }

        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            var candidate = $"{baseWorkflowId}-{suffix}";
            if (!existingIds.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unable to allocate a unique scoped workflow draft id.");
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

    private static string BuildLayoutCacheDirectoryPath() =>
        Path.Combine(
            AevatarPaths.Root,
            "app",
            "scope-workflow-layouts");

    private static string BuildLayoutCachePath(string scopeId, string workflowId) =>
        Path.Combine(
            BuildLayoutCacheDirectoryPath(),
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

    private sealed record RemoteErrorResponse(string? Code, string? Message);
}
