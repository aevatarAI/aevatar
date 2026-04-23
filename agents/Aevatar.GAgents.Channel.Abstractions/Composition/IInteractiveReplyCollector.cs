namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Turn-scoped buffer that collects an interactive reply intent produced by an LLM tool call
/// so that the relay finalize path can dispatch it through a composer.
/// </summary>
/// <remarks>
/// The collector is process-neutral; callers open a scope before invoking the LLM turn,
/// tool implementations capture a <see cref="MessageContent"/> intent during the turn,
/// and the finalize path consumes it exactly once via <see cref="TryTake"/>.
/// </remarks>
public interface IInteractiveReplyCollector
{
    /// <summary>
    /// Opens a new capture scope. The scope lives until the returned handle is disposed.
    /// </summary>
    IDisposable BeginScope();

    /// <summary>
    /// Captures the supplied intent into the currently active scope.
    /// No-op when no scope is active.
    /// </summary>
    void Capture(MessageContent intent);

    /// <summary>
    /// Consumes the captured intent for the active scope, returning <c>null</c>
    /// when no intent was captured or no scope is active.
    /// </summary>
    MessageContent? TryTake();
}
