using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Hosting.Responses;

internal sealed class MissingLlmProviderRunCore : ILlmRunCore
{
    public static MissingLlmProviderRunCore Instance { get; } = new();

    private MissingLlmProviderRunCore()
    {
    }

    public async Task RunAsync(
        LlmRunCoreRequest request,
        ILlmRunSink sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(request.Command);

        _ = await sink.RecordRunFailedAsync(
            new LlmRunFailed
            {
                ResponseId = request.Command.ResponseId,
                RunId = request.RunId,
                FailureCode = "llm_provider_factory_missing",
                FailureMessage = "ILLMProviderFactory is not registered. Configure an AI provider before executing LLM runs.",
                FailedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            },
            ct).ConfigureAwait(false);
    }
}
