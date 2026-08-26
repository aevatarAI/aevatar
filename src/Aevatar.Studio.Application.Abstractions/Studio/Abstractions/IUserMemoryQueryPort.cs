namespace Aevatar.Studio.Application.Studio.Abstractions;

public readonly record struct UserMemoryOwnerKey
{
    public string ScopeId { get; }

    private UserMemoryOwnerKey(string scopeId)
    {
        ScopeId = scopeId;
    }

    public static UserMemoryOwnerKey ForScope(string scopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        return new UserMemoryOwnerKey(scopeId.Trim());
    }
}

public enum UserMemoryCategory
{
    Unspecified = 0,
    Preference = 1,
    Instruction = 2,
    Context = 3,
}

public enum UserMemorySource
{
    Unspecified = 0,
    Explicit = 1,
    Inferred = 2,
}

public sealed record UserMemoryEntrySnapshot(
    string Id,
    UserMemoryCategory Category,
    string Content,
    UserMemorySource Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record UserMemoryCategoryRetentionRule(
    UserMemoryCategory Category,
    int MaxEntries,
    int EvictionRank);

public sealed record UserMemoryRetentionPolicy(
    IReadOnlyList<UserMemoryCategoryRetentionRule> Rules);

public sealed record UserMemorySnapshot(
    UserMemoryOwnerKey Owner,
    long StateVersion,
    IReadOnlyList<UserMemoryEntrySnapshot> Entries,
    UserMemoryRetentionPolicy? RetentionPolicy = null,
    long PolicyRevision = 0)
{
    public static UserMemorySnapshot Empty(UserMemoryOwnerKey owner) =>
        new(owner, 0, []);
}

public sealed record ReplaceUserMemoryRetentionPolicy(
    UserMemoryOwnerKey Owner,
    IReadOnlyList<UserMemoryCategoryRetentionRule> Rules,
    long ExpectedStateVersion,
    string MutationId);

/// <summary>
/// Reads the per-user current-state replica materialized from committed
/// <c>UserMemoryGAgent</c> facts. Implementations must not activate actors,
/// prime projections, replay events, or write while serving this query.
/// </summary>
public interface IUserMemoryQueryPort
{
    Task<UserMemorySnapshot> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// Replaces actor-owned user-memory retention policy through the standard
/// command dispatch path. Acceptance does not imply commit or read-model visibility.
/// </summary>
public interface IUserMemoryRetentionPolicyCommandPort
{
    Task<UserConfigSaveReceipt> ReplaceAsync(
        ReplaceUserMemoryRetentionPolicy command,
        CancellationToken ct = default);
}
