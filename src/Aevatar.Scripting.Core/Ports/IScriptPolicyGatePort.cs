using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Core.Ports;

public sealed record ScriptPolicyGateDecision(
    bool IsAllowed,
    string FailureReason)
{
    public static readonly ScriptPolicyGateDecision Allow = new(
        true,
        string.Empty);

    public static ScriptPolicyGateDecision Deny(string failureReason) =>
        new(false, failureReason ?? string.Empty);
}

public interface IScriptPolicyGatePort
{
    Task<ScriptPolicyGateDecision> EvaluateAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct);
}
