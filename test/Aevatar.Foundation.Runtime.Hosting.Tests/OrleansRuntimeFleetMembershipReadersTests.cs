using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansRuntimeFleetMembershipReadersTests
{
    [Fact]
    public void ExactActiveManifestSet_ShouldRequireSameIncarnations()
    {
        var first = Address(port: 31001, generation: 10);
        var second = Address(port: 31002, generation: 20);

        OrleansRuntimeFleetMembershipSnapshotSource.HasExactActiveManifestSet(
                [first, second],
                [second, first])
            .Should().BeTrue();
        OrleansRuntimeFleetMembershipSnapshotSource.HasExactActiveManifestSet(
                [first, second],
                [first])
            .Should().BeFalse();
        OrleansRuntimeFleetMembershipSnapshotSource.HasExactActiveManifestSet(
                [first],
                [first, second])
            .Should().BeFalse();
        OrleansRuntimeFleetMembershipSnapshotSource.HasExactActiveManifestSet(
                [first],
                [Address(port: 31001, generation: 11)])
            .Should().BeFalse();
    }

    [Fact]
    public void DeploymentRevision_ShouldBindManifestContractAndReaderImplementationModule()
    {
        var capability = new RuntimeFleetMemberCapability
        {
            Capability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            ContractId = RuntimeFleetCapabilityContracts.WorkflowNormalizedStateV1,
            ReaderContractVersion = RuntimeFleetCapabilityContracts.WorkflowNormalizedStateReaderVersion,
        };

        var first = RuntimeFleetCapabilityManifest.ResolveDeploymentRevision(
            typeof(OrleansRuntimeFleetMembershipSnapshotSource),
            [(capability, typeof(OrleansRuntimeFleetMembershipSnapshotSource))]);
        var same = RuntimeFleetCapabilityManifest.ResolveDeploymentRevision(
            typeof(OrleansRuntimeFleetMembershipSnapshotSource),
            [(capability.Clone(), typeof(OrleansRuntimeFleetMembershipSnapshotSource))]);

        var changedContract = capability.Clone();
        changedContract.ContractId = "aevatar.workflow.normalized-state.v2";
        var contractRevision = RuntimeFleetCapabilityManifest.ResolveDeploymentRevision(
            typeof(OrleansRuntimeFleetMembershipSnapshotSource),
            [(changedContract, typeof(OrleansRuntimeFleetMembershipSnapshotSource))]);
        var readerModuleRevision = RuntimeFleetCapabilityManifest.ResolveDeploymentRevision(
            typeof(OrleansRuntimeFleetMembershipSnapshotSource),
            [(capability.Clone(), typeof(OrleansRuntimeFleetMembershipReadersTests))]);

        first.Should().StartWith("manifest-v1:");
        same.Should().Be(first);
        contractRevision.Should().NotBe(first);
        readerModuleRevision.Should().NotBe(first);
    }

    [Fact]
    public void CapabilityAdvertisement_DefaultReaderMarker_ShouldBeAdvertisementImplementation()
    {
        IRuntimeFleetCapabilityAdvertisement advertisement = new TestCapabilityAdvertisement();

        advertisement.IsAvailable.Should().BeTrue();
        advertisement.GetReaderImplementationType().Should().Be(typeof(TestCapabilityAdvertisement));
    }

    [Fact]
    public void CapabilityManifest_WhenAdvertisementIsUnavailable_ShouldOmitItWithoutReadingContract()
    {
        var services = new ServiceCollection()
            .AddSingleton<IRuntimeFleetCapabilityAdvertisement>(
                new UnavailableCapabilityAdvertisement())
            .BuildServiceProvider();
        var properties = new Dictionary<string, string>();

        new RuntimeFleetCapabilityManifestAttribute().Populate(
            services,
            typeof(OrleansRuntimeFleetMembershipSnapshotSource),
            GrainType.Create("runtime-actor"),
            properties);

        properties.Should().ContainKey(
            RuntimeFleetCapabilityManifest.DeploymentRevisionProperty);
        properties.Should().NotContainKey(
            RuntimeFleetCapabilityManifest.ContractIdProperty(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV3));
        properties.Should().NotContainKey(
            RuntimeFleetCapabilityManifest.ReaderVersionProperty(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV3));
    }

    [Fact]
    public async Task CurrentSnapshot_MixedModernAndLegacySilos_ShouldPreserveExactUnanimityEvidence()
    {
        var modern = Address(port: 31003, generation: 30);
        var legacy = Address(port: 31004, generation: 40);
        var membership = new ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty
                .Add(modern, new ClusterMember(modern, SiloStatus.Active, "modern"))
                .Add(legacy, new ClusterMember(legacy, SiloStatus.Active, "legacy")),
            new MembershipVersion(42));
        var modernProperties = ImmutableDictionary<string, string>.Empty
            .Add(RuntimeFleetCapabilityManifest.DeploymentRevisionProperty, "revision-modern")
            .Add(
                RuntimeFleetCapabilityManifest.ContractIdProperty(
                    RuntimeFleetCapability.WorkflowNormalizedStateWritesV1),
                RuntimeFleetCapabilityContracts.WorkflowNormalizedStateV1)
            .Add(
                RuntimeFleetCapabilityManifest.ReaderVersionProperty(
                    RuntimeFleetCapability.WorkflowNormalizedStateWritesV1),
                RuntimeFleetCapabilityContracts.WorkflowNormalizedStateReaderVersion.ToString());
        var manifests = new ClusterManifest(
            MajorMinorVersion.Zero,
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(modern, Manifest(modernProperties))
                .Add(legacy, Manifest(ImmutableDictionary<string, string>.Empty)));
        var source = new OrleansRuntimeFleetMembershipSnapshotSource(
            WithCurrent<IClusterMembershipService>(membership),
            WithCurrent<IClusterManifestProvider>(manifests));

        var snapshot = await source.GetCurrentAsync();

        snapshot.Should().NotBeNull();
        snapshot!.MembershipEpoch.Should().Be(42);
        snapshot.DeploymentRevision.Should().StartWith("mixed:");
        snapshot.ActiveMembers.Should().HaveCount(2);
        snapshot.MembershipDigest.Should().Be(RuntimeFleetMembershipDigest.Compute(snapshot));
        var modernMember = snapshot.ActiveMembers.Single(member =>
            member.Incarnation == modern.Generation.ToString());
        modernMember.Capabilities.Should().ContainSingle(capability =>
            capability.Capability == RuntimeFleetCapability.WorkflowNormalizedStateWritesV1 &&
            capability.ContractId == RuntimeFleetCapabilityContracts.WorkflowNormalizedStateV1 &&
            capability.ReaderContractVersion ==
            RuntimeFleetCapabilityContracts.WorkflowNormalizedStateReaderVersion);
        var legacyMember = snapshot.ActiveMembers.Single(member =>
            member.Incarnation == legacy.Generation.ToString());
        legacyMember.Capabilities.Should().BeEmpty();
        snapshot.ActiveMembers.All(member => member.Capabilities.Any(capability =>
                capability.Capability == RuntimeFleetCapability.WorkflowNormalizedStateWritesV1))
            .Should().BeFalse();
    }

    private static SiloAddress Address(int port, int generation) =>
        SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), generation);

    private static GrainManifest Manifest(ImmutableDictionary<string, string> properties) =>
        new(
            ImmutableDictionary<GrainType, GrainProperties>.Empty.Add(
                GrainType.Create("runtime-actor"),
                new GrainProperties(properties)),
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);

    private static T WithCurrent<T>(object current) where T : class
    {
        var proxy = DispatchProxy.Create<T, CurrentPropertyProxy>();
        ((CurrentPropertyProxy)(object)proxy).Current = current;
        return proxy;
    }

    private sealed class TestCapabilityAdvertisement : IRuntimeFleetCapabilityAdvertisement
    {
        public RuntimeFleetMemberCapability GetCapability() =>
            new()
            {
                Capability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
                ContractId = RuntimeFleetCapabilityContracts.WorkflowNormalizedStateV1,
                ReaderContractVersion =
                    RuntimeFleetCapabilityContracts.WorkflowNormalizedStateReaderVersion,
            };
    }

    private sealed class UnavailableCapabilityAdvertisement
        : IRuntimeFleetCapabilityAdvertisement
    {
        public bool IsAvailable => false;

        public RuntimeFleetMemberCapability GetCapability() =>
            throw new InvalidOperationException(
                "An unavailable advertisement must not be materialized.");
    }

    private class CurrentPropertyProxy : DispatchProxy
    {
        internal object? Current { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name.StartsWith("get_Current", StringComparison.Ordinal) == true)
                return Current;
            return targetMethod?.ReturnType is { IsValueType: true } returnType
                ? Activator.CreateInstance(returnType)
                : null;
        }
    }
}
