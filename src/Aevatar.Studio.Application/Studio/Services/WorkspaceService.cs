using System.Text.RegularExpressions;
using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Application.Studio.Services;

// Refactor (iter42/issue-864-studio-workspace-execution-fact-owner):
//   Old pattern: Studio executions/workspace facts mixed FileStudioWorkspaceStore JSON, draft index sidecars, and authoritative server UI/layout state across multiple owners.
//   New principle: Studio executions are a bounded ServiceRunGAgent readmodel facade; UI/layout/draft index are deleted/downgraded to client cache or derived from existing actor-backed sources. No new history/draft index actor.
public sealed class WorkspaceService
{
    private static readonly Regex FileNameCleaner = new("[^a-zA-Z0-9._-]+", RegexOptions.Compiled);

    private readonly IStudioWorkspaceQueryPort _queryPort;
    private readonly IStudioWorkspaceCommandPort _commandPort;
    private readonly IWorkflowYamlDocumentService _yamlDocumentService;

    public WorkspaceService(
        IStudioWorkspaceQueryPort queryPort,
        IStudioWorkspaceCommandPort commandPort,
        IWorkflowYamlDocumentService yamlDocumentService)
    {
        _queryPort = queryPort;
        _commandPort = commandPort;
        _yamlDocumentService = yamlDocumentService;
    }

