using Aevatar.AI.Abstractions.LLMProviders;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Core.LLMProviders;

/// <summary>
/// Single source of truth for applying the bot owner's UserConfig to outbound LLM control.
/// Scheduled agents (SkillRunnerGAgent, WorkflowAgentGAgent) and channel-bot turn runners
/// (NyxidChat) all delegate here so scope-id guard and swallow-and-log policy can never drift.
/// </summary>
public static class OwnerLlmConfigApplier
{
    /// <summary>
    /// Reads the owner's <see cref="OwnerLlmConfig"/> for <paramref name="scopeId"/> via
    /// <paramref name="source"/> and overlays model / route / max-tool-rounds onto typed
    /// <see cref="LLMControlContext"/>. No-ops when scope id is blank, the source isn't wired,
    /// or the config fields are empty. Transient lookup failures are logged at warning level and
    /// swallowed so a flaky projection cannot fail the agent's execution turn.
    /// </summary>
    public static async Task<LLMControlContext> ApplyAsync(
        LLMControlContext? control,
        string? scopeId,
        IOwnerLlmConfigSource? source,
        ILogger logger,
        string actorLabel,
        string actorId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(scopeId) || source is null)
            return control ?? LLMControlContext.Empty;

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
            return control ?? LLMControlContext.Empty;
        }

        var current = control ?? LLMControlContext.Empty;
        return current with
        {
            ModelOverride = string.IsNullOrWhiteSpace(config.DefaultModel)
                ? current.ModelOverride
                : config.DefaultModel.Trim(),
            NyxIdRoutePreference = string.IsNullOrWhiteSpace(config.PreferredLlmRoute)
                ? current.NyxIdRoutePreference
                : config.PreferredLlmRoute.Trim(),
            MaxToolRoundsOverride = config.MaxToolRounds > 0
                ? config.MaxToolRounds
                : current.MaxToolRoundsOverride,
        };
    }

}
