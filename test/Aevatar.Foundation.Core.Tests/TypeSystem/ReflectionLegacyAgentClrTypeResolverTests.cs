using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Core.Tests.TypeSystem;

public class ReflectionLegacyAgentClrTypeResolverTests
{
    [Fact]
    public void TryResolve_FindsAgentClassByFullName_AndConstructsViaServiceProvider()
    {
        var resolver = BuildResolver();

        var found = resolver.TryResolve(typeof(LegacyResolverFixtureAgent).FullName!, out var implementation);

        found.Should().BeTrue();
        implementation.Should().NotBeNull();
        implementation.Metadata.ImplementationClrTypeName.Should().Be(typeof(LegacyResolverFixtureAgent).FullName);
        implementation.Metadata.LegacyClrTypeNames.Should().Contain(typeof(LegacyResolverFixtureAgent).FullName);
        // No registered kind for this fallback path: the metadata kind is
        // intentionally empty, since un-decorated classes have no stable kind
        // to record. RuntimeActorGrain relies on this to skip lazy-tagging.
        implementation.Metadata.Kind.Should().BeEmpty();

        var instance = implementation.Factory(BuildProvider());
        instance.Should().BeOfType<LegacyResolverFixtureAgent>();
    }

    [Fact]
    public void TryResolve_FindsAgentClassViaAssemblyQualifiedName()
    {
        var resolver = BuildResolver();

        var found = resolver.TryResolve(typeof(LegacyResolverFixtureAgent).AssemblyQualifiedName!, out var implementation);

        found.Should().BeTrue();
        implementation.Factory(BuildProvider()).Should().BeOfType<LegacyResolverFixtureAgent>();
    }

    [Fact]
    public void TryResolve_ReturnsFalseForUnknownClrName()
    {
        var resolver = BuildResolver();

        var found = resolver.TryResolve("Aevatar.Foundation.Core.Tests.TypeSystem.NoSuchAgent", out _);

        found.Should().BeFalse();
    }

    [Fact]
    public void TryResolve_ReturnsFalseForNonAgentClrName()
    {
        var resolver = BuildResolver();

        // Resolves to a Type, but it does not implement IAgent — must reject
        // rather than activate something arbitrary.
        var found = resolver.TryResolve(typeof(string).FullName!, out _);

        found.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolve_ReturnsFalseForEmptyOrWhitespaceInput(string clrTypeName)
    {
        var resolver = BuildResolver();

        var found = resolver.TryResolve(clrTypeName, out _);

        found.Should().BeFalse();
    }

    [Fact]
    public void TryResolve_ThrowsWhenResolvedTypeFactoryReturnsNonAgent()
    {
        // Edge case: a class implements IAgent but throws inside its
        // constructor; ActivatorUtilities propagates the exception. The
        // resolver claims it found the type (returns true at the outer level)
        // but invoking the factory propagates the failure rather than
        // silently swallowing it.
        var resolver = BuildResolver();
        resolver.TryResolve(typeof(ThrowingFixtureAgent).FullName!, out var implementation)
            .Should().BeTrue();

        var act = () => implementation.Factory(BuildProvider());
        act.Should().Throw<InvalidOperationException>().WithMessage("*construction failed*");
    }

    private static ILegacyAgentClrTypeResolver BuildResolver() => new ReflectionLegacyAgentClrTypeResolver();

    private static IServiceProvider BuildProvider() => new ServiceCollection().BuildServiceProvider();
}

internal sealed class LegacyResolverFixtureAgent : IAgent
{
    public string Id { get; } = "legacy-fixture";

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> GetDescriptionAsync() => Task.FromResult(nameof(LegacyResolverFixtureAgent));

    public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<Type>>(Array.Empty<Type>());

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class ThrowingFixtureAgent : IAgent
{
    public ThrowingFixtureAgent()
    {
        throw new InvalidOperationException("construction failed");
    }

    public string Id => "throwing-fixture";

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> GetDescriptionAsync() => Task.FromResult(nameof(ThrowingFixtureAgent));

    public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<Type>>(Array.Empty<Type>());

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
}
