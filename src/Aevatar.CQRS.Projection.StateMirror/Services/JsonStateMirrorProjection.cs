using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.CQRS.Projection.StateMirror.Abstractions;
using Aevatar.CQRS.Projection.StateMirror.Configuration;

namespace Aevatar.CQRS.Projection.StateMirror.Services;

public sealed class JsonStateMirrorProjection<TState, TReadModel>
    : IStateMirrorProjection<TState, TReadModel>
    where TState : class
    where TReadModel : class
{
    private readonly StateMirrorProjectionOptions _options;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public JsonStateMirrorProjection(StateMirrorProjectionOptions options)
    {
        _options = options;
    }

    public TReadModel Project(TState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var rootNode = JsonSerializer.SerializeToNode(state, _serializerOptions);
        if (rootNode is not JsonObject sourceObject)
        {
            throw new InvalidOperationException(
                $"State '{typeof(TState).FullName}' cannot be projected to '{typeof(TReadModel).FullName}'.");
        }

        var mirroredObject = new JsonObject();
        foreach (var property in sourceObject)
        {
            if (property.Key == null)
                continue;
            if (_options.IgnoredFields.Contains(property.Key))
                continue;

            var targetName = ResolveTargetName(property.Key);
            mirroredObject[targetName] = property.Value?.DeepClone();
        }

        var projected = mirroredObject.Deserialize<TReadModel>(_serializerOptions);
        if (projected == null)
        {
            throw new InvalidOperationException(
                $"State projection failed from '{typeof(TState).FullName}' to '{typeof(TReadModel).FullName}'.");
        }

        return projected;
    }

    private string ResolveTargetName(string sourceName)
    {
        if (_options.RenamedFields.TryGetValue(sourceName, out var targetName) &&
            !string.IsNullOrWhiteSpace(targetName))
        {
            return targetName.Trim();
        }

        return sourceName;
    }
}
