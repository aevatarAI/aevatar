using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

/// <summary>
/// Verifies the primary-only kind identity contract through Orleans runtime
/// registration and serialization.
/// </summary>
public sealed class AgentKindIdentityIntegrationTests
{
    [Fact]
    public void OrleansRuntimeRegistration_ShouldRegisterPrimaryKindServices()
    {
        var services = new ServiceCollection();
        services.AddAevatarFoundationRuntimeOrleans();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IAgentKindRegistry>().Should().NotBeNull();
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IActorKindProbe) &&
            descriptor.ImplementationType == typeof(OrleansActorKindProbe));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IAgentKindVerifier) &&
            descriptor.ImplementationType == typeof(DefaultAgentKindVerifier));
    }

    [Fact]
    public void OrleansRuntimeRegistration_PreservesPrimaryKindContributionsAcrossModuleExtensions()
    {
        var services = new ServiceCollection();
        services.AddAevatarFoundationRuntimeOrleans();
        services.AddAevatarAgentKindRegistry(builder => builder.Register<IdentityFixtureAgent>());

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentKindRegistry>();

        var implementation = registry.Resolve("tests.identity-primary");
        implementation.Metadata.Kind.Should().Be("tests.identity-primary");
        implementation.Metadata.ImplementationClrTypeName.Should().Be(typeof(IdentityFixtureAgent).FullName);
        registry.TryResolve("tests.identity-legacy", out _).Should().BeFalse();
    }

    [Fact]
    public void RuntimeActorIdentity_ShouldRoundtripPrimaryKindThroughOrleansSerializer()
    {
        var services = new ServiceCollection();
        services.AddAevatarFoundationRuntimeOrleans();

        using var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<Serializer>();

        var original = new RuntimeActorIdentity
        {
            Kind = "scheduled.skill-runner",
            StateSchemaVersion = 3,
        };

        var bytes = serializer.SerializeToArray(original);
        var roundtripped = serializer.Deserialize<RuntimeActorIdentity>(bytes);

        roundtripped.Should().NotBeNull();
        roundtripped.Kind.Should().Be("scheduled.skill-runner");
        roundtripped.StateSchemaVersion.Should().Be(3);
    }

    [Fact]
    public void RuntimeActorGrainState_ShouldRoundtripIdentityFieldWithoutAgentTypeNameMirror()
    {
        var services = new ServiceCollection();
        services.AddAevatarFoundationRuntimeOrleans();

        using var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<Serializer>();

        var state = new RuntimeActorGrainState
        {
            AgentId = "actor-1",
            Identity = new RuntimeActorIdentity
            {
                Kind = "scheduled.skill-runner",
                StateSchemaVersion = 1,
            },
        };

        var bytes = serializer.SerializeToArray(state);
        var roundtripped = serializer.Deserialize<RuntimeActorGrainState>(bytes);

        roundtripped.AgentTypeName.Should().BeNull();
        roundtripped.Identity.Should().NotBeNull();
        roundtripped.Identity!.Kind.Should().Be("scheduled.skill-runner");
        roundtripped.Identity.StateSchemaVersion.Should().Be(1);
    }

    [Fact]
    public void RuntimeActorGrainState_WithoutIdentityKind_ShouldDeserializeWithoutFallbackIdentity()
    {
        var services = new ServiceCollection();
        services.AddAevatarFoundationRuntimeOrleans();

        using var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<Serializer>();

        var legacyState = new RuntimeActorGrainState
        {
            AgentId = "legacy-actor",
            AgentTypeName = "Old.Clr.Type, Old.Assembly",
            Identity = null,
        };

        var bytes = serializer.SerializeToArray(legacyState);
        var roundtripped = serializer.Deserialize<RuntimeActorGrainState>(bytes);

        roundtripped.AgentTypeName.Should().Be("Old.Clr.Type, Old.Assembly");
        roundtripped.Identity.Should().BeNull();
    }
}

[GAgent("tests.identity-primary")]
internal sealed class IdentityFixtureAgent : IAgent
{
    public string Id { get; } = "fixture";

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> GetDescriptionAsync() => Task.FromResult(GetType().Name);

    public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<Type>>(Array.Empty<Type>());

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
}
