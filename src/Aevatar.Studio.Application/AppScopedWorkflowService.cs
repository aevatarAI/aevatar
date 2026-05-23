using System.Net;
using Microsoft.AspNetCore.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Aevatar.Configuration;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Domain.Studio.Models;
using Microsoft.Extensions.Logging;

using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Application.Studio.Services;
namespace Aevatar.Studio.Application;

// Refactor (iter42/issue-864-studio-workspace-execution-fact-owner):
//   Old pattern: Studio executions/workspace facts mixed FileStudioWorkspaceStore JSON, draft index sidecars, and authoritative server UI/layout state across multiple owners.
//   New principle: Studio executions are a bounded ServiceRunGAgent readmodel facade; UI/layout/draft index are deleted/downgraded to client cache or derived from existing actor-backed sources. No new history/draft index actor.
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
    private readonly IStudioWorkspaceQueryPort? _workspaceQueryPort;
    private readonly IStudioWorkspaceCommandPort? _workspaceCommandPort;
    private readonly ILogger<AppScopedWorkflowService>? _logger;

    public AppScopedWorkflowService(
        IHttpClientFactory httpClientFactory,
        IWorkflowYamlDocumentService yamlDocumentService,
        IScopeWorkflowQueryPort? workflowQueryPort = null,
        IWorkflowActorBindingReader? workflowActorBindingReader = null,
        IServiceRevisionArtifactStore? artifactStore = null,
        IServiceLifecycleQueryPort? serviceLifecycleQueryPort = null,
        IStudioWorkspaceQueryPort? workspaceQueryPort = null,
        IStudioWorkspaceCommandPort? workspaceCommandPort = null,
        ILogger<AppScopedWorkflowService>? logger = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _yamlDocumentService = yamlDocumentService ?? throw new ArgumentNullException(nameof(yamlDocumentService));
        _workflowQueryPort = workflowQueryPort;
        _workflowActorBindingReader = workflowActorBindingReader;
        _artifactStore = artifactStore;
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort;
        _workspaceQueryPort = workspaceQueryPort;
        _workspaceCommandPort = workspaceCommandPort;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WorkflowDraftSummary>> ListDraftsAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var draftsById = await ListDraftsByIdAsync(normalizedScopeId, ct);
        return draftsById.Values
            .Select(draft => ToDraftWorkflowSummary(
                normalizedScopeId,
                draft))
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
                draft);
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
        var workspaceQueryPort = _workspaceQueryPort
            ?? throw new InvalidOperationException("Scoped workflow workspace query port is not configured.");
        var workspaceCommandPort = _workspaceCommandPort
            ?? throw new InvalidOperationException("Scoped workflow workspace command port is not configured.");
        var workspace = await workspaceQueryPort.GetAsync(normalizedScopeId, ct);
        var savedAtUtc = DateTimeOffset.UtcNow;
        var normalizedWorkflowId = string.IsNullOrWhiteSpace(workflowId)
            ? CreateScopedWorkflowId(workflowName, workspace.Drafts.Select(static draft => draft.WorkflowId))
            : workflowId;

        var existingDraft = workspace.Drafts.FirstOrDefault(draft =>
            string.Equals(draft.WorkflowId, normalizedWorkflowId, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(workflowId))
        {
            if (existingDraft == null)
            {
                throw new WorkflowDraftNotFoundException(normalizedWorkflowId);
            }
        }

        var scopeDirectory = CreateScopeDirectory(normalizedScopeId);
        var fileName = EnsureYamlExtension(normalizedWorkflowId);
        var stored = new StudioWorkflowDraftRecord(
            WorkflowId: normalizedWorkflowId,
            Name: workflowName,
            FileName: fileName,
            FilePath: $"{scopeDirectory.Path}/{fileName}",
            DirectoryId: scopeDirectory.DirectoryId,
            DirectoryLabel: scopeDirectory.Label,
            Yaml: normalizedYaml,
            Layout: null,
            UpdatedAtUtc: savedAtUtc,
            CreatedAtUtc: existingDraft?.CreatedAtUtc ?? savedAtUtc,
            Version: existingDraft?.Version ?? 0);

        // Scoped workspace save persists an editor draft; publish stays on the scope-binding flow.
        await workspaceCommandPort.SaveDraftAsync(
            normalizedScopeId,
            stored,
            workspace.StateVersion,
            ct);

        return ToDraftWorkflowResponse(
            normalizedScopeId,
            stored);
    }

    public async Task DeleteDraftAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedWorkflowId = NormalizeRequired(workflowId, nameof(workflowId));
        var workspaceQueryPort = _workspaceQueryPort
            ?? throw new InvalidOperationException("Scoped workflow workspace query port is not configured.");
        var workspaceCommandPort = _workspaceCommandPort
            ?? throw new InvalidOperationException("Scoped workflow workspace command port is not configured.");
        var workspace = await workspaceQueryPort.GetAsync(normalizedScopeId, ct);
        var existingDraft = workspace.Drafts.FirstOrDefault(draft =>
            string.Equals(draft.WorkflowId, normalizedWorkflowId, StringComparison.Ordinal));
        if (existingDraft == null)
        {
            throw new WorkflowDraftNotFoundException(normalizedWorkflowId);
        }

        await workspaceCommandPort.DeleteDraftAsync(normalizedScopeId, normalizedWorkflowId, workspace.StateVersion, ct);
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
        var summaries = workflows
            .OrderByDescending(static item => item.UpdatedAt)
            .Select(workflow => ToLegacyWorkflowSummary(
                normalizedScopeId,
                workflow,
                draftsById.TryGetValue(workflow.WorkflowId, out var draft)
                    ? draft
                    : null))
            .ToList();

        return MergeLegacyDraftSummaries(normalizedScopeId, summaries, draftsById);
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
                draft);
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
                            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
                            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
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
                    layout: null,
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
            layout: null,
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
            Layout: null);
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

    // Refactor (iter42/issue-864-studio-workspace-execution-fact-owner):
    //   Old pattern: Studio executions/workspace facts mixed FileStudioWorkspaceStore JSON, draft index sidecars, and authoritative server UI/layout state across multiple owners.
    //   New principle: Studio executions are a bounded ServiceRunGAgent readmodel facade; UI/layout/draft index are deleted/downgraded to client cache or derived from existing actor-backed sources. No new history/draft index actor.
    private async Task<IReadOnlyDictionary<string, StudioWorkflowDraftRecord>> ListDraftsByIdAsync(
        string scopeId,
        CancellationToken ct)
    {
        if (_workspaceQueryPort == null)
            return new Dictionary<string, StudioWorkflowDraftRecord>(StringComparer.Ordinal);

        try
        {
            return (await _workspaceQueryPort.GetAsync(scopeId, ct)).Drafts
                .GroupBy(static workflow => workflow.WorkflowId, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group
                        .OrderByDescending(static workflow => workflow.UpdatedAtUtc)
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
            return new Dictionary<string, StudioWorkflowDraftRecord>(StringComparer.Ordinal);
        }
    }

    // Refactor (iter42/issue-864-studio-workspace-execution-fact-owner):
    //   Old pattern: Studio executions/workspace facts mixed FileStudioWorkspaceStore JSON, draft index sidecars, and authoritative server UI/layout state across multiple owners.
    //   New principle: Studio executions are a bounded ServiceRunGAgent readmodel facade; UI/layout/draft index are deleted/downgraded to client cache or derived from existing actor-backed sources. No new history/draft index actor.
    private async Task<StudioWorkflowDraftRecord?> TryGetDraftAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct)
    {
        if (_workspaceQueryPort == null)
            return null;

        try
        {
            var workspace = await _workspaceQueryPort.GetAsync(scopeId, ct);
            return workspace.Drafts.FirstOrDefault(draft =>
                string.Equals(draft.WorkflowId, workflowId, StringComparison.Ordinal));
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
        StudioWorkflowDraftRecord draft)
    {
        var parse = _yamlDocumentService.Parse(draft.Yaml);
        var scopeDirectory = CreateScopeDirectory(scopeId);
        return new WorkflowDraftSummary(
            draft.WorkflowId,
            ResolveDraftWorkflowName(draft, parse),
            parse.Document?.Description ?? string.Empty,
            string.IsNullOrWhiteSpace(draft.FileName) ? $"{draft.WorkflowId}.yaml" : draft.FileName,
            string.IsNullOrWhiteSpace(draft.FilePath) ? $"{scopeDirectory.Path}/{draft.WorkflowId}.yaml" : draft.FilePath,
            scopeDirectory.DirectoryId,
            scopeDirectory.Label,
            parse.Document?.Steps.Count ?? 0,
            HasLayout: false,
            draft.UpdatedAtUtc);
    }

    private WorkflowSummary ToLegacyWorkflowSummary(
        string scopeId,
        ScopeWorkflowSummary workflow,
        StudioWorkflowDraftRecord? draft)
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
            HasLayout: false,
            ResolveWorkflowSummaryUpdatedAt(workflow, draft));
    }

    private IReadOnlyList<WorkflowSummary> MergeLegacyDraftSummaries(
        string scopeId,
        IReadOnlyList<WorkflowSummary> runtimeSummaries,
        IReadOnlyDictionary<string, StudioWorkflowDraftRecord> draftsById)
    {
        if (draftsById.Count == 0)
            return runtimeSummaries;

        var merged = runtimeSummaries.ToDictionary(summary => summary.WorkflowId, StringComparer.Ordinal);
        foreach (var draft in draftsById.Values)
        {
            if (merged.ContainsKey(draft.WorkflowId))
                continue;

            var nextDraftSummary = ToDraftWorkflowSummary(scopeId, draft);
            merged[draft.WorkflowId] = new WorkflowSummary(
                nextDraftSummary.WorkflowId,
                nextDraftSummary.Name,
                nextDraftSummary.Description,
                nextDraftSummary.FileName,
                nextDraftSummary.FilePath,
                nextDraftSummary.DirectoryId,
                nextDraftSummary.DirectoryLabel,
                nextDraftSummary.StepCount,
                HasLayout: false,
                nextDraftSummary.UpdatedAtUtc);
        }

        return merged.Values
            .OrderByDescending(static item => item.UpdatedAtUtc)
            .ToList();
    }

    private static string ResolveWorkflowSummaryName(
        ScopeWorkflowSummary workflow,
        StudioWorkflowDraftRecord? draft,
        WorkflowParseResult? parseResult)
    {
        var parsedName = parseResult?.Document?.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(parsedName))
            return parsedName;

        var storedName = draft?.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(storedName))
            return storedName;

        return ResolveWorkflowDisplayName(workflow);
    }

    private static DateTimeOffset ResolveWorkflowSummaryUpdatedAt(
        ScopeWorkflowSummary workflow,
        StudioWorkflowDraftRecord? draft)
    {
        if (draft is not null &&
            draft.UpdatedAtUtc > workflow.UpdatedAt)
        {
            return draft.UpdatedAtUtc;
        }

        return workflow.UpdatedAt;
    }

    private WorkflowDraftResponse ToDraftWorkflowResponse(
        string scopeId,
        StudioWorkflowDraftRecord draft)
    {
        var scopeDirectory = CreateScopeDirectory(scopeId);
        return new WorkflowDraftResponse(
            draft.WorkflowId,
            ResolveDraftWorkflowName(draft, _yamlDocumentService.Parse(draft.Yaml)),
            string.IsNullOrWhiteSpace(draft.FileName) ? $"{draft.WorkflowId}.yaml" : draft.FileName,
            string.IsNullOrWhiteSpace(draft.FilePath) ? $"{scopeDirectory.Path}/{draft.WorkflowId}.yaml" : draft.FilePath,
            scopeDirectory.DirectoryId,
            scopeDirectory.Label,
            draft.Yaml,
            Layout: null,
            draft.UpdatedAtUtc);
    }

    private WorkflowFileResponse ToLegacyDraftWorkflowFileResponse(
        string scopeId,
        StudioWorkflowDraftRecord draft)
    {
        var parse = _yamlDocumentService.Parse(draft.Yaml);
        var scopeDirectory = CreateScopeDirectory(scopeId);
        return new WorkflowFileResponse(
            draft.WorkflowId,
            ResolveDraftWorkflowName(draft, parse),
            string.IsNullOrWhiteSpace(draft.FileName) ? $"{draft.WorkflowId}.yaml" : draft.FileName,
            string.IsNullOrWhiteSpace(draft.FilePath) ? $"{scopeDirectory.Path}/{draft.WorkflowId}.yaml" : draft.FilePath,
            scopeDirectory.DirectoryId,
            scopeDirectory.Label,
            draft.Yaml,
            parse.Document,
            Layout: null,
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
        StudioWorkflowDraftRecord draft,
        WorkflowParseResult parseResult)
    {
        var parsedName = parseResult.Document?.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(parsedName))
            return parsedName;

        var storedName = draft.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(storedName))
            return storedName;

        return draft.WorkflowId;
    }

    private static string CreateScopedWorkflowId(
        string workflowName,
        IEnumerable<string> existingWorkflowIds)
    {
        var baseWorkflowId = StudioDocumentIdNormalizer.Normalize(workflowName, "workflow");
        var existingIds = existingWorkflowIds.ToHashSet(StringComparer.Ordinal);
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

    private static string NormalizeRequired(string value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");

        return normalized;
    }

    private static string EnsureYamlExtension(string fileName)
    {
        var normalized = NormalizeRequired(fileName, nameof(fileName));
        return normalized.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"{normalized}.yaml";
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
