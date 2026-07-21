using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Abstractions.ToolProviders;

public interface IAgentToolContinuationCapability
{
    Any CaptureContinuationCapability();

    bool MatchesContinuationCapability(Any capability);
}
