using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Runtime;

public sealed partial record ScriptBehaviorDispatchRequest(
    string ActorId,
    string DefinitionActorId,
    string ScriptId,
    string Revision,
    string SourceText,
    string SourceHash,
    ScriptPackageSpec ScriptPackage,
    string StateTypeUrl,
    string ReadModelTypeUrl,
    Any? CurrentStateRoot,
    long CurrentStateVersion,
    EventEnvelope Envelope,
    IScriptBehaviorRuntimeCapabilities Capabilities);

public sealed partial record ScriptBehaviorDispatchRequest
{
    public ScriptBehaviorDispatchRequest(
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
        IScriptBehaviorRuntimeCapabilities Capabilities)
        : this(
            ActorId,
            DefinitionActorId,
            ScriptId,
            Revision,
            SourceText,
            SourceHash,
            ScriptPackageSpecExtensions.CreateSingleSource(SourceText),
            StateTypeUrl,
            ReadModelTypeUrl,
            CurrentStateRoot,
            CurrentStateVersion,
            Envelope,
            Capabilities)
    {
    }
}
