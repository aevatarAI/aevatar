namespace Aevatar.Foundation.Abstractions.Streaming;

/// <summary>
/// Maintenance-only stream pub/sub state operations.
///
/// Stream backends that persist subscription/producer rendezvous state (e.g.
/// Orleans + Redis PubSubStore) can leak stale entries after ungraceful
/// deactivations or actor type migrations. The retained etag then blocks fresh
/// stream-producer registration on the next silo wave with
/// <c>InconsistentStateException</c>, breaking projection pipelines that depend
/// on the stream.
///
/// This contract lets cleanup code (retired-actor cleanup, projection scope
/// self-heal) reset that state without coupling to a specific backend.
/// In-memory implementations are no-ops.
/// </summary>
public interface IStreamPubSubMaintenance
{
    /// <summary>
    /// Resets stream pub/sub rendezvous state for the actor's self-stream so a
    /// fresh stream-producer registration can succeed without an etag conflict.
    /// </summary>
    /// <returns><c>true</c> when stale state was cleared; <c>false</c> when nothing was found.</returns>
    Task<bool> ResetActorStreamPubSubAsync(string actorId, CancellationToken ct = default);
}
