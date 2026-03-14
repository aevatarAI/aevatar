using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.Serialization;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Infrastructure.Serialization;

public sealed class ProtobufMessageCodec : IProtobufMessageCodec
{
    public Any? Pack(IMessage? message) => message == null ? null : Any.Pack(message);

    public IMessage? Unpack(Any? payload, System.Type messageClrType)
    {
        ArgumentNullException.ThrowIfNull(messageClrType);
        if (payload == null)
            return null;

        var expectedTypeUrl = ScriptMessageTypes.GetTypeUrl(messageClrType);
        if (!string.Equals(payload.TypeUrl, expectedTypeUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected protobuf payload type `{expectedTypeUrl}`, but got `{payload.TypeUrl}`.");
        }

        var message = ScriptMessageTypes.CreateMessage(messageClrType);
        message.MergeFrom(payload.Value);
        return message;
    }

    public IMessage? Unpack(Any? payload, MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (payload == null)
            return null;

        var expectedTypeUrl = ScriptMessageTypes.GetTypeUrl(descriptor.ClrType);
        if (!string.Equals(payload.TypeUrl, expectedTypeUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected protobuf payload type `{expectedTypeUrl}`, but got `{payload.TypeUrl}`.");
        }

        if (descriptor.Parser.ParseFrom(payload.Value) is not IMessage message)
            throw new InvalidOperationException($"Descriptor `{descriptor.FullName}` did not produce a protobuf message.");

        return message;
    }

    public string GetTypeUrl(System.Type messageClrType) => ScriptMessageTypes.GetTypeUrl(messageClrType);
}
