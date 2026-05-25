using Aevatar.ChatRouting.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.ChatRouting.Core;

public sealed class EnvChatRouteFallbackProvider : IChatRouteFallbackProvider
{
    public const string DefaultModelEnvironmentVariable = "AEVATAR_DEFAULT_LLM_MODEL";

    private readonly IOptions<ChatRoutingOptions> _options;

    public EnvChatRouteFallbackProvider(IOptions<ChatRoutingOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    // Implement (issue #693):
    //   Behavior: cold-start policy lookup falls back to env model, then configured default model.
    //   Why this shape: fallback is configuration, not actor/readmodel state, and stays outside resolver matching.
    public ChatRouteDecision GetFallbackDecision()
    {
        var modelName = Environment.GetEnvironmentVariable(DefaultModelEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(modelName))
        {
            modelName = _options.Value.Defaults.FallbackModel;
        }

        return ChatRouteResolver.NewDecision(
            new ChatRouteAction
            {
                ForwardToModel = new ForwardToModel { ModelName = modelName ?? string.Empty },
            },
            matchedRuleId: string.Empty,
            usedFallback: true);
    }
}
