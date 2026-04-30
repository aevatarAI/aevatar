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
        var registry = BuildRegistry(new AgentKindRegistryBuilder().Register<KindRegistryFixtureSubscription>());

        var implementation = registry.Resolve("test.subscription");

        implementation.Metadata.Kind.Should().Be("test.subscription");
        implementation.Metadata.ImplementationClrTypeName
            .Should().Be(typeof(KindRegistryFixtureSubscription).FullName);

        var instance = implementation.Factory();
        instance.Should().BeOfType<KindRegistryFixtureSubscription>();
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

    private static IAgentKindRegistry BuildRegistry(AgentKindRegistryBuilder builder)
    {
        var services = new ServiceCollection();
        services.AddSingleton(builder);
        services.AddSingleton<IAgentKindRegistry>(sp =>
            new AgentKindRegistry(sp, sp.GetRequiredService<AgentKindRegistryBuilder>().Build()));

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
