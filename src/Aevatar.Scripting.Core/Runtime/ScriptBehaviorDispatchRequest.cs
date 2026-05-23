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
    // Refactor (iter42/cluster-044-scripting-source-package-json-shadow):
    //   Old pattern: Scripting persists and republishes source_text as a compatibility shadow of ScriptPackageSpec; multi-file packages can be encoded as JSON text and reparsed from persisted source.
    //   New principle: ScriptPackageSpec is the sole internal source-package contract for commands/state/events/readmodels; source_text is only an external one-file adapter field at Host/Application boundary.
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

}
