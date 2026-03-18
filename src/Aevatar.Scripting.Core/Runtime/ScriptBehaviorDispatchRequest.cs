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
    public string ReadModelSchemaVersion { get; init; } = string.Empty;

    public string ReadModelSchemaHash { get; init; } = string.Empty;

    /// <summary>
    /// Pre-compiled materialization plan cached by the calling actor.
    /// When non-null the dispatcher skips compilation; when null the dispatcher compiles on the fly.
    /// </summary>
    public Materialization.ScriptReadModelMaterializationPlan? CachedMaterializationPlan { get; init; }

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
