using Aevatar.Foundation.Abstractions.Propagation;

namespace Aevatar.Foundation.Core.Propagation;

/// <summary>
/// Default correlation/causation policy:
/// - correlation inherits from inbound when outbound is empty
/// - causation is always inbound event id when available
/// </summary>
public sealed class DefaultCorrelationLinkPolicy : ICorrelationLinkPolicy
{
    public string? ResolveCorrelationId(EventEnvelope outboundEnvelope, EventEnvelope? inboundEnvelope)
    {
        if (!string.IsNullOrWhiteSpace(outboundEnvelope.CorrelationId))
            return outboundEnvelope.CorrelationId;

        return string.IsNullOrWhiteSpace(inboundEnvelope?.CorrelationId)
            ? null
            : inboundEnvelope.CorrelationId;
    }

    public string? ResolveCausationId(EventEnvelope outboundEnvelope, EventEnvelope? inboundEnvelope)
    {
        ArgumentNullException.ThrowIfNull(outboundEnvelope);

        return string.IsNullOrWhiteSpace(inboundEnvelope?.Id)
            ? null
            : inboundEnvelope.Id;
    }
}
