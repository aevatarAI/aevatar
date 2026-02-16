// Provider type definitions (OpenAI, DeepSeek, etc.) for the config UI.

static class ProviderProfiles
{
    private static readonly IReadOnlyList<ProviderProfile> Profiles =
    [
        new ProviderProfile("openai", "OpenAI", "tier1", "GPT-4o, o1, o3 series", LLMProviderKind.OpenAi, "https://api.openai.com", "gpt-4o-mini", Recommended: true),
        new ProviderProfile("anthropic", "Anthropic", "tier1", "Claude 3.5 Sonnet, Opus, Haiku", LLMProviderKind.Anthropic, "https://api.anthropic.com", "claude-sonnet-4-20250514", Recommended: true),
        new ProviderProfile("google", "Google", "tier1", "Gemini 2.0, 1.5 Pro/Flash", LLMProviderKind.Google, "https://generativelanguage.googleapis.com", "gemini-2.0-flash"),
        new ProviderProfile("azure", "Azure OpenAI", "tier1", "Enterprise OpenAI (requires deployment)", LLMProviderKind.Azure, "", ""),
        new ProviderProfile("deepseek", "DeepSeek", "tier2", "DeepSeek V3, Coder, Reasoner", LLMProviderKind.DeepSeek, "https://api.deepseek.com", "deepseek-chat", Recommended: true),
        new ProviderProfile("mistral", "Mistral", "tier2", "Mistral Large, Small, Codestral", LLMProviderKind.Mistral, "https://api.mistral.ai", "mistral-small-latest"),
        new ProviderProfile("groq", "Groq", "tier2", "Ultra-fast inference (Llama, Mixtral)", LLMProviderKind.Groq, "https://api.groq.com/openai", "llama-3.3-70b-versatile"),
        new ProviderProfile("xai", "xAI", "tier2", "Grok-2, Grok-2 Vision", LLMProviderKind.XAi, "https://api.x.ai", "grok-2-latest"),
        new ProviderProfile("cohere", "Cohere", "tier2", "Command R+, Embed, Rerank", LLMProviderKind.Cohere, "https://api.cohere.com", "command-r-plus"),
        new ProviderProfile("perplexity", "Perplexity", "tier2", "Sonar Pro (search-augmented)", LLMProviderKind.Perplexity, "https://api.perplexity.ai", "sonar-pro"),
        new ProviderProfile("openrouter", "OpenRouter", "aggregator", "200+ models, unified API", LLMProviderKind.OpenRouter, "https://openrouter.ai/api/v1", "openai/gpt-4o-mini", Recommended: true),
        new ProviderProfile("deepinfra", "DeepInfra", "aggregator", "Open-source models, GPU inference", LLMProviderKind.DeepInfra, "https://api.deepinfra.com/v1/openai", "meta-llama/Llama-3.3-70B-Instruct-Turbo"),
        new ProviderProfile("together", "Together AI", "aggregator", "Open-source models at scale", LLMProviderKind.Together, "https://api.together.xyz", "meta-llama/Llama-3.3-70B-Instruct-Turbo"),
        new ProviderProfile("alibaba", "Alibaba (Qwen)", "regional", "Qwen series via DashScope", LLMProviderKind.OpenAiCompatible, "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"),
        new ProviderProfile("moonshot", "Moonshot AI", "regional", "Kimi series (China)", LLMProviderKind.OpenAiCompatible, "https://api.moonshot.cn/v1", "moonshot-v1-8k"),
        new ProviderProfile("zhipu", "Zhipu AI", "regional", "GLM-4 series (China)", LLMProviderKind.OpenAiCompatible, "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
        new ProviderProfile("ollama", "Ollama", "local", "Local models (llama3, qwen, etc.)", LLMProviderKind.OpenAiCompatible, "http://localhost:11434/v1", "llama3.2"),
        new ProviderProfile("lmstudio", "LM Studio", "local", "Local GUI with OpenAI API", LLMProviderKind.OpenAiCompatible, "http://localhost:1234/v1", ""),
    ];

    public static IReadOnlyList<ProviderProfile> All => Profiles;

    public static ProviderProfile Get(string providerName)
    {
        var name = (providerName ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(name))
        {
            var match = Profiles.FirstOrDefault(p => string.Equals(p.Id, name, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return new ProviderProfile(name, name, "configured", "Configured via user secrets", LLMProviderKind.OpenAiCompatible, "", "");
    }

    public static bool TryInferProviderTypeFromInstanceName(string instanceName, out string providerType)
    {
        providerType = string.Empty;
        var name = (instanceName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (Profiles.Any(p => string.Equals(p.Id, name, StringComparison.OrdinalIgnoreCase)))
        {
            providerType = name;
            return true;
        }
        var idx = name.IndexOf('-', StringComparison.Ordinal);
        if (idx <= 0) return false;
        var head = name.Substring(0, idx).Trim();
        if (string.IsNullOrWhiteSpace(head)) return false;
        if (Profiles.Any(p => string.Equals(p.Id, head, StringComparison.OrdinalIgnoreCase)))
        {
            providerType = head;
            return true;
        }
        return false;
    }
}
