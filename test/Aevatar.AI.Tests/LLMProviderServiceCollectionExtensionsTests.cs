using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.LLMProviders.MEAI;
using Aevatar.AI.LLMProviders.Tornado;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public class LLMProviderServiceCollectionExtensionsTests
{
    [Fact]
    public void AddMEAIProviders_WhenFactoryAlreadyExists_ShouldThrow()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILLMProviderFactory>(new StubLLMProviderFactory());

        var act = () => services.AddMEAIProviders(_ => { });

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ILLMProviderFactory is already registered*");
    }

    [Fact]
    public void AddTornadoProviders_WhenFactoryAlreadyExists_ShouldThrow()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILLMProviderFactory>(new StubLLMProviderFactory());

        var act = () => services.AddTornadoProviders(_ => { });

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ILLMProviderFactory is already registered*");
    }

    [Fact]
    public void AddTornadoProviders_AfterMEAIProviders_ShouldThrowInsteadOfSilentlyIgnoring()
    {
        var services = new ServiceCollection();
        services.AddMEAIProviders(_ => { });

        var act = () => services.AddTornadoProviders(_ => { });

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ILLMProviderFactory is already registered*");
    }

    [Fact]
    public void AddMEAIProviders_AfterTornadoProviders_ShouldThrowInsteadOfSilentlyIgnoring()
    {
        var services = new ServiceCollection();
        services.AddTornadoProviders(_ => { });

        var act = () => services.AddMEAIProviders(_ => { });

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ILLMProviderFactory is already registered*");
    }

    private sealed class StubLLMProviderFactory : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) => new StubLLMProvider();
        public ILLMProvider GetDefault() => new StubLLMProvider();
        public IReadOnlyList<string> GetAvailableProviders() => ["stub"];
    }

    private sealed class StubLLMProvider : ILLMProvider
    {
        public string Name => "stub";

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new LLMResponse());
        }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }
}
