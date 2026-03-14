using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Runtime;

public interface IScriptBehaviorRuntimeCapabilityFactory
{
    IScriptBehaviorRuntimeCapabilities Create(
        ScriptBehaviorRuntimeCapabilityContext context,
        Func<IMessage, Aevatar.Foundation.Abstractions.TopologyAudience, CancellationToken, Task> publishAsync,
        Func<string, IMessage, CancellationToken, Task> sendToAsync,
        Func<IMessage, CancellationToken, Task> publishToSelfAsync,
        Func<string, TimeSpan, IMessage, CancellationToken, Task<RuntimeCallbackLease>> scheduleSelfSignalAsync,
        Func<RuntimeCallbackLease, CancellationToken, Task> cancelCallbackAsync);
}
