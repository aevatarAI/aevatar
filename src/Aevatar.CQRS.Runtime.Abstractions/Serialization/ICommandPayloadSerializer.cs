namespace Aevatar.CQRS.Runtime.Abstractions.Serialization;

public interface ICommandPayloadSerializer
{
    string Serialize<TCommand>(TCommand command)
        where TCommand : class;

    object Deserialize(string payloadJson, Type commandType);
}
