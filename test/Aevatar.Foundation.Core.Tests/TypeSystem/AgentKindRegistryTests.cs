using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Compatibility;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Core.Tests.TypeSystem;

public class AgentKindRegistryTests
{
    [Fact]
    public void Resolve_ReturnsImplementationForRegisteredKind()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AgentKindRegistryBuilder().Register<KindRegistryFixtureSubscription>());
        services.AddSingleton<IAgentKindRegistry>(sp =>
            new AgentKindRegistry(sp.GetRequiredService<AgentKindRegistryBuilder>().Build()));
        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentKindRegistry>();

        var implementation = registry.Resolve("test.subscription");

        implementation.Metadata.Kind.Should().Be("test.subscription");
        implementation.Metadata.ImplementationClrTypeName
            .Should().Be(typeof(KindRegistryFixtureSubscription).FullName);

        var instance = implementation.Factory(provider);
        instance.Should().BeOfType<KindRegistryFixtureSubscription>();
    }

    [Fact]
    public void Factory_UsesCallerSuppliedServiceProvider_NotRegistryCaptureTime()
    {
        // The implementation factory must resolve dependencies from the
        // *caller's* container so grain-scoped services (or test-overridden
        // singletons) bind correctly. A factory that captured the registry's
        // construction-time provider would silently use stale dependencies.
        var registryServices = new ServiceCollection();
        registryServices.AddSingleton(new AgentKindRegistryBuilder().Register<DependencyConsumingAgent>());
        registryServices.AddSingleton<IAgentKindRegistry>(sp =>
            new AgentKindRegistry(sp.GetRequiredService<AgentKindRegistryBuilder>().Build()));
        // Registry-time provider has Marker = "registry".
        registryServices.AddSingleton(new DependencyMarker("registry"));
        var registry = registryServices.BuildServiceProvider().GetRequiredService<IAgentKindRegistry>();

        // Caller-time provider has Marker = "caller". Factory should use it.
        var callerServices = new ServiceCollection();
        callerServices.AddSingleton(new DependencyMarker("caller"));
        using var callerProvider = callerServices.BuildServiceProvider();

        var implementation = registry.Resolve("test.dependency-consumer");
        var instance = (DependencyConsumingAgent)implementation.Factory(callerProvider);

        instance.Marker.Value.Should().Be("caller");
    }

    [Fact]
    public void Resolve_LooksUpLegacyKindAlias()
    {
        var registry = BuildRegistry(new AgentKindRegistryBuilder().Register<KindRegistryFixtureSplit>());

        var via_canonical = registry.Resolve("test.split-new");
        var via_legacy = registry.Resolve("test.split-old");

        via_canonical.Metadata.Kind.Should().Be("test.split-new");
        via_legacy.Metadata.Kind.Should().Be("test.split-new");
    }

    [Fact]
    public void Resolve_ThrowsUnknownAgentKindException_ForUnregisteredKind()
    {
        var registry = BuildRegistry(new AgentKindRegistryBuilder());

        var act = () => registry.Resolve("nope.gone");

        act.Should().Throw<UnknownAgentKindException>().Where(ex => ex.Kind == "nope.gone");
    }

    [Fact]
    public void TryResolveKindByClrTypeName_FindsKindFromCurrentTypeFullName()
    {
        var registry = BuildRegistry(new AgentKindRegistryBuilder().Register<KindRegistryFixtureSubscription>());

        var found = registry.TryResolveKindByClrTypeName(
            typeof(KindRegistryFixtureSubscription).FullName!,
            out var kind);

        found.Should().BeTrue();
        kind.Should().Be("test.subscription");
    }

    [Fact]
    public void TryResolveKindByClrTypeName_FindsKindFromLegacyClrTypeNameAlias()
    {
        var registry = BuildRegistry(new AgentKindRegistryBuilder().Register<KindRegistryFixtureSplit>());

        var found = registry.TryResolveKindByClrTypeName(
            "Some.Old.Namespace.SkillRunnerGAgent",
            out var kind);

        found.Should().BeTrue();
        kind.Should().Be("test.split-new");
    }

    [Fact]
    public void TryResolveKindByClrTypeName_ReturnsFalseForUnknownClrName()
    {
        var registry = BuildRegistry(new AgentKindRegistryBuilder().Register<KindRegistryFixtureSubscription>());

        var found = registry.TryResolveKindByClrTypeName(
            "Aevatar.Some.Nonexistent.Type",
            out _);

        found.Should().BeFalse();
    }

    [Fact]
    public void Build_ThrowsOnDuplicateKindRegistration()
    {
        var first = new AgentRegistration(
            Kind: "test.duplicate",
            ImplementationType: typeof(KindRegistryFixtureSubscription),
            StateContractType: typeof(object),
            LegacyKinds: Array.Empty<string>(),
            LegacyClrTypeNames: Array.Empty<string>());
        var second = new AgentRegistration(
            Kind: "test.duplicate",
            ImplementationType: typeof(KindRegistryFixtureSplit),
            StateContractType: typeof(object),
            LegacyKinds: Array.Empty<string>(),
            LegacyClrTypeNames: Array.Empty<string>());

        var builder = new AgentKindRegistryBuilder().Register(first);
        var act = () => builder.Register(second);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate agent kind 'test.duplicate'*");
    }

    [Fact]
    public void Resolver_ScansAssemblyForDecoratedTypes()
    {
        var builder = new AgentKindRegistryBuilder().ScanAssemblies(typeof(KindRegistryFixtureSubscription).Assembly);

        var registry = BuildRegistry(builder);

        // Both decorated fixtures must be discoverable. Other test fixtures may
        // share the assembly; we only assert presence of the ones we care about.
        registry.Resolve("test.subscription").Metadata.ImplementationClrTypeName
            .Should().Be(typeof(KindRegistryFixtureSubscription).FullName);
        registry.Resolve("test.split-new").Metadata.ImplementationClrTypeName
            .Should().Be(typeof(KindRegistryFixtureSplit).FullName);
    }

    [Fact]
    public void TryGetKind_ReturnsTrueForRegisteredImplementation()
    {
        var registry = BuildRegistry(new AgentKindRegistryBuilder().Register<KindRegistryFixtureSubscription>());
        var implementation = registry.Resolve("test.subscription");

        var found = registry.TryGetKind(implementation, out var kind);

        found.Should().BeTrue();
        kind.Should().Be("test.subscription");
    }

    [Fact]
    public void TryGetKind_ThrowsForNullImplementation()
    {
        var registry = BuildRegistry(new AgentKindRegistryBuilder());

        var act = () => registry.TryGetKind(null!, out _);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Resolve_ThrowsForEmptyKind(string? kind)
    {
        var registry = BuildRegistry(new AgentKindRegistryBuilder());

        var act = () => registry.Resolve(kind!);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void TryResolveKindByClrTypeName_ReturnsFalseForEmptyInput(string? clrName)
    {
        var registry = BuildRegistry(new AgentKindRegistryBuilder().Register<KindRegistryFixtureSubscription>());

        var found = registry.TryResolveKindByClrTypeName(clrName!, out _);

        found.Should().BeFalse();
    }

    [Fact]
    public void Build_RejectsConflictingClrTypeNameClaims()
    {
        // Two different kinds claim the same legacy CLR type name. Registry
        // construction must reject this — otherwise reverse lookup is
        // ambiguous and lazy-tagging picks one arbitrarily.
        var first = new AgentRegistration(
            Kind: "test.first",
            ImplementationType: typeof(KindRegistryFixtureSubscription),
            StateContractType: typeof(object),
            LegacyKinds: Array.Empty<string>(),
            LegacyClrTypeNames: new[] { "Some.Shared.Old.ClassName" });
        var second = new AgentRegistration(
            Kind: "test.second",
            ImplementationType: typeof(KindRegistryFixtureSplit),
            StateContractType: typeof(object),
            LegacyKinds: Array.Empty<string>(),
            LegacyClrTypeNames: new[] { "Some.Shared.Old.ClassName" });

        var act = () => new AgentKindRegistry(new[] { first, second });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'Some.Shared.Old.ClassName' is claimed by both kinds 'test.first' and 'test.second'*");
    }

    [Fact]
    public void Build_RejectsLegacyKindClaimedByMultipleCanonicalKinds()
    {
        var first = new AgentRegistration(
            Kind: "test.alpha",
            ImplementationType: typeof(KindRegistryFixtureSubscription),
            StateContractType: typeof(object),
            LegacyKinds: new[] { "shared.legacy" },
            LegacyClrTypeNames: Array.Empty<string>());
        var second = new AgentRegistration(
            Kind: "test.beta",
            ImplementationType: typeof(KindRegistryFixtureSplit),
            StateContractType: typeof(object),
            LegacyKinds: new[] { "shared.legacy" },
            LegacyClrTypeNames: Array.Empty<string>());

        var act = () => new AgentKindRegistry(new[] { first, second });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Legacy agent kind 'shared.legacy' is claimed by both 'test.alpha' and 'test.beta'*");
    }

    [Fact]
    public void Builder_RegisterSameTypeTwice_IsIdempotent()
    {
        var builder = new AgentKindRegistryBuilder()
            .Register<KindRegistryFixtureSubscription>()
            .Register<KindRegistryFixtureSubscription>();

        var registry = BuildRegistry(builder);
        registry.Resolve("test.subscription").Should().NotBeNull();
    }

    [Fact]
    public void FromAgentType_ThrowsWhenTypeIsNotAgent()
    {
        var act = () => AgentRegistration.FromAgentType(typeof(string));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not implement IAgent*");
    }

    [Fact]
    public void FromAgentType_ThrowsWhenTypeMissingGAgentAttribute()
    {
        var act = () => AgentRegistration.FromAgentType(typeof(LegacyResolverFixtureAgent));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*has no [GAgent] attribute*");
    }

    [Fact]
    public void FromAgentType_ThrowsForNullArgument()
    {
        var act = () => AgentRegistration.FromAgentType(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Builder_Register_ThrowsForNullRegistration()
    {
        var builder = new AgentKindRegistryBuilder();

        var act = () => builder.Register((AgentRegistration)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Build_RejectsLegacyAliasCollidingWithExistingPrimaryKind()
    {
        // Without this guard, Resolve("test.shared") silently routes to the
        // first registration (primary lookup wins) and the second
        // registration's [LegacyAgentKind("test.shared")] declaration is
        // silently ignored — surface the conflict at registration time.
        var first = new AgentRegistration(
            Kind: "test.shared",
            ImplementationType: typeof(KindRegistryFixtureSubscription),
            StateContractType: typeof(object),
            LegacyKinds: Array.Empty<string>(),
            LegacyClrTypeNames: Array.Empty<string>());
        var second = new AgentRegistration(
            Kind: "test.other",
            ImplementationType: typeof(KindRegistryFixtureSplit),
            StateContractType: typeof(object),
            LegacyKinds: new[] { "test.shared" },
            LegacyClrTypeNames: Array.Empty<string>());

        var act = () => new AgentKindRegistry(new[] { first, second });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Legacy agent kind 'test.shared' is also a primary kind*");
    }

    [Fact]
    public void Build_RejectsBuilderSnapshotMutationsAfterBuild()
    {
        // Build() must return a defensive copy: post-Build mutations to the
        // builder must not leak into the constructed registry.
        var builder = new AgentKindRegistryBuilder().Register<KindRegistryFixtureSubscription>();
        var snapshot = builder.Build();
        builder.Register<KindRegistryFixtureSplit>(); // mutates builder

        snapshot.Should().HaveCount(1);
        snapshot.Should().ContainSingle(r => r.Kind == "test.subscription");
    }

    private static IAgentKindRegistry BuildRegistry(AgentKindRegistryBuilder builder)
    {
        var services = new ServiceCollection();
        services.AddSingleton(builder);
        services.AddSingleton<IAgentKindRegistry>(sp =>
            new AgentKindRegistry(sp.GetRequiredService<AgentKindRegistryBuilder>().Build()));

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IAgentKindRegistry>();
    }
}

[GAgent("test.subscription")]
internal sealed class KindRegistryFixtureSubscription : KindRegistryFixtureAgentBase
{
}

[GAgent("test.split-new")]
[LegacyAgentKind("test.split-old")]
[LegacyClrTypeName("Some.Old.Namespace.SkillRunnerGAgent")]
internal sealed class KindRegistryFixtureSplit : KindRegistryFixtureAgentBase
{
}

internal sealed class DependencyMarker(string value)
{
    public string Value { get; } = value;
}

[GAgent("test.dependency-consumer")]
internal sealed class DependencyConsumingAgent : KindRegistryFixtureAgentBase
{
    public DependencyConsumingAgent(DependencyMarker marker)
    {
        Marker = marker;
    }

    public DependencyMarker Marker { get; }
}

internal abstract class KindRegistryFixtureAgentBase : IAgent
{
    public string Id { get; } = "fixture";

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> GetDescriptionAsync() => Task.FromResult(GetType().Name);

    public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<Type>>(Array.Empty<Type>());

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
}
