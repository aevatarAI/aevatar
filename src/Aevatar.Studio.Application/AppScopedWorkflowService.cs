using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;

using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Application.Studio.Services;
namespace Aevatar.Studio.Application;

// Refactor (iter42/issue-864-studio-workspace-execution-fact-owner):
//   Old pattern: Studio executions/workspace facts mixed FileStudioWorkspaceStore JSON, draft index sidecars, and authoritative server UI/layout state across multiple owners.
//   New principle: Studio executions are a bounded ServiceRunGAgent readmodel facade; UI/layout/draft index are deleted/downgraded to client cache or derived from existing actor-backed sources. No new history/draft index actor.
public sealed class AppScopedWorkflowService
{
    private readonly IWorkflowYamlDocumentService _yamlDocumentService;
    private readonly IWorkflowDefinitionParser _workflowDefinitionParser;
    private readonly IStudioWorkspaceQueryPort? _workspaceQueryPort;
    private readonly IStudioWorkspaceCommandPort? _workspaceCommandPort;
    private readonly ILogger<AppScopedWorkflowService>? _logger;

    public AppScopedWorkflowService(
        IWorkflowYamlDocumentService yamlDocumentService,
        IWorkflowDefinitionParser workflowDefinitionParser,
        IStudioWorkspaceQueryPort? workspaceQueryPort = null,
        IStudioWorkspaceCommandPort? workspaceCommandPort = null,
        ILogger<AppScopedWorkflowService>? logger = null)
    {
        _yamlDocumentService = yamlDocumentService ?? throw new ArgumentNullException(nameof(yamlDocumentService));
        _workflowDefinitionParser = workflowDefinitionParser
            ?? throw new ArgumentNullException(nameof(workflowDefinitionParser));
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

    public async Task<WorkflowDraftCreateAcceptedResponse> CreateDraftAsync(
        string scopeId,
        SaveWorkflowDraftRequest request,
        CancellationToken ct = default)
    {
        var (draft, receipt) = await SaveDraftCommandAsync(
            scopeId,
            workflowId: null,
            request,
            requireExisting: false,
            ct);
        return ToDraftCreateAcceptedResponse(draft.WorkflowId, receipt);
    }

    public async Task<WorkflowDraftCreateAcceptedResponse> SaveDraftAsync(
        string scopeId,
        string workflowId,
        SaveWorkflowDraftRequest request,
        CancellationToken ct = default)
    {
        var (draft, receipt) = await SaveDraftCommandAsync(
            scopeId,
            NormalizeRequired(workflowId, nameof(workflowId)),
            request,
            requireExisting: false,
            ct);
        return ToDraftCreateAcceptedResponse(draft.WorkflowId, receipt);
    }

    public async Task<WorkflowDraftResponse> UpdateDraftAsync(
        string scopeId,
        string workflowId,
        SaveWorkflowDraftRequest request,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var (draft, _) = await SaveDraftCommandAsync(
            normalizedScopeId,
            NormalizeRequired(workflowId, nameof(workflowId)),
            request,
            requireExisting: true,
            ct);
        return ToDraftWorkflowResponse(normalizedScopeId, draft);
    }

    private async Task<(StudioWorkflowDraftRecord Draft, StudioWorkspaceCommandReceipt Receipt)> SaveDraftCommandAsync(
        string scopeId,
        string? workflowId,
        SaveWorkflowDraftRequest request,
        bool requireExisting,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedYaml = NormalizeRequired(request.Yaml, nameof(request.Yaml));
        var parsed = await _workflowDefinitionParser.ParseWorkflowYamlForPublicationAsync(normalizedYaml, ct);
        if (!parsed.Succeeded)
            throw new InvalidOperationException(parsed.Error);

        var workflowName = string.IsNullOrWhiteSpace(request.WorkflowName)
            ? NormalizeRequired(parsed.WorkflowName, nameof(request.WorkflowName))
            : request.WorkflowName.Trim();
        normalizedYaml = AlignWorkflowYamlName(normalizedYaml, workflowName);
        var workspaceQueryPort = _workspaceQueryPort
            ?? throw new InvalidOperationException("Scoped workflow workspace query port is not configured.");
        var workspaceCommandPort = _workspaceCommandPort
            ?? throw new InvalidOperationException("Scoped workflow workspace command port is not configured.");
        var workspace = await workspaceQueryPort.GetAsync(normalizedScopeId, ct);
        var savedAtUtc = DateTimeOffset.UtcNow;
        var normalizedWorkflowId = string.IsNullOrWhiteSpace(workflowId)
            ? CreateWorkflowDraftId()
            : workflowId;

        var existingDraft = workspace.Drafts.FirstOrDefault(draft =>
            string.Equals(draft.WorkflowId, normalizedWorkflowId, StringComparison.Ordinal));
        if (requireExisting)
        {
            if (existingDraft == null)
            {
                throw new WorkflowDraftNotFoundException(normalizedWorkflowId);
            }
        }

        var scopeDirectory = CreateScopeDirectory(normalizedScopeId);
        var fileName = EnsureYamlExtension(string.IsNullOrWhiteSpace(request.FileName)
            ? workflowName
            : request.FileName.Trim());
        var filePath = $"{scopeDirectory.Path}/{fileName}";
        var conflictingDraft = workspace.Drafts.FirstOrDefault(draft =>
            string.Equals(draft.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (conflictingDraft != null &&
            !string.Equals(conflictingDraft.WorkflowId, normalizedWorkflowId, StringComparison.Ordinal))
        {
            throw new WorkflowDraftPathConflictException(
                normalizedWorkflowId,
                $"{scopeDirectory.Label}/{fileName}",
                conflictingDraft.WorkflowId);
        }

        var stored = new StudioWorkflowDraftRecord(
            WorkflowId: normalizedWorkflowId,
            Name: workflowName,
            FileName: fileName,
            FilePath: filePath,
            DirectoryId: scopeDirectory.DirectoryId,
            DirectoryLabel: scopeDirectory.Label,
            Yaml: normalizedYaml,
            Layout: null,
            UpdatedAtUtc: savedAtUtc,
            CreatedAtUtc: existingDraft?.CreatedAtUtc ?? savedAtUtc,
            Version: existingDraft?.Version ?? 0);

        // Scoped workspace save persists an editor draft; publish stays on the scope-binding flow.
        var receipt = await workspaceCommandPort.SaveDraftAsync(
            normalizedScopeId,
            stored,
            expectedVersion: null,
            ct);

        return (stored, receipt);
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

        await workspaceCommandPort.DeleteDraftAsync(normalizedScopeId, normalizedWorkflowId, expectedVersion: null, ct);
    }

    // Refactor (iter56/cluster-929-studio-workflow-obsolete-shims):
    //   old=Obsolete wrapper, new=removed (use draft methods)
    //   ListAsync/GetAsync legacy shims were deleted; callers use explicit draft methods.

    public static WorkflowDirectorySummary CreateScopeDirectory(string scopeId) =>
        new(
            BuildScopeDirectoryId(scopeId),
            scopeId,
            $"scope://{scopeId}",
            true);

    public static string BuildScopeDirectoryId(string scopeId) =>
        $"scope:{NormalizeRequired(scopeId, nameof(scopeId))}";

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
                "Failed to list stored scoped workflow drafts for scope {ScopeId}. Returning an empty draft list.",
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
                "Failed to load stored scoped workflow draft {WorkflowId} for scope {ScopeId}. Returning no draft.",
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

    private static string AlignWorkflowYamlName(string yaml, string workflowName)
    {
        if (string.IsNullOrWhiteSpace(yaml) || string.IsNullOrWhiteSpace(workflowName))
            return yaml;

        var alignedNameLine = $"name: {FormatWorkflowNameScalar(workflowName)}";
        var lines = yaml.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var withoutCarriageReturn = line.TrimEnd('\r');
            if (!IsTopLevelNameLine(withoutCarriageReturn))
                continue;

            lines[index] = line.EndsWith('\r')
                ? alignedNameLine + "\r"
                : alignedNameLine;
            return string.Join('\n', lines);
        }

        return alignedNameLine + "\n" + yaml;
    }

    private static bool IsTopLevelNameLine(string line)
    {
        if (!line.StartsWith("name", StringComparison.Ordinal))
            return false;

        for (var index = "name".Length; index < line.Length; index++)
        {
            var character = line[index];
            if (character == ':')
                return true;

            if (character != ' ' && character != '\t')
                return false;
        }

        return false;
    }

    private static string FormatWorkflowNameScalar(string workflowName)
    {
        if (workflowName.All(static character =>
                char.IsLetterOrDigit(character) ||
                character == ' ' ||
                character == '_' ||
                character == '-' ||
                character == '.'))
        {
            return workflowName;
        }

        return "'" + workflowName.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static string ResolveDraftWorkflowName(
        StudioWorkflowDraftRecord draft,
        WorkflowParseResult parseResult)
    {
        var storedName = draft.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(storedName))
            return storedName;

        var parsedName = parseResult.Document?.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(parsedName))
            return parsedName;

        return draft.WorkflowId;
    }

    private static WorkflowDraftCreateAcceptedResponse ToDraftCreateAcceptedResponse(
        string workflowId,
        StudioWorkspaceCommandReceipt receipt) =>
        new(
            Accepted: true,
            WorkflowId: workflowId,
            CommandId: receipt.CommandId,
            AckStage: "accepted",
            ActorId: receipt.ActorId,
            WorkspaceId: receipt.WorkspaceId,
            ExpectedVersion: receipt.ExpectedVersion,
            AckedAtUtc: DateTimeOffset.UtcNow,
            Readiness: new WorkflowDraftReadinessResponse(
                Readable: false,
                Stage: "projection_pending",
                Message: "The workflow draft create command was accepted. Poll the workflow draft by id until the scoped workspace read model observes it."));

    private static string CreateWorkflowDraftId() =>
        Guid.NewGuid().ToString("N");

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

}
