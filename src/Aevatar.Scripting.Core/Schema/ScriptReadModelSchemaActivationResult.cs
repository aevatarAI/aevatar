namespace Aevatar.Scripting.Core.Schema;

public sealed record ScriptReadModelSchemaActivationResult(
    bool IsActivated,
    IReadOnlyList<ScriptReadModelStoreKind> ValidatedStoreKinds,
    IReadOnlyList<ScriptReadModelStoreKind> MissingStoreKinds,
    string FailureReason);
