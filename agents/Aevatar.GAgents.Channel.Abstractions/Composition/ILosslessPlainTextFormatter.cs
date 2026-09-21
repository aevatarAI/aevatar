namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Prepares text for a platform without trimming, truncating, or adding presentation content.
/// Callers can measure the result before fixing a segment's delivery boundary.
/// </summary>
public interface ILosslessPlainTextFormatter
{
    /// <summary>Escapes the entire text for the platform while preserving all source content.</summary>
    string PreparePlainText(string text, ConversationReference conversation);
}
