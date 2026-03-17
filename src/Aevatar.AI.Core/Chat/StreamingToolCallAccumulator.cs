using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.AI.Core.Chat;

internal sealed class StreamingToolCallAccumulator
{
    private readonly Dictionary<string, ToolCallAggregate> _aggregates = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private int _anonymousCounter;
    private string? _activeAnonymousKey;

    public ToolCall TrackDelta(ToolCall delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        var aggregate = ResolveAggregate(delta);
        if (!string.IsNullOrWhiteSpace(delta.Name))
            aggregate.Name = delta.Name;

        if (!string.IsNullOrEmpty(delta.ArgumentsJson))
            aggregate.Arguments.Append(delta.ArgumentsJson);

        return new ToolCall
        {
            Id = aggregate.Id,
            Name = string.IsNullOrWhiteSpace(delta.Name)
                ? aggregate.Name ?? string.Empty
                : delta.Name,
            ArgumentsJson = delta.ArgumentsJson ?? string.Empty,
        };
    }

    public IReadOnlyList<ToolCall> BuildToolCalls()
    {
        var result = new List<ToolCall>(_order.Count);
        foreach (var key in _order)
        {
            if (!_aggregates.TryGetValue(key, out var aggregate))
                continue;

            result.Add(new ToolCall
            {
                Id = aggregate.Id,
                Name = aggregate.Name ?? string.Empty,
                ArgumentsJson = aggregate.Arguments.ToString(),
            });
        }

        return result;
    }

    private ToolCallAggregate ResolveAggregate(ToolCall delta)
    {
        if (!string.IsNullOrWhiteSpace(delta.Id))
            return ResolveKnownIdAggregate(delta.Id);

        return ResolveAnonymousAggregate();
    }

    private ToolCallAggregate ResolveKnownIdAggregate(string id)
    {
        var knownKey = $"id:{id}";
        if (TryPromoteActiveAnonymousAggregate(knownKey, id, out var promoted))
        {
            _activeAnonymousKey = null;
            return promoted;
        }

        _activeAnonymousKey = null;
        if (!_aggregates.TryGetValue(knownKey, out var aggregate))
        {
            aggregate = new ToolCallAggregate(id);
            _aggregates[knownKey] = aggregate;
            _order.Add(knownKey);
        }

        return aggregate;
    }

    private ToolCallAggregate ResolveAnonymousAggregate()
    {
        if (!string.IsNullOrWhiteSpace(_activeAnonymousKey) &&
            _aggregates.TryGetValue(_activeAnonymousKey, out var activeAggregate))
        {
            return activeAggregate;
        }

        _anonymousCounter++;
        var anonymousKey = $"anon:{_anonymousCounter}";
        var anonymousId = $"stream-tool-call-{_anonymousCounter}";
        var aggregate = new ToolCallAggregate(anonymousId);
        _aggregates[anonymousKey] = aggregate;
        _order.Add(anonymousKey);
        _activeAnonymousKey = anonymousKey;
        return aggregate;
    }

    private bool TryPromoteActiveAnonymousAggregate(
        string knownKey,
        string knownId,
        out ToolCallAggregate aggregate)
    {
        aggregate = default!;

        if (string.IsNullOrWhiteSpace(_activeAnonymousKey))
            return false;

        if (!_aggregates.TryGetValue(_activeAnonymousKey, out var anonymousAggregate))
            return false;

        if (_aggregates.ContainsKey(knownKey))
            return false;

        anonymousAggregate.Id = knownId;
        _aggregates.Remove(_activeAnonymousKey);
        _aggregates[knownKey] = anonymousAggregate;
        ReplaceOrderKey(_activeAnonymousKey, knownKey);
        aggregate = anonymousAggregate;
        return true;
    }

    private void ReplaceOrderKey(string sourceKey, string targetKey)
    {
        for (var index = 0; index < _order.Count; index++)
        {
            if (!string.Equals(_order[index], sourceKey, StringComparison.Ordinal))
                continue;

            _order[index] = targetKey;
            return;
        }
    }

    private sealed class ToolCallAggregate
    {
        public ToolCallAggregate(string id)
        {
            Id = id;
        }

        public string Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();
    }
}
