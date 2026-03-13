using System.Text;
using Aevatar.Configuration;
using Aevatar.Workflow.Application.Abstractions.Authoring;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Infrastructure.Workflows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.Authoring;

internal sealed class FileSystemWorkflowAuthoringPersistencePort : IWorkflowAuthoringPersistencePort
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly IWorkflowDefinitionRegistry _workflowRegistry;
    private readonly IWorkflowDefinitionSourceRefreshPort _refreshPort;
    private readonly IOptions<WorkflowDefinitionFileSourceOptions> _options;
    private readonly ILogger<FileSystemWorkflowAuthoringPersistencePort> _logger;
    private readonly Func<string> _workflowDirectoryAccessor;

    public FileSystemWorkflowAuthoringPersistencePort(
        IWorkflowDefinitionRegistry workflowRegistry,
        IWorkflowDefinitionSourceRefreshPort refreshPort,
        IOptions<WorkflowDefinitionFileSourceOptions> options,
        ILogger<FileSystemWorkflowAuthoringPersistencePort>? logger = null)
        : this(
            workflowRegistry,
            refreshPort,
            options,
            logger,
            static () => AevatarPaths.Workflows)
    {
    }

    internal FileSystemWorkflowAuthoringPersistencePort(
        IWorkflowDefinitionRegistry workflowRegistry,
        IWorkflowDefinitionSourceRefreshPort refreshPort,
        IOptions<WorkflowDefinitionFileSourceOptions> options,
        ILogger<FileSystemWorkflowAuthoringPersistencePort>? logger,
        Func<string> workflowDirectoryAccessor)
    {
        _workflowRegistry = workflowRegistry ?? throw new ArgumentNullException(nameof(workflowRegistry));
        _refreshPort = refreshPort ?? throw new ArgumentNullException(nameof(refreshPort));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<FileSystemWorkflowAuthoringPersistencePort>.Instance;
        _workflowDirectoryAccessor = workflowDirectoryAccessor ?? throw new ArgumentNullException(nameof(workflowDirectoryAccessor));
    }

    public async Task<PlaygroundWorkflowSaveResult> SaveWorkflowAsync(
        PlaygroundWorkflowSaveRequest request,
        string workflowName,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(workflowName))
            throw new InvalidOperationException("workflow name is required");

        var filename = NormalizeWorkflowSaveFilename(request.Filename, workflowName);
        var workflowDirectory = _workflowDirectoryAccessor();
        if (string.IsNullOrWhiteSpace(workflowDirectory))
            throw new InvalidOperationException("workflow directory is not configured");

        Directory.CreateDirectory(workflowDirectory);
        var savedPath = Path.Combine(workflowDirectory, filename);
        var existed = File.Exists(savedPath);
        if (existed && !request.Overwrite)
        {
            throw new WorkflowAuthoringConflictException(
                $"Workflow '{filename}' already exists.",
                filename,
                savedPath);
        }

        var normalizedContent = NormalizeWorkflowContentForSave(request.Yaml);
        await File.WriteAllTextAsync(savedPath, normalizedContent, Utf8NoBom, ct);
        _workflowRegistry.Register(workflowName, normalizedContent);
        await _refreshPort.RefreshAsync(workflowName, ct);

        var savedSource = WorkflowDefinitionFileSourceResolver.ResolveSourceKind(workflowDirectory);
        var effectiveFileEntries = WorkflowDefinitionFileSourceResolver.DiscoverWorkflowFiles(
            _options.Value.WorkflowDirectories,
            _logger);
        effectiveFileEntries.TryGetValue(workflowName, out var effectiveFileEntry);

        return new PlaygroundWorkflowSaveResult
        {
            Saved = true,
            Filename = filename,
            SavedPath = savedPath,
            WorkflowName = workflowName,
            Overwritten = existed,
            SavedSource = savedSource,
            EffectiveSource = effectiveFileEntry?.SourceKind ?? savedSource,
            EffectivePath = effectiveFileEntry?.FilePath ?? savedPath,
        };
    }

    internal static string NormalizeWorkflowSaveFilename(string? requestedFilename, string workflowName)
    {
        var candidate = string.IsNullOrWhiteSpace(requestedFilename)
            ? workflowName
            : requestedFilename.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            throw new InvalidOperationException("workflow filename is required");

        var fileNameOnly = Path.GetFileName(candidate);
        if (!string.Equals(fileNameOnly, candidate, StringComparison.Ordinal))
            throw new InvalidOperationException("workflow filename must not include directory segments");

        var stem = Path.GetFileNameWithoutExtension(fileNameOnly);
        if (string.IsNullOrWhiteSpace(stem))
            throw new InvalidOperationException("workflow filename is invalid");

        var sanitizedChars = stem
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')
            .ToArray();
        var sanitizedStem = new string(sanitizedChars).Trim('_');
        while (sanitizedStem.Contains("__", StringComparison.Ordinal))
            sanitizedStem = sanitizedStem.Replace("__", "_", StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(sanitizedStem))
            throw new InvalidOperationException("workflow filename must contain letters or digits");

        return sanitizedStem + ".yaml";
    }

    internal static string NormalizeWorkflowContentForSave(string yaml) =>
        (yaml ?? string.Empty).Trim() + Environment.NewLine;
}
