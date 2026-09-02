using Aevatar.Audit.Abstractions.CommittedFacts;

namespace Aevatar.Audit.Core.CommittedFacts;

public sealed class AuditCommittedEventTranslatorRegistry
{
    private readonly IReadOnlyDictionary<string, IAuditCommittedEventTranslator> _translators;

    public AuditCommittedEventTranslatorRegistry(IEnumerable<IAuditCommittedEventTranslator> translators)
    {
        ArgumentNullException.ThrowIfNull(translators);

        var map = new Dictionary<string, IAuditCommittedEventTranslator>(StringComparer.Ordinal);
        foreach (var translator in translators)
        {
            if (translator == null)
                continue;

            var key = translator.EventTypeUrl;
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException($"{translator.GetType().Name} returned an empty event type url.");
            if (!map.TryAdd(key, translator))
                throw new InvalidOperationException($"Duplicate audit committed-event translator for '{key}'.");
        }

        _translators = map;
    }

    public bool TryGet(string eventTypeUrl, out IAuditCommittedEventTranslator translator) =>
        _translators.TryGetValue(eventTypeUrl, out translator!);
}
