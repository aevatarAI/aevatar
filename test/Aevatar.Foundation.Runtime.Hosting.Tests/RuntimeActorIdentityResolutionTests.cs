using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

/// <summary>
/// Direct tests for the pure helpers extracted from <see cref="RuntimeActorGrain"/>.
/// The grain itself is hard to exercise without an Orleans test cluster, but
/// the activation-time identity-resolution decisions live entirely in
/// <see cref="RuntimeActorIdentityResolution"/> and are exercisable as pure
/// functions.
/// </summary>
public sealed class RuntimeActorIdentityResolutionTests
{
    [Theory]
    [InlineData("Foo.Bar.Baz", "Foo.Bar.Baz")]
    [InlineData("Foo.Bar.Baz, Some.Asm", "Foo.Bar.Baz")]
    [InlineData("Foo.Bar.Baz, Some.Asm, Version=1.0.0.0", "Foo.Bar.Baz")]
    [InlineData("  Foo.Bar.Baz  ", "Foo.Bar.Baz")]
    [InlineData("  Foo.Bar.Baz  , Some.Asm", "Foo.Bar.Baz")]
    public void TryNormalizeClrTypeName_StripsAssemblyQualifierForNonGenericTypes(string input, string expected)
    {
        RuntimeActorIdentityResolution.TryNormalizeClrTypeName(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData(
        "Foo.Bar`1[[T1, ParamAsm, Version=1.0.0.0]], OuterAsm, Version=2.0.0.0",
        "Foo.Bar`1[[T1, ParamAsm, Version=1.0.0.0]]")]
    [InlineData(
        "Foo.Bar`2[[T1, A1],[T2, A2]], OuterAsm",
        "Foo.Bar`2[[T1, A1],[T2, A2]]")]
    [InlineData(
        "Foo.Bar`1[[Nested.Generic`1[[T2, ParamAsm]], InnerAsm]], OuterAsm",
        "Foo.Bar`1[[Nested.Generic`1[[T2, ParamAsm]], InnerAsm]]")]
    public void TryNormalizeClrTypeName_BracketAware_ForGenericAssemblyQualifiedNames(string input, string expected)
    {
        // A naive IndexOf(',') would split inside the generic-parameter
        // brackets and return a truncated, unmatchable name. The
        // bracket-aware scan must skip commas inside [...] entirely.
        RuntimeActorIdentityResolution.TryNormalizeClrTypeName(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryNormalizeClrTypeName_RejectsEmptyOrWhitespace(string? input)
    {
        RuntimeActorIdentityResolution.TryNormalizeClrTypeName(input!, out var normalized).Should().BeFalse();
        normalized.Should().BeEmpty();
    }

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
    public void ResolvesToSameImplementation_TreatsLegacyAliasAsSameAsCanonical()
    {
        var registry = BuildRegistryWithAlias();

        RuntimeActorIdentityResolution
            .ResolvesToSameImplementation(registry, activeKind: "tests.canonical", requestedKind: "tests.legacy")
            .Should().BeTrue();
    }

    [Fact]
    public void ResolvesToSameImplementation_ReturnsFalseForUnregisteredKind()
    {
        var registry = BuildRegistryWithAlias();

        RuntimeActorIdentityResolution
            .ResolvesToSameImplementation(registry, activeKind: "tests.canonical", requestedKind: "tests.never-registered")
            .Should().BeFalse();
    }

    private static IAgentKindRegistry BuildRegistryWithAlias()
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
[LegacyAgentKind("tests.legacy")]
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
