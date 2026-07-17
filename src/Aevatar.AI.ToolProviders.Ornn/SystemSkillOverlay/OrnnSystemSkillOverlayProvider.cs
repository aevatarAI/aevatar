using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AbstractionsOptions = Aevatar.AI.Abstractions.ToolProviders.SystemSkillOverlayOptions;

namespace Aevatar.AI.ToolProviders.Ornn.SystemSkillOverlay;

/// <summary>
/// Host-level, context-aware system skill overlay source (issue #2498). Reads a public, org-owned
/// Ornn skillset (the trust anchor is set membership, not a squattable tag), pre-renders one overlay
/// variant per channel platform, and serves them to both reply seams via <see cref="GetCurrent"/> —
/// a synchronous O(1) cached read. Staleness triggers a single-flight, fire-and-forget background
/// refresh using the per-turn token supplied by the seam; the current turn always serves
/// last-known-good, so the reply hot path never waits on Ornn ("never query-time"). Missing or empty
/// remote content returns no global layer; the mandatory built-in floor is owned separately.
/// </summary>
public sealed class OrnnSystemSkillOverlayProvider : ISystemSkillOverlayProvider
{
    private const int DefaultMaxBytes = 32 * 1024;
    private const int DefaultMaxSkills = 32;
    private static readonly TimeSpan DefaultRefreshTtl = TimeSpan.FromMinutes(15);
    private const string OverlayScopeTagPrefix = "overlay-scope-";
    private const string GlobalScope = "global";
    private const string SkillMarkdownFileName = "SKILL.md";

    private readonly AbstractionsOptions _options;
    private readonly OrnnSkillClient _client;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private volatile Snapshot? _snapshot;
    private long _lastRefreshTicks = long.MinValue;
    private volatile string? _pinnedGuid;

    public OrnnSystemSkillOverlayProvider(
        AbstractionsOptions options,
        OrnnSkillClient client,
        ILogger<OrnnSystemSkillOverlayProvider>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? NullLogger<OrnnSystemSkillOverlayProvider>.Instance;
    }

    public GlobalSystemSkillPromptLayer? GetCurrent(SystemSkillOverlayRequest request)
    {
        try
        {
            TriggerRefreshIfStale(request.NyxIdAccessToken);
        }
        catch (Exception ex)
        {
            // GetCurrent runs on the reply hot path; a refresh scheduling failure must never break a turn.
            _logger.LogWarning(ex, "System skill overlay refresh scheduling failed; serving cached overlay");
        }

        var resolved = _snapshot?.Resolve(request.Platform);
        if (resolved is not null && !string.IsNullOrWhiteSpace(resolved.Content))
            return resolved;

        return null;
    }

