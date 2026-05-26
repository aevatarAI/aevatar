using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.GAgentService.Abstractions.Responses;

public interface IResponsesToolProvider
{
    ValueTask<IReadOnlyList<IAgentTool>> GetSubstituteToolsAsync(
        ResponsesToolProviderContext context,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<IAgentTool>>([]);

    ValueTask<IReadOnlyList<IAgentTool>> GetAdditiveToolsAsync(
        ResponsesToolProviderContext context,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<IAgentTool>>([]);
}

public sealed record ResponsesToolProviderContext(
    ResponsesToolProviderCallerScope CallerScope,
    IReadOnlyDictionary<string, string> ToolContextMetadata);

public sealed record ResponsesToolProviderCallerScope(
    string ScopeId,
    string OwnerSubject,
    string OriginKind);

