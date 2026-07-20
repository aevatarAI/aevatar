namespace Aevatar.GAgents.Channel.Runtime;

public static class ChannelTextMessageSegmenter
{
    public const int MaxTextSegmentLength = 30_000;

    public static IReadOnlyList<string> Segment(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length <= MaxTextSegmentLength)
            return [text];

        var segments = new List<string>((text.Length + MaxTextSegmentLength - 1) / MaxTextSegmentLength);
        for (var offset = 0; offset < text.Length; offset += MaxTextSegmentLength)
        {
            var length = Math.Min(MaxTextSegmentLength, text.Length - offset);
            segments.Add(text.Substring(offset, length));
        }

        return segments;
    }
}
