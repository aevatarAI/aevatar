using System.Globalization;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxRelayAppendSegmenterTests
{
    public static IEnumerable<object[]> SoftTargetCases()
    {
        foreach (var limit in new[] { 2000, 4096 })
        foreach (var acceptedCount in new[] { 0, 1 })
        foreach (var whitespacePrefix in new[] { false, true })
        {
            var target = acceptedCount == 0 ? 400 : 1600;
            foreach (var prefixLength in new[] { target - 1, target, target + 1, target + 101, limit - 1, limit })
                yield return [limit, acceptedCount, whitespacePrefix, prefixLength, prefixLength >= target];
        }
    }

    [Theory]
    [MemberData(nameof(SoftTargetCases))]
    public void SoftTargetExhaustion_SelectsUsefulBodyWithinHardLimit_AndPreservesTerminalTail(
        int limit, int acceptedCount, bool whitespacePrefix, int prefixLength, bool sendInitially)
    {
        var prefix = whitespacePrefix
            ? new string(' ', prefixLength - 1) + "A"
            : "A" + new string('\u0301', prefixLength - 1);
        var text = prefix + "B";

        var initial = NyxRelayAppendSegmenter.Select(text, acceptedCount, limit, false, false, raw => raw);
        initial.Should().Be(sendInitially ? prefix : string.Empty);
        var due = NyxRelayAppendSegmenter.Select(text, acceptedCount, limit, false, true, raw => raw);
        due.Should().Be(prefix);
        var terminal = NyxRelayAppendSegmenter.Select(text[due.Length..], acceptedCount + 1, limit, true, false, raw => raw);
        (due + terminal).Should().Be(text);
    }

    [Theory]
    [InlineData(2000, false, false)]
    [InlineData(2000, false, true)]
    [InlineData(2000, true, false)]
    [InlineData(2000, true, true)]
    [InlineData(4096, false, false)]
    [InlineData(4096, false, true)]
    [InlineData(4096, true, false)]
    [InlineData(4096, true, true)]
    public void SoftTargetExhaustion_StillRejectsPrefixBeyondHardLimit(int limit, bool whitespacePrefix, bool terminal)
    {
        var text = whitespacePrefix ? new string(' ', limit) + "AB" : "A" + new string('\u0301', limit) + "B";
        var select = () => NyxRelayAppendSegmenter.Select(text, 0, limit, terminal, false, raw => raw);

        select.Should().Throw<InvalidOperationException>().WithMessage("append_grapheme_exceeds_limit");
    }

    [Theory]
    [InlineData(2000, false)]
    [InlineData(2000, true)]
    [InlineData(4096, false)]
    [InlineData(4096, true)]
    public void SoftTargetExhaustion_StillRejectsFormattedGraphemeBeyondHardLimit(int limit, bool terminal)
    {
        var text = "*" + new string('\u0301', limit - 1) + "B";
        var select = () => NyxRelayAppendSegmenter.Select(text, 0, limit, terminal, false,
            raw => raw.Replace("*", "\\*", StringComparison.Ordinal));

        select.Should().Throw<InvalidOperationException>().WithMessage("append_grapheme_exceeds_limit");
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\n\n")]
    [InlineData("\t \r\n")]
    public void WhitespaceOnlyPendingText_IsNotASendableProgressCandidate(string text)
    {
        NyxRelayAppendSegmenter.Select(text, 0, 4096, false, true, raw => raw).Should().BeEmpty();
    }

    [Theory]
    [InlineData(" A")]
    [InlineData("\n\n好")]
    [InlineData("A")]
    [InlineData("👩🏽‍💻")]
    [InlineData(" 👩🏽‍💻")]
    public void PendingSingleVisibleGrapheme_WaitsForTerminalWithoutDroppingWhitespace(string text)
    {
        NyxRelayAppendSegmenter.Select(text, 0, 4096, false, false, raw => raw).Should().BeEmpty();
        NyxRelayAppendSegmenter.Select(text, 0, 4096, false, true, raw => raw).Should().BeEmpty();
        NyxRelayAppendSegmenter.Select(text, 0, 4096, true, false, raw => raw).Should().Be(text);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void OversizedTerminalGrapheme_RemainsAHardLimitFailure(string prefix)
    {
        var text = prefix + "A" + new string('\u0301', 8);
        var select = () => NyxRelayAppendSegmenter.Select(text, 0, 8, true, false, raw => raw);

        select.Should().Throw<InvalidOperationException>().WithMessage("append_grapheme_exceeds_limit");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedVisibleGraphemeExceedingLimit_RemainsAHardLimitFailure(bool terminal)
    {
        var select = () => NyxRelayAppendSegmenter.Select("AB", 0, 8, terminal, true, _ => new string('x', 9));

        select.Should().Throw<InvalidOperationException>().WithMessage("append_grapheme_exceeds_limit");
    }

    [Fact]
    public void FirstSegment_WaitsForUsefulText_AndPrefersParagraph()
    {
        NyxRelayAppendSegmenter.Select("short", 0, 2000, false, false, text => text).Should().BeEmpty();
        var paragraph = new string('a', 100) + "\n\n";
        NyxRelayAppendSegmenter.Select(paragraph + new string('b', 400), 0, 2000, false, false, text => text)
            .Should().Be(paragraph);
    }

    [Fact]
    public void DueAndTerminal_FlushShortBodyWithoutDroppingWhitespace()
    {
        NyxRelayAppendSegmenter.Select("hello ", 0, 2000, false, true, text => text).Should().Be("hell");
        NyxRelayAppendSegmenter.Select("hello ", 0, 2000, true, false, text => text).Should().Be("hello ");
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(4096)]
    public void Escaping_IsMeasuredAfterFormatting_AndEveryGraphemeIsRetained(int limit)
    {
        var text = string.Concat(Enumerable.Repeat("👩🏽‍💻e\u0301_", 700));
        var remaining = text;
        var reconstructed = "";
        var accepted = 0;
        while (remaining.Length > 0)
        {
            var segment = NyxRelayAppendSegmenter.Select(remaining, accepted++, limit, true, false,
                raw => raw.Replace("_", "\\_", StringComparison.Ordinal));
            segment.Should().NotBeEmpty();
            segment.Replace("_", "\\_", StringComparison.Ordinal).Length.Should().BeLessThanOrEqualTo(limit);
            var offsets = StringInfo.ParseCombiningCharacters(remaining);
            (segment.Length == remaining.Length || offsets.Contains(segment.Length)).Should().BeTrue();
            reconstructed += segment;
            remaining = remaining[segment.Length..];
        }
        reconstructed.Should().Be(text);
    }

    [Fact]
    public void StableChunk_DoesNotFlushLastGraphemeThatMayStillExtend()
    {
        NyxRelayAppendSegmenter.Select("Hi e", 0, 2000, false, true, text => text).Should().Be("Hi ");
    }
}
