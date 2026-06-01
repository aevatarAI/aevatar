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
    // Refactor (issue1289): dispatch requests expose current state inputs, not precomputed projection payloads.
    // Refactor (iter76/cluster-076-scripting-domain-fact-derived-readmodel-payloads):
    //   Old pattern: ScriptDomainFactCommitted persisted derived readmodel/native_document/native_graph payloads inside the domain event
    //   New principle: domain event keeps only committed facts; projection materializer derives readmodel/native_document/(optional)native_graph from fact + state_root
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
