namespace Aevatar.AI.Abstractions;

/// <summary>
/// Canonical metadata keys used in <see cref="ChatRequestEvent.Metadata" />.
/// </summary>
public static class ChatRequestMetadataKeys
{
    /// <summary>
    /// Command identifier from CQRS command ingestion.
    /// </summary>
    public const string CommandId = "command_id";
}
