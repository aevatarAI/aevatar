using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

/// <summary>
/// Direct tests for the pure kind identity helper used by
/// <see cref="RuntimeActorGrain"/>.
/// </summary>
public sealed class RuntimeActorIdentityResolutionTests
{
    [Fact]
    public void ResolvesToSameImplementation_ReturnsTrueForExactKindMatch()
    {
        RuntimeActorIdentityResolution
            .ResolvesToSameImplementation(registry: null, activeKind: "scheduled.skill-runner", requestedKind: "scheduled.skill-runner")
            .Should().BeTrue();
    }

    [Fact]
    public void ResolvesToSameImplementation_ReturnsFalseWhenActiveKindEmpty()
    {
        RuntimeActorIdentityResolution
            .ResolvesToSameImplementation(registry: null, activeKind: null, requestedKind: "anything")
            .Should().BeFalse();
        RuntimeActorIdentityResolution
            .ResolvesToSameImplementation(registry: null, activeKind: "", requestedKind: "anything")
            .Should().BeFalse();
    }

    [Fact]
    public void ResolvesToSameImplementation_ReturnsFalseWhenRegistryNullAndKindsDiffer()
    {
        RuntimeActorIdentityResolution
            .ResolvesToSameImplementation(registry: null, activeKind: "scheduled.skill-definition", requestedKind: "scheduled.skill-runner")
            .Should().BeFalse();
    }

    [Fact]
    public void ResolvesToSameImplementation_TreatsOnlyPrimaryKindAsSameImplementation()
    {
        var registry = BuildRegistry();

        RuntimeActorIdentityResolution
            .ResolvesToSameImplementation(registry, activeKind: "tests.canonical", requestedKind: "tests.legacy")
            .Should().BeFalse();
    }

    [Fact]
    public void ResolvesToSameImplementation_ReturnsFalseForUnregisteredKind()
    {
        var registry = BuildRegistry();

        RuntimeActorIdentityResolution
            .ResolvesToSameImplementation(registry, activeKind: "tests.canonical", requestedKind: "tests.never-registered")
            .Should().BeFalse();
    }

    private static IAgentKindRegistry BuildRegistry()
    {
        var services = new ServiceCollection();
        var builder = new AgentKindRegistryBuilder().Register<ResolutionFixtureAgent>();
        services.AddSingleton(builder);
        services.AddSingleton<IAgentKindRegistry>(sp =>
            new AgentKindRegistry(sp.GetRequiredService<AgentKindRegistryBuilder>().Build()));
        return services.BuildServiceProvider().GetRequiredService<IAgentKindRegistry>();
    }
}

[GAgent("tests.canonical")]
internal sealed class ResolutionFixtureAgent : IAgent
{
    public string Id { get; } = "resolution-fixture";

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> GetDescriptionAsync() => Task.FromResult(nameof(ResolutionFixtureAgent));

    public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<Type>>(Array.Empty<Type>());

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
}
