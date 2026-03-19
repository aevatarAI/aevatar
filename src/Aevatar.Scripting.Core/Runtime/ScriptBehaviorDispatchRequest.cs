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
    string ScopeId,
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
    public string ReadModelSchemaVersion { get; init; } = string.Empty;

    public string ReadModelSchemaHash { get; init; } = string.Empty;

    public ScriptBehaviorDispatchRequest(
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
        IScriptBehaviorRuntimeCapabilities Capabilities)
        : this(
            ActorId,
            DefinitionActorId,
            ScriptId,
            Revision,
            ScopeId: string.Empty,
            SourceText,
            SourceHash,
            ScriptPackage,
            StateTypeUrl,
            ReadModelTypeUrl,
            CurrentStateRoot,
            CurrentStateVersion,
            Envelope,
            Capabilities)
    {
    }

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
            ScopeId: string.Empty,
            SourceText,
            SourceHash,
            StateTypeUrl,
            ReadModelTypeUrl,
            CurrentStateRoot,
            CurrentStateVersion,
            Envelope,
            Capabilities)
    {
    }

    public ScriptBehaviorDispatchRequest(
        string ActorId,
        string DefinitionActorId,
        string ScriptId,
        string Revision,
        string ScopeId,
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
            ScopeId,
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
