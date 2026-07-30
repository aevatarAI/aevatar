namespace Aevatar.Foundation.Abstractions.EventSourcing;

/// <summary>
/// Marker semantics for a maintenance republish of committed state
/// (<c>GAgentBase.RepublishCommittedStateAsync</c>): the envelope re-broadcasts
/// the actor's current committed state under a deterministic synthetic event id
/// instead of a newly appended event. Consumers that must only react to
/// genuinely new committed facts — the committed-fact audit materializer in
/// particular — use the marker to skip republished envelopes instead of
/// fabricating a duplicate record for a fact that did not newly occur.
/// </summary>
public static class CommittedStateRepublish
{
    public const string EventIdPrefix = "rebuild:";

    public static string BuildEventId(string actorId, long version) =>
        $"{EventIdPrefix}{actorId}:{version}";

    public static bool IsRepublishEventId(string? eventId) =>
        eventId is not null && eventId.StartsWith(EventIdPrefix, StringComparison.Ordinal);
}
