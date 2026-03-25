using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IStudioWorkspaceStore
{
    Task<StudioWorkspaceSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(StudioWorkspaceSettings settings, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredWorkflowFile>> ListWorkflowFilesAsync(CancellationToken cancellationToken = default);

    Task<StoredWorkflowFile?> GetWorkflowFileAsync(string workflowId, CancellationToken cancellationToken = default);

    Task<StoredWorkflowFile> SaveWorkflowFileAsync(StoredWorkflowFile workflowFile, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredExecutionRecord>> ListExecutionsAsync(CancellationToken cancellationToken = default);

    Task<StoredExecutionRecord?> GetExecutionAsync(string executionId, CancellationToken cancellationToken = default);

    Task<StoredExecutionRecord> SaveExecutionAsync(StoredExecutionRecord execution, CancellationToken cancellationToken = default);

    Task<StoredConnectorCatalog> GetConnectorCatalogAsync(CancellationToken cancellationToken = default);

    Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(StoredConnectorCatalog catalog, CancellationToken cancellationToken = default);

    Task<StoredConnectorDraft> GetConnectorDraftAsync(CancellationToken cancellationToken = default);

    Task<StoredConnectorDraft> SaveConnectorDraftAsync(StoredConnectorDraft draft, CancellationToken cancellationToken = default);

    Task DeleteConnectorDraftAsync(CancellationToken cancellationToken = default);

    Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken cancellationToken = default);

    Task<StoredRoleCatalog> SaveRoleCatalogAsync(StoredRoleCatalog catalog, CancellationToken cancellationToken = default);

    Task<StoredRoleDraft> GetRoleDraftAsync(CancellationToken cancellationToken = default);

    Task<StoredRoleDraft> SaveRoleDraftAsync(StoredRoleDraft draft, CancellationToken cancellationToken = default);

    Task DeleteRoleDraftAsync(CancellationToken cancellationToken = default);
}

public sealed record StudioWorkspaceSettings(
    string RuntimeBaseUrl,
    IReadOnlyList<StudioWorkspaceDirectory> Directories,
    string AppearanceTheme,
    string ColorMode);

public sealed record StudioWorkspaceDirectory(
    string DirectoryId,
    string Label,
    string Path,
    bool IsBuiltIn = false);

public sealed record StoredWorkflowFile(
    string WorkflowId,
    string Name,
    string FileName,
    string FilePath,
    string DirectoryId,
    string DirectoryLabel,
    string Yaml,
    WorkflowLayoutDocument? Layout,
    DateTimeOffset UpdatedAtUtc);

public sealed record StoredExecutionRecord(
    string ExecutionId,
    string WorkflowName,
    string Prompt,
    string RuntimeBaseUrl,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ActorId,
    string? Error,
    IReadOnlyList<StoredExecutionFrame> Frames);

public sealed record StoredExecutionFrame(
    DateTimeOffset ReceivedAtUtc,
    string Payload);

public sealed record StoredConnectorCatalog(
    string HomeDirectory,
    string FilePath,
    bool FileExists,
    IReadOnlyList<StoredConnectorDefinition> Connectors);

public sealed record StoredRoleCatalog(
    string HomeDirectory,
    string FilePath,
    bool FileExists,
    IReadOnlyList<StoredRoleDefinition> Roles);

public sealed record StoredConnectorDraft(
    string HomeDirectory,
    string FilePath,
    bool FileExists,
    DateTimeOffset? UpdatedAtUtc,
    StoredConnectorDefinition? Draft);

public sealed record StoredRoleDraft(
    string HomeDirectory,
    string FilePath,
    bool FileExists,
    DateTimeOffset? UpdatedAtUtc,
    StoredRoleDefinition? Draft);

public sealed record StoredConnectorDefinition(
    string Name,
    string Type,
    bool Enabled,
    int TimeoutMs,
    int Retry,
    StoredHttpConnectorConfig Http,
    StoredCliConnectorConfig Cli,
    StoredMcpConnectorConfig Mcp);

public sealed record StoredHttpConnectorConfig(
    string BaseUrl,
    IReadOnlyList<string> AllowedMethods,
    IReadOnlyList<string> AllowedPaths,
    IReadOnlyList<string> AllowedInputKeys,
    IReadOnlyDictionary<string, string> DefaultHeaders);

public sealed record StoredCliConnectorConfig(
    string Command,
    IReadOnlyList<string> FixedArguments,
    IReadOnlyList<string> AllowedOperations,
    IReadOnlyList<string> AllowedInputKeys,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

public sealed record StoredMcpConnectorConfig(
    string ServerName,
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string DefaultTool,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> AllowedInputKeys);

public sealed record StoredRoleDefinition(
    string Id,
    string Name,
    string SystemPrompt,
    string Provider,
    string Model,
    IReadOnlyList<string> Connectors);
