namespace Aevatar.Studio.Application.Studio.Contracts;

public sealed record ConnectorCatalogResponse(
    string HomeDirectory,
    string FilePath,
    bool FileExists,
    IReadOnlyList<ConnectorDefinitionDto> Connectors);

public sealed record ConnectorDraftResponse(
    string HomeDirectory,
    string FilePath,
    bool FileExists,
    DateTimeOffset? UpdatedAtUtc,
    ConnectorDefinitionDto? Draft);

public sealed record SaveConnectorCatalogRequest(
    IReadOnlyList<ConnectorDefinitionDto> Connectors);

public sealed record SaveConnectorDraftRequest(
    ConnectorDefinitionDto? Draft);

public sealed record ImportConnectorCatalogResponse(
    string SourceFilePath,
    bool SourceFileExists,
    int ImportedCount,
    string HomeDirectory,
    string FilePath,
    bool FileExists,
    IReadOnlyList<ConnectorDefinitionDto> Connectors);

public sealed record ConnectorDefinitionDto(
    string Name,
    string Type,
    bool Enabled,
    int TimeoutMs,
    int Retry,
    HttpConnectorDefinitionDto Http,
    CliConnectorDefinitionDto Cli,
    McpConnectorDefinitionDto Mcp);

public sealed record HttpConnectorDefinitionDto(
    string BaseUrl,
    IReadOnlyList<string> AllowedMethods,
    IReadOnlyList<string> AllowedPaths,
    IReadOnlyList<string> AllowedInputKeys,
    IReadOnlyDictionary<string, string> DefaultHeaders,
    ConnectorAuthDefinitionDto Auth);

public sealed record CliConnectorDefinitionDto(
    string Command,
    IReadOnlyList<string> FixedArguments,
    IReadOnlyList<string> AllowedOperations,
    IReadOnlyList<string> AllowedInputKeys,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

public sealed record McpConnectorDefinitionDto(
    string ServerName,
    string Command,
    string Url,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, string> AdditionalHeaders,
    ConnectorAuthDefinitionDto Auth,
    string DefaultTool,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> AllowedInputKeys);

public sealed record ConnectorAuthDefinitionDto(
    string Type,
    string TokenUrl,
    string ClientId,
    string ClientSecret,
    string Scope);
