using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Abstractions.Behaviors;

public static class ScriptMessageTypes
{
    private const string TypeUrlPrefix = "type.googleapis.com/";

    public static string GetTypeUrl<TMessage>()
        where TMessage : class, IMessage<TMessage>, new() =>
        GetTypeUrl(typeof(TMessage));

    public static string GetTypeUrl(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return GetTypeUrl(message.GetType());
    }

    public static string GetTypeUrl(System.Type messageClrType)
    {
        ArgumentNullException.ThrowIfNull(messageClrType);
        var descriptor = GetDescriptor(messageClrType);
        return GetTypeUrl(descriptor);
    }

    public static string GetTypeUrl(MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return TypeUrlPrefix + descriptor.FullName;
    }

    public static MessageDescriptor GetDescriptor(System.Type messageClrType)
    {
        var message = CreateMessage(messageClrType);
        return message.Descriptor;
    }

    public static IMessage CreateMessage(System.Type messageClrType)
    {
        ArgumentNullException.ThrowIfNull(messageClrType);
        if (Activator.CreateInstance(messageClrType) is not IMessage message)
            throw new InvalidOperationException(
                $"Type `{messageClrType.FullName}` must be a protobuf message with a public parameterless constructor.");

        return message;
    }

    public static TMessage? Unpack<TMessage>(Any? payload)
        where TMessage : class, IMessage<TMessage>, new()
    {
        if (payload == null)
            return null;

        if (!string.Equals(payload.TypeUrl, GetTypeUrl<TMessage>(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected protobuf payload type `{GetTypeUrl<TMessage>()}`, but got `{payload.TypeUrl}`.");
        }

        return payload.Unpack<TMessage>();
    }
}
