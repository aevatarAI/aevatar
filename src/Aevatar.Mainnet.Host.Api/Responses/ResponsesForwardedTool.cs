using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.Mainnet.Host.Api.Responses;

internal sealed class ResponsesForwardedTool : IAgentTool
{
    public ResponsesForwardedTool(ResponsesToolDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        Name = declaration.Name;
        Description = declaration.Description;
        ParametersSchema = declaration.ParametersJson;
        SchemaHash = declaration.SchemaHash;
    }

    public string Name { get; }

    public string Description { get; }

    public string ParametersSchema { get; }

    public string SchemaHash { get; }

    public bool IsReadOnly => true;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            $"Forwarded Responses tool '{Name}' must be executed by the client, not by Aevatar.");
}
