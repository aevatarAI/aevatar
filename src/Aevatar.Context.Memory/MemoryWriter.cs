using Aevatar.Context.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Context.Memory;

/// <summary>
/// 将去重后的记忆写入 IContextStore。
/// 处理 CREATE / UPDATE / MERGE / SKIP 四种决策。
/// </summary>
public sealed class MemoryWriter
{
    private readonly IContextStore _store;
    private readonly ILogger _logger;

    public MemoryWriter(
        IContextStore store,
        ILogger<MemoryWriter>? logger = null)
    {
        _store = store;
        _logger = logger ?? NullLogger<MemoryWriter>.Instance;
    }

    /// <summary>
    /// 将去重结果写入存储。
    /// </summary>
    public async Task WriteAsync(
        IReadOnlyList<DeduplicationResult> results,
        CancellationToken ct = default)
    {
        var written = 0;
        var skipped = 0;

        foreach (var result in results)
        {
            switch (result.Decision)
            {
                case DeduplicationDecision.Create:
                    await CreateMemoryAsync(result, ct);
                    written++;
                    break;

                case DeduplicationDecision.Update when result.ExistingUri.HasValue:
                    await UpdateMemoryAsync(result, ct);
                    written++;
                    break;

                case DeduplicationDecision.Merge when result.ExistingUri.HasValue:
                    await MergeMemoryAsync(result, ct);
                    written++;
                    break;

                case DeduplicationDecision.Skip:
                    skipped++;
                    break;
            }
        }

        _logger.LogInformation("Memory write complete: {Written} written, {Skipped} skipped",
            written, skipped);
    }

    private async Task CreateMemoryAsync(DeduplicationResult result, CancellationToken ct)
    {
        var fileName = GenerateFileName(result.Candidate);
        var fileUri = result.TargetScope.Join(fileName);

        await _store.CreateDirectoryAsync(result.TargetScope, ct);
        await _store.WriteAsync(fileUri, result.Candidate.Content, ct);

        _logger.LogDebug("Created memory: {Uri}", fileUri);
    }

    private async Task UpdateMemoryAsync(DeduplicationResult result, CancellationToken ct)
    {
        await _store.WriteAsync(result.ExistingUri!.Value, result.Candidate.Content, ct);
        _logger.LogDebug("Updated memory: {Uri}", result.ExistingUri);
    }

    private async Task MergeMemoryAsync(DeduplicationResult result, CancellationToken ct)
    {
        var existingContent = await _store.ReadAsync(result.ExistingUri!.Value, ct);
        var merged = $"{existingContent}\n\n---\n\n{result.Candidate.Content}";
        await _store.WriteAsync(result.ExistingUri!.Value, merged, ct);
        _logger.LogDebug("Merged memory: {Uri}", result.ExistingUri);
    }

    private static string GenerateFileName(CandidateMemory memory)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var slug = memory.Content.Length > 30
            ? memory.Content[..30].Replace(' ', '-').Replace('/', '-')
            : memory.Content.Replace(' ', '-').Replace('/', '-');

        // Keep only safe chars
        slug = new string(slug.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray()).Trim('-');
        if (slug.Length == 0)
            slug = "memory";

        return $"{timestamp}-{slug}.md";
    }
}
