using System.Text.Json;
using Aevatar.Foundation.Abstractions.EventModules;

namespace Aevatar.Workflow.Core.Runtime;

internal static class WorkflowRunModuleStateAccess
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string GetRunId(IEventHandlerContext ctx) =>
        RequireHost(ctx).RunId;

    public static T Load<T>(IEventHandlerContext ctx, string moduleName)
        where T : class, new()
    {
        var json = RequireHost(ctx).GetModuleStateJson(moduleName);
        if (string.IsNullOrWhiteSpace(json))
            return new T();

        return JsonSerializer.Deserialize<T>(json, SerializerOptions) ?? new T();
    }

    public static Task SaveAsync<T>(
        IEventHandlerContext ctx,
        string moduleName,
        T state,
        CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(state);
        var json = JsonSerializer.Serialize(state, SerializerOptions);
        return RequireHost(ctx).UpsertModuleStateJsonAsync(moduleName, json, ct);
    }

    public static Task ClearAsync(
        IEventHandlerContext ctx,
        string moduleName,
        CancellationToken ct) =>
        RequireHost(ctx).ClearModuleStateAsync(moduleName, ct);

    private static IWorkflowRunModuleStateHost RequireHost(IEventHandlerContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx.Agent is IWorkflowRunModuleStateHost host)
            return host;

        throw new InvalidOperationException(
            $"Workflow module state host is required for agent '{ctx.AgentId}'.");
    }
}
