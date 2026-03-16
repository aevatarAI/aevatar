using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Scripting.Projection.Orchestration;

internal sealed class ScriptExecutionMaterializationScopeGAgent
    : ProjectionMaterializationScopeGAgentBase<ScriptExecutionMaterializationContext>;

internal sealed class ScriptAuthorityMaterializationScopeGAgent
    : ProjectionMaterializationScopeGAgentBase<ScriptAuthorityProjectionContext>;

internal sealed class ScriptEvolutionMaterializationScopeGAgent
    : ProjectionMaterializationScopeGAgentBase<ScriptEvolutionMaterializationContext>;

internal sealed class ScriptExecutionSessionScopeGAgent
    : ProjectionSessionScopeGAgentBase<ScriptExecutionProjectionContext>;

internal sealed class ScriptEvolutionSessionScopeGAgent
    : ProjectionSessionScopeGAgentBase<ScriptEvolutionSessionProjectionContext>;
