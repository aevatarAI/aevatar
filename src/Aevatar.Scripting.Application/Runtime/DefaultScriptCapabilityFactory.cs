using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Runtime;

namespace Aevatar.Scripting.Application.Runtime;

public sealed class DefaultScriptCapabilityFactory : IScriptCapabilityFactory
{
    public IScriptRuntimeCapabilities Create(
        string runtimeActorId,
        string runId,
        string correlationId,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new ScriptRuntimeCapabilities(runtimeActorId, runId, correlationId, services);
    }
}
