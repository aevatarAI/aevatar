using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Bootstrap.Tests;

[Trait("Category", "Integration")]
public class AIFeatureBootstrapRealProviderIntegrationTests
{
    [OpenAIIntegrationFact]
    public async Task AddAevatarAIFeatures_WithOpenAIEnvironmentKey_ShouldCallRealProviderSuccessfully()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")!.Trim();
        var model = Environment.GetEnvironmentVariable("AEVATAR_TEST_OPENAI_MODEL");
        if (string.IsNullOrWhiteSpace(model))
            model = "gpt-5.4";

        var baseUrl = Environment.GetEnvironmentVariable("AEVATAR_TEST_OPENAI_BASE_URL");

        var providerConfig = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LLMProviders:Providers:openai:ApiKey"] = apiKey,
            ["LLMProviders:Providers:openai:ProviderType"] = "openai",
            ["LLMProviders:Providers:openai:Model"] = model.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(baseUrl))
            providerConfig["LLMProviders:Providers:openai:Endpoint"] = baseUrl.Trim();

        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        // Inject provider config explicitly so model/baseUrl can be customized by env.
        services.AddAevatarAIFeatures(configuration, options =>
        {
            options.EnableMEAIProviders = true;
            options.EnableMCPTools = false;
            options.EnableSkills = false;
            options.SecretsStore = new InMemorySecretsStore(providerConfig);
            options.DefaultProvider = "openai";
        });

        using var provider = services.BuildServiceProvider();
        var llmFactory = provider.GetRequiredService<ILLMProviderFactory>();

        llmFactory.GetAvailableProviders().Should().Contain("openai");
        llmFactory.GetDefault().Name.Should().Be("openai");

        var request = new LLMRequest
        {
            Messages =
            [
                ChatMessage.System("You are a concise assistant."),
                ChatMessage.User("Reply with a short confirmation message.")
            ],
            MaxTokens = 24,
            Temperature = 0,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var response = await llmFactory.GetDefault().ChatAsync(request, cts.Token);

        response.Content.Should().NotBeNullOrWhiteSpace();
    }

    private sealed class InMemorySecretsStore : IAevatarSecretsStore
    {
        private readonly Dictionary<string, string> _values;

        public InMemorySecretsStore(Dictionary<string, string> seed)
        {
            _values = new Dictionary<string, string>(seed, StringComparer.OrdinalIgnoreCase);
        }

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public string? GetApiKey(string providerName)
        {
            if (_values.TryGetValue($"LLMProviders:Providers:{providerName}:ApiKey", out var providerScoped) &&
                !string.IsNullOrWhiteSpace(providerScoped))
                return providerScoped;

            if (_values.TryGetValue($"LLMProviders:{providerName}:ApiKey", out var legacyScoped) &&
                !string.IsNullOrWhiteSpace(legacyScoped))
                return legacyScoped;

            if (_values.TryGetValue($"{providerName}_API_KEY", out var envScoped) &&
                !string.IsNullOrWhiteSpace(envScoped))
                return envScoped;

            return null;
        }

        public string? GetDefaultProvider() => _values.GetValueOrDefault("LLMProviders:Default");

        public IReadOnlyDictionary<string, string> GetAll() => _values;

        public void Set(string key, string value) => _values[key] = value;

        public void Remove(string key) => _values.Remove(key);
    }
}
