using System.Text.RegularExpressions;
using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class WorkspaceService
{
    private static readonly Regex FileNameCleaner = new("[^a-zA-Z0-9._-]+", RegexOptions.Compiled);

    private readonly IStudioWorkspaceStore _store;
    private readonly IWorkflowYamlDocumentService _yamlDocumentService;

    public WorkspaceService(
        IStudioWorkspaceStore store,
        IWorkflowYamlDocumentService yamlDocumentService)
    {
        _store = store;
        _yamlDocumentService = yamlDocumentService;
    }

    public async Task<WorkspaceSettingsResponse> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _store.GetSettingsAsync(cancellationToken);
        return ToSettingsResponse(settings);
    }

    public async Task<WorkspaceSettingsResponse> UpdateSettingsAsync(
        UpdateWorkspaceSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var settings = await _store.GetSettingsAsync(cancellationToken);
        var updated = settings with
        {
            RuntimeBaseUrl = NormalizeRuntimeBaseUrl(request.RuntimeBaseUrl),
        };

        await _store.SaveSettingsAsync(updated, cancellationToken);
        return ToSettingsResponse(updated);
    }

    public async Task<WorkspaceSettingsResponse> AddDirectoryAsync(
        AddWorkflowDirectoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeDirectoryPath(request.Path);
        Directory.CreateDirectory(normalizedPath);

        var settings = await _store.GetSettingsAsync(cancellationToken);
        if (settings.Directories.Any(directory => string.Equals(directory.Path, normalizedPath, StringComparison.OrdinalIgnoreCase)))
        {
            return ToSettingsResponse(settings);
        }

        var directories = settings.Directories
            .Append(new StudioWorkspaceDirectory(
                DirectoryId: CreateStableId(normalizedPath),
                Label: string.IsNullOrWhiteSpace(request.Label) ? Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar)) : request.Label.Trim(),
                Path: normalizedPath,
                IsBuiltIn: false))
            .ToList();

        var updated = settings with { Directories = directories };
        await _store.SaveSettingsAsync(updated, cancellationToken);
        return ToSettingsResponse(updated);
    }

    public async Task<WorkspaceSettingsResponse> RemoveDirectoryAsync(
        string directoryId,
        CancellationToken cancellationToken = default)
    {
        var settings = await _store.GetSettingsAsync(cancellationToken);
        var updated = settings with
        {
            Directories = settings.Directories
                .Where(directory => directory.IsBuiltIn || !string.Equals(directory.DirectoryId, directoryId, StringComparison.Ordinal))
                .ToList(),
        };

        await _store.SaveSettingsAsync(updated, cancellationToken);
        return ToSettingsResponse(updated);
    }

    public async Task<IReadOnlyList<WorkflowDraftSummary>> ListDraftsAsync(CancellationToken cancellationToken = default)
    {
        var files = await _store.ListWorkflowFilesAsync(cancellationToken);
        return files
            .OrderByDescending(file => file.UpdatedAtUtc)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file =>
            {
                var parsed = _yamlDocumentService.Parse(file.Yaml);
                return ToWorkflowDraftSummary(file, parsed.Document);
            })
            .ToList();
    }

    #pragma warning disable CS0618
    [Obsolete("Use ListDraftsAsync.")]
    public async Task<IReadOnlyList<WorkflowSummary>> ListWorkflowsAsync(CancellationToken cancellationToken = default)
    {
        return (await ListDraftsAsync(cancellationToken))
            .Select(summary => new WorkflowSummary(
                summary.WorkflowId,
                summary.Name,
                summary.Description,
                summary.FileName,
                summary.FilePath,
                summary.DirectoryId,
                summary.DirectoryLabel,
                summary.StepCount,
                summary.HasLayout,
                summary.UpdatedAtUtc))
            .ToList();
    }

    public async Task<WorkflowDraftResponse?> GetDraftAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var file = await _store.GetWorkflowFileAsync(workflowId, cancellationToken);
        if (file is null)
        {
            return null;
        }

        return ToWorkflowDraftResponse(file);
    }

    [Obsolete("Use GetDraftAsync.")]
    public async Task<WorkflowFileResponse?> GetWorkflowAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var file = await _store.GetWorkflowFileAsync(workflowId, cancellationToken);
        if (file is null)
        {
            return null;
        }

        var parse = _yamlDocumentService.Parse(file.Yaml);
        return new WorkflowFileResponse(
            file.WorkflowId,
            file.Name,
            file.FileName,
            file.FilePath,
            file.DirectoryId,
            file.DirectoryLabel,
            file.Yaml,
            parse.Document,
            file.Layout,
            parse.Findings,
            file.UpdatedAtUtc);
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

    [Obsolete("Use CreateDraftAsync or UpdateDraftAsync.")]
    public Task<WorkflowDraftResponse> SaveWorkflowAsync(
        SaveWorkflowFileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var nextRequest = new SaveWorkflowDraftRequest(
            request.DirectoryId,
            request.WorkflowName,
            request.FileName,
            request.Yaml,
            request.Layout);
        return string.IsNullOrWhiteSpace(request.WorkflowId)
            ? CreateDraftAsync(nextRequest, cancellationToken)
            : UpdateDraftAsync(request.WorkflowId, nextRequest, cancellationToken);
    }
    #pragma warning restore CS0618

    private async Task<WorkflowDraftResponse> SaveDraftAsyncCore(
        string? workflowId,
        SaveWorkflowDraftRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await _store.GetSettingsAsync(cancellationToken);
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
        var existingFiles = await _store.ListWorkflowFilesAsync(cancellationToken);
        var conflictingFile = existingFiles.FirstOrDefault(file =>
            string.Equals(file.FilePath, targetPath, StringComparison.OrdinalIgnoreCase));
        var existingDraft = string.IsNullOrWhiteSpace(workflowId)
            ? null
            : await _store.GetWorkflowFileAsync(workflowId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(workflowId) && existingDraft is null)
        {
            throw new WorkflowDraftNotFoundException(workflowId);
        }

        var stableWorkflowId = string.IsNullOrWhiteSpace(workflowId)
            ? GenerateWorkflowId(normalizedName, existingFiles.Select(file => file.WorkflowId))
            : workflowId;

        if (conflictingFile != null &&
            !string.Equals(conflictingFile.WorkflowId, stableWorkflowId, StringComparison.Ordinal))
        {
            throw new WorkflowDraftPathConflictException(
                stableWorkflowId,
                $"{directory.Label}/{normalizedFileName}",
                conflictingFile.WorkflowId);
        }

        var stored = await _store.SaveWorkflowFileAsync(
            new StoredWorkflowFile(
                WorkflowId: stableWorkflowId,
                Name: normalizedName,
                FileName: Path.GetFileName(targetPath),
                FilePath: targetPath,
                DirectoryId: directory.DirectoryId,
                DirectoryLabel: directory.Label,
                Yaml: normalizedYaml,
                Layout: request.Layout,
                UpdatedAtUtc: DateTimeOffset.UtcNow),
            cancellationToken);

        return ToWorkflowDraftResponse(stored);
    }

    public async Task DeleteDraftAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var normalizedWorkflowId = NormalizeRequired(workflowId, nameof(workflowId));
        var existingDraft = await _store.GetWorkflowFileAsync(normalizedWorkflowId, cancellationToken);
        if (existingDraft is null)
        {
            throw new WorkflowDraftNotFoundException(normalizedWorkflowId);
        }

        await _store.DeleteWorkflowFileAsync(normalizedWorkflowId, cancellationToken);
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

    private static WorkflowDraftResponse ToWorkflowDraftResponse(StoredWorkflowFile file) =>
        new(
            file.WorkflowId,
            file.Name,
            file.FileName,
            file.FilePath,
            file.DirectoryId,
            file.DirectoryLabel,
            file.Yaml,
            file.Layout,
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

    private static WorkflowDraftSummary ToWorkflowDraftSummary(StoredWorkflowFile file, WorkflowDocument? document) =>
        new(
            file.WorkflowId,
            string.IsNullOrWhiteSpace(document?.Name) ? file.Name : document.Name,
            document?.Description ?? string.Empty,
            file.FileName,
            file.FilePath,
            file.DirectoryId,
            file.DirectoryLabel,
            document?.Steps.Count ?? 0,
            file.Layout is not null,
            file.UpdatedAtUtc);

    private static string GenerateWorkflowId(string workflowName, IEnumerable<string> existingWorkflowIds)
    {
        var baseId = StudioDocumentIdNormalizer.Normalize(workflowName, "workflow");
        var usedIds = existingWorkflowIds.ToHashSet(StringComparer.Ordinal);
        if (!usedIds.Contains(baseId))
        {
            return baseId;
        }

        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            var candidate = $"{baseId}-{suffix}";
            if (!usedIds.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unable to allocate a unique workflow draft id.");
    }

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