    private void TriggerRefreshIfStale(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return;

        var ttlMs = (long)(_options.RefreshTtl > TimeSpan.Zero ? _options.RefreshTtl : DefaultRefreshTtl).TotalMilliseconds;
        var last = Interlocked.Read(ref _lastRefreshTicks);
        if (last != long.MinValue && Environment.TickCount64 - last < ttlMs)
            return;

        // Single-flight: if a refresh is already running, this turn just serves last-known-good.
        if (!_refreshGate.Wait(0))
            return;

        var capturedToken = token;
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(capturedToken, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "System skill overlay background refresh failed; keeping last-known-good overlay");
            }
            finally
            {
                _refreshGate.Release();
            }
        });
    }

    /// <summary>
    /// Builds a fresh snapshot from the Ornn set and swaps it in, resetting the refresh TTL clock. Used
    /// by the lazy background refresh, and available for a host to prime the overlay at startup or for
    /// deterministic testing. Keeps the last-known-good snapshot when the set is unreachable/unchanged.
    /// </summary>
    public async Task RefreshAsync(string token, CancellationToken ct = default)
    {
        // Stamp the attempt time so a failing upstream is retried once per TTL, not per turn.
        Interlocked.Exchange(ref _lastRefreshTicks, Environment.TickCount64);
        var snapshot = await BuildSnapshotAsync(token, ct).ConfigureAwait(false);
        if (snapshot is null)
            return; // fetch failed / unreachable → keep last-known-good

        var previous = _snapshot;
        if (previous is not null && string.Equals(previous.Watermark, snapshot.Watermark, StringComparison.Ordinal))
            return; // unchanged

        _snapshot = snapshot;
        _logger.LogInformation(
            "System skill overlay refreshed: set_guid={SetGuid}, watermark={Watermark}, platform_variants={Variants}",
            snapshot.Guid,
            snapshot.Watermark,
            snapshot.PlatformCount);
    }

    private async Task<Snapshot?> BuildSnapshotAsync(string token, CancellationToken ct)
    {
        var reference = _pinnedGuid ?? _options.SetName;
        if (string.IsNullOrWhiteSpace(reference))
            return null;

        OrnnSkillSet? set;
        try
        {
            set = await _client.GetSkillSetAsync(token, reference, ct).ConfigureAwait(false);
        }
        catch (RemoteSkillFetchException ex)
        {
            _logger.LogWarning(ex, "System skill overlay set fetch was denied");
            return null;
        }

        // On a pinned-guid miss (set deleted/renamed) we keep last-known-good and never silently
        // re-resolve by name — that would re-open the same-name squat window (issue #2498).
        if (set is null)
            return null;

        if (_pinnedGuid is null && !string.IsNullOrWhiteSpace(set.Guid))
            _pinnedGuid = set.Guid;

        var members = new List<ParsedMember>();
        foreach (var member in set.Members)
        {
            ct.ThrowIfCancellationRequested();

            // The member converter yields null for unrecognized JSON shapes; skip those instead of
            // failing the whole refresh with an NRE.
            var memberReference = member?.Reference;
            if (member is null || string.IsNullOrWhiteSpace(memberReference))
                continue;

            OrnnSkillJson? skill;
            try
            {
                skill = await _client.GetSkillJsonAsync(token, memberReference, ct).ConfigureAwait(false);
            }
            catch (RemoteSkillFetchException)
            {
                // One denied member must not abort the whole snapshot.
                continue;
            }

            if (skill is null)
                continue;

            var scopes = ResolveScopes(skill.Metadata?.Tags);
            if (scopes.Count == 0)
                continue; // no overlay-scope-* tag → not a forced overlay member

            var body = OverlayBodyExtractor.ExtractBody(ExtractSkillMarkdown(skill));
            if (string.IsNullOrWhiteSpace(body))
                continue;

            members.Add(new ParsedMember(
                member.Name ?? skill.Name ?? memberReference,
                skill.Description ?? string.Empty,
                body,
                scopes));
        }

        return BuildSnapshot(_pinnedGuid ?? set.Guid ?? string.Empty, members);
    }

    private Snapshot BuildSnapshot(string guid, IReadOnlyList<ParsedMember> members)
    {
        var maxBytes = _options.MaxBytes > 0 ? _options.MaxBytes : DefaultMaxBytes;
        var maxSkills = _options.MaxSkills > 0 ? _options.MaxSkills : DefaultMaxSkills;
        var watermark = ComputeWatermark(members);

        var globalOnly = RenderVariant(members, platform: null, maxSkills, maxBytes, watermark);

        var platforms = members
            .SelectMany(static member => member.Scopes)
            .Where(static scope => !string.Equals(scope, GlobalScope, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var byPlatform = new Dictionary<string, GlobalSystemSkillPromptLayer>(StringComparer.Ordinal);
        foreach (var platform in platforms)
            byPlatform[platform] = RenderVariant(members, platform, maxSkills, maxBytes, watermark);

        return new Snapshot(guid, watermark, globalOnly, byPlatform);
    }

    private static GlobalSystemSkillPromptLayer RenderVariant(
        IReadOnlyList<ParsedMember> members,
        string? platform,
        int maxSkills,
        int maxBytes,
        string watermark)
    {
        var entries = members
            .Where(member => member.AppliesTo(platform))
            .Select(static member => new OverlaySkillEntry(member.Name, member.Description, member.Body))
            .ToArray();

        var markdown = SystemSkillOverlayRenderer.Render(entries, maxSkills, maxBytes);
        return new GlobalSystemSkillPromptLayer(
            markdown,
            new GlobalSystemSkillPromptProvenance(string.IsNullOrEmpty(markdown) ? string.Empty : watermark),
            new PromptLayerBounds(maxBytes, (maxBytes + 3) / 4));
    }

    private static IReadOnlyList<string> ResolveScopes(IEnumerable<string>? tags)
    {
        if (tags is null)
            return [];

        var scopes = new List<string>();
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            var normalized = tag.Trim().ToLowerInvariant();
            if (!normalized.StartsWith(OverlayScopeTagPrefix, StringComparison.Ordinal))
                continue;

            var scope = normalized[OverlayScopeTagPrefix.Length..];
            if (scope.Length > 0 && !scopes.Contains(scope))
                scopes.Add(scope);
        }

        return scopes;
    }

    private static string? ExtractSkillMarkdown(OrnnSkillJson skill)
    {
        if (skill.Files is null || skill.Files.Count == 0)
            return null;

        foreach (var file in skill.Files)
        {
            if (file.Key.Equals(SkillMarkdownFileName, StringComparison.OrdinalIgnoreCase))
                return file.Value;
        }

        return null;
    }

    private static string ComputeWatermark(IReadOnlyList<ParsedMember> members)
    {
        var canonical = string.Join(
            "\n",
            members.Select(static member =>
                $"{string.Join(',', member.Scopes.OrderBy(static s => s, StringComparer.Ordinal))}\n{member.Name}\n{member.Description}\n{member.Body}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private sealed record ParsedMember(string Name, string Description, string Body, IReadOnlyList<string> Scopes)
    {
        public bool AppliesTo(string? platform)
        {
            if (Scopes.Contains(GlobalScope))
                return true;

            return platform is not null && Scopes.Contains(platform);
        }
    }

    private sealed class Snapshot(
        string guid,
        string watermark,
        GlobalSystemSkillPromptLayer globalOnly,
        IReadOnlyDictionary<string, GlobalSystemSkillPromptLayer> byPlatform)
    {
        public string Guid { get; } = guid;
        public string Watermark { get; } = watermark;
        public int PlatformCount => byPlatform.Count;

        public GlobalSystemSkillPromptLayer Resolve(string? platform)
        {
            if (!string.IsNullOrWhiteSpace(platform) &&
                byPlatform.TryGetValue(platform.Trim().ToLowerInvariant(), out var overlay))
            {
                return overlay;
            }

            return globalOnly;
        }
    }
}
