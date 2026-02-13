// ─────────────────────────────────────────────────────────────
// IStreamProvider - stream provider contract.
// Factory abstraction for resolving streams by actor ID.
// ─────────────────────────────────────────────────────────────

namespace Aevatar;

/// <summary>
/// Stream provider contract that resolves streams by actor ID.
/// </summary>
public interface IStreamProvider
{
    /// <summary>Gets the stream for the specified actor.</summary>
    IStream GetStream(string actorId);
}
