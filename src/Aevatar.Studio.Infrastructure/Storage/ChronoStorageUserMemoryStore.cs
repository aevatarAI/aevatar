using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions.LLMProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageUserMemoryStore : IUserMemoryStore
{
    private const string MemoryFileName = "user-memory.json";
    private const int MaxEntries = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ChronoStorageCatalogBlobClient _blobClient;
    private readonly ConnectorCatalogStorageOptions _options;
    private readonly ILogger<ChronoStorageUserMemoryStore> _logger;

    public ChronoStorageUserMemoryStore(
        ChronoStorageCatalogBlobClient blobClient,
        IOptions<ConnectorCatalogStorageOptions> options,
        ILogger<ChronoStorageUserMemoryStore> logger)
    {
        _blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<UserMemoryDocument> GetAsync(CancellationToken ct = default)
    {
        try
        {
            var context = _blobClient.TryResolveContext(_options.UserConfigPrefix, MemoryFileName);
            if (context is null)
                return UserMemoryDocument.Empty;

            var payload = await _blobClient.TryDownloadAsync(context, ct);
            if (payload is null)
                return UserMemoryDocument.Empty;

            var dto = JsonSerializer.Deserialize<UserMemoryDocumentDto>(payload, JsonOptions);
            if (dto is null)
                return UserMemoryDocument.Empty;

            var entries = (dto.Entries ?? [])
                .Where(e => !string.IsNullOrWhiteSpace(e.Id) && !string.IsNullOrWhiteSpace(e.Content))
                .Select(e => new UserMemoryEntry(
                    Id: e.Id!,
                    Category: NormalizeCategory(e.Category),
                    Content: e.Content!,
                    Source: NormalizeSource(e.Source),
                    CreatedAt: e.CreatedAt,
                    UpdatedAt: e.UpdatedAt))
                .ToList();

            return new UserMemoryDocument(dto.Version > 0 ? dto.Version : 1, entries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read user memory from chrono-storage; returning empty");
            return UserMemoryDocument.Empty;
        }
    }

    public async Task SaveAsync(UserMemoryDocument document, CancellationToken ct = default)
    {
        var context = _blobClient.TryResolveContext(_options.UserConfigPrefix, MemoryFileName);
        if (context is null)
            throw new InvalidOperationException(
                "User memory storage is not available. Chrono-storage is disabled or the remote context could not be resolved.");

        var dto = new UserMemoryDocumentDto
        {
            Version = document.Version,
            Entries = document.Entries.Select(e => new UserMemoryEntryDto
            {
                Id = e.Id,
                Category = e.Category,
                Content = e.Content,
                Source = e.Source,
                CreatedAt = e.CreatedAt,
                UpdatedAt = e.UpdatedAt,
            }).ToList(),
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
        await _blobClient.UploadAsync(context, json, "application/json", ct);
    }

    public async Task<UserMemoryEntry> AddEntryAsync(
        string category, string content, string source, CancellationToken ct = default)
    {
        var doc = await GetAsync(ct);
        var entries = new List<UserMemoryEntry>(doc.Entries);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entry = new UserMemoryEntry(
            Id: GenerateId(),
            Category: NormalizeCategory(category),
            Content: content.Trim(),
            Source: NormalizeSource(source),
            CreatedAt: now,
            UpdatedAt: now);

        entries.Add(entry);

        // Enforce cap: evict oldest in same category first, then globally oldest.
        while (entries.Count > MaxEntries)
        {
            var normalizedCategory = entry.Category;
            var oldestSameCategory = entries
                .Where(e => e.Category == normalizedCategory && e.Id != entry.Id)
                .OrderBy(e => e.CreatedAt)
                .FirstOrDefault();

            if (oldestSameCategory is not null)
            {
                entries.Remove(oldestSameCategory);
            }
            else
            {
                var globallyOldest = entries
                    .Where(e => e.Id != entry.Id)
                    .OrderBy(e => e.CreatedAt)
                    .FirstOrDefault();
                if (globallyOldest is not null)
                    entries.Remove(globallyOldest);
                else
                    break;
            }
        }

        await SaveAsync(new UserMemoryDocument(doc.Version, entries), ct);
        return entry;
    }

    public async Task<bool> RemoveEntryAsync(string id, CancellationToken ct = default)
    {
        var doc = await GetAsync(ct);
        var entries = doc.Entries.ToList();
        var index = entries.FindIndex(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (index < 0)
            return false;

        entries.RemoveAt(index);
        await SaveAsync(new UserMemoryDocument(doc.Version, entries), ct);
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

    // ── DTOs for JSON serialization ─────────────────────────────────────────

    private sealed class UserMemoryDocumentDto
    {
        public int Version { get; set; } = 1;
        public List<UserMemoryEntryDto>? Entries { get; set; }
    }

    private sealed class UserMemoryEntryDto
    {
        public string? Id { get; set; }
        public string? Category { get; set; }
        public string? Content { get; set; }
        public string? Source { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }
    }
}
