namespace Aevatar.Scripting.Abstractions.Definitions;

public sealed record ScriptContractManifest
{
    public ScriptContractManifest(
        string inputSchema,
        IReadOnlyList<string> outputEvents,
        string stateSchema,
        string readModelSchema,
        ScriptReadModelDefinition? readModelDefinition = null,
        IReadOnlyList<string>? readModelStoreCapabilities = null)
    {
        InputSchema = inputSchema ?? string.Empty;
        OutputEvents = outputEvents ?? Array.Empty<string>();
        StateSchema = stateSchema ?? string.Empty;
        ReadModelSchema = readModelSchema ?? string.Empty;
        ReadModelDefinition = readModelDefinition;
        ReadModelStoreCapabilities = readModelStoreCapabilities ?? Array.Empty<string>();
    }

    public string InputSchema { get; init; }
    public IReadOnlyList<string> OutputEvents { get; init; }
    public string StateSchema { get; init; }
    public string ReadModelSchema { get; init; }
    public ScriptReadModelDefinition? ReadModelDefinition { get; init; }
    public IReadOnlyList<string> ReadModelStoreCapabilities { get; init; }
}
