using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Abstractions.Behaviors;

// Refactor (iter113/cluster-113-scripting-runtime-definition-snapshot-side-read):
//   Old pattern: Scripting runtime side-read scripting definition snapshot via runtime readmodel (cache + factory injection).
//   New principle: Direct command-owned ScriptDefinitionSnapshot;delete runtime readmodel side-read/cache/factory injection;migrate script-facing API as in-scope migration risk(public API break in scope).
public sealed record ScriptDefinitionUpsertResult(
    string ActorId,
    ScriptDefinitionBindingSpec DefinitionSnapshot);
