namespace Aevatar.Tools.Cli.Studio.Application.Contracts;

public sealed record RoleCatalogResponse(
    string HomeDirectory,
    string FilePath,
    bool FileExists,
    IReadOnlyList<RoleDefinitionDto> Roles);

public sealed record RoleDraftResponse(
    string HomeDirectory,
    string FilePath,
    bool FileExists,
    DateTimeOffset? UpdatedAtUtc,
    RoleDefinitionDto? Draft);

public sealed record SaveRoleCatalogRequest(
    IReadOnlyList<RoleDefinitionDto> Roles);

public sealed record SaveRoleDraftRequest(
    RoleDefinitionDto? Draft);

public sealed record ImportRoleCatalogResponse(
    string SourceFilePath,
    bool SourceFileExists,
    int ImportedCount,
    string HomeDirectory,
    string FilePath,
    bool FileExists,
    IReadOnlyList<RoleDefinitionDto> Roles);

public sealed record RoleDefinitionDto(
    string Id,
    string Name,
    string SystemPrompt,
    string Provider,
    string Model,
    IReadOnlyList<string> Connectors);
