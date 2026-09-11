using Aevatar.Foundation.Abstractions.Credentials;

namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// Resolves the vault-backed Agent Key owned by a channel registration before a workflow starts.
/// Implementations keep raw key material inside the adapter boundary and fail closed when the exact
/// durable reference cannot be resolved. NyxID authorization is enforced by the target invocation.
/// </summary>
public interface IChannelNyxIdAgentKeyReadinessPort
{
    Task<ChannelNyxIdAgentKeyReadinessResult> EnsureReadyAsync(
        DurableCallerCredentialRef credential,
        CancellationToken ct = default);
}

public sealed record ChannelNyxIdAgentKeyReadinessResult(bool Ready, string FailureCode)
{
    public static ChannelNyxIdAgentKeyReadinessResult Succeeded { get; } = new(true, string.Empty);

    public static ChannelNyxIdAgentKeyReadinessResult Failed(string failureCode) =>
        new(false, string.IsNullOrWhiteSpace(failureCode) ? "channel_agent_key_not_ready" : failureCode.Trim());
}
