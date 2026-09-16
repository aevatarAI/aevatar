using System.Security.Cryptography;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Bindings;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScopeBindingReadinessQueryServiceTests
{
    private static readonly ScopeWorkflowCapabilityOptions DefaultOptions = new()
    {
        DefaultServiceId = "default",
        ServiceAppId = "default",
        ServiceNamespace = "default",
    };

    [Fact]
    public async Task GetReadinessAsync_WhenServiceCatalogMissing_ShouldReturnServiceCatalogMissing()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort();
        var servingPort = new FakeServiceServingQueryPort();
        var service = CreateService(lifecyclePort, servingPort);

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(" scope-a ", " service-a "));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.ServiceCatalogMissing);
        snapshot.ServiceCatalogVisible.Should().BeFalse();
        snapshot.ServingSetVisible.Should().BeFalse();
        snapshot.EligibleServingTargetVisible.Should().BeFalse();
        snapshot.InvokeReady.Should().BeFalse();
        snapshot.ScopeId.Should().Be("scope-a");
        snapshot.ServiceId.Should().Be("service-a");
        servingPort.GetServingSetCallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetReadinessAsync_WhenServiceCatalogMissingButActivationFailed_ShouldExposeTerminalFailure()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Deployments = new ServiceDeploymentCatalogSnapshot(
                "scope-a:default:default:service-a",
                [],
                [
                    new ServiceDeploymentActivationFailureSnapshot(
                        "rev-terminal",
                        ServiceDeploymentActivationFailureCode.PreparedArtifactMissing,
                        "sensitive internal projection details",
                        DateTimeOffset.Parse("2026-08-14T19:47:24+00:00"),
                        ActivationAttemptId: "attempt-terminal"),
                ],
                DateTimeOffset.Parse("2026-08-14T19:47:24+00:00")),
        };
        var servingPort = new FakeServiceServingQueryPort();
        var service = CreateService(lifecyclePort, servingPort);

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(
            "scope-a",
            "service-a",
            ExpectedRevisionId: "rev-terminal",
            ExpectedActivationAttemptId: "attempt-terminal"));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.ServiceCatalogMissing);
        snapshot.TerminalActivationFailureCode.Should()
            .Be(ServiceDeploymentActivationFailureCode.PreparedArtifactMissing);
        lifecyclePort.GetDeploymentsCallCount.Should().Be(1);
        servingPort.GetServingSetCallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetReadinessAsync_WhenServingSetMissing_ShouldReturnServingSetMissing()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a"),
        };
        var servingPort = new FakeServiceServingQueryPort();
        var service = CreateService(lifecyclePort, servingPort);

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest("scope-a", "service-a"));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.ServingSetMissing);
        snapshot.ServiceCatalogVisible.Should().BeTrue();
        snapshot.ServingSetVisible.Should().BeFalse();
        snapshot.EligibleServingTargetVisible.Should().BeFalse();
        snapshot.InvokeReady.Should().BeFalse();
        servingPort.GetServingSetCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetReadinessAsync_WhenServingSetMissingAndActivationFailed_ShouldExposeTerminalFailure()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a"),
            Deployments = new ServiceDeploymentCatalogSnapshot(
                "scope-a:default:default:service-a",
                [],
                [
                    new ServiceDeploymentActivationFailureSnapshot(
                        "rev-terminal",
                        ServiceDeploymentActivationFailureCode.PreparedArtifactMissing,
                        "sensitive internal projection details",
                        DateTimeOffset.Parse("2026-08-14T19:47:24+00:00"),
                        ActivationAttemptId: "attempt-terminal"),
                ],
                DateTimeOffset.Parse("2026-08-14T19:47:24+00:00")),
        };
        var servingPort = new FakeServiceServingQueryPort();
        var service = CreateService(lifecyclePort, servingPort);

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(
            "scope-a",
            "service-a",
            ExpectedRevisionId: "rev-terminal",
            ExpectedDeploymentId: "deployment-terminal",
            ExpectedActivationAttemptId: "attempt-terminal"));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.ServingSetMissing);
        snapshot.InvokeReady.Should().BeFalse();
        snapshot.TerminalActivationFailureCode.Should()
            .Be(ServiceDeploymentActivationFailureCode.PreparedArtifactMissing);
        lifecyclePort.GetDeploymentsCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetReadinessAsync_WhenActivationFailureAttemptDoesNotMatch_ShouldRemainPending()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a"),
            Deployments = new ServiceDeploymentCatalogSnapshot(
                "scope-a:default:default:service-a",
                [],
                [
                    new ServiceDeploymentActivationFailureSnapshot(
                        "rev-pending",
                        ServiceDeploymentActivationFailureCode.PreparedArtifactMissing,
                        "previous attempt failed",
                        DateTimeOffset.Parse("2026-08-14T19:47:24+00:00"),
                        ActivationAttemptId: "attempt-old"),
                ],
                DateTimeOffset.Parse("2026-08-14T19:47:24+00:00")),
        };
        var servingPort = new FakeServiceServingQueryPort();
        var service = CreateService(lifecyclePort, servingPort);

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(
            "scope-a",
            "service-a",
            ExpectedRevisionId: "rev-pending",
            ExpectedDeploymentId: "deployment-pending",
            ExpectedActivationAttemptId: "attempt-new"));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.ServingSetMissing);
        snapshot.InvokeReady.Should().BeFalse();
        snapshot.TerminalActivationFailureCode.Should().BeNull();
        lifecyclePort.GetDeploymentsCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetReadinessAsync_WhenLegacyRequestHasNoActivationFence_ShouldRemainPending()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a"),
            DeploymentsFailure = new InvalidOperationException("legacy requests must not query terminal failures"),
        };
        var service = CreateService(lifecyclePort, new FakeServiceServingQueryPort());

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(
            "scope-a",
            "service-a",
            ExpectedRevisionId: "rev-legacy",
            ExpectedDeploymentId: "deployment-legacy"));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.ServingSetMissing);
        snapshot.TerminalActivationFailureCode.Should().BeNull();
        lifecyclePort.GetDeploymentsCallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetReadinessAsync_WhenMatchingActivationFailureHasEarlierWallClock_ShouldExposeTerminalFailure()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a"),
            Deployments = new ServiceDeploymentCatalogSnapshot(
                "scope-a:default:default:service-a",
                [],
                [
                    new ServiceDeploymentActivationFailureSnapshot(
                        "rev-replayed",
                        ServiceDeploymentActivationFailureCode.PreparedArtifactMissing,
                        "previous attempt failed",
                        DateTimeOffset.Parse("2026-08-14T19:47:22+00:00"),
                        ActivationAttemptId: "attempt-replayed"),
                ],
                DateTimeOffset.Parse("2026-08-14T19:47:24+00:00")),
        };
        var service = CreateService(lifecyclePort, new FakeServiceServingQueryPort());

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(
            "scope-a",
            "service-a",
            ExpectedRevisionId: "rev-replayed",
            ExpectedDeploymentId: "deployment-replayed",
            ExpectedActivationAttemptId: "attempt-replayed"));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.ServingSetMissing);
        snapshot.TerminalActivationFailureCode.Should()
            .Be(ServiceDeploymentActivationFailureCode.PreparedArtifactMissing);
        lifecyclePort.GetDeploymentsCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetReadinessAsync_WhenDefaultServingCommitIsPending_ShouldNotReturnReady()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a"),
            DeploymentsFailure = new InvalidOperationException("lagging deployment projection"),
        };
        var servingPort = new FakeServiceServingQueryPort
        {
            ServingSet = CreateServingSet([
                CreateTarget("rev-new", ServiceServingState.Active, allocationWeight: 100),
            ]),
        };
        var service = CreateService(lifecyclePort, servingPort);

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(
            "scope-a",
            "service-a",
            ExpectedRevisionId: "rev-new",
            ExpectedDeploymentId: "deployment-rev-new"));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.ServiceCatalogTargetMissing);
        snapshot.ServiceCatalogVisible.Should().BeTrue();
        snapshot.ServingSetVisible.Should().BeTrue();
        snapshot.EligibleServingTargetVisible.Should().BeTrue();
        snapshot.InvokeReady.Should().BeFalse();
        snapshot.RevisionId.Should().Be("rev-new");
        snapshot.DeploymentId.Should().Be("deployment-rev-new");
        snapshot.TerminalActivationFailureCode.Should().BeNull();
        lifecyclePort.GetDeploymentsCallCount.Should().Be(0, "service-catalog fencing runs before deployment evidence");
        servingPort.GetServingSetCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetReadinessAsync_WhenServingSetHasNoEligibleTarget_ShouldReturnEligibleServingTargetMissing()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a"),
        };
        var servingPort = new FakeServiceServingQueryPort
        {
            ServingSet = CreateServingSet([
                CreateTarget("rev-paused", ServiceServingState.Paused, allocationWeight: 100),
            ]),
        };
        var service = CreateService(lifecyclePort, servingPort);

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest("scope-a", "service-a"));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.EligibleServingTargetMissing);
        snapshot.ServiceCatalogVisible.Should().BeTrue();
        snapshot.ServingSetVisible.Should().BeTrue();
        snapshot.EligibleServingTargetVisible.Should().BeFalse();
        snapshot.InvokeReady.Should().BeFalse();
        snapshot.RevisionId.Should().BeNull();
        snapshot.DeploymentId.Should().BeNull();
    }

    [Fact]
    public async Task GetReadinessAsync_WhenServingSetTargetDoesNotEnableServiceEndpoint_ShouldReturnEligibleServingTargetMissing()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a", endpoints: [CreateServiceEndpoint("chat")]),
        };
        var servingPort = new FakeServiceServingQueryPort
        {
            ServingSet = CreateServingSet([
                CreateTarget("rev-ready", ServiceServingState.Active, allocationWeight: 100, enabledEndpointIds: ["command"]),
            ]),
        };
        var service = CreateService(lifecyclePort, servingPort);

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest("scope-a", "service-a"));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.EligibleServingTargetMissing);
        snapshot.InvokeReady.Should().BeFalse();
        snapshot.RevisionId.Should().BeNull();
        snapshot.DeploymentId.Should().BeNull();
    }

    [Fact]
    public async Task GetReadinessAsync_WhenExpectedEndpointMissingFromStaleCatalog_ShouldReturnServiceCatalogTargetMissing()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a", endpoints: [CreateServiceEndpoint("old")]),
        };
        var servingPort = new FakeServiceServingQueryPort
        {
            ServingSet = CreateServingSet([
                CreateTarget("rev-ready", ServiceServingState.Active, allocationWeight: 100, enabledEndpointIds: ["chat"]),
            ]),
        };
        var service = CreateService(lifecyclePort, servingPort);

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(
            "scope-a",
            "service-a",
            ExpectedEndpointIds: ["chat"]));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.ServiceCatalogTargetMissing);
        snapshot.ServiceCatalogVisible.Should().BeTrue();
        snapshot.ServingSetVisible.Should().BeTrue();
        snapshot.EligibleServingTargetVisible.Should().BeTrue();
        snapshot.InvokeReady.Should().BeFalse();
        snapshot.RevisionId.Should().Be("rev-ready");
        snapshot.DeploymentId.Should().Be("deployment-rev-ready");
    }

    [Fact]
    public async Task GetReadinessAsync_WhenServingSetHasActiveZeroWeightTarget_ShouldReturnEligibleServingTargetMissing()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a"),
        };
        var servingPort = new FakeServiceServingQueryPort
        {
            ServingSet = CreateServingSet([
                CreateTarget("rev-zero", ServiceServingState.Active, allocationWeight: 0),
            ]),
        };
        var service = CreateService(lifecyclePort, servingPort);

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest("scope-a", "service-a"));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.EligibleServingTargetMissing);
        snapshot.InvokeReady.Should().BeFalse();
    }

    [Fact]
    public async Task GetReadinessAsync_WhenServingSetHasEligibleTarget_ShouldReturnReady()
    {
        var artifact = CreateArtifact("rev-ready", ["chat", "command"]);
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a"),
            Deployments = CreateDeploymentCatalog(
                "rev-ready",
                "deployment-rev-ready",
                "actor-rev-ready",
                artifact.ArtifactHash),
        };
        var servingPort = new FakeServiceServingQueryPort
        {
            ServingSet = CreateServingSet([
                CreateTarget("rev-zero", ServiceServingState.Active, allocationWeight: 0),
                CreateTarget("rev-ready", ServiceServingState.Active, allocationWeight: 100),
            ]),
            TrafficView = CreateTrafficView([
                CreateTrafficEndpoint("chat", [CreateTrafficTarget("rev-ready", ServiceServingState.Active, allocationWeight: 100)]),
            ]),
        };
        var service = CreateService(
            lifecyclePort,
            servingPort,
            CreateRevisionCatalogReader(new Dictionary<string, PreparedServiceRevisionArtifact>
            {
                ["rev-ready"] = artifact,
            }));

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest("scope-a", "service-a"));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.Ready);
        snapshot.ServiceCatalogVisible.Should().BeTrue();
        snapshot.ServingSetVisible.Should().BeTrue();
        snapshot.EligibleServingTargetVisible.Should().BeTrue();
        snapshot.InvokeReady.Should().BeTrue();
        snapshot.RevisionId.Should().Be("rev-ready");
        snapshot.DeploymentId.Should().Be("deployment-rev-ready");
    }

    [Fact]
    public async Task GetReadinessAsync_WhenInvocationCatalogRejectsPreparedRevision_ShouldNotReturnReady()
    {
        var artifact = CreateArtifact(
            "revision-runtime-beta",
            ["endpoint-chat-gamma"]);
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot(
                "service-platform-alpha",
                activeRevisionId: "revision-runtime-beta",
                endpoints: [CreateServiceEndpoint("endpoint-chat-gamma")]),
            Deployments = CreateDeploymentCatalog(
                "revision-runtime-beta",
                "deployment-revision-runtime-beta",
                "actor-revision-runtime-beta",
                artifact.ArtifactHash),
        };
        var servingPort = new FakeServiceServingQueryPort
        {
            ServingSet = CreateServingSet([
                CreateTarget(
                    "revision-runtime-beta",
                    ServiceServingState.Active,
                    allocationWeight: 100,
                    enabledEndpointIds: ["endpoint-chat-gamma"]),
            ]),
        };
        var revisionCatalog = CreateRevisionCatalogReader(
            preparedArtifacts: new Dictionary<string, PreparedServiceRevisionArtifact>(StringComparer.Ordinal)
            {
                ["revision-runtime-beta"] = artifact,
            });
        var invocationCatalog = new FakeServiceInvocationCatalogQueryReader(
            CreateInvocationCatalog(
                "service-platform-alpha",
                "revision-runtime-beta",
                "endpoint-chat-gamma",
                ServiceInvokeReadinessStatus.Unavailable,
                ServiceInvokeUnavailableReason.RevisionNotPrepared));
        var service = CreateService(
            lifecyclePort,
            servingPort,
            revisionCatalog,
            invocationCatalog);

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(
            "scope-authority-delta",
            "service-platform-alpha",
            ExpectedRevisionId: "revision-runtime-beta",
            ExpectedEndpointIds: ["endpoint-chat-gamma"]));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.InvocationCatalogNotReady);
        snapshot.InvokeReady.Should().BeFalse();
        snapshot.RevisionId.Should().Be("revision-runtime-beta");
        snapshot.DeploymentId.Should().Be("deployment-revision-runtime-beta");
    }

    [Fact]
    public async Task GetReadinessAsync_WhenPreparedArtifactMissing_ShouldReturnPreparedArtifactMissing()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a", activeRevisionId: "rev-ready"),
        };
        var servingPort = new FakeServiceServingQueryPort
        {
            ServingSet = CreateServingSet([
                CreateTarget("rev-ready", ServiceServingState.Active, allocationWeight: 100),
            ]),
        };
        var service = CreateService(lifecyclePort, servingPort, CreateRevisionCatalogReader(preparedArtifactRevisionIds: []));

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(
            "scope-a",
            "service-a",
            ExpectedRevisionId: "rev-ready"));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.PreparedArtifactMissing);
        snapshot.ServiceCatalogVisible.Should().BeTrue();
        snapshot.ServingSetVisible.Should().BeTrue();
        snapshot.EligibleServingTargetVisible.Should().BeTrue();
        snapshot.InvokeReady.Should().BeFalse();
        snapshot.RevisionId.Should().Be("rev-ready");
        snapshot.DeploymentId.Should().Be("deployment-rev-ready");
    }

    [Fact]
    public async Task GetReadinessAsync_WhenRevisionCatalogMissing_ShouldReturnPreparedArtifactMissing()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a", activeRevisionId: "rev-ready"),
        };
        var servingPort = new FakeServiceServingQueryPort
        {
            ServingSet = CreateServingSet([
                CreateTarget("rev-ready", ServiceServingState.Active, allocationWeight: 100),
            ]),
        };
        var service = CreateService(lifecyclePort, servingPort, new FakeServiceRevisionCatalogQueryReader(null));

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(
            "scope-a",
            "service-a",
            ExpectedRevisionId: "rev-ready"));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.PreparedArtifactMissing);
        snapshot.InvokeReady.Should().BeFalse();
        snapshot.RevisionId.Should().Be("rev-ready");
        snapshot.DeploymentId.Should().Be("deployment-rev-ready");
    }

    [Fact]
    public async Task GetReadinessAsync_WhenPreparedArtifactDoesNotExposeExpectedEndpoint_ShouldReturnPreparedArtifactMissing()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a", activeRevisionId: "rev-ready", endpoints: [CreateServiceEndpoint("chat")]),
        };
        var servingPort = new FakeServiceServingQueryPort
        {
            ServingSet = CreateServingSet([
                CreateTarget("rev-ready", ServiceServingState.Active, allocationWeight: 100, enabledEndpointIds: ["chat"]),
            ]),
        };
        var revisionCatalogReader = CreateRevisionCatalogReader(preparedArtifacts: new Dictionary<string, PreparedServiceRevisionArtifact>(StringComparer.Ordinal)
        {
            ["rev-ready"] = CreateArtifact("rev-ready", ["command"]),
        });
        var service = CreateService(lifecyclePort, servingPort, revisionCatalogReader);

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(
            "scope-a",
            "service-a",
            ExpectedRevisionId: "rev-ready",
            ExpectedEndpointIds: ["chat"]));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.PreparedArtifactMissing);
        snapshot.InvokeReady.Should().BeFalse();
        snapshot.RevisionId.Should().Be("rev-ready");
        snapshot.DeploymentId.Should().Be("deployment-rev-ready");
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("implementation")]
    public void TryGetPublishedPreparedArtifact_WhenArtifactBindingDoesNotMatchSnapshot_ShouldReject(
        string mismatch)
    {
        var artifact = CreateArtifact("rev-ready", ["chat"]);
        var implementationKind = artifact.ImplementationKind.ToString();
        if (string.Equals(mismatch, "identity", StringComparison.Ordinal))
        {
            artifact.Identity.ServiceId = "service-other";
            CanonicalizeArtifact(artifact);
        }
        else
        {
            implementationKind = ServiceImplementationKind.Workflow.ToString();
        }

        var catalog = new ServiceRevisionCatalogSnapshot(
            "scope-a:default:default:service-a",
            [
                new ServiceRevisionSnapshot(
                    "rev-ready",
                    implementationKind,
                    ServiceRevisionStatus.Published.ToString(),
                    artifact.ArtifactHash,
                    string.Empty,
                    [],
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    null,
                    PreparedArtifact: artifact),
            ],
            DateTimeOffset.UtcNow);

        catalog.TryGetPublishedPreparedArtifact(
                "rev-ready",
                artifact.ArtifactHash,
                out _)
            .Should().BeFalse();
    }

    [Fact]
    public async Task GetReadinessAsync_WhenWorkflowArtifactHasLegacyAdmissionPlan_ShouldReturnInvocationCatalogNotReady()
    {
        var artifact = CreateWorkflowArtifact(
            "rev-ready",
            WorkflowCapabilityAdmissionPlanIntegrity.LegacySchemaVersion,
            ExternalCapabilityExecutionMode.Interactive);
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a", activeRevisionId: "rev-ready", endpoints: [CreateServiceEndpoint("chat")]),
            Deployments = CreateDeploymentCatalog(
                "rev-ready",
                "deployment-rev-ready",
                "actor-rev-ready",
                artifact.ArtifactHash),
        };
        var servingPort = new FakeServiceServingQueryPort
        {
            ServingSet = CreateServingSet([
                CreateTarget("rev-ready", ServiceServingState.Active, allocationWeight: 100, enabledEndpointIds: ["chat"]),
            ]),
            TrafficView = CreateTrafficView([
                CreateTrafficEndpoint("chat", [CreateTrafficTarget("rev-ready", ServiceServingState.Active, allocationWeight: 100)]),
            ]),
        };
        var revisionCatalogReader = CreateRevisionCatalogReader(preparedArtifacts: new Dictionary<string, PreparedServiceRevisionArtifact>(StringComparer.Ordinal)
        {
            ["rev-ready"] = artifact,
        });
        var service = CreateService(lifecyclePort, servingPort, revisionCatalogReader);

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(
            "scope-a",
            "service-a",
            ExpectedRevisionId: "rev-ready",
            ExpectedEndpointIds: ["chat"]));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.InvocationCatalogNotReady);
        snapshot.InvokeReady.Should().BeFalse();
        snapshot.RevisionId.Should().Be("rev-ready");
        snapshot.DeploymentId.Should().Be("deployment-rev-ready");
    }

    [Fact]
    public async Task GetReadinessAsync_WhenTrafficViewHasStaleTargets_ShouldReturnTrafficViewTargetMissing()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a", activeRevisionId: "rev-ready"),
        };
        var servingPort = new FakeServiceServingQueryPort
        {
            ServingSet = CreateServingSet([
                CreateTarget("rev-ready", ServiceServingState.Active, allocationWeight: 100),
            ]),
            TrafficView = CreateTrafficView([
                CreateTrafficEndpoint("chat", [CreateTrafficTarget("rev-old", ServiceServingState.Active, allocationWeight: 100)]),
            ]),
        };
        var service = CreateService(lifecyclePort, servingPort);

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(
            "scope-a",
            "service-a",
            ExpectedRevisionId: "rev-ready"));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.TrafficViewTargetMissing);
        snapshot.ServiceCatalogVisible.Should().BeTrue();
        snapshot.ServingSetVisible.Should().BeTrue();
        snapshot.EligibleServingTargetVisible.Should().BeTrue();
        snapshot.InvokeReady.Should().BeFalse();
    }

    [Fact]
    public async Task GetReadinessAsync_WhenTrafficViewIsMissing_ShouldAllowInvokeFallbackToServingSet()
    {
        var artifact = CreateArtifact("rev-ready", ["chat", "command"]);
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a", activeRevisionId: "rev-ready"),
            Deployments = CreateDeploymentCatalog(
                "rev-ready",
                "deployment-rev-ready",
                "actor-rev-ready",
                artifact.ArtifactHash),
        };
        var servingPort = new FakeServiceServingQueryPort
        {
            ServingSet = CreateServingSet([
                CreateTarget("rev-ready", ServiceServingState.Active, allocationWeight: 100),
            ]),
        };
        var service = CreateService(
            lifecyclePort,
            servingPort,
            CreateRevisionCatalogReader(new Dictionary<string, PreparedServiceRevisionArtifact>
            {
                ["rev-ready"] = artifact,
            }));

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(
            "scope-a",
            "service-a",
            ExpectedRevisionId: "rev-ready"));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.Ready);
        snapshot.InvokeReady.Should().BeTrue();
    }

    [Fact]
    public async Task GetReadinessAsync_WhenExpectedRevisionTargetIsMissing_ShouldReturnEligibleServingTargetMissing()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a", activeRevisionId: "rev-new"),
        };
        var servingPort = new FakeServiceServingQueryPort
        {
            ServingSet = CreateServingSet([
                CreateTarget("rev-old", ServiceServingState.Active, allocationWeight: 100),
            ]),
        };
        var service = CreateService(lifecyclePort, servingPort);

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(
            "scope-a",
            "service-a",
            ExpectedRevisionId: "rev-new"));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.EligibleServingTargetMissing);
        snapshot.InvokeReady.Should().BeFalse();
        snapshot.RevisionId.Should().BeNull();
        snapshot.DeploymentId.Should().BeNull();
    }

    [Fact]
    public async Task GetReadinessAsync_WhenExpectedDeploymentTargetIsMissing_ShouldReturnEligibleServingTargetMissing()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a", activeRevisionId: "rev-ready"),
        };
        var servingPort = new FakeServiceServingQueryPort
        {
            ServingSet = CreateServingSet([
                CreateTarget("rev-ready", ServiceServingState.Active, allocationWeight: 100),
            ]),
        };
        var service = CreateService(lifecyclePort, servingPort);

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(
            "scope-a",
            "service-a",
            ExpectedRevisionId: "rev-ready",
            ExpectedDeploymentId: "deployment-new"));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.EligibleServingTargetMissing);
        snapshot.InvokeReady.Should().BeFalse();
    }

    [Fact]
    public async Task GetReadinessAsync_WhenExpectedDeploymentArtifactHashMatchesPublishedArtifact_ShouldReturnReady()
    {
        var artifact = CreateArtifact("rev-ready", ["chat"]);
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot(
                "service-a",
                activeRevisionId: "rev-ready",
                endpoints: [CreateServiceEndpoint("chat")]),
            Deployments = CreateDeploymentCatalog(
                "rev-ready",
                "deployment-rev-ready",
                "actor-rev-ready",
                artifact.ArtifactHash),
        };
        var servingPort = new FakeServiceServingQueryPort
        {
            ServingSet = CreateServingSet([
                CreateTarget(
                    "rev-ready",
                    ServiceServingState.Active,
                    allocationWeight: 100,
                    enabledEndpointIds: ["chat"]),
            ]),
        };
        var service = CreateService(
            lifecyclePort,
            servingPort,
            CreateRevisionCatalogReader(new Dictionary<string, PreparedServiceRevisionArtifact>
            {
                ["rev-ready"] = artifact,
            }));

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(
            "scope-a",
            "service-a",
            ExpectedRevisionId: "rev-ready",
            ExpectedDeploymentId: "deployment-rev-ready",
            ExpectedEndpointIds: ["chat"]));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.Ready);
        snapshot.InvokeReady.Should().BeTrue();
        lifecyclePort.GetDeploymentsCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetReadinessAsync_WhenSelectedDeploymentArtifactHashDiffersWithoutExpectedDeploymentId_ShouldNotReturnReadyAndShouldExposeFailure()
    {
        var artifact = CreateArtifact("rev-ready", ["chat"]);
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot(
                "service-a",
                activeRevisionId: "rev-ready",
                endpoints: [CreateServiceEndpoint("chat")]),
            Deployments = CreateDeploymentCatalog(
                "rev-ready",
                "deployment-rev-ready",
                "actor-rev-ready",
                "STALE-HASH",
                [new ServiceDeploymentActivationFailureSnapshot(
                    "rev-ready",
                    ServiceDeploymentActivationFailureCode.PreparedArtifactMissing,
                    "redacted",
                    DateTimeOffset.UtcNow,
                    "attempt-hash-mismatch")]),
        };
        var servingPort = new FakeServiceServingQueryPort
        {
            ServingSet = CreateServingSet([
                CreateTarget(
                    "rev-ready",
                    ServiceServingState.Active,
                    allocationWeight: 100,
                    enabledEndpointIds: ["chat"]),
            ]),
        };
        var service = CreateService(
            lifecyclePort,
            servingPort,
            CreateRevisionCatalogReader(new Dictionary<string, PreparedServiceRevisionArtifact>
            {
                ["rev-ready"] = artifact,
            }));

        var snapshot = await service.GetReadinessAsync(new ScopeBindingReadinessRequest(
            "scope-a",
            "service-a",
            ExpectedRevisionId: "rev-ready",
            ExpectedEndpointIds: ["chat"],
            ExpectedActivationAttemptId: "attempt-hash-mismatch"));

        snapshot.Status.Should().Be(ScopeBindingReadinessStatus.PreparedArtifactMissing);
        snapshot.InvokeReady.Should().BeFalse();
        snapshot.TerminalActivationFailureCode.Should()
            .Be(ServiceDeploymentActivationFailureCode.PreparedArtifactMissing);
        lifecyclePort.GetDeploymentsCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetReadinessAsync_ShouldBuildServiceIdentityFromNormalizedRequestAndAppId()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort
        {
            Service = CreateServiceSnapshot("service-a"),
        };
        var servingPort = new FakeServiceServingQueryPort
        {
            ServingSet = CreateServingSet([
                CreateTarget("rev-ready", ServiceServingState.Active, allocationWeight: 100),
            ]),
        };
        var service = CreateService(lifecyclePort, servingPort);

        await service.GetReadinessAsync(new ScopeBindingReadinessRequest(" scope-a ", " service-a ", " app-custom "));

        lifecyclePort.LastIdentity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-a",
            AppId = "app-custom",
            Namespace = ScopeWorkflowCapabilityOptions.FixedServiceNamespace,
            ServiceId = "service-a",
        });
        servingPort.LastIdentity.Should().BeEquivalentTo(lifecyclePort.LastIdentity);
    }

    private static ScopeBindingReadinessQueryService CreateService(
        FakeServiceLifecycleQueryPort lifecyclePort,
        FakeServiceServingQueryPort servingPort,
        FakeServiceRevisionCatalogQueryReader? revisionCatalogReader = null,
        FakeServiceInvocationCatalogQueryReader? invocationCatalogReader = null) =>
        new(
            lifecyclePort,
            servingPort,
            revisionCatalogReader ?? CreateRevisionCatalogReader(),
            invocationCatalogReader ?? CreateReadyInvocationCatalogReader(),
            Options.Create(DefaultOptions));

    private static FakeServiceInvocationCatalogQueryReader CreateReadyInvocationCatalogReader()
    {
        var entries = new[] { "rev-1", "rev-new", "rev-ready", "rev-zero" }
            .SelectMany(revisionId => new[] { "chat", "command" }.Select(endpointId =>
                CreateInvocationEntry(
                    revisionId,
                    endpointId,
                    ServiceInvokeReadinessStatus.Ready,
                    ServiceInvokeUnavailableReason.Unspecified)))
            .ToArray();
        return new FakeServiceInvocationCatalogQueryReader(new ServiceInvocationCatalogSnapshot(
            "scope-a:default:default:service-a",
            entries,
            DateTimeOffset.UtcNow,
            1,
            "event-invocation-ready",
            1,
            1,
            1));
    }

    private static ServiceInvocationCatalogSnapshot CreateInvocationCatalog(
        string serviceId,
        string revisionId,
        string endpointId,
        ServiceInvokeReadinessStatus status,
        ServiceInvokeUnavailableReason reason) =>
        new(
            $"scope-authority-delta:default:default:{serviceId}",
            [CreateInvocationEntry(revisionId, endpointId, status, reason)],
            DateTimeOffset.UtcNow,
            2,
            "event-invocation-observed",
            2,
            2,
            2);

    private static ServiceInvokeReadinessSnapshot CreateInvocationEntry(
        string revisionId,
        string endpointId,
        ServiceInvokeReadinessStatus status,
        ServiceInvokeUnavailableReason reason) =>
        new(
            "scope-a:default:default:service-a",
            endpointId,
            status,
            reason,
            revisionId,
            $"deployment-{revisionId}",
            $"actor-{revisionId}",
            DateTimeOffset.UtcNow,
            1,
            "event-invocation-entry",
            1,
            1,
            1);

    private static FakeServiceRevisionCatalogQueryReader CreateRevisionCatalogReader(
        IReadOnlyDictionary<string, PreparedServiceRevisionArtifact>? preparedArtifacts = null,
        IReadOnlyList<string>? preparedArtifactRevisionIds = null)
    {
        preparedArtifacts ??= (preparedArtifactRevisionIds ?? ["rev-1", "rev-new", "rev-ready", "rev-zero"])
            .ToDictionary(
                revisionId => revisionId,
                revisionId => CreateArtifact(revisionId, revisionId == "rev-ready" ? ["chat", "command"] : ["chat"]),
                StringComparer.Ordinal);

        return new FakeServiceRevisionCatalogQueryReader(new ServiceRevisionCatalogSnapshot(
            ServiceKey: "scope-a:default:default:service-a",
            Revisions: preparedArtifacts.Select(entry => new ServiceRevisionSnapshot(
                RevisionId: entry.Key,
                ImplementationKind: entry.Value.ImplementationKind.ToString(),
                Status: ServiceRevisionStatus.Published.ToString(),
                ArtifactHash: entry.Value.ArtifactHash,
                FailureReason: string.Empty,
                Endpoints: [],
                CreatedAt: DateTimeOffset.UtcNow,
                PreparedAt: DateTimeOffset.UtcNow,
                PublishedAt: DateTimeOffset.UtcNow,
                RetiredAt: null,
                PreparedArtifact: entry.Value)).ToArray(),
            UpdatedAt: DateTimeOffset.UtcNow));
    }

    private static PreparedServiceRevisionArtifact CreateArtifact(
        string revisionId,
        IReadOnlyList<string> endpointIds)
    {
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = new ServiceIdentity
            {
                TenantId = "scope-a",
                AppId = DefaultOptions.ServiceAppId,
                Namespace = DefaultOptions.ServiceNamespace,
                ServiceId = "service-a",
            },
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Static,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                StaticPlan = new StaticServiceDeploymentPlan
                {
                    AgentKind = "tests.static",
                    PreferredActorId = $"actor-{revisionId}",
                },
            },
        };
        artifact.Endpoints.Add(endpointIds.Select(endpointId => new ServiceEndpointDescriptor
        {
            EndpointId = endpointId,
            DisplayName = endpointId,
            Kind = ServiceEndpointKind.Chat,
            RequestTypeUrl = "type.googleapis.com/a.Request",
            ResponseTypeUrl = "type.googleapis.com/a.Response",
        }));
        return CanonicalizeArtifact(artifact);
    }

    private static PreparedServiceRevisionArtifact CreateWorkflowArtifact(
        string revisionId,
        string schemaVersion,
        ExternalCapabilityExecutionMode executionMode)
    {
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = new ServiceIdentity
            {
                TenantId = "scope-a",
                AppId = DefaultOptions.ServiceAppId,
                Namespace = DefaultOptions.ServiceNamespace,
                ServiceId = "service-a",
            },
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Workflow,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                WorkflowPlan = new WorkflowServiceDeploymentPlan
                {
                    ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                    WorkflowName = "document_file_extract",
                    WorkflowYaml = "name: document_file_extract\nsteps: []",
                    ExecutionMode = executionMode,
                    CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
                    {
                        SchemaVersion = schemaVersion,
                        ExecutionMode = executionMode,
                    },
                },
            },
        };
        artifact.Endpoints.Add(new ServiceEndpointDescriptor
        {
            EndpointId = "chat",
            DisplayName = "chat",
            Kind = ServiceEndpointKind.Chat,
            RequestTypeUrl = "type.googleapis.com/a.Request",
            ResponseTypeUrl = "type.googleapis.com/a.Response",
        });
        return CanonicalizeArtifact(artifact);
    }

    private static PreparedServiceRevisionArtifact CanonicalizeArtifact(
        PreparedServiceRevisionArtifact artifact)
    {
        artifact.ArtifactHash = string.Empty;
        artifact.ArtifactHash = Convert.ToHexString(SHA256.HashData(artifact.ToByteArray()));
        return artifact;
    }

    private static ServiceDeploymentCatalogSnapshot CreateDeploymentCatalog(
        string revisionId,
        string deploymentId,
        string primaryActorId,
        string artifactHash,
        IReadOnlyList<ServiceDeploymentActivationFailureSnapshot>? failures = null) =>
        new(
            "scope-a:default:default:service-a",
            [new ServiceDeploymentSnapshot(
                deploymentId,
                revisionId,
                primaryActorId,
                ServiceDeploymentStatus.Active.ToString(),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                artifactHash)],
            failures ?? [],
            DateTimeOffset.UtcNow);

    private static ServiceCatalogSnapshot CreateServiceSnapshot(
        string serviceId,
        string activeRevisionId = "rev-1",
        IReadOnlyList<ServiceEndpointSnapshot>? endpoints = null) =>
        new(
            ServiceKey: $"scope-a:default:default:{serviceId}",
            TenantId: "scope-a",
            AppId: DefaultOptions.ServiceAppId,
            Namespace: DefaultOptions.ServiceNamespace,
            ServiceId: serviceId,
            DisplayName: serviceId,
            DefaultServingRevisionId: activeRevisionId,
            ActiveServingRevisionId: string.Empty,
            DeploymentId: string.Empty,
            PrimaryActorId: string.Empty,
            DeploymentStatus: string.Empty,
            Endpoints: endpoints ?? [],
            PolicyIds: [],
            UpdatedAt: DateTimeOffset.UtcNow);

    private static ServiceServingSetSnapshot CreateServingSet(IReadOnlyList<ServiceServingTargetSnapshot> targets) =>
        new(
            ServiceKey: "scope-a:default:default:service-a",
            Generation: 1,
            ActiveRolloutId: "",
            Targets: targets,
            UpdatedAt: DateTimeOffset.UtcNow);

    private static ServiceEndpointSnapshot CreateServiceEndpoint(string endpointId) =>
        new(
            endpointId,
            endpointId,
            ServiceEndpointKind.Chat.ToString(),
            "type.googleapis.com/a.Request",
            "type.googleapis.com/a.Response",
            endpointId);

    private static ServiceServingTargetSnapshot CreateTarget(
        string revisionId,
        ServiceServingState state,
        int allocationWeight,
        string? deploymentId = null,
        IReadOnlyList<string>? enabledEndpointIds = null) =>
        new(
            DeploymentId: deploymentId ?? $"deployment-{revisionId}",
            RevisionId: revisionId,
            PrimaryActorId: $"actor-{revisionId}",
            AllocationWeight: allocationWeight,
            ServingState: state.ToString(),
            EnabledEndpointIds: enabledEndpointIds ?? []);

    private static ServiceTrafficViewSnapshot CreateTrafficView(IReadOnlyList<ServiceTrafficEndpointSnapshot> endpoints) =>
        new(
            ServiceKey: "scope-a:default:default:service-a",
            Generation: 1,
            ActiveRolloutId: "",
            Endpoints: endpoints,
            UpdatedAt: DateTimeOffset.UtcNow);

    private static ServiceTrafficEndpointSnapshot CreateTrafficEndpoint(
        string endpointId,
        IReadOnlyList<ServiceTrafficTargetSnapshot> targets) =>
        new(endpointId, targets);

    private static ServiceTrafficTargetSnapshot CreateTrafficTarget(
        string revisionId,
        ServiceServingState state,
        int allocationWeight,
        string? deploymentId = null) =>
        new(
            DeploymentId: deploymentId ?? $"deployment-{revisionId}",
            RevisionId: revisionId,
            PrimaryActorId: $"actor-{revisionId}",
            AllocationWeight: allocationWeight,
            ServingState: state.ToString());

    private sealed class FakeServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        public ServiceCatalogSnapshot? Service { get; init; }
        public ServiceDeploymentCatalogSnapshot? Deployments { get; init; }
        public Exception? DeploymentsFailure { get; init; }
        public ServiceIdentity? LastIdentity { get; private set; }
        public int GetDeploymentsCallCount { get; private set; }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastIdentity = identity.Clone();
            return Task.FromResult(Service);
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            Task.FromResult<ServiceRevisionCatalogSnapshot?>(null);

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(
            ServiceIdentity identity,
            CancellationToken ct = default)
        {
            GetDeploymentsCallCount++;
            if (DeploymentsFailure != null)
                throw DeploymentsFailure;
            return Task.FromResult(Deployments);
        }
    }

    private sealed class FakeServiceServingQueryPort : IServiceServingQueryPort
    {
        public ServiceServingSetSnapshot? ServingSet { get; init; }
        public ServiceTrafficViewSnapshot? TrafficView { get; init; }
        public ServiceIdentity? LastIdentity { get; private set; }
        public int GetServingSetCallCount { get; private set; }

        public Task<ServiceServingSetSnapshot?> GetServiceServingSetAsync(
            ServiceIdentity identity,
            CancellationToken ct = default)
        {
            GetServingSetCallCount++;
            LastIdentity = identity.Clone();
            return Task.FromResult(ServingSet);
        }

        public Task<ServiceRolloutSnapshot?> GetServiceRolloutAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            Task.FromResult<ServiceRolloutSnapshot?>(null);

        public Task<ServiceRolloutCommandObservationSnapshot?> GetServiceRolloutCommandObservationAsync(
            ServiceIdentity identity,
            string commandId,
            CancellationToken ct = default) =>
            Task.FromResult<ServiceRolloutCommandObservationSnapshot?>(null);

        public Task<ServiceTrafficViewSnapshot?> GetServiceTrafficViewAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            Task.FromResult(TrafficView);
    }

    private sealed class FakeServiceRevisionCatalogQueryReader : IServiceRevisionCatalogQueryReader
    {
        private readonly ServiceRevisionCatalogSnapshot? _catalog;

        public FakeServiceRevisionCatalogQueryReader(ServiceRevisionCatalogSnapshot? catalog)
        {
            _catalog = catalog;
        }

        public Task<ServiceRevisionCatalogSnapshot?> GetAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            Task.FromResult(_catalog);
    }

    private sealed class FakeServiceInvocationCatalogQueryReader : IServiceInvocationCatalogQueryReader
    {
        private readonly ServiceInvocationCatalogSnapshot? _catalog;

        public FakeServiceInvocationCatalogQueryReader(ServiceInvocationCatalogSnapshot? catalog)
        {
            _catalog = catalog;
        }

        public Task<ServiceInvocationCatalogSnapshot?> GetAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            Task.FromResult(_catalog);
    }
}
