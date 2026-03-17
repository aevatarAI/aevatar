using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Serialization;

public interface IProtobufMessageCodec
{
    Any? Pack(IMessage? message);

    IMessage? Unpack(Any? payload, System.Type messageClrType);

    IMessage? Unpack(Any? payload, MessageDescriptor descriptor);

    string GetTypeUrl(System.Type messageClrType);
}
