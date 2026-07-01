using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Ornn.SystemSkillOverlay;

public sealed class SystemSkillOverlayBuilder : ISystemSkillOverlayBuilder
{
    private const string Header = "## System Skill Overlay (force-injected)";
    private const string SkillMarkdownFileName = "SKILL.md";

    private readonly Aevatar.AI.Abstractions.ToolProviders.SystemSkillOverlayOptions _options;
    private readonly OrnnSkillClient _client;
    private readonly ILogger _logger;

    public SystemSkillOverlayBuilder(
        Aevatar.AI.Abstractions.ToolProviders.SystemSkillOverlayOptions options,
        OrnnSkillClient client,
        ILogger<SystemSkillOverlayBuilder>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? NullLogger<SystemSkillOverlayBuilder>.Instance;
    }

    public async Task<Aevatar.AI.Abstractions.SystemSkillOverlay> BuildAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.OrgServiceToken) || _options.MaxBytes <= 0)
            return EmptyOverlay();

        try
        {
            var result = await _client.SearchSkillsAsync(
                _options.OrgServiceToken,
                query: _options.Tag,
                scope: "private",
                pageSize: 100,
                ct: ct);

            var summaries = result.Items
                .Where(IsTrustedSystemSkillSummary)
                .ToArray();

            var entries = new List<OverlaySkillEntry>(summaries.Length);
            foreach (var summary in summaries)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(summary.Guid))
                    continue;

                var skill = await _client.GetSkillJsonAsync(_options.OrgServiceToken, summary.Guid, ct);
                var body = ExtractSkillBody(skill);
                if (string.IsNullOrWhiteSpace(body))
                    continue;

                if (!OverlayFrontmatterParser.TryParse(body, out var frontmatter, out var overlayBody) ||
                    !OverlayAuthoringContract.IsValid(frontmatter))
                {
                    continue;
                }

                entries.Add(new OverlaySkillEntry(
                    summary.Name ?? skill?.Name ?? summary.Guid,
                    summary.Description ?? skill?.Description ?? string.Empty,
                    overlayBody.Trim()));
            }

            var markdown = BuildWithinBudget(entries);
            return string.IsNullOrEmpty(markdown)
                ? EmptyOverlay()
                : new Aevatar.AI.Abstractions.SystemSkillOverlay
                {
                    OverlayMarkdown = markdown,
                    SourceWatermark = ComputeWatermark(entries),
                    MaterializedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "System skill overlay build failed for tag '{Tag}'", _options.Tag);
            throw;
        }
    }

    private bool IsTrustedSystemSkillSummary(OrnnSkillSummary summary) =>
        summary.IsPrivate &&
        summary.Tags != null &&
        summary.Tags.Contains(_options.Tag);

    private static string? ExtractSkillBody(OrnnSkillJson? skill)
    {
        if (skill?.Files == null || skill.Files.Count == 0)
            return null;

        return skill.Files
            .FirstOrDefault(static file => file.Key.Equals(SkillMarkdownFileName, StringComparison.OrdinalIgnoreCase))
            .Value;
    }

    private string BuildWithinBudget(IReadOnlyList<OverlaySkillEntry> entries)
    {
        if (entries.Count == 0)
            return string.Empty;

        var fullBodyCount = Math.Clamp(_options.MaxSkills, 0, entries.Count);
        var markdown = Render(entries, fullBodyCount);
        while (fullBodyCount > 0 && Utf8ByteCount(markdown) > _options.MaxBytes)
        {
            fullBodyCount--;
            markdown = Render(entries, fullBodyCount);
        }

        if (Utf8ByteCount(markdown) <= _options.MaxBytes)
            return markdown;

        return RenderCatalogWithinBudget(entries);
    }

    private static string Render(IReadOnlyList<OverlaySkillEntry> entries, int fullBodyCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Header);

        for (var i = 0; i < entries.Count; i++)
        {
            builder.AppendLine();
            if (i < fullBodyCount)
                builder.AppendLine(entries[i].Body);
            else
                builder.AppendLine(BuildCatalogLine(entries[i]));
        }

        return builder.ToString().TrimEnd();
    }

    private string RenderCatalogWithinBudget(IReadOnlyList<OverlaySkillEntry> entries)
    {
        var builder = new StringBuilder(Header);
        if (Utf8ByteCount(builder.ToString()) > _options.MaxBytes)
            return string.Empty;

        foreach (var entry in entries)
        {
            var candidate = builder.ToString() + Environment.NewLine + Environment.NewLine + BuildCatalogLine(entry);
            if (Utf8ByteCount(candidate) > _options.MaxBytes)
                break;

            builder.Clear();
            builder.Append(candidate);
        }

        return builder.ToString();
    }

    private static string BuildCatalogLine(OverlaySkillEntry entry)
    {
        var description = string.IsNullOrWhiteSpace(entry.Description)
            ? "No description."
            : entry.Description.Trim();
        return $"- {entry.Name.Trim()}: {description}";
    }

    private static int Utf8ByteCount(string value) => Encoding.UTF8.GetByteCount(value);

    private static string ComputeWatermark(IReadOnlyList<OverlaySkillEntry> entries)
    {
        var canonical = string.Join("\n", entries.Select(static entry => $"{entry.Name}\n{entry.Description}\n{entry.Body}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static Aevatar.AI.Abstractions.SystemSkillOverlay EmptyOverlay() =>
        new()
        {
            OverlayMarkdown = string.Empty,
        };

    private sealed record OverlaySkillEntry(string Name, string Description, string Body);
}
