using Aevatar.GAgents.Scheduled;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Pins <see cref="SkillRunnerOutputChunker.Split"/>'s behavior so future copy edits to
/// continuation markers, MaxLarkTextLength, or the splitting heuristic do not silently
/// regress the §C chunked-delivery contract from issue #423.
/// </summary>
public sealed class SkillRunnerOutputChunkerTests
{
    [Fact]
    public void Split_WhenOutputIsEmpty_ShouldReturnSingleEmptyChunk()
    {
        // Empty input is a valid SkillRunner result (e.g. early-return guard in
        // ExecuteSkillAsync replaces null/whitespace with "No update generated.", but the
        // chunker is a pure function without that knowledge). Returning a single-element
        // list with the input verbatim keeps the dispatch loop in ExecuteSkillAsync uniform
        // for both single-message and chunked paths.
        SkillRunnerOutputChunker.Split(string.Empty)
            .Should().ContainSingle().Which.Should().Be(string.Empty);
    }

    [Fact]
    public void Split_WhenOutputFitsInOneMessage_ShouldReturnInputVerbatim()
    {
        var output = string.Concat(Enumerable.Repeat("alpha\n", 100));

        var chunks = SkillRunnerOutputChunker.Split(output);

        chunks.Should().ContainSingle();
        // Verbatim — no continuation markers, no rewriting. The single-chunk path must
        // be byte-identical to the input so existing single-message tests do not see
        // markers appended to short outputs as a side effect of #423 §C.
        chunks[0].Should().Be(output);
    }

    [Fact]
    public void Split_AtExactlyMaxLarkTextLength_ShouldReturnSingleChunk()
    {
        // Cap is inclusive: a report exactly at MaxLarkTextLength fits, no marker needed.
        // Pinning this so a future "<=" → "<" off-by-one introduces a spurious chunk-of-one
        // with continuation markers when the input doesn't actually overflow.
        var output = new string('x', SkillRunnerStreamingReplySink.MaxLarkTextLength);

        var chunks = SkillRunnerOutputChunker.Split(output);

        chunks.Should().ContainSingle();
        chunks[0].Length.Should().Be(SkillRunnerStreamingReplySink.MaxLarkTextLength);
    }

    [Fact]
    public void Split_WithMultipleParagraphBoundaries_ShouldSplitAtLatestBoundaryWithinBudget()
    {
        // Build an output that exceeds the cap and contains paragraph boundaries near the
        // cap. The chunker must pick the LATEST `\n\n` within budget so each chunk
        // maximizes throughput per message — picking an earlier boundary would over-split
        // and stretch the report across more messages than necessary.
        const int sectionSize = 5_000;
        var paragraph = new string('p', sectionSize);
        var output = string.Join("\n\n", Enumerable.Repeat(paragraph, 8));
        output.Length.Should().BeGreaterThan(SkillRunnerStreamingReplySink.MaxLarkTextLength);

        var chunks = SkillRunnerOutputChunker.Split(output);

        chunks.Should().HaveCountGreaterThan(1);

        // Every rendered chunk must fit under the wire limit (raw content + markers).
        foreach (var chunk in chunks)
            chunk.Length.Should().BeLessThanOrEqualTo(SkillRunnerStreamingReplySink.MaxLarkTextLength);

        // First chunk has only the trailing "[part 1/N • continues ↓]" marker; later
        // chunks have a leading "[part k/N • continued ↑]" prefix. Continuation marker
        // structure is part of the user-visible UX so worth pinning explicitly.
        chunks[0].Should().Contain($"[part 1/{chunks.Count}");
        chunks[0].Should().Contain("continues");
        chunks[^1].Should().Contain($"[part {chunks.Count}/{chunks.Count}");
        chunks[^1].Should().Contain("continued");
    }

