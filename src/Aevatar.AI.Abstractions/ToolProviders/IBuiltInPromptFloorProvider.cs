using Aevatar.AI.Abstractions.Prompting;

namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>Provides the mandatory built-in prompt floor independently of optional remote layers.</summary>
public interface IBuiltInPromptFloorProvider
{
    BuiltInPromptFloorLayer GetFloor();
}