    public async Task<WorkspaceSettingsResponse> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var workspace = await _queryPort.GetAsync(cancellationToken);
        return ToSettingsResponse(workspace.Settings);
    }

    public async Task<WorkspaceSettingsResponse> UpdateSettingsAsync(
        UpdateWorkspaceSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var workspace = await _queryPort.GetAsync(cancellationToken);
        var settings = workspace.Settings;
        var updated = settings with
        {
            RuntimeBaseUrl = NormalizeRuntimeBaseUrl(request.RuntimeBaseUrl),
        };

        await _commandPort.UpdateSettingsAsync(updated, workspace.StateVersion, cancellationToken);
        return ToSettingsResponse(updated);
    }

    public async Task<WorkspaceSettingsResponse> AddDirectoryAsync(
        AddWorkflowDirectoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeDirectoryPath(request.Path);
        Directory.CreateDirectory(normalizedPath);

        var workspace = await _queryPort.GetAsync(cancellationToken);
        var settings = workspace.Settings;
        if (settings.Directories.Any(directory => string.Equals(directory.Path, normalizedPath, StringComparison.OrdinalIgnoreCase)))
        {
            return ToSettingsResponse(settings);
        }

        var directory = new StudioWorkspaceDirectory(
            DirectoryId: CreateStableId(normalizedPath),
            Label: string.IsNullOrWhiteSpace(request.Label) ? Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar)) : request.Label.Trim(),
            Path: normalizedPath,
            IsBuiltIn: false);

        var updated = settings with { Directories = settings.Directories.Append(directory).ToList() };
        await _commandPort.AddDirectoryAsync(directory, workspace.StateVersion, cancellationToken);
        return ToSettingsResponse(updated);
    }

    public async Task<WorkspaceSettingsResponse> RemoveDirectoryAsync(
        string directoryId,
        CancellationToken cancellationToken = default)
    {
        var workspace = await _queryPort.GetAsync(cancellationToken);
        var settings = workspace.Settings;
        var updated = settings with
        {
            Directories = settings.Directories
                .Where(directory => directory.IsBuiltIn || !string.Equals(directory.DirectoryId, directoryId, StringComparison.Ordinal))
                .ToList(),
        };

        await _commandPort.RemoveDirectoryAsync(directoryId, workspace.StateVersion, cancellationToken);
        return ToSettingsResponse(updated);
    }

    public async Task<IReadOnlyList<WorkflowDraftSummary>> ListDraftsAsync(CancellationToken cancellationToken = default)
    {
        var workspace = await _queryPort.GetAsync(cancellationToken);
        return workspace.Drafts
            .OrderByDescending(file => file.UpdatedAtUtc)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file =>
            {
                var parsed = _yamlDocumentService.Parse(file.Yaml);
                return ToWorkflowDraftSummary(file, parsed.Document);
            })
            .ToList();
    }

    public async Task<WorkflowDraftResponse?> GetDraftAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var workspace = await _queryPort.GetAsync(cancellationToken);
        var file = workspace.Drafts.FirstOrDefault(item =>
            string.Equals(item.WorkflowId, workflowId, StringComparison.Ordinal));
        if (file is null)
        {
            return null;
        }

        return ToWorkflowDraftResponse(file);
    }

    public Task<WorkflowDraftResponse> CreateDraftAsync(
        SaveWorkflowDraftRequest request,
        CancellationToken cancellationToken = default)
        => SaveDraftAsyncCore(null, request, cancellationToken);

    public Task<WorkflowDraftResponse> UpdateDraftAsync(
        string workflowId,
        SaveWorkflowDraftRequest request,
        CancellationToken cancellationToken = default)
        => SaveDraftAsyncCore(NormalizeRequired(workflowId, nameof(workflowId)), request, cancellationToken);

    private async Task<WorkflowDraftResponse> SaveDraftAsyncCore(
        string? workflowId,
        SaveWorkflowDraftRequest request,
        CancellationToken cancellationToken)
    {
        var workspace = await _queryPort.GetAsync(cancellationToken);
        var settings = workspace.Settings;
        var directory = settings.Directories.FirstOrDefault(item =>
            string.Equals(item.DirectoryId, request.DirectoryId, StringComparison.Ordinal));

        if (directory is null)
        {
            throw new InvalidOperationException($"Workflow directory '{request.DirectoryId}' was not found.");
        }

        var normalizedName = string.IsNullOrWhiteSpace(request.WorkflowName)
            ? "workflow"
            : request.WorkflowName.Trim();
        var normalizedYaml = AlignWorkflowYamlName(request.Yaml, normalizedName);

        var normalizedFileName = EnsureYamlExtension(string.IsNullOrWhiteSpace(request.FileName)
            ? normalizedName
            : request.FileName.Trim());
        var targetPath = Path.Combine(directory.Path, normalizedFileName);
        var existingFiles = workspace.Drafts;
        var conflictingFile = existingFiles.FirstOrDefault(file =>
            string.Equals(file.FilePath, targetPath, StringComparison.OrdinalIgnoreCase));
        var existingDraft = string.IsNullOrWhiteSpace(workflowId)
            ? null
            : existingFiles.FirstOrDefault(file =>
                string.Equals(file.WorkflowId, workflowId, StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(workflowId) && existingDraft is null)
        {
            throw new WorkflowDraftNotFoundException(workflowId);
        }

        var stableWorkflowId = string.IsNullOrWhiteSpace(workflowId)
            ? CreateWorkflowDraftId()
            : workflowId;

        if (conflictingFile != null &&
            !string.Equals(conflictingFile.WorkflowId, stableWorkflowId, StringComparison.Ordinal))
        {
            throw new WorkflowDraftPathConflictException(
                stableWorkflowId,
                $"{directory.Label}/{normalizedFileName}",
                conflictingFile.WorkflowId);
        }

        var updatedAtUtc = DateTimeOffset.UtcNow;
        var stored = new StudioWorkflowDraftRecord(
            WorkflowId: stableWorkflowId,
            Name: normalizedName,
            FileName: Path.GetFileName(targetPath),
            FilePath: targetPath,
            DirectoryId: directory.DirectoryId,
            DirectoryLabel: directory.Label,
            Yaml: normalizedYaml,
            Layout: null,
            UpdatedAtUtc: updatedAtUtc,
            CreatedAtUtc: existingDraft?.CreatedAtUtc ?? updatedAtUtc,
            Version: existingDraft?.Version ?? 0);
        await _commandPort.SaveDraftAsync(stored, workspace.StateVersion, cancellationToken);

        return ToWorkflowDraftResponse(stored);
    }

    public async Task DeleteDraftAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var normalizedWorkflowId = NormalizeRequired(workflowId, nameof(workflowId));
        var workspace = await _queryPort.GetAsync(cancellationToken);
        var existingDraft = workspace.Drafts.FirstOrDefault(item =>
            string.Equals(item.WorkflowId, normalizedWorkflowId, StringComparison.Ordinal));
        if (existingDraft is null)
        {
            throw new WorkflowDraftNotFoundException(normalizedWorkflowId);
        }

        await _commandPort.DeleteDraftAsync(normalizedWorkflowId, workspace.StateVersion, cancellationToken);
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

    private static WorkflowDraftResponse ToWorkflowDraftResponse(StudioWorkflowDraftRecord file) =>
        new(
            file.WorkflowId,
            file.Name,
            file.FileName,
            file.FilePath,
            file.DirectoryId,
            file.DirectoryLabel,
            file.Yaml,
            Layout: null,
            file.UpdatedAtUtc);

    private static WorkspaceSettingsResponse ToSettingsResponse(StudioWorkspaceSettings settings) =>
        new(
            settings.RuntimeBaseUrl,
            settings.Directories
                .Select(directory => new WorkflowDirectorySummary(
                    directory.DirectoryId,
                    directory.Label,
                    directory.Path,
                    directory.IsBuiltIn))
                .ToList());

    private static WorkflowDraftSummary ToWorkflowDraftSummary(StudioWorkflowDraftRecord file, WorkflowDocument? document) =>
        new(
            file.WorkflowId,
            string.IsNullOrWhiteSpace(document?.Name) ? file.Name : document.Name,
            document?.Description ?? string.Empty,
            file.FileName,
            file.FilePath,
            file.DirectoryId,
            file.DirectoryLabel,
            document?.Steps.Count ?? 0,
            HasLayout: false,
            file.UpdatedAtUtc);

    private static string CreateWorkflowDraftId() =>
        Guid.NewGuid().ToString("N");

    private static string NormalizeRuntimeBaseUrl(string url)
    {
        var normalized = string.IsNullOrWhiteSpace(url)
            ? UserConfigRuntimeDefaults.LocalRuntimeBaseUrl
            : url.Trim();

        return normalized.TrimEnd('/');
    }

    private static string NormalizeDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Workflow directory path is required.");
        }

        return ExpandPath(path.Trim());
    }

    private static string ExpandPath(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]));
        }

        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    private static string NormalizeRequired(string value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException($"{fieldName} is required.");
        }

        return normalized;
    }

    private static string EnsureYamlExtension(string fileName)
    {
        var cleaned = FileNameCleaner.Replace(fileName.Trim(), "_").Trim('_');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "workflow";
        }

        return cleaned.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
               cleaned.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            ? cleaned
            : $"{cleaned}.yaml";
    }

    public static string CreateStableId(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string DecodeStableId(string id)
    {
        var base64 = id
            .Replace('-', '+')
            .Replace('_', '/');

        var padding = base64.Length % 4;
        if (padding > 0)
        {
            base64 = base64.PadRight(base64.Length + (4 - padding), '=');
        }

        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }
}
