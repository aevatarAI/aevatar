using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.ExternalCapabilities;
using Aevatar.Workflow.Application.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Application.Tests;

public sealed class ExternalWorkflowCapabilityReadinessServiceTests
{
    [Fact]
    public void AddWorkflowApplication_ShouldRegisterExternalCapabilityQueryPorts()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication();

        services.Should().Contain(static descriptor =>
            descriptor.ServiceType == typeof(IExternalWorkflowCapabilityListPort));
        services.Should().Contain(static descriptor =>
            descriptor.ServiceType == typeof(IExternalWorkflowCapabilityReadinessPort));
    }

    [Fact]
    public async Task ListAsync_ShouldFanOutWithoutCollapsingExactIdentities()
    {
        var connector = Descriptor(new ExternalWorkflowCapabilityRef
        {
            HostConnector = new HostConnectorCapabilityRef
            {
                ConnectorCapabilityRef = "connector-home-alpha",
                OperationId = "GET",
                ContractDigest = "connector-digest",
            },
        });
        var nyxId = Descriptor(new ExternalWorkflowCapabilityRef
        {
            NyxIdUserService = new NyxIdUserServiceCapabilityRef
            {
                UserServiceId = "us-home-alpha",
                ServiceSlugSnapshot = "home-assistant",
                OperationId = "get-state",
                HttpMethod = "GET",
                PathTemplate = "/states/{entity_id}",
                ContractDigest = "nyxid-digest",
            },
        });
        var connectorSource = new StubSource(
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.HostConnector,
            [connector]);
        var nyxIdSource = new StubSource(
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService,
            [nyxId]);
        var service = new ExternalWorkflowCapabilityReadinessService([nyxIdSource, connectorSource]);
        var access = Access();

        var result = await service.ListAsync(
            new ListExternalWorkflowCapabilitiesRequest(access),
            CancellationToken.None);

        result.Should().HaveCount(2);
        result.Select(static item => item.Capability.CapabilityCase).Should().Contain([
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.HostConnector,
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService,
        ]);
        result.Single(item => item.Capability.CapabilityCase ==
                              ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService)
            .Capability.NyxIdUserService.UserServiceId.Should().Be("us-home-alpha");
        connectorSource.ListCalls.Should().Be(1);
        nyxIdSource.ListCalls.Should().Be(1);
    }

    [Fact]
    public async Task InspectAsync_ShouldRouteOnlyToCapabilityOwnerSource()
    {
        var selected = new ExternalWorkflowCapabilityRef
        {
            NyxIdUserService = new NyxIdUserServiceCapabilityRef
            {
                UserServiceId = "us-home-alpha",
                ServiceSlugSnapshot = "home-assistant",
                OperationId = "get-state",
                HttpMethod = "GET",
                PathTemplate = "/states/{entity_id}",
                ContractDigest = "nyxid-digest",
            },
        };
        var connectorSource = new StubSource(
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.HostConnector,
            []);
        var nyxIdSource = new StubSource(
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService,
            [],
            new ExternalCapabilityReadiness
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                Status = ExternalCapabilityReadinessStatus.Ready,
                SelectedCapability = selected,
            });
        var service = new ExternalWorkflowCapabilityReadinessService([connectorSource, nyxIdSource]);

        var result = await service.InspectAsync(
            new InspectExternalWorkflowCapabilityReadinessRequest(
                Access(),
                selected,
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        result.SelectedCapability.NyxIdUserService.UserServiceId.Should().Be("us-home-alpha");
        nyxIdSource.InspectCalls.Should().Be(1);
        connectorSource.InspectCalls.Should().Be(0);
    }

    [Fact]
    public async Task InspectAsync_ShouldReturnTypedSelectionBlocker_WhenCapabilityIsMissing()
    {
        var service = new ExternalWorkflowCapabilityReadinessService([]);

        var result = await service.InspectAsync(
            new InspectExternalWorkflowCapabilityReadinessRequest(
                Access(),
                new ExternalWorkflowCapabilityRef(),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.SelectionRequired);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be("CAPABILITY_SELECTION_REQUIRED");
        result.Remediations.Should().ContainSingle().Which.ActionKind
            .Should().Be(ExternalCapabilityRemediationActionKind.SelectCapability);
    }

    [Fact]
    public void AccessContext_ShouldNeverRenderRuntimeCredentials()
    {
        var access = new ExternalWorkflowCapabilityAccessContext(
            "scope-alpha",
            "caller-alpha",
            "runtime-caller-credential",
            "runtime-organization-credential");

        access.ToString().Should()
            .Contain("scope-alpha")
            .And.Contain("caller-alpha")
            .And.NotContain("runtime-caller-credential")
            .And.NotContain("runtime-organization-credential");
    }

    private static ExternalWorkflowCapabilityAccessContext Access() =>
        new("scope-alpha", "caller-alpha", "runtime-caller-credential");

    private static ExternalWorkflowCapabilityDescriptor Descriptor(ExternalWorkflowCapabilityRef capability) =>
        new()
        {
            Capability = capability,
            DisplayName = capability.CapabilityCase.ToString(),
        };

    private sealed class StubSource(
        ExternalWorkflowCapabilityRef.CapabilityOneofCase capabilityKind,
        IReadOnlyList<ExternalWorkflowCapabilityDescriptor> descriptors,
        ExternalCapabilityReadiness? readiness = null) : IExternalWorkflowCapabilitySource
    {
        public ExternalWorkflowCapabilityRef.CapabilityOneofCase CapabilityKind => capabilityKind;

        public int ListCalls { get; private set; }

        public int InspectCalls { get; private set; }

        public Task<IReadOnlyList<ExternalWorkflowCapabilityDescriptor>> ListAsync(
            ExternalWorkflowCapabilityAccessContext access,
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(descriptors);
        }

        public Task<ExternalCapabilityReadiness> InspectAsync(
            ExternalWorkflowCapabilityAccessContext access,
            ExternalWorkflowCapabilityRef capability,
            ExternalCapabilityExecutionMode executionMode,
            CancellationToken cancellationToken = default)
        {
            InspectCalls++;
            return Task.FromResult(readiness ?? new ExternalCapabilityReadiness
            {
                ExecutionMode = executionMode,
                Status = ExternalCapabilityReadinessStatus.Ready,
                SelectedCapability = capability,
            });
        }
    }
}
