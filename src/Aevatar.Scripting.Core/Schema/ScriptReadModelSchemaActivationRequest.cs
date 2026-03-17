namespace Aevatar.Scripting.Core.Schema;

public sealed record ScriptReadModelSchemaActivationRequest(
    bool RequiresDocumentStore,
    bool RequiresGraphStore,
    IReadOnlyList<string> DeclaredProviderHints);
