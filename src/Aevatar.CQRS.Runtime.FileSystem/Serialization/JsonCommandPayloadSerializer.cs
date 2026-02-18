using System.Text.Json;
using Aevatar.CQRS.Runtime.Abstractions.Serialization;

namespace Aevatar.CQRS.Runtime.FileSystem.Serialization;

internal sealed class JsonCommandPayloadSerializer : ICommandPayloadSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string Serialize<TCommand>(TCommand command)
        where TCommand : class
    {
        ArgumentNullException.ThrowIfNull(command);
        return JsonSerializer.Serialize(command, SerializerOptions);
    }

    public object Deserialize(string payloadJson, Type commandType)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            throw new ArgumentException("Payload is required.", nameof(payloadJson));
        ArgumentNullException.ThrowIfNull(commandType);

        return JsonSerializer.Deserialize(payloadJson, commandType, SerializerOptions)
               ?? throw new InvalidOperationException($"Unable to deserialize command '{commandType.FullName}'.");
    }
}
