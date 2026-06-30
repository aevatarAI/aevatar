using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.GAgentService.Application.Responses;

internal static class ResponsesSafeToolExecutor
{
    public static async Task<string> ExecuteAsync(
        IAgentTool tool,
        string argumentsJson,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);

        try
        {
            return await tool.ExecuteAsync(argumentsJson, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BuildFailureJson(tool.Name, ex);
        }
    }

    private static string BuildFailureJson(string toolName, Exception ex) =>
        JsonSerializer.Serialize(new
        {
            error = new
            {
                code = "aevatar_local_tool_execution_failed",
                message = "Aevatar local tool execution failed.",
                tool_name = toolName,
                exception_type = ex.GetType().Name,
            },
        });
}
