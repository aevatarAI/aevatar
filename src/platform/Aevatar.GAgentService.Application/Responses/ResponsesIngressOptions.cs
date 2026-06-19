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

    // Total time the ingress waits for an LLM run to emit a terminal event before returning
    // response_timeout. Long agentic turns (multiple tool rounds, large formatted answers) can run
    // well past the old hardcoded 30s; cutting them surfaced to users as the run being interrupted.
    // Configurable as a host fact (Aevatar:Responses:Ingress:ObservationTimeoutSeconds); the wider
    // default lets normal long turns finish. NOTE: this only governs the client-facing wait — it does
    // not change the per-actor execution model (see the deblock-session-actor task for the root fix).
    public int ObservationTimeoutSeconds { get; set; } = 300;

    public TimeSpan ObservationTimeout =>
        ObservationTimeoutSeconds > 0 ? TimeSpan.FromSeconds(ObservationTimeoutSeconds) : TimeSpan.FromSeconds(300);

    public bool OffActorLlmRunExecutorEnabled { get; set; }
}
