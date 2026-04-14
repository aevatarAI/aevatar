using System.Text.RegularExpressions;
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

    public async Task<IReadOnlyList<WorkflowSummary>> ListWorkflowsAsync(CancellationToken cancellationToken = default)
    {
        var files = await _store.ListWorkflowFilesAsync(cancellationToken);
        return files
            .OrderByDescending(file => file.UpdatedAtUtc)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file =>
            {
                var parsed = _yamlDocumentService.Parse(file.Yaml);
                return ToWorkflowSummary(file, parsed.Document);
            })
            .ToList();
    }

    public async Task<WorkflowFileResponse?> GetWorkflowAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var file = await _store.GetWorkflowFileAsync(workflowId, cancellationToken);
        if (file is null)
        {
            return null;
        }

        return ToWorkflowFileResponse(file);
    }

    public async Task<WorkflowFileResponse> SaveWorkflowAsync(
        SaveWorkflowFileRequest request,
        CancellationToken cancellationToken = default)
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

        var filePath = string.IsNullOrWhiteSpace(request.WorkflowId)
            ? Path.Combine(directory.Path, normalizedFileName)
            : DecodeStableId(request.WorkflowId);

        var stored = await _store.SaveWorkflowFileAsync(
            new StoredWorkflowFile(
                WorkflowId: CreateStableId(filePath),
                Name: normalizedName,
                FileName: Path.GetFileName(filePath),
                FilePath: filePath,
                DirectoryId: directory.DirectoryId,
                DirectoryLabel: directory.Label,
                Yaml: normalizedYaml,
                Layout: request.Layout,
                UpdatedAtUtc: DateTimeOffset.UtcNow),
            cancellationToken);

        return ToWorkflowFileResponse(stored);
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

    private WorkflowFileResponse ToWorkflowFileResponse(StoredWorkflowFile file)
    {
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

    private static WorkflowSummary ToWorkflowSummary(StoredWorkflowFile file, WorkflowDocument? document) =>
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
