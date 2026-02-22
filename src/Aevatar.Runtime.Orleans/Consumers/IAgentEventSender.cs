// ─────────────────────────────────────────────────────────────
// IAgentEventSender — Transport-agnostic interface for sending
// AgentEventMessage to the messaging backbone (Kafka / RabbitMQ / InMemory).
//
// Implementations:
//   Kafka  → uses ITopicProducer<AgentEventMessage>
//   Rabbit → uses ISendEndpointProvider + queue: URI
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Orleans.Consumers;

/// <summary>
/// Sends <see cref="AgentEventMessage"/> to the messaging backbone.
/// Registered in DI by the host project (Silo or Client) based on
/// the chosen transport (Kafka / RabbitMQ / InMemory).
/// </summary>
public interface IAgentEventSender
{
    /// <summary>Sends an event message to the configured transport.</summary>
    Task SendAsync(AgentEventMessage message, CancellationToken ct = default);
}
