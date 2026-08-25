using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.Runtime;
using Aevatar.Foundation.Core.TypeSystem;
using FluentAssertions;
using Google.Protobuf;
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
        var registryServices = new ServiceCollection();
        registryServices.AddSingleton(new AgentKindRegistryBuilder().Register<DependencyConsumingAgent>());
        registryServices.AddSingleton<IAgentKindRegistry>(sp =>
            new AgentKindRegistry(sp.GetRequiredService<AgentKindRegistryBuilder>().Build()));
        registryServices.AddSingleton(new DependencyMarker("registry"));
        var registry = registryServices.BuildServiceProvider().GetRequiredService<IAgentKindRegistry>();

        var callerServices = new ServiceCollection();
        callerServices.AddSingleton(new DependencyMarker("caller"));
        using var callerProvider = callerServices.BuildServiceProvider();

        var implementation = registry.Resolve("test.dependency-consumer");
        var instance = (DependencyConsumingAgent)implementation.Factory(callerProvider);

        instance.Marker.Value.Should().Be("caller");
    }

    [Fact]
    public void TryResolve_OnlyLooksUpPrimaryKindWithoutThrowing()
    {
        var registry = BuildRegistry(new AgentKindRegistryBuilder().Register<KindRegistryFixtureSplit>());

        var primaryFound = registry.TryResolve("test.split-new", out var primary);
        var formerAliasFound = registry.TryResolve("test.split-old", out var formerAlias);
        var missingFound = registry.TryResolve("test.missing", out var missing);

        primaryFound.Should().BeTrue();
        primary.Metadata.Kind.Should().Be("test.split-new");
        formerAliasFound.Should().BeFalse();
        formerAlias.Should().BeNull();
        missingFound.Should().BeFalse();
        missing.Should().BeNull();
    }

    [Fact]
    public void Resolve_ThrowsUnknownAgentKindException_ForUnregisteredKind()
    {
        var registry = BuildRegistry(new AgentKindRegistryBuilder());

        var act = () => registry.Resolve("nope.gone");

        act.Should().Throw<UnknownAgentKindException>().Where(ex => ex.Kind == "nope.gone");
    }

    [Fact]
    public void TryGetKindForAgentType_FindsPrimaryKindForRegisteredType()
    {
        var registry = BuildRegistry(new AgentKindRegistryBuilder().Register<KindRegistryFixtureSubscription>());

        var found = registry.TryGetKindForAgentType(typeof(KindRegistryFixtureSubscription), out var kind);

        found.Should().BeTrue();
        kind.Should().Be("test.subscription");
    }

    [Fact]
    public void TryGetKindForAgentType_FindsPrimaryKindForDerivedRegisteredBase()
    {
        var registry = new AgentKindRegistry(
            [
                new AgentRegistration(
                    Kind: "test.base",
                    ImplementationType: typeof(KindRegistryFixtureAgentBase),
                    StateContractType: typeof(object)),
            ]);

        var found = registry.TryGetKindForAgentType(typeof(KindRegistryFixtureSubscription), out var kind);

        found.Should().BeTrue();
        kind.Should().Be("test.base");
    }

    [Fact]
    public void TryGetKindForAgentType_ReturnsFalseForUnregisteredType()
    {
        var registry = BuildRegistry(new AgentKindRegistryBuilder().Register<KindRegistryFixtureSubscription>());

        var found = registry.TryGetKindForAgentType(typeof(UnregisteredAgent), out var kind);

        found.Should().BeFalse();
        kind.Should().BeEmpty();
    }

    [Fact]
    public void Build_ThrowsOnDuplicateKindRegistration()
    {
        var first = new AgentRegistration(
            Kind: "test.duplicate",
            ImplementationType: typeof(KindRegistryFixtureSubscription),
            StateContractType: typeof(object));
        var second = new AgentRegistration(
            Kind: "test.duplicate",
            ImplementationType: typeof(KindRegistryFixtureSplit),
            StateContractType: typeof(object));

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
        var act = () => AgentRegistration.FromAgentType(typeof(UnregisteredAgent));

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
    public void Build_RejectsBuilderSnapshotMutationsAfterBuild()
    {
        var builder = new AgentKindRegistryBuilder().Register<KindRegistryFixtureSubscription>();
        var snapshot = builder.Build();
        builder.Register<KindRegistryFixtureSplit>();

        snapshot.Should().ContainSingle();
        snapshot.Should().ContainSingle(r => r.Kind == "test.subscription");
    }

    [Fact]
    public void StateMigrationApply_ShouldUseAnIsolatedMigrationInstancePerInvocation()
    {
        var registry = BuildRegistry(
            new AgentKindRegistryBuilder()
                .ScanAssemblies(typeof(MigrationIsolationAgent).Assembly));
        var step = registry.Resolve("test.migration-isolation")
            .StateMigrations.Should().ContainSingle().Subject;
        var input = new EventEnvelope { Id = "actor-state" }.ToByteArray();

        var first = EventEnvelope.Parser.ParseFrom(step.Apply(input));
        var second = EventEnvelope.Parser.ParseFrom(step.Apply(input));

        first.Id.Should().Be("actor-state:1");
        second.Id.Should().Be("actor-state:1");
    }

    [Fact]
    public void BuildImplementation_RejectsStateMigrationDeclaredForAnotherAgentKind()
    {
        var registration = new AgentRegistration(
            Kind: "test.wrong-migration-owner",
            ImplementationType: typeof(MigrationIsolationAgent),
            StateContractType: typeof(EventEnvelope),
            StateSchemaVersion: 1,
            StateMigrationTypes: [typeof(StatefulMigrationIsolationFixture)]);

        var act = () => registration.BuildImplementation();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage(
                "*declares agent kind 'test.migration-isolation', " +
                "but is registered for agent kind 'test.wrong-migration-owner'*");
    }

    [Fact]
    public void BuildImplementation_ShouldMergeReflectedAndPrebuiltMigrationsIntoCompleteChain()
    {
        var prebuilt = PrebuiltStep(fromVersion: 1, toVersion: 2);
        var registration = new AgentRegistration(
            Kind: "test.migration-isolation",
            ImplementationType: typeof(MigrationIsolationAgent),
            StateContractType: typeof(EventEnvelope),
            StateSchemaVersion: 2,
            StateMigrationTypes: [typeof(StatefulMigrationIsolationFixture)],
            PrebuiltStateMigrationSteps: [prebuilt]);

        var implementation = registration.BuildImplementation();
        implementation.StateMigrations.Should().NotBeNull();
        var migrations = implementation.StateMigrations!;

        migrations.Select(static step =>
                (step.FromStateVersion, step.ToStateVersion))
            .Should().Equal((0, 1), (1, 2));
        migrations[1].Should().BeSameAs(prebuilt);
    }

    [Fact]
    public void BuildImplementation_ShouldRejectDuplicateReflectedAndPrebuiltMigration()
    {
        var registration = new AgentRegistration(
            Kind: "test.migration-isolation",
            ImplementationType: typeof(MigrationIsolationAgent),
            StateContractType: typeof(EventEnvelope),
            StateSchemaVersion: 1,
            StateMigrationTypes: [typeof(StatefulMigrationIsolationFixture)],
            PrebuiltStateMigrationSteps: [PrebuiltStep(0, 1)]);

        var act = () => registration.BuildImplementation();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*multiple migrations from state schema version 0*");
    }

    [Fact]
    public void BuildImplementation_ShouldRejectMismatchedPrebuiltStateContract()
    {
        var registration = new AgentRegistration(
            Kind: "test.prebuilt-mismatch",
            ImplementationType: typeof(MigrationIsolationAgent),
            StateContractType: typeof(EventEnvelope),
            StateSchemaVersion: 1,
            PrebuiltStateMigrationSteps:
            [
                PrebuiltStep(
                    0,
                    1,
                    stateContractType: typeof(RuntimeActorIdentity)),
            ]);

        var act = () => registration.BuildImplementation();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*targets*RuntimeActorIdentity*owns*EventEnvelope*");
    }

    [Fact]
    public void BuildImplementation_ShouldRejectGappedPrebuiltMigrationChain()
    {
        var registration = new AgentRegistration(
            Kind: "test.prebuilt-gap",
            ImplementationType: typeof(MigrationIsolationAgent),
            StateContractType: typeof(EventEnvelope),
            StateSchemaVersion: 2,
            PrebuiltStateMigrationSteps: [PrebuiltStep(1, 2)]);

        var act = () => registration.BuildImplementation();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no complete typed state migration chain from version 0 to 1*");
    }

    [Fact]
    public void FoundationRegistry_ShouldDeclareAuthorityBootstrapMigrationFromTypedV2Quiescence()
    {
        var registry = BuildRegistry(
            new AgentKindRegistryBuilder().ScanAssemblies(
                typeof(RuntimeFleetCapabilityAuthorityGAgent).Assembly));

        var implementation = registry.Resolve(
            RuntimeFleetCapabilityAuthorityIdentity.AgentKind);

        implementation.Metadata.StateSchemaVersion.Should().Be(
            RuntimeFleetCapabilityAuthorityGAgent.SupportedStateSchemaVersion);
        var migration = implementation.StateMigrations.Should().ContainSingle().Subject;
        migration.FromStateVersion.Should().Be(0);
        migration.ToStateVersion.Should().Be(1);
        migration.RequiredCapability.Should().Be(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2);
        migration.RequiredContractId.Should().Be(
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceV1);
        migration.RequiredContractVersion.Should().Be(
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceReaderVersion);
        migration.RequiredGateStatus.Should().Be(
            RuntimeFleetCapabilityGateStatus.Quiesced);
    }

    private static ActorStateMigrationStep PrebuiltStep(
        int fromVersion,
        int toVersion,
        Type? stateContractType = null) =>
        new(
            fromVersion,
            toVersion,
            stateContractType ?? typeof(EventEnvelope),
            typeof(PrebuiltMigrationMarker),
            static bytes => bytes.ToArray(),
            RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            "test.prebuilt.contract.v1",
            1);

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

internal sealed class UnregisteredAgent : KindRegistryFixtureAgentBase
{
}

[GAgent("test.migration-isolation", StateSchemaVersion = 1)]
internal sealed class MigrationIsolationAgent : KindRegistryFixtureAgentBase, IAgent<EventEnvelope>
{
    public EventEnvelope State { get; } = new();
}

[ActorStateMigration(
    "test.migration-isolation",
    RequiredCapability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
    RequiredContractId = "test.migration-isolation.v1",
    RequiredContractVersion = 1)]
internal sealed class StatefulMigrationIsolationFixture : IActorStateMigration<EventEnvelope>
{
    private int _applyCount;

    public int FromStateVersion => 0;

    public int ToStateVersion => 1;

    public EventEnvelope Apply(EventEnvelope state)
    {
        state.Id = $"{state.Id}:{++_applyCount}";
        return state;
    }
}

internal sealed class PrebuiltMigrationMarker
{
}
