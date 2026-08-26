namespace Aevatar.Studio.Application.Studio.Abstractions;

// Refactor (iter42/issue-864-studio-workspace-execution-fact-owner):
//   Old pattern: Studio executions/workspace facts mixed FileStudioWorkspaceStore JSON, draft index sidecars, and authoritative server UI/layout state across multiple owners.
//   New principle: Studio executions are a bounded ServiceRunGAgent readmodel facade; UI/layout/draft index are deleted/downgraded to client cache or derived from existing actor-backed sources. No new history/draft index actor.
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

public sealed record StoredConnectorCatalog(
    string HomeDirectory,
    string FilePath,
    bool FileExists,
    IReadOnlyList<StoredConnectorDefinition> Connectors,
    long Version = 0);

public sealed record StoredRoleCatalog(
    string HomeDirectory,
    string FilePath,
    bool FileExists,
    IReadOnlyList<StoredRoleDefinition> Roles,
    long Version = 0);

public sealed record StoredConnectorDraft(
    string HomeDirectory,
    string FilePath,
    bool FileExists,
    DateTimeOffset? UpdatedAtUtc,
    StoredConnectorDefinition? Draft,
    long Version = 0);

public sealed record StoredRoleDraft(
    string HomeDirectory,
    string FilePath,
    bool FileExists,
    DateTimeOffset? UpdatedAtUtc,
    StoredRoleDefinition? Draft,
    long Version = 0);

public sealed record StoredConnectorDefinition(
    string Name,
    string Type,
    bool Enabled,
    int TimeoutMs,
    int Retry,
    StoredHttpConnectorConfig Http,
    StoredCliConnectorConfig Cli,
    StoredMcpConnectorConfig Mcp,
    StoredHostCallbackConnectorConfig HostCallback);

public sealed record StoredHttpConnectorConfig(
    string BaseUrl,
    IReadOnlyList<string> AllowedMethods,
    IReadOnlyList<string> AllowedPaths,
    IReadOnlyList<string> AllowedInputKeys,
    IReadOnlyDictionary<string, string> DefaultHeaders,
    StoredConnectorAuthConfig Auth);

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
    string Url,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, string> AdditionalHeaders,
    StoredConnectorAuthConfig Auth,
    string DefaultTool,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> AllowedInputKeys);

public sealed record StoredHostCallbackConnectorConfig(
    string Handler,
    IReadOnlyList<string> AllowedOperations,
    IReadOnlyList<string> AllowedInputKeys);

public sealed record StoredConnectorAuthConfig(
    string Type,
    string TokenUrl,
    string ClientId,
    string ClientSecret,
    string Scope,
    string SecretRef,
    string HeaderName,
    string HeaderValuePrefix);

public sealed record StoredRoleDefinition(
    string Id,
    string Name,
    string SystemPrompt,
    string Provider,
    string Model,
    IReadOnlyList<string> Connectors);