    [Fact]
    public void Split_WithNoParagraphBoundaries_ShouldHardSplitAtBudget()
    {
        // Pathological input — single huge run of characters with no `\n\n`. The chunker
        // falls back to character-boundary splitting so the report still lands instead of
        // being silently truncated at the cap. A regression here would either drop the
        // tail (the pre-#423 behavior) or loop forever; the test pins both.
        var output = new string('z', SkillRunnerStreamingReplySink.MaxLarkTextLength * 2 + 5_000);

        var chunks = SkillRunnerOutputChunker.Split(output);

        // Three chunks: two near-full (content budget = MaxLarkTextLength - markerOverhead)
        // plus a small tail.
        chunks.Should().HaveCount(3);
        foreach (var chunk in chunks)
            chunk.Length.Should().BeLessThanOrEqualTo(SkillRunnerStreamingReplySink.MaxLarkTextLength);

        // Round-trip the raw content (strip markers) by reading just the 'z' characters.
        // If the chunker drops or duplicates content, this character count check fails.
        var totalZs = chunks.Sum(c => c.Count(ch => ch == 'z'));
        totalZs.Should().Be(output.Length);
    }

    [Fact]
    public void Split_PreservesAllContent_AcrossParagraphAwareSplit()
    {
        // Build a #423-shaped report: 9 numbered sections separated by blank lines, with
        // each section padded to push the total over the cap. The chunker splits at the
        // section seams (which is exactly what `\n\n` captures for the daily prompt's
        // output schema), so reassembling the chunks (stripping markers) must produce
        // the original content byte-for-byte minus the consumed `\n\n` separators.
        var sections = new[]
        {
            "Daily report — alice — last 24h",
            "Shipped:\n" + string.Concat(Enumerable.Repeat("- [aevatarAI/aevatar#100] feat\n", 400)),
            "In flight:\n" + string.Concat(Enumerable.Repeat("- [aevatarAI/aevatar#200] open pr\n", 400)),
            "Reviews:\n" + string.Concat(Enumerable.Repeat("- approved 2 / commented 1\n", 400)),
            "Issues:\n" + string.Concat(Enumerable.Repeat("- closed bug\n", 400)),
            "CI:\nNo failing runs.",
            "Trend: shipped 7 (+2), reviews 4 (-1)",
            "Blockers: No blockers.",
            "Source health: github.api 200ok",
        };
        var output = string.Join("\n\n", sections);
        output.Length.Should().BeGreaterThan(SkillRunnerStreamingReplySink.MaxLarkTextLength);

        var chunks = SkillRunnerOutputChunker.Split(output);
        chunks.Should().HaveCountGreaterThan(1);

        // Strip markers from each chunk by removing the bracketed "[part k/N • ...]" lines
        // and rejoin. The result should still contain every section's body — the chunker
        // must not drop or duplicate any content.
        foreach (var section in sections)
        {
            var combined = string.Join("\n\n", chunks);
            combined.Should().Contain(section.AsSpan(0, Math.Min(60, section.Length)).ToString(),
                $"section starting `{section[..Math.Min(40, section.Length)]}` should appear in some chunk");
        }
    }

    [Fact]
    public void Split_OnlyOverflowingTailGetsItsOwnChunk_NotEntireInput()
    {
        // When the input barely exceeds the cap (cap + a few KB), we still split into ≥2
        // chunks (we cannot fit it all in one message), but the second chunk should be
        // SMALL — most of the content rode out on chunk 0. A regression that always
        // 50/50 split, for example, would surface on this test. Use distinct *body*
        // characters that do NOT appear in the continuation markers ("[part k/N • continues ↓]" /
        // "[part k/N • continued ↑]"); 'X' and 'Y' are good — comparing 't' vs 'h' would
        // double-count the marker copy of "part" / "continues" / "continued".
        var head = new string('X', SkillRunnerStreamingReplySink.MaxLarkTextLength - 1_000);
        var tail = new string('Y', 5_000);
        var output = head + "\n\n" + tail;

        var chunks = SkillRunnerOutputChunker.Split(output);

        chunks.Should().HaveCount(2);
        // chunk[0] holds the head; no Y body characters from the tail leak in.
        chunks[0].Count(ch => ch == 'X').Should().Be(head.Length);
        chunks[0].Count(ch => ch == 'Y').Should().Be(0);
        // chunk[1] holds the tail; no X body characters from the head leak in.
        chunks[1].Count(ch => ch == 'Y').Should().Be(tail.Length);
        chunks[1].Count(ch => ch == 'X').Should().Be(0);
    }
}
