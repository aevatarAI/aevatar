namespace Aevatar.Studio.Application.Studio.Contracts;

public sealed record RoleCatalogResponse(
    string HomeDirectory,
    string FilePath,
    bool FileExists,
    IReadOnlyList<RoleDefinitionDto> Roles,
    long Version = 0);

public sealed record RoleDraftResponse(
    string HomeDirectory,
    string FilePath,
    bool FileExists,
    DateTimeOffset? UpdatedAtUtc,
    RoleDefinitionDto? Draft,
    long Version = 0);

public sealed record SaveRoleCatalogRequest(
    IReadOnlyList<RoleDefinitionDto> Roles,
    long? ExpectedVersion = null);

public sealed record SaveRoleDraftRequest(
    RoleDefinitionDto? Draft,
    long? ExpectedVersion = null);

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
