using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Core.Runtime;

public interface IScriptRuntimeCapabilityComposer
{
    IScriptRuntimeCapabilities Compose(ScriptRuntimeCapabilityContext context);
}
