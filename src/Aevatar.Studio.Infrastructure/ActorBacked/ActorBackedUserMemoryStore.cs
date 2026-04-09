using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserMemory;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IUserMemoryStore"/>.
/// Reads the write actor's state directly.
/// Writes send commands to the Write GAgent.
/// </summary>
internal sealed class ActorBackedUserMemoryStore : IUserMemoryStore
{
    private const string WriteActorIdPrefix = "user-memory-";

    private readonly IActorRuntime _runtime;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly ILogger<ActorBackedUserMemoryStore> _logger;

    public ActorBackedUserMemoryStore(
        IActorRuntime runtime,
        IAppScopeResolver scopeResolver,
        ILogger<ActorBackedUserMemoryStore> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<UserMemoryDocument> GetAsync(CancellationToken ct = default)
    {
        var state = await ReadWriteActorStateAsync(ct);
        if (state is null)
            return UserMemoryDocument.Empty;

        var entries = state.Entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Id) && !string.IsNullOrWhiteSpace(e.Content))
            .Select(e => new UserMemoryEntry(
                Id: e.Id,
                Category: e.Category,
                Content: e.Content,
                Source: e.Source,
                CreatedAt: e.CreatedAt,
                UpdatedAt: e.UpdatedAt))
            .ToList();

        return new UserMemoryDocument(1, entries);
    }

    public async Task SaveAsync(UserMemoryDocument document, CancellationToken ct = default)
    {
        // Actor-backed store does not support bulk save.
        // The canonical path is AddEntryAsync / RemoveEntryAsync.
        // SaveAsync is kept for interface compatibility; it reconciles
        // the actor state by adding missing entries and removing stale ones.
        var current = await GetAsync(ct);

        var currentIds = new HashSet<string>(current.Entries.Select(e => e.Id), StringComparer.Ordinal);
        var targetIds = new HashSet<string>(document.Entries.Select(e => e.Id), StringComparer.Ordinal);

        // Remove entries not in the target document
        foreach (var id in currentIds.Except(targetIds))
        {
            await RemoveEntryAsync(id, ct);
        }

        // Add entries not in the current state
        foreach (var entry in document.Entries.Where(e => !currentIds.Contains(e.Id)))
        {
            var actor = await EnsureWriteActorAsync(ct);
            var evt = new MemoryEntryAddedEvent
            {
                Entry = new UserMemoryEntryProto
                {
                    Id = entry.Id,
                    Category = entry.Category,
                    Content = entry.Content,
                    Source = entry.Source,
                    CreatedAt = entry.CreatedAt,
                    UpdatedAt = entry.UpdatedAt,
                },
            };
            await ActorCommandDispatcher.SendAsync(actor, evt, ct);
        }
    }

    public async Task<UserMemoryEntry> AddEntryAsync(
        string category, string content, string source, CancellationToken ct = default)
    {
        var actor = await EnsureWriteActorAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entry = new UserMemoryEntryProto
        {
            Id = GenerateId(),
            Category = NormalizeCategory(category),
            Content = content.Trim(),
            Source = NormalizeSource(source),
            CreatedAt = now,
            UpdatedAt = now,
        };

        var evt = new MemoryEntryAddedEvent { Entry = entry };
        await ActorCommandDispatcher.SendAsync(actor, evt, ct);

        return new UserMemoryEntry(
            Id: entry.Id,
            Category: entry.Category,
            Content: entry.Content,
            Source: entry.Source,
            CreatedAt: entry.CreatedAt,
            UpdatedAt: entry.UpdatedAt);
    }

    public async Task<bool> RemoveEntryAsync(string id, CancellationToken ct = default)
    {
        var state = await ReadWriteActorStateAsync(ct);
        if (state is null || !state.Entries.Any(e => string.Equals(e.Id, id, StringComparison.Ordinal)))
            return false;

        var actor = await EnsureWriteActorAsync(ct);
        var evt = new MemoryEntryRemovedEvent { EntryId = id };
        await ActorCommandDispatcher.SendAsync(actor, evt, ct);
        return true;
    }

    public async Task<string> BuildPromptSectionAsync(int maxChars = 2000, CancellationToken ct = default)
    {
        UserMemoryDocument doc;
        try
        {
            doc = await GetAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load user memory for prompt injection");
            return string.Empty;
        }

        if (doc.Entries.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("<user-memory>");

        var categoryOrder = new[]
        {
            UserMemoryCategories.Preference,
            UserMemoryCategories.Instruction,
            UserMemoryCategories.Context,
        };

        var grouped = doc.Entries
            .GroupBy(e => e.Category)
            .OrderBy(g => Array.IndexOf(categoryOrder, g.Key) is var i && i >= 0 ? i : int.MaxValue);

        foreach (var group in grouped)
        {
            var header = group.Key switch
            {
                UserMemoryCategories.Preference => "## Preferences",
                UserMemoryCategories.Instruction => "## Instructions",
                UserMemoryCategories.Context => "## Context",
                _ => $"## {Capitalize(group.Key)}",
            };
            sb.AppendLine(header);
            foreach (var entry in group.OrderByDescending(e => e.UpdatedAt))
                sb.AppendLine($"- {entry.Content}");
            sb.AppendLine();
        }

        sb.Append("</user-memory>");

        var result = sb.ToString();
        if (result.Length <= maxChars)
            return result;

        // Truncate to maxChars at a newline boundary.
        var truncated = result[..maxChars];
        var lastNewline = truncated.LastIndexOf('\n');
        return lastNewline > 0
            ? truncated[..lastNewline] + "\n</user-memory>"
            : truncated;
    }

    // ── Read write actor state directly ──

    private async Task<UserMemoryState?> ReadWriteActorStateAsync(CancellationToken ct)
    {
        var actorId = ResolveWriteActorId();
        var actor = await _runtime.GetAsync(actorId);
        return (actor?.Agent as IAgent<UserMemoryState>)?.State;
    }

    // ── Actor resolution ──

    private string ResolveScopeId()
        => _scopeResolver.Resolve()?.ScopeId
           ?? throw new InvalidOperationException(
               "User memory store requires an authenticated user scope. No scope could be resolved.");

    private string ResolveWriteActorId() => WriteActorIdPrefix + ResolveScopeId();

    private async Task<IActor> EnsureWriteActorAsync(CancellationToken ct)
    {
        var actorId = ResolveWriteActorId();
        var actor = await _runtime.GetAsync(actorId);
        return actor ?? await _runtime.CreateAsync<UserMemoryGAgent>(actorId, ct);
    }

    private static string GenerateId()
    {
        var bytes = RandomNumberGenerator.GetBytes(6);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizeCategory(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            UserMemoryCategories.Preference => UserMemoryCategories.Preference,
            UserMemoryCategories.Instruction => UserMemoryCategories.Instruction,
            UserMemoryCategories.Context => UserMemoryCategories.Context,
            null or "" => UserMemoryCategories.Context,
            var v => v,
        };

    private static string NormalizeSource(string? value) =>
        string.Equals(value?.Trim(), UserMemorySources.Explicit, StringComparison.OrdinalIgnoreCase)
            ? UserMemorySources.Explicit
            : UserMemorySources.Inferred;

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
