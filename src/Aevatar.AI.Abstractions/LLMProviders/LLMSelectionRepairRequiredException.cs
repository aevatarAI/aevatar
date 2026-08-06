namespace Aevatar.AI.Abstractions.LLMProviders;

public sealed class LLMSelectionRepairRequiredException : InvalidOperationException
{
    public const string StableCode = "llm_selection_repair_required";

    public string Code => StableCode;

    public string Remediation => "reselect_llm";

    public LLMSelectionRepairRequiredException()
        : base("Select an LLM service and model again before continuing.")
    {
    }
}
