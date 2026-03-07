using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core;

internal static class WorkflowCapabilityRoutes
{
    public static string For<TMessage>()
        where TMessage : IMessage, new() =>
        Any.Pack(new TMessage()).TypeUrl;
}
