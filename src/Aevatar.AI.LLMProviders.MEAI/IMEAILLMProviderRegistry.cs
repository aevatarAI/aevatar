using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.LLMProviders.MEAI;

public interface IMEAILLMProviderRegistry
{
    IMEAILLMProviderRegistry Register(string name, IChatClient client, ILogger? logger = null);
    IMEAILLMProviderRegistry RegisterOpenAI(
        string name,
        string model,
        string apiKey,
        string? baseUrl = null,
        ILogger? logger = null);
    IMEAILLMProviderRegistry SetDefault(string name);
}
