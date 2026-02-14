// ─────────────────────────────────────────────────────────────
// IAgent - core agent contract.
// Defines lifecycle, event handling, and subscription metadata for agent units.
// ─────────────────────────────────────────────────────────────

using Google.Protobuf;

namespace Aevatar.Foundation.Abstractions;

/// <summary>
/// Core agent contract for event handling, activation, and subscriptions.
/// </summary>
public interface IAgent
{
    /// <summary>Unique agent identifier.</summary>
    string Id { get; }

    /// <summary>Handles an incoming event envelope.</summary>
    Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default);

    /// <summary>Returns a human-readable agent description.</summary>
    Task<string> GetDescriptionAsync();

    /// <summary>Returns subscribed event types for the agent.</summary>
    Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync();

    /// <summary>Activates the agent and runs initialization logic.</summary>
    Task ActivateAsync(CancellationToken ct = default);

    /// <summary>Deactivates the agent and runs cleanup logic.</summary>
    Task DeactivateAsync(CancellationToken ct = default);
}

/// <summary>
/// Stateful agent contract that extends IAgent with typed Protobuf state.
/// </summary>
/// <typeparam name="TState">Agent state type, must be a Protobuf IMessage.</typeparam>
public interface IAgent<TState> : IAgent where TState : class, IMessage
{
    /// <summary>Current agent state.</summary>
    TState State { get; }
}
