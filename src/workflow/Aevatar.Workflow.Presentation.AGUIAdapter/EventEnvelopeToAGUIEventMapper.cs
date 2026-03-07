using Aevatar.Foundation.Abstractions;
using Aevatar.Presentation.AGUI;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public interface IEventEnvelopeToAGUIEventMapper
{
    IReadOnlyList<AGUIEvent> Map(EventEnvelope envelope);
}

public interface IAGUIEventEnvelopeMappingHandler
{
    int Order { get; }

    bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events);
}

public sealed class EventEnvelopeToAGUIEventMapper : IEventEnvelopeToAGUIEventMapper
{
    private readonly IReadOnlyList<IAGUIEventEnvelopeMappingHandler> _handlers;

    public EventEnvelopeToAGUIEventMapper(IEnumerable<IAGUIEventEnvelopeMappingHandler> handlers)
    {
        _handlers = handlers.OrderBy(x => x.Order).ToList();
    }

    public IReadOnlyList<AGUIEvent> Map(EventEnvelope envelope)
    {
        if (envelope.Payload == null)
            return [];

        var output = new List<AGUIEvent>();
        foreach (var handler in _handlers)
        {
            if (!handler.TryMap(envelope, out var mapped) || mapped.Count == 0)
                continue;

            output.AddRange(mapped);
        }

        return output;
    }
}
