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
    public void GetAll_ShouldFlattenConfigurationEntries_AndDropEmptyValues()
    {
        var configuration = BuildConfiguration(new()
        {
            ["A:B"] = "1",
            ["C"] = "",
            ["D"] = "2",
        });
        var store = new EnvironmentSecretsStore(configuration);

        var snapshot = store.GetAll();

        snapshot.ContainsKey("A:B").ShouldBeTrue();
        snapshot["A:B"].ShouldBe("1");
        snapshot["D"].ShouldBe("2");
        snapshot.ContainsKey("C").ShouldBeFalse();
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
