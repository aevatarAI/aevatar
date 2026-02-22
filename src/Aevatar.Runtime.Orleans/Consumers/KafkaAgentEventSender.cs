// ─────────────────────────────────────────────────────────────
// KafkaAgentEventSender — Sends AgentEventMessage to Kafka topic
// via MassTransit ITopicProducer<T>.
// ─────────────────────────────────────────────────────────────

using MassTransit;

namespace Aevatar.Orleans.Consumers;

/// <summary>
/// Sends <see cref="AgentEventMessage"/> to a Kafka topic via
/// MassTransit's <see cref="ITopicProducer{T}"/>.
/// </summary>
public sealed class KafkaAgentEventSender(
    ITopicProducer<AgentEventMessage> producer) : IAgentEventSender
{
    /// <inheritdoc />
    public Task SendAsync(AgentEventMessage message, CancellationToken ct = default)
        => producer.Produce(message, ct);
}
