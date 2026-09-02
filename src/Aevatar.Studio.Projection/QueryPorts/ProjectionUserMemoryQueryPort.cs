using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.UserMemory;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.ReadModels;
using ApplicationUserMemoryCategory = Aevatar.Studio.Application.Studio.Abstractions.UserMemoryCategory;
using ApplicationUserMemorySource = Aevatar.Studio.Application.Studio.Abstractions.UserMemorySource;
using ActorUserMemoryCategory = Aevatar.GAgents.UserMemory.UserMemoryCategory;
using ActorUserMemorySource = Aevatar.GAgents.UserMemory.UserMemorySource;

namespace Aevatar.Studio.Projection.QueryPorts;

/// <summary>
/// Reads user-memory current-state replicas without touching actor lifecycle.
/// </summary>
public sealed class ProjectionUserMemoryQueryPort : IUserMemoryQueryPort
{
    private const string ActorIdPrefix = "user-memory-";

    private readonly IAppScopeResolver _scopeResolver;
    private readonly IProjectionDocumentReader<UserMemoryCurrentStateDocument, string> _documentReader;

    public ProjectionUserMemoryQueryPort(
        IAppScopeResolver scopeResolver,
        IProjectionDocumentReader<UserMemoryCurrentStateDocument, string> documentReader)
    {
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<UserMemorySnapshot> GetAsync(CancellationToken ct = default)
    {
        var owner = UserMemoryOwnerKey.ForScope(_scopeResolver.ResolveScopeIdOrDefault());
        var actorId = ActorIdPrefix + owner.ScopeId;
        var document = await _documentReader.GetAsync(actorId, ct);
        if (document?.StateRoot == null ||
            !document.StateRoot.Is(UserMemoryState.Descriptor))
        {
            return UserMemorySnapshot.Empty(owner);
        }

        var state = document.StateRoot.Unpack<UserMemoryState>();
        var entries = state.Entries
            .Where(static entry => IsReadable(entry))
            .Select(static entry => new UserMemoryEntrySnapshot(
                entry.Id,
                MapCategory(entry.Category),
                entry.Content,
                MapSource(entry.Source),
                DateTimeOffset.FromUnixTimeMilliseconds(entry.CreatedAtMs),
                DateTimeOffset.FromUnixTimeMilliseconds(entry.UpdatedAtMs)))
            .ToList()
            .AsReadOnly();

        return new UserMemorySnapshot(owner, document.StateVersion, entries);
    }

    private static bool IsReadable(UserMemoryEntryProto entry) =>
        !string.IsNullOrWhiteSpace(entry.Id) &&
        !string.IsNullOrWhiteSpace(entry.Content) &&
        entry.CreatedAtMs >= 0 &&
        entry.UpdatedAtMs >= entry.CreatedAtMs &&
        entry.UpdatedAtMs <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    private static ApplicationUserMemoryCategory MapCategory(ActorUserMemoryCategory category) =>
        category switch
        {
            ActorUserMemoryCategory.Preference => ApplicationUserMemoryCategory.Preference,
            ActorUserMemoryCategory.Instruction => ApplicationUserMemoryCategory.Instruction,
            ActorUserMemoryCategory.Context => ApplicationUserMemoryCategory.Context,
            _ => ApplicationUserMemoryCategory.Unspecified,
        };

    private static ApplicationUserMemorySource MapSource(ActorUserMemorySource source) =>
        source switch
        {
            ActorUserMemorySource.Explicit => ApplicationUserMemorySource.Explicit,
            ActorUserMemorySource.Inferred => ApplicationUserMemorySource.Inferred,
            _ => ApplicationUserMemorySource.Unspecified,
        };
}
