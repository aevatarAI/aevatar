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

    // Runs each LLM stream/tool loop in the off-actor ILlmRunExecutor (epic #2271, B1-B5
    // #2272-#2276) instead of on the session actor's own turn. The on-actor path holds a
    // single Orleans grain turn for the whole upstream call, so a slow/large run exceeds the
    // 30s stream-delivery timeout, breaks delivery, and truncates the client SSE with no
    // terminal frame — OpenAI-compatible clients (e.g. chrono-app) then report "cannot parse
    // response". Graduated to on-by-default now that the staged rollout has landed; the value
    // is the code default because no deployed config layer sets the key, and changing the
    // default is the reliable way to enable it in production. Set false (config/env) to roll
    // back to the legacy on-actor path instantly.
    public bool OffActorLlmRunExecutorEnabled { get; set; } = true;
}
