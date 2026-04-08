using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.UserMemory;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IUserMemoryStore"/>.
/// Per-scope isolation: each scope gets its own <c>user-memory-{scopeId}</c> actor.
/// </summary>
internal sealed class ActorBackedUserMemoryStore : IUserMemoryStore, IAsyncDisposable
{
    private const string ActorIdPrefix = "user-memory-";

    private readonly IActorRuntime _runtime;
    private readonly IActorEventSubscriptionProvider _subscriptions;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly ILogger<ActorBackedUserMemoryStore> _logger;

    private readonly ConcurrentDictionary<string, ScopeState> _scopes = new(StringComparer.Ordinal);

    public ActorBackedUserMemoryStore(
        IActorRuntime runtime,
        IActorEventSubscriptionProvider subscriptions,
        IAppScopeResolver scopeResolver,
        ILogger<ActorBackedUserMemoryStore> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<UserMemoryDocument> GetAsync(CancellationToken ct = default)
    {
        var scopeState = await EnsureScopeAsync(ct);

        var state = scopeState.Snapshot;
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
            var actor = await EnsureActorAsync(ct);
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
            await SendCommandAsync(actor, evt, ct);
        }
    }

    public async Task<UserMemoryEntry> AddEntryAsync(
        string category, string content, string source, CancellationToken ct = default)
    {
        var actor = await EnsureActorAsync(ct);
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
        await SendCommandAsync(actor, evt, ct);

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
        var scopeState = await EnsureScopeAsync(ct);

        var state = scopeState.Snapshot;
        if (state is null || !state.Entries.Any(e => string.Equals(e.Id, id, StringComparison.Ordinal)))
            return false;

        var actor = await EnsureActorAsync(ct);
        var evt = new MemoryEntryRemovedEvent { EntryId = id };
        await SendCommandAsync(actor, evt, ct);
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

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes.Values)
        {
            if (scope.Subscription is not null)
                await scope.Subscription.DisposeAsync();
        }
    }

    private string ResolveActorId()
    {
        var scope = _scopeResolver.Resolve();
        if (scope is null)
            throw new InvalidOperationException(
                "User memory store requires an authenticated user scope. No scope could be resolved.");
        return ActorIdPrefix + scope.ScopeId;
    }

    private async Task<ScopeState> EnsureScopeAsync(CancellationToken ct)
    {
        var actorId = ResolveActorId();
        var scopeState = _scopes.GetOrAdd(actorId, _ => new ScopeState(actorId));

        if (scopeState.Initialized)
            return scopeState;

        await scopeState.InitLock.WaitAsync(ct);
        try
        {
            if (scopeState.Initialized)
                return scopeState;

            scopeState.Subscription = await _subscriptions.SubscribeAsync<EventEnvelope>(
                actorId,
                envelope => HandleSnapshotEventAsync(actorId, envelope),
                ct);

            await EnsureActorAsync(actorId, ct);
            scopeState.Initialized = true;
        }
        finally
        {
            scopeState.InitLock.Release();
        }

        return scopeState;
    }

    private Task HandleSnapshotEventAsync(string actorId, EventEnvelope envelope)
    {
        if (envelope.Payload is null)
            return Task.CompletedTask;

        if (envelope.Payload.Is(UserMemoryStateSnapshotEvent.Descriptor))
        {
            var snapshot = envelope.Payload.Unpack<UserMemoryStateSnapshotEvent>();
            if (_scopes.TryGetValue(actorId, out var scopeState))
            {
                scopeState.Snapshot = snapshot.Snapshot;
                _logger.LogDebug("User memory readmodel updated for {ActorId}: {EntryCount} entries",
                    actorId, snapshot.Snapshot?.Entries.Count ?? 0);
            }
        }

        return Task.CompletedTask;
    }

    private async Task<IActor> EnsureActorAsync(string actorId, CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(actorId);
        if (actor is not null)
            return actor;

        return await _runtime.CreateAsync<UserMemoryGAgent>(actorId, ct);
    }

    private async Task<IActor> EnsureActorAsync(CancellationToken ct)
    {
        var actorId = ResolveActorId();
        return await EnsureActorAsync(actorId, ct);
    }

    private static async Task SendCommandAsync(IActor actor, IMessage command, CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };
        await actor.HandleEventAsync(envelope, ct);
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

    private sealed class ScopeState(string actorId)
    {
        public string ActorId { get; } = actorId;
        public SemaphoreSlim InitLock { get; } = new(1, 1);
        public volatile bool Initialized;
        public volatile UserMemoryState? Snapshot;
        public IAsyncDisposable? Subscription;
    }
}
