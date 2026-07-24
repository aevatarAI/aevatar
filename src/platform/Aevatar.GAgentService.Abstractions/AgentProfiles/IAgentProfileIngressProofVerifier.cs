using Google.Protobuf;

namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

public interface IAgentProfileIngressProofVerifier
{
    bool Verify(string targetActorId, IMessage command);
}

public sealed class AgentProfileIngressProofUnavailableException : AgentProfileBoundaryException
{
    public AgentProfileIngressProofUnavailableException()
        : base("AGENT_PROFILE_INGRESS_PROOF_UNAVAILABLE")
    {
    }
}
