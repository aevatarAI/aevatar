using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Compatibility;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

/// <summary>
/// Issue #498 Phase 1: verifies that the kind-token identity model is wired
/// end-to-end through DI and that the persisted <see cref="RuntimeActorIdentity"/>
/// envelope round-trips through Orleans serialization.
/// </summary>
public sealed class AgentKindIdentityIntegrationTests
{
    [Fact]
    public void OrleansRuntimeRegistration_ShouldRegisterAgentKindRegistry()
    {
        var services = new ServiceCollection();
        services.AddAevatarFoundationRuntimeOrleans();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentKindRegistry>();
        var legacyResolver = provider.GetRequiredService<ILegacyAgentClrTypeResolver>();

        registry.Should().NotBeNull();
        legacyResolver.Should().NotBeNull();
        legacyResolver.Should().BeOfType<ReflectionLegacyAgentClrTypeResolver>();
    }

    [Fact]
    public void OrleansRuntimeRegistration_PreservesContributionsAcrossModuleExtensions()
    {
        var services = new ServiceCollection();
        services.AddAevatarFoundationRuntimeOrleans();
        services.AddAevatarAgentKindRegistry(builder => builder.Register<IdentityFixtureRenamedAgent>());

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentKindRegistry>();

        var implementation = registry.Resolve("tests.identity-renamed");
        implementation.Metadata.Kind.Should().Be("tests.identity-renamed");

        registry.TryResolveKindByClrTypeName(
            "Aevatar.Foundation.Runtime.Hosting.Tests.IdentityFixtureLegacyAgent",
            out var aliasedKind).Should().BeTrue();
        aliasedKind.Should().Be("tests.identity-renamed");

        registry.Resolve("tests.identity-legacy").Metadata.ImplementationClrTypeName
            .Should().Be(typeof(IdentityFixtureRenamedAgent).FullName);
    }

    [Fact]
    public void RuntimeActorIdentity_ShouldRoundtripThroughOrleansSerializer()
    {
        var services = new ServiceCollection();
        services.AddAevatarFoundationRuntimeOrleans();

        using var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<Serializer>();

        var original = new RuntimeActorIdentity
        {
            Kind = "scheduled.skill-runner",
            StateSchemaVersion = 3,
            LegacyClrTypeName = "Aevatar.GAgents.Scheduled.SkillRunnerGAgent",
        };

        var bytes = serializer.SerializeToArray(original);
        var roundtripped = serializer.Deserialize<RuntimeActorIdentity>(bytes);

        roundtripped.Should().NotBeNull();
        roundtripped.Kind.Should().Be("scheduled.skill-runner");
        roundtripped.StateSchemaVersion.Should().Be(3);
        roundtripped.LegacyClrTypeName.Should().Be("Aevatar.GAgents.Scheduled.SkillRunnerGAgent");
    }

    [Fact]
    public void RuntimeActorGrainState_ShouldRoundtripIdentityField()
    {
        var services = new ServiceCollection();
        services.AddAevatarFoundationRuntimeOrleans();

        using var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<Serializer>();

        var state = new RuntimeActorGrainState
        {
            AgentId = "actor-1",
            AgentTypeName = "Aevatar.GAgents.Scheduled.SkillRunnerGAgent",
            Identity = new RuntimeActorIdentity
            {
                Kind = "scheduled.skill-runner",
                StateSchemaVersion = 1,
            },
        };

        var bytes = serializer.SerializeToArray(state);
        var roundtripped = serializer.Deserialize<RuntimeActorGrainState>(bytes);

        roundtripped.AgentTypeName.Should().Be("Aevatar.GAgents.Scheduled.SkillRunnerGAgent");
        roundtripped.Identity.Should().NotBeNull();
        roundtripped.Identity!.Kind.Should().Be("scheduled.skill-runner");
        roundtripped.Identity.StateSchemaVersion.Should().Be(1);
    }

    [Fact]
    public void RuntimeActorGrainState_LegacyRowsWithoutIdentity_ShouldDeserializeWithNullIdentity()
    {
        // Round-trips a state row that was serialized before [Id(7)] Identity
        // existed. The identity envelope must default to null so legacy data
        // continues to activate via the AgentTypeName fallback.
        var services = new ServiceCollection();
        services.AddAevatarFoundationRuntimeOrleans();

        using var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<Serializer>();

        var legacyState = new RuntimeActorGrainState
        {
            AgentId = "legacy-actor",
            AgentTypeName = "Aevatar.GAgents.Scheduled.SkillRunnerGAgent",
            Identity = null,
        };

        var bytes = serializer.SerializeToArray(legacyState);
        var roundtripped = serializer.Deserialize<RuntimeActorGrainState>(bytes);

        roundtripped.AgentTypeName.Should().Be("Aevatar.GAgents.Scheduled.SkillRunnerGAgent");
        roundtripped.Identity.Should().BeNull();
    }
}

/// <summary>
/// Stand-in for a renamed/split actor that takes over a previously-used
/// kind and CLR type name. Mirrors the pattern PR #497 will use:
/// <c>[GAgent("scheduled.skill-definition")]</c> +
/// <c>[LegacyAgentKind("scheduled.skill-runner")]</c> + a
/// <c>[LegacyClrTypeName]</c> alias.
/// </summary>
[GAgent("tests.identity-renamed")]
[LegacyAgentKind("tests.identity-legacy")]
[LegacyClrTypeName("Aevatar.Foundation.Runtime.Hosting.Tests.IdentityFixtureLegacyAgent")]
internal sealed class IdentityFixtureRenamedAgent : IdentityFixtureAgentBase
{
}

internal abstract class IdentityFixtureAgentBase : IAgent
{
    public string Id { get; } = "fixture";

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> GetDescriptionAsync() => Task.FromResult(GetType().Name);

    public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<Type>>(Array.Empty<Type>());

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
}
