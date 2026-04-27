using Aevatar.Configuration;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public sealed class EnvironmentSecretsStoreTests
{
    [Fact]
    public void Get_ShouldReturnConfigurationValue_OrNullWhenMissing()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Custom:Value"] = "v",
            ["Empty"] = "",
        });
        var store = new EnvironmentSecretsStore(configuration);

        store.Get("Custom:Value").ShouldBe("v");
        store.Get("Missing").ShouldBeNull();
        store.Get("Empty").ShouldBeNull();
    }

    [Fact]
    public void GetApiKey_ShouldResolveByProviderConventions_InOrder()
    {
        var configuration = BuildConfiguration(new()
        {
            ["LLMProviders:Providers:deepseek:ApiKey"] = "k1",
            ["LLMProviders:openai:ApiKey"] = "k2",
            ["GROQ_API_KEY"] = "k3",
            ["LLMProviders:Default"] = "deepseek",
        });
        var store = new EnvironmentSecretsStore(configuration);

        // Convention 1: LLMProviders:Providers:{name}:ApiKey
        store.GetApiKey("deepseek").ShouldBe("k1");
        // Convention 2: LLMProviders:{name}:ApiKey
        store.GetApiKey("openai").ShouldBe("k2");
        // Convention 3: {PROVIDER}_API_KEY
        store.GetApiKey("GROQ").ShouldBe("k3");
        store.GetApiKey("missing").ShouldBeNull();
        store.GetDefaultProvider().ShouldBe("deepseek");
    }

    [Fact]
    public void GetAll_ShouldOnlyReturnSecretShapedKeys_NotArbitraryConfiguration()
    {
        var configuration = BuildConfiguration(new()
        {
            // Secret-shaped: included
            ["LLMProviders:Providers:deepseek:ApiKey"] = "k1",
            ["LLMProviders:Default"] = "deepseek",
            ["GROQ_API_KEY"] = "k2",
            // Non-secret config: must NOT leak through GetAll
            ["ConnectionStrings:Db"] = "Server=...;Pwd=should-not-leak",
            ["Cors:AllowedOrigins:0"] = "https://app.example.com",
            ["FeatureFlags:Foo"] = "true",
            ["Empty"] = "",
        });
        var store = new EnvironmentSecretsStore(configuration);

        var snapshot = store.GetAll();

        snapshot.ContainsKey("LLMProviders:Providers:deepseek:ApiKey").ShouldBeTrue();
        snapshot.ContainsKey("LLMProviders:Default").ShouldBeTrue();
        snapshot.ContainsKey("GROQ_API_KEY").ShouldBeTrue();
        snapshot.ContainsKey("ConnectionStrings:Db").ShouldBeFalse();
        snapshot.ContainsKey("Cors:AllowedOrigins:0").ShouldBeFalse();
        snapshot.ContainsKey("FeatureFlags:Foo").ShouldBeFalse();
        snapshot.ContainsKey("Empty").ShouldBeFalse();
    }

    [Fact]
    public void Set_ShouldThrow_BecauseStoreIsReadOnly()
    {
        var store = new EnvironmentSecretsStore(BuildConfiguration());

        Should.Throw<InvalidOperationException>(() => store.Set("k", "v"));
    }

    [Fact]
    public void Remove_ShouldThrow_BecauseStoreIsReadOnly()
    {
        var store = new EnvironmentSecretsStore(BuildConfiguration());

        Should.Throw<InvalidOperationException>(() => store.Remove("k"));
    }

    [Fact]
    public void Constructor_ShouldRejectNullConfiguration()
    {
        Should.Throw<ArgumentNullException>(() => new EnvironmentSecretsStore(null!));
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? values = null)
    {
        var builder = new ConfigurationBuilder();
        if (values != null)
            builder.AddInMemoryCollection(values);
        return builder.Build();
    }
}
