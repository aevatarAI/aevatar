namespace Aevatar.AI.Abstractions.ToolProviders;

public interface IAgentToolCapabilityDescriptor
{
    IReadOnlyCollection<string> Capabilities { get; }
}
