using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Runtime;

public sealed record ScriptBehaviorDispatchRequest(
    string ActorId,
    string DefinitionActorId,
    string ScriptId,
    string Revision,
    string SourceText,
    string SourceHash,
    string StateTypeUrl,
    string ReadModelTypeUrl,
    Any? CurrentStateRoot,
    long CurrentStateVersion,
    EventEnvelope Envelope,
    IScriptBehaviorRuntimeCapabilities Capabilities);
