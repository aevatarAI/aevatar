using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.GAgentService.Application.Responses;

// Host-configured defaults for the OpenAI-compatible LLM ingress endpoints
// (/v1/responses, /v1/messages, /v1/chat/completions). The default model is a host
// fact, so its value is injected from host configuration rather than hardcoded in the
// engine. The Application normalizers apply it when a direct caller omits `model`.
public sealed class ResponsesIngressOptions
{
    public const string SectionName = "Aevatar:Responses:Ingress";

    // Model applied when a direct caller does not specify `model`, in the `route-slug/model`
    // shape (e.g. "chrono-llm-public/gpt-5.5") to pin both the NyxID route and the model. An
    // explicit caller model always wins over this default. Defaults to the shared
    // LlmDefaults so an unconfigured deployment still resolves a connected route+model rather
    // than failing closed; set to empty to restore the "model is required" contract.
    public string? DefaultModel { get; set; } = LlmDefaults.NyxIdRouteModel;

    public string? NormalizedDefaultModel =>
        string.IsNullOrWhiteSpace(DefaultModel) ? null : DefaultModel.Trim();
}
