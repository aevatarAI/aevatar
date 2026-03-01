using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Core.Runtime;

public interface IScriptCapabilityFactory
{
    IScriptRuntimeCapabilities Create(
        string runtimeActorId,
        string runId,
        string correlationId,
        IServiceProvider services);
}
