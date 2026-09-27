using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.Ornn.Publishing;

namespace Aevatar.AI.ToolProviders.Ornn;

internal static class OrnnSkillMutationFailureProtocol
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(string resultType, OrnnSkillMutationFailure failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultType);
        ArgumentNullException.ThrowIfNull(failure);

        return JsonSerializer.Serialize(new
        {
            result_type = resultType,
            status = "error",
            error_code = failure.Code,
            error = failure.Message,
            failure_kind = FormatKind(failure.Kind),
            http_status = failure.HttpStatus,
            failure_outcome = FormatOutcome(failure.Outcome),
        }, SerializerOptions);
    }

    public static bool TryParse(JsonElement root, out OrnnSkillMutationFailure failure)
    {
        failure = default!;
        if (!TryGetNonEmptyString(root, "error_code", out var code) ||
            !TryGetNonEmptyString(root, "error", out var message) ||
            !TryGetNonEmptyString(root, "failure_kind", out var kindValue) ||
            !TryParseKind(kindValue, out var kind) ||
            !TryGetNonEmptyString(root, "failure_outcome", out var outcomeValue) ||
            !TryParseOutcome(outcomeValue, out var outcome) ||
            !TryReadOptionalHttpStatus(root, out var httpStatus))
        {
            return false;
        }

        failure = new OrnnSkillMutationFailure(kind, code, message, httpStatus, outcome);
        return true;
    }

    private static string FormatKind(OrnnSkillMutationFailureKind kind) =>
        kind switch
        {
            OrnnSkillMutationFailureKind.Rejected => "rejected",
            OrnnSkillMutationFailureKind.Timeout => "timeout",
            OrnnSkillMutationFailureKind.Transport => "transport",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported mutation failure kind."),
        };

    private static string FormatOutcome(AgentToolFailureOutcome outcome) =>
        outcome switch
        {
            AgentToolFailureOutcome.CalleeConfirmed => "callee_confirmed",
            AgentToolFailureOutcome.OutcomeUncertain => "outcome_uncertain",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unsupported mutation outcome."),
        };

    private static bool TryParseKind(string value, out OrnnSkillMutationFailureKind kind)
    {
        kind = value switch
        {
            "rejected" => OrnnSkillMutationFailureKind.Rejected,
            "timeout" => OrnnSkillMutationFailureKind.Timeout,
            "transport" => OrnnSkillMutationFailureKind.Transport,
            _ => OrnnSkillMutationFailureKind.Unspecified,
        };
        return kind != OrnnSkillMutationFailureKind.Unspecified;
    }

    private static bool TryParseOutcome(string value, out AgentToolFailureOutcome outcome)
    {
        outcome = value switch
        {
            "callee_confirmed" => AgentToolFailureOutcome.CalleeConfirmed,
            "outcome_uncertain" => AgentToolFailureOutcome.OutcomeUncertain,
            _ => AgentToolFailureOutcome.Unspecified,
        };
        return outcome != AgentToolFailureOutcome.Unspecified;
    }

    private static bool TryReadOptionalHttpStatus(JsonElement root, out int? httpStatus)
    {
        httpStatus = null;
        if (!root.TryGetProperty("http_status", out var property) || property.ValueKind == JsonValueKind.Null)
            return true;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
            return false;

        httpStatus = value;
        return true;
    }

    private static bool TryGetNonEmptyString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString()?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }
}
