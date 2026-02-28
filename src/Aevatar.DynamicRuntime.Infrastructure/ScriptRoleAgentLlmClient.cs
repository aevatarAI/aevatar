using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class ScriptRoleAgentLlmClient : IScriptRoleAgentLlmClient
{
    private readonly ILLMProviderFactory _providerFactory;

    public ScriptRoleAgentLlmClient(ILLMProviderFactory providerFactory)
    {
        _providerFactory = providerFactory;
    }

    public async Task<string> ChatAsync(
        string prompt,
        string? systemPrompt = null,
        string? providerName = null,
        string? model = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("LLM prompt is required.");

        var provider = string.IsNullOrWhiteSpace(providerName)
            ? _providerFactory.GetDefault()
            : _providerFactory.GetProvider(providerName);

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(ChatMessage.System(systemPrompt));
        messages.Add(ChatMessage.User(prompt));

        var response = await provider.ChatAsync(new LLMRequest
        {
            Messages = messages,
            Model = model,
        }, ct);

        return response.Content ?? string.Empty;
    }
}

public sealed class ScriptRoleAgentClient : IScriptRoleAgentClient
{
    private readonly IScriptRoleAgentLlmClient _llmClient;

    public ScriptRoleAgentClient(IScriptRoleAgentLlmClient llmClient)
    {
        _llmClient = llmClient;
    }

    public Task<string> ChatAsync(
        string prompt,
        string? systemPrompt = null,
        string? providerName = null,
        string? model = null,
        CancellationToken ct = default)
        => _llmClient.ChatAsync(prompt, systemPrompt, providerName, model, ct);
}

public sealed class ScriptRoleAgentCapabilities : IScriptRoleAgentCapabilities
{
    public ScriptRoleAgentCapabilities(IScriptRoleAgentClient roleAgent, IScriptRoleAgentLlmClient llm)
    {
        RoleAgent = roleAgent;
        LLM = llm;
    }

    public ScriptRoleAgentCapabilities(IScriptRoleAgentLlmClient llm)
        : this(new ScriptRoleAgentClient(llm), llm)
    {
    }

    public IScriptRoleAgentClient RoleAgent { get; }
    public IScriptRoleAgentLlmClient LLM { get; }
}

internal sealed class NullScriptRoleAgentCapabilities : IScriptRoleAgentCapabilities
{
    public IScriptRoleAgentClient RoleAgent { get; } = new NullScriptRoleAgentClient();
    public IScriptRoleAgentLlmClient LLM { get; } = new NullScriptRoleAgentLlmClient();

    private sealed class NullScriptRoleAgentLlmClient : IScriptRoleAgentLlmClient
    {
        public Task<string> ChatAsync(
            string prompt,
            string? systemPrompt = null,
            string? providerName = null,
            string? model = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("LLM client is not configured for script runtime.");
        }
    }

    private sealed class NullScriptRoleAgentClient : IScriptRoleAgentClient
    {
        public Task<string> ChatAsync(
            string prompt,
            string? systemPrompt = null,
            string? providerName = null,
            string? model = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("RoleAgent client is not configured for script runtime.");
        }
    }
}
