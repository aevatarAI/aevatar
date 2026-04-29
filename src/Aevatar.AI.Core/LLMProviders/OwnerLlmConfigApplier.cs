using System.Globalization;
using Aevatar.AI.Abstractions.LLMProviders;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Core.LLMProviders;

/// <summary>
/// Single source of truth for "given the bot owner's UserConfig, what should the outbound LLM
/// metadata look like?". Scheduled agents (SkillRunnerGAgent, WorkflowAgentGAgent) and
/// channel-bot turn runners (NyxidChat) all delegate here so the metadata-key list, scope-id
/// guard, and swallow-and-log policy can never drift. Adding another agent or runner callsite
/// is a one-line call.
/// </summary>
public static class OwnerLlmConfigApplier
{
    /// <summary>
    /// Reads the owner's <see cref="OwnerLlmConfig"/> for <paramref name="scopeId"/> via
    /// <paramref name="source"/> and pins <c>ModelOverride</c> / <c>NyxIdRoutePreference</c> /
    /// <c>MaxToolRoundsOverride</c> onto <paramref name="metadata"/>. No-ops when scope id is
    /// blank, the source isn't wired, or the config fields are empty — provider defaults take
    /// over in those cases. Transient lookup failures are logged at warning level and swallowed
    /// so a flaky projection cannot fail the agent's execution turn.
    /// </summary>
    public static async Task ApplyAsync(
        IDictionary<string, string> metadata,
        string? scopeId,
        IOwnerLlmConfigSource? source,
        ILogger logger,
        string actorLabel,
        string actorId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(scopeId) || source is null)
            return;

        OwnerLlmConfig config;
        try
        {
            config = await source.GetForScopeAsync(scopeId, ct) ?? OwnerLlmConfig.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "{ActorLabel} {ActorId}: failed to load owner LLM config for scope {ScopeId}; falling back to provider defaults",
                actorLabel,
                actorId,
                scopeId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(config.DefaultModel))
            metadata[LLMRequestMetadataKeys.ModelOverride] = config.DefaultModel.Trim();
        if (!string.IsNullOrWhiteSpace(config.PreferredLlmRoute))
            metadata[LLMRequestMetadataKeys.NyxIdRoutePreference] = config.PreferredLlmRoute.Trim();
        if (config.MaxToolRounds > 0)
            metadata[LLMRequestMetadataKeys.MaxToolRoundsOverride] =
                config.MaxToolRounds.ToString(CultureInfo.InvariantCulture);
    }
}
