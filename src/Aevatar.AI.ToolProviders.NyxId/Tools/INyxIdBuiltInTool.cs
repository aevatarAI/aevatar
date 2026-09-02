using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Tools;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

public interface INyxIdBuiltInTool : IAgentTool
{
    ToolPresentationDescriptor IAgentTool.Presentation =>
        ToolPresentationDescriptors.BuiltIn(Name, Name, Description);

    AgentToolReceipt? IAgentTool.CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson) =>
        NyxIdManagedToolReceiptFactory.TryCreate(callId, toolName, resultJson);
}
