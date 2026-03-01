using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Core;

namespace Aevatar.Scripting.Core.Runtime;

public sealed record ScriptRuntimeExecutionRequest(
    string RuntimeActorId,
    string CurrentStateJson,
    string CurrentReadModelJson,
    RunScriptRequestedEvent RunEvent,
    string ScriptId,
    string ScriptRevision,
    string SourceText,
    IServiceProvider Services,
    IEventPublisher EventPublisher);
