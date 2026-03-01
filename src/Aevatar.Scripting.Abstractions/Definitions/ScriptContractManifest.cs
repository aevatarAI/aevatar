namespace Aevatar.Scripting.Abstractions.Definitions;

public sealed record ScriptContractManifest(
    string InputSchema,
    IReadOnlyList<string> OutputEvents,
    string StateSchema,
    string ReadModelSchema);
