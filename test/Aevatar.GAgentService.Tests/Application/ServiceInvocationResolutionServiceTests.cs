using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ServiceInvocationResolutionServiceTests
{
    [Fact]
    public async Task ResolveAsync_ShouldUseInvocationCatalogReadinessAndPreparedArtifact()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r2",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "r2",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        var service = CreateService(
            identity,
            revisionCatalog,
            readiness: Ready(identity, "chat", "r2", "dep-2", "actor-2"),
            policyIds: ["policy-a"]);

        var resolved = await service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        resolved.Service.RevisionId.Should().Be("r2");
        resolved.Service.DeploymentId.Should().Be("dep-2");
        resolved.Service.PrimaryActorId.Should().Be("actor-2");
        resolved.Service.PolicyIds.Should().ContainSingle("policy-a");
        resolved.Artifact.RevisionId.Should().Be("r2");
        resolved.Endpoint.EndpointId.Should().Be("chat");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectMissingIdentity()
    {
        var service = CreateService(GAgentServiceTestKit.CreateIdentity(), new FakeServiceRevisionCatalogQueryReader());

        var act = () => service.ResolveAsync(new ServiceInvocationRequest
        {
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("service identity is required.");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectBlankEndpointId()
    {
        var service = CreateService(GAgentServiceTestKit.CreateIdentity(), new FakeServiceRevisionCatalogQueryReader());

        var act = () => service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = " ",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("endpoint_id is required.");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectMissingCatalogSnapshot()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader(),
            new RecordingInvocationCatalogQueryReader(),
            new FakeServiceRevisionCatalogQueryReader(),
            new RecordingServingSetQueryReader());

        var act = () => service.ResolveAsync(NewRequest(identity, "chat"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*was not found*");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectMissingInvocationCatalogReadModel_AsUnspecified()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingInvocationCatalogQueryReader(),
            new FakeServiceRevisionCatalogQueryReader(),
            new RecordingServingSetQueryReader());

        var act = () => service.ResolveAsync(NewRequest(identity, "chat"));

        var ex = await act.Should().ThrowAsync<ServiceInvokeReadinessException>();
        ex.Which.Snapshot.ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unspecified);
        ex.Which.Snapshot.UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.Unspecified);
    }

    [Theory]
    [InlineData(ServiceInvokeUnavailableReason.ServingTargetMissing)]
    [InlineData(ServiceInvokeUnavailableReason.RevisionNotPrepared)]
    [InlineData(ServiceInvokeUnavailableReason.PreparedArtifactMissing)]
    [InlineData(ServiceInvokeUnavailableReason.PreparedArtifactIncompatible)]
    public async Task ResolveAsync_ShouldRejectUnavailableReadiness_WithCanonicalReason(ServiceInvokeUnavailableReason reason)
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = CreateService(
            identity,
            new FakeServiceRevisionCatalogQueryReader(),
            readiness: Unavailable(identity, "chat", reason, "r1", "dep-1", "actor-1"));

        var act = () => service.ResolveAsync(NewRequest(identity, "chat"));

        var ex = await act.Should().ThrowAsync<ServiceInvokeReadinessException>();
        ex.Which.Snapshot.ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable);
        ex.Which.Snapshot.UnavailableReason.Should().Be(reason);
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectMissingEndpointReadiness_AsUnspecified()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = CreateService(
            identity,
            new FakeServiceRevisionCatalogQueryReader(),
            readiness: Ready(identity, "other", "r1", "dep-1", "actor-1"));

        var act = () => service.ResolveAsync(NewRequest(identity, "chat"));

        var ex = await act.Should().ThrowAsync<ServiceInvokeReadinessException>();
        ex.Which.Snapshot.ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unspecified);
        ex.Which.Snapshot.UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.Unspecified);
        ex.Which.Snapshot.AggregateStateVersion.Should().Be(7);
    }

    [Fact]
    public async Task ResolveAsync_ShouldHonorExplicitRevisionSelection_FromReadinessEntry()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "r1",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r2",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "r2",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        var service = CreateService(
            identity,
            revisionCatalog,
            readinessEntries:
            [
                Ready(identity, "chat", "r1", "dep-1", "actor-1"),
                Ready(identity, "chat", "r2", "dep-2", "actor-2"),
            ]);

        var resolved = await service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            RevisionId = "r1",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        resolved.Service.RevisionId.Should().Be("r1");
        resolved.Service.DeploymentId.Should().Be("dep-1");
        resolved.Artifact.RevisionId.Should().Be("r1");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectReadyReadiness_WhenDefaultServingRevisionHasCutOver()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "r1",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        var service = CreateService(
            identity,
            revisionCatalog,
            readiness: Ready(identity, "chat", "r1", "dep-1", "actor-1"),
            servingTargets: [ServingTarget("r2", "dep-2", "actor-2", "chat")],
            defaultServingRevisionId: "r2");

        var act = () => service.ResolveAsync(NewRequest(identity, "chat"));

        var ex = await act.Should().ThrowAsync<ServiceInvokeReadinessException>();
        ex.Which.Snapshot.ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable);
        ex.Which.Snapshot.UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.ServingTargetMissing);
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectReadyReadiness_WhenServingSetIsMissing()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "r1",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingInvocationCatalogQueryReader
            {
                GetResult = new ServiceInvocationCatalogSnapshot(
                    ServiceKeys.Build(identity),
                    [Ready(identity, "chat", "r1", "dep-1", "actor-1")],
                    DateTimeOffset.Parse("2026-06-05T00:00:00+00:00"),
                    7,
                    $"{ServiceKeys.Build(identity)}:invocation-catalog:7",
                    1,
                    2,
                    3),
            },
            revisionCatalog,
            new RecordingServingSetQueryReader());

        var act = () => service.ResolveAsync(NewRequest(identity, "chat"));

        var ex = await act.Should().ThrowAsync<ServiceInvokeReadinessException>();
        ex.Which.Snapshot.ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable);
        ex.Which.Snapshot.UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.ServingTargetMissing);
    }

    [Fact]
    public async Task ResolveAsync_ShouldUseCurrentServingTarget_WhenStaleReadyEntryRemains()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        foreach (var revisionId in new[] { "r1", "r2" })
        {
            await revisionCatalog.UpsertRevisionAsync(
                ServiceKeys.Build(identity),
                revisionId,
                GAgentServiceTestKit.CreatePreparedStaticArtifact(
                    identity,
                    revisionId,
                    GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        }

        var service = CreateService(
            identity,
            revisionCatalog,
            readinessEntries:
            [
                Ready(identity, "chat", "r1", "dep-1", "actor-1"),
                Ready(identity, "chat", "r2", "dep-2", "actor-2"),
            ],
            servingTargets: [ServingTarget("r2", "dep-2", "actor-2", "chat")],
            defaultServingRevisionId: "r2");

        var resolved = await service.ResolveAsync(NewRequest(identity, "chat"));

        resolved.Service.RevisionId.Should().Be("r2");
        resolved.Service.DeploymentId.Should().Be("dep-2");
        resolved.Service.PrimaryActorId.Should().Be("actor-2");
        resolved.Artifact.RevisionId.Should().Be("r2");
    }

    [Theory]
    [InlineData("r2", "dep-1", "actor-1", 100, "Active", "chat")]
    [InlineData("r1", "dep-2", "actor-1", 100, "Active", "chat")]
    [InlineData("r1", "dep-1", "actor-2", 100, "Active", "chat")]
    [InlineData("r1", "dep-1", "actor-1", 0, "Active", "chat")]
    [InlineData("r1", "dep-1", "actor-1", 100, "Draining", "chat")]
    [InlineData("r1", "dep-1", "actor-1", 100, "invalid", "chat")]
    [InlineData("r1", "dep-1", "actor-1", 100, "Active", "other")]
    public async Task ResolveAsync_ShouldRejectReadyReadiness_WhenServingTargetIsNotEligible(
        string revisionId,
        string deploymentId,
        string actorId,
        int allocationWeight,
        string servingState,
        string endpointId)
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "r1",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        var service = CreateService(
            identity,
            revisionCatalog,
            readiness: Ready(identity, "chat", "r1", "dep-1", "actor-1"),
            servingTargets:
            [
                new ServiceServingTargetSnapshot(
                    deploymentId,
                    revisionId,
                    actorId,
                    allocationWeight,
                    servingState,
                    [endpointId]),
            ]);

        var act = () => service.ResolveAsync(NewRequest(identity, "chat"));

        var ex = await act.Should().ThrowAsync<ServiceInvokeReadinessException>();
        ex.Which.Snapshot.ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable);
        ex.Which.Snapshot.UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.ServingTargetMissing);
    }

    [Fact]
    public async Task ResolveAsync_ShouldMapReadyDriftMissingArtifact_ToPreparedArtifactMissing()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = CreateService(
            identity,
            new FakeServiceRevisionCatalogQueryReader(),
            readiness: Ready(identity, "chat", "r1", "dep-1", "actor-1"));

        var act = () => service.ResolveAsync(NewRequest(identity, "chat"));

        var ex = await act.Should().ThrowAsync<ServiceInvokeReadinessException>();
        ex.Which.Snapshot.ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable);
        ex.Which.Snapshot.UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.PreparedArtifactMissing);
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectReadyReadModel_WhenWorkflowArtifactRequiresCapabilityAdmissionRebind()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            PreparedWorkflowArtifact(identity, "r1", WorkflowCapabilityAdmissionPlanIntegrity.LegacySchemaVersion));
        var service = CreateService(
            identity,
            revisionCatalog,
            readiness: Ready(identity, "chat", "r1", "dep-1", "actor-1"));

        var act = () => service.ResolveAsync(NewRequest(identity, "chat"));

        var ex = await act.Should().ThrowAsync<ServiceInvokeReadinessException>();
        ex.Which.Snapshot.ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable);
        ex.Which.Snapshot.UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.PreparedArtifactIncompatible);
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectReadyReadModel_WhenWorkflowExecutionModeIsMissing()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        var artifact = PreparedWorkflowArtifact(identity, "r1", WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion);
        artifact.DeploymentPlan.WorkflowPlan.ExecutionMode = ExternalCapabilityExecutionMode.Unspecified;
        await revisionCatalog.UpsertRevisionAsync(ServiceKeys.Build(identity), "r1", artifact);
        var service = CreateService(
            identity,
            revisionCatalog,
            readiness: Ready(identity, "chat", "r1", "dep-1", "actor-1"));

        var act = () => service.ResolveAsync(NewRequest(identity, "chat"));

        var ex = await act.Should().ThrowAsync<ServiceInvokeReadinessException>();
        ex.Which.Snapshot.ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable);
        ex.Which.Snapshot.UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.PreparedArtifactIncompatible);
    }

    private static ServiceInvocationRequest NewRequest(ServiceIdentity identity, string endpointId) =>
        new()
        {
            Identity = identity.Clone(),
            EndpointId = endpointId,
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        };

    private static ServiceInvocationResolutionService CreateService(
        ServiceIdentity identity,
        IServiceRevisionCatalogQueryReader revisionCatalog,
        ServiceInvokeReadinessSnapshot? readiness = null,
        ServiceInvocationCatalogSnapshot? readinessCatalog = default,
        IReadOnlyList<ServiceInvokeReadinessSnapshot>? readinessEntries = null,
        IReadOnlyList<string>? policyIds = null,
        IReadOnlyList<ServiceServingTargetSnapshot>? servingTargets = null,
        string defaultServingRevisionId = "")
    {
        var serviceKey = ServiceKeys.Build(identity);
        var entries = readinessEntries ?? (readiness == null ? [] : [readiness]);
        readinessCatalog ??= new ServiceInvocationCatalogSnapshot(
            serviceKey,
            entries,
            DateTimeOffset.Parse("2026-06-05T00:00:00+00:00"),
            7,
            $"{serviceKey}:invocation-catalog:7",
            1,
            2,
            3);

        return new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity, policyIds, defaultServingRevisionId),
            },
            new RecordingInvocationCatalogQueryReader
            {
                GetResult = readinessCatalog,
            },
            revisionCatalog,
            new RecordingServingSetQueryReader
            {
                GetResult = new ServiceServingSetSnapshot(
                    serviceKey,
                    1,
                    "rollout-1",
                    servingTargets ?? entries.Select(entry => ServingTarget(
                        entry.SelectedRevisionId,
                        entry.SelectedDeploymentId,
                        entry.SelectedActorId,
                        entry.EndpointId)).ToArray(),
                    DateTimeOffset.Parse("2026-06-05T00:00:00+00:00")),
            });
    }

    private static ServiceServingTargetSnapshot ServingTarget(
        string revisionId,
        string deploymentId,
        string actorId,
        string endpointId) =>
        new(
            deploymentId,
            revisionId,
            actorId,
            100,
            ServiceServingState.Active.ToString(),
            [endpointId]);

    private static ServiceInvokeReadinessSnapshot Ready(
        ServiceIdentity identity,
        string endpointId,
        string revisionId,
        string deploymentId,
        string actorId) =>
        Snapshot(
            identity,
            endpointId,
            ServiceInvokeReadinessStatus.Ready,
            ServiceInvokeUnavailableReason.Unspecified,
            revisionId,
            deploymentId,
            actorId);

    private static ServiceInvokeReadinessSnapshot Unavailable(
        ServiceIdentity identity,
        string endpointId,
        ServiceInvokeUnavailableReason reason,
        string revisionId = "",
        string deploymentId = "",
        string actorId = "") =>
        Snapshot(
            identity,
            endpointId,
            ServiceInvokeReadinessStatus.Unavailable,
            reason,
            revisionId,
            deploymentId,
            actorId);

    private static ServiceInvokeReadinessSnapshot Snapshot(
        ServiceIdentity identity,
        string endpointId,
        ServiceInvokeReadinessStatus status,
        ServiceInvokeUnavailableReason reason,
        string revisionId,
        string deploymentId,
        string actorId) =>
        new(
            ServiceKeys.Build(identity),
            endpointId,
            status,
            reason,
            revisionId,
            deploymentId,
            actorId,
            DateTimeOffset.Parse("2026-06-05T00:00:00+00:00"),
            7,
            $"{ServiceKeys.Build(identity)}:invocation-catalog:7",
            1,
            2,
            3);

    private static ServiceCatalogSnapshot CreateCatalogSnapshot(
        ServiceIdentity identity,
        IReadOnlyList<string>? policyIds = null,
        string defaultServingRevisionId = "") =>
        new(
            ServiceKeys.Build(identity),
            identity.TenantId,
            identity.AppId,
            identity.Namespace,
            identity.ServiceId,
            "Service",
            defaultServingRevisionId,
            string.Empty,
            string.Empty,
            string.Empty,
            ServiceDeploymentStatus.Unspecified.ToString(),
            [],
            policyIds ?? [],
            DateTimeOffset.UtcNow);

    private static PreparedServiceRevisionArtifact PreparedWorkflowArtifact(
        ServiceIdentity identity,
        string revisionId,
        string schemaVersion)
    {
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Workflow,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                WorkflowPlan = new WorkflowServiceDeploymentPlan
                {
                    ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                    WorkflowName = "document_file_extract",
                    WorkflowYaml = "name: document_file_extract\nsteps: []",
                    ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                    CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
                    {
                        SchemaVersion = schemaVersion,
                        ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                    },
                },
            },
        };
        artifact.Endpoints.Add(GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat"));
        return artifact;
    }

    private sealed class RecordingCatalogQueryReader : IServiceCatalogQueryReader
    {
        public ServiceCatalogSnapshot? GetResult { get; init; }

        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(GetResult);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(int take = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);
    }

    private sealed class RecordingInvocationCatalogQueryReader : IServiceInvocationCatalogQueryReader
    {
        public ServiceInvocationCatalogSnapshot? GetResult { get; init; }

        public Task<ServiceInvocationCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(GetResult);
    }

    private sealed class RecordingServingSetQueryReader : IServiceServingSetQueryReader
    {
        public ServiceServingSetSnapshot? GetResult { get; init; }

        public Task<ServiceServingSetSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(GetResult);
    }
}
