using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using Aevatar.Tools.Cli.Studio.Application.Contracts;

namespace Aevatar.Tools.Cli.Studio.Application.Services;

public sealed class RoleCatalogService
{
    private readonly IStudioWorkspaceStore _store;

    public RoleCatalogService(IStudioWorkspaceStore store)
    {
        _store = store;
    }

    public async Task<RoleCatalogResponse> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await _store.GetRoleCatalogAsync(cancellationToken);
        return ToResponse(catalog);
    }

    public async Task<RoleDraftResponse> GetDraftAsync(CancellationToken cancellationToken = default)
    {
        var draft = await _store.GetRoleDraftAsync(cancellationToken);
        return ToDraftResponse(draft);
    }

    public async Task<RoleCatalogResponse> SaveCatalogAsync(
        SaveRoleCatalogRequest request,
        CancellationToken cancellationToken = default)
    {
        var roles = request.Roles ?? [];
        EnsureUniqueIds(roles);

        var existing = await _store.GetRoleCatalogAsync(cancellationToken);
        var saved = await _store.SaveRoleCatalogAsync(
            existing with
            {
                Roles = roles
                    .Where(role => !string.IsNullOrWhiteSpace(role.Id))
                    .Select(ToStoredRole)
                    .ToList(),
            },
            cancellationToken);

        return ToResponse(saved);
    }

    public async Task<RoleDraftResponse> SaveDraftAsync(
        SaveRoleDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Draft is null)
        {
            await _store.DeleteRoleDraftAsync(cancellationToken);
            return await GetDraftAsync(cancellationToken);
        }

        var saved = await _store.SaveRoleDraftAsync(
            new StoredRoleDraft(
                HomeDirectory: string.Empty,
                FilePath: string.Empty,
                FileExists: false,
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                Draft: ToStoredRoleDraft(request.Draft)),
            cancellationToken);

        return ToDraftResponse(saved);
    }

    public Task DeleteDraftAsync(CancellationToken cancellationToken = default) =>
        _store.DeleteRoleDraftAsync(cancellationToken);

    private static void EnsureUniqueIds(IEnumerable<RoleDefinitionDto> roles)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in roles)
        {
            var id = role.Id?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException("Role id is required.");
            }

            if (!ids.Add(id))
            {
                throw new InvalidOperationException($"Duplicate role id '{id}'.");
            }
        }
    }

    private static StoredRoleDefinition ToStoredRole(RoleDefinitionDto role) =>
        new(
            Id: role.Id.Trim(),
            Name: string.IsNullOrWhiteSpace(role.Name) ? role.Id.Trim() : role.Name.Trim(),
            SystemPrompt: role.SystemPrompt?.Trim() ?? string.Empty,
            Provider: role.Provider?.Trim() ?? string.Empty,
            Model: role.Model?.Trim() ?? string.Empty,
            Connectors: role.Connectors
                .Select(item => item?.Trim() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList());

    private static StoredRoleDefinition ToStoredRoleDraft(RoleDefinitionDto role) =>
        new(
            Id: role.Id?.Trim() ?? string.Empty,
            Name: role.Name?.Trim() ?? string.Empty,
            SystemPrompt: role.SystemPrompt?.Trim() ?? string.Empty,
            Provider: role.Provider?.Trim() ?? string.Empty,
            Model: role.Model?.Trim() ?? string.Empty,
            Connectors: role.Connectors
                .Select(item => item?.Trim() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList());

    private static RoleCatalogResponse ToResponse(StoredRoleCatalog catalog) =>
        new(
            catalog.HomeDirectory,
            catalog.FilePath,
            catalog.FileExists,
            catalog.Roles
                .Select(role => new RoleDefinitionDto(
                    role.Id,
                    role.Name,
                    role.SystemPrompt,
                    role.Provider,
                    role.Model,
                    role.Connectors.ToList()))
                .ToList());

    private static RoleDraftResponse ToDraftResponse(StoredRoleDraft draft) =>
        new(
            draft.HomeDirectory,
            draft.FilePath,
            draft.FileExists,
            draft.UpdatedAtUtc,
            draft.Draft is null ? null : ToDto(draft.Draft));

    private static RoleDefinitionDto ToDto(StoredRoleDefinition role) =>
        new(
            role.Id,
            role.Name,
            role.SystemPrompt,
            role.Provider,
            role.Model,
            role.Connectors.ToList());
}
