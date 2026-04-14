using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.RoleCatalog;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IRoleCatalogStore"/>.
/// Reads the write actor's state directly.
/// Writes send commands to the Write GAgent.
/// Local workspace operations (import, draft backup) delegate to
/// <see cref="IStudioWorkspaceStore"/>.
/// Per-scope isolation: each scope gets its own <c>role-catalog-{scopeId}</c> actor.
/// </summary>
internal sealed class ActorBackedRoleCatalogStore : IRoleCatalogStore
{
    private const string WriteActorIdPrefix = "role-catalog-";
    private const string ActorHomeDirectory = "actor://role-catalog";
    private const string ActorFilePath = "actor://role-catalog/roles";

    private readonly IActorRuntime _runtime;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly IStudioWorkspaceStore _localWorkspaceStore;
    private readonly ILogger<ActorBackedRoleCatalogStore> _logger;

    public ActorBackedRoleCatalogStore(
        IActorRuntime runtime,
        IAppScopeResolver scopeResolver,
        IStudioWorkspaceStore localWorkspaceStore,
        ILogger<ActorBackedRoleCatalogStore> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _localWorkspaceStore = localWorkspaceStore ?? throw new ArgumentNullException(nameof(localWorkspaceStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken cancellationToken = default)
    {
        var state = await ReadWriteActorStateAsync(cancellationToken);
        var roles = state?.Roles
            .Select(ToStoredRoleDefinition)
            .ToList()
            .AsReadOnly()
            ?? (IReadOnlyList<StoredRoleDefinition>)[];

        return new StoredRoleCatalog(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath,
            FileExists: roles.Count > 0,
            Roles: roles);
    }

    public async Task<StoredRoleCatalog> SaveRoleCatalogAsync(
        StoredRoleCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        var actor = await EnsureWriteActorAsync(cancellationToken);
        var evt = new RoleCatalogSavedEvent();
        evt.Roles.AddRange(catalog.Roles.Select(ToProtoRoleDefinition));
        await ActorCommandDispatcher.SendAsync(actor, evt, cancellationToken);

        return new StoredRoleCatalog(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath,
            FileExists: true,
            Roles: catalog.Roles);
    }

    public async Task<ImportedRoleCatalog> ImportLocalCatalogAsync(CancellationToken cancellationToken = default)
    {
        var localCatalog = await _localWorkspaceStore.GetRoleCatalogAsync(cancellationToken);
        if (!localCatalog.FileExists)
        {
            throw new InvalidOperationException($"Local role catalog not found at '{localCatalog.FilePath}'.");
        }

        var actor = await EnsureWriteActorAsync(cancellationToken);
        var evt = new RoleCatalogSavedEvent();
        evt.Roles.AddRange(localCatalog.Roles.Select(ToProtoRoleDefinition));
        await ActorCommandDispatcher.SendAsync(actor, evt, cancellationToken);

        var importedCatalog = new StoredRoleCatalog(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath,
            FileExists: true,
            Roles: localCatalog.Roles);

        return new ImportedRoleCatalog(localCatalog.FilePath, true, importedCatalog);
    }

    public async Task<StoredRoleDraft> GetRoleDraftAsync(CancellationToken cancellationToken = default)
    {
        var state = await ReadWriteActorStateAsync(cancellationToken);
        var draftEntry = state?.Draft;
        if (draftEntry is null)
        {
            return new StoredRoleDraft(
                HomeDirectory: ActorHomeDirectory,
                FilePath: ActorFilePath + "/draft",
                FileExists: false,
                UpdatedAtUtc: null,
                Draft: null);
        }

        return new StoredRoleDraft(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath + "/draft",
            FileExists: true,
            UpdatedAtUtc: draftEntry.UpdatedAtUtc?.ToDateTimeOffset(),
            Draft: draftEntry.Draft is not null ? ToStoredRoleDefinition(draftEntry.Draft) : null);
    }

    public async Task<StoredRoleDraft> SaveRoleDraftAsync(
        StoredRoleDraft draft,
        CancellationToken cancellationToken = default)
    {
        var actor = await EnsureWriteActorAsync(cancellationToken);
        var updatedAtUtc = draft.UpdatedAtUtc ?? DateTimeOffset.UtcNow;
        var evt = new RoleDraftSavedEvent
        {
            Draft = draft.Draft is not null ? ToProtoRoleDefinition(draft.Draft) : null,
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(updatedAtUtc),
        };
        await ActorCommandDispatcher.SendAsync(actor, evt, cancellationToken);

        await _localWorkspaceStore.SaveRoleDraftAsync(draft, cancellationToken);

        return new StoredRoleDraft(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath + "/draft",
            FileExists: true,
            UpdatedAtUtc: updatedAtUtc,
            Draft: draft.Draft);
    }

    public async Task DeleteRoleDraftAsync(CancellationToken cancellationToken = default)
    {
        var actor = await EnsureWriteActorAsync(cancellationToken);
        await ActorCommandDispatcher.SendAsync(actor, new RoleDraftDeletedEvent(), cancellationToken);

        await _localWorkspaceStore.DeleteRoleDraftAsync(cancellationToken);
    }

    // ── Read write actor state directly ──

    private async Task<RoleCatalogState?> ReadWriteActorStateAsync(CancellationToken ct)
    {
        var actorId = ResolveWriteActorId();
        var actor = await _runtime.GetAsync(actorId);
        return (actor?.Agent as IAgent<RoleCatalogState>)?.State;
    }

    // ── Actor resolution ──

    private string ResolveWriteActorId() => WriteActorIdPrefix + _scopeResolver.ResolveScopeIdOrDefault();

    private async Task<IActor> EnsureWriteActorAsync(CancellationToken ct)
    {
        var actorId = ResolveWriteActorId();
        var actor = await _runtime.GetAsync(actorId);
        return actor ?? await _runtime.CreateAsync<RoleCatalogGAgent>(actorId, ct);
    }

    private static StoredRoleDefinition ToStoredRoleDefinition(RoleDefinitionEntry entry) =>
        new(
            Id: entry.Id,
            Name: entry.Name,
            SystemPrompt: entry.SystemPrompt,
            Provider: entry.Provider,
            Model: entry.Model,
            Connectors: entry.Connectors.ToList().AsReadOnly());

    private static RoleDefinitionEntry ToProtoRoleDefinition(StoredRoleDefinition def)
    {
        var entry = new RoleDefinitionEntry
        {
            Id = def.Id,
            Name = def.Name,
            SystemPrompt = def.SystemPrompt,
            Provider = def.Provider,
            Model = def.Model,
        };
        entry.Connectors.AddRange(def.Connectors);
        return entry;
    }
}
