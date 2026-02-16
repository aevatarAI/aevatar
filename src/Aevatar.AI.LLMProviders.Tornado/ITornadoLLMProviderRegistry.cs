using LlmTornado.Code;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.LLMProviders.Tornado;

public interface ITornadoLLMProviderRegistry
{
    ITornadoLLMProviderRegistry Register(
        string name,
        LLmProviders providerType,
        string apiKey,
        string model,
        ILogger? logger = null);
    ITornadoLLMProviderRegistry RegisterOpenAICompatible(
        string name,
        string apiKey,
        string model,
        string? baseUrl = null,
        ILogger? logger = null);
    ITornadoLLMProviderRegistry SetDefault(string name);
}
