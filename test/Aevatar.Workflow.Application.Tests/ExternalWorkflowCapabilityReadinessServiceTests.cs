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
    public async Task ListAsync_ShouldPreserveTypedDiscoveryDiagnosticsAndCounts()
    {
        var discovery = new ExternalWorkflowCapabilityDiscoveryResult
        {
            CandidateCount = 2,
            RejectedCount = 1,
        };
        discovery.Capabilities.Add(Descriptor(NyxIdSelector()));
        discovery.Diagnostics.Add(new ExternalCapabilityDiscoveryDiagnostic
        {
            Code = ExternalCapabilityDiscoveryDiagnosticCode.GenericProxyRejected,
            Count = 1,
            SafeMessage = "Generic proxy services are not eligible for workflow admission.",
        });
        var service = new ExternalWorkflowCapabilityReadinessService(
            [new StubSource(
                ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation,
                [],
                discovery: discovery)]);

        var result = await service.ListAsync(
            new ListExternalWorkflowCapabilitiesRequest(Access()),
            CancellationToken.None);

        result.Capabilities.Should().ContainSingle();
        result.CandidateCount.Should().Be(2);
        result.RejectedCount.Should().Be(1);
        result.Diagnostics.Should().ContainSingle().Which.Code.Should()
            .Be(ExternalCapabilityDiscoveryDiagnosticCode.GenericProxyRejected);
    }

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
        var connector = Descriptor(new ExternalWorkflowCapabilitySelector
        {
            HostConnector = new HostConnectorCapabilityRef
            {
                ConnectorCapabilityRef = "connector-home-alpha",
                OperationId = "GET",
                ContractDigest = "connector-digest",
            },
        });
        var nyxId = Descriptor(NyxIdSelector());
        var connectorSource = new StubSource(
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.HostConnector,
            [connector]);
        var nyxIdSource = new StubSource(
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation,
            [nyxId]);
        var service = new ExternalWorkflowCapabilityReadinessService([nyxIdSource, connectorSource]);
        var access = Access();

        var result = await service.ListAsync(
            new ListExternalWorkflowCapabilitiesRequest(access),
            CancellationToken.None);

        result.Capabilities.Should().HaveCount(2);
        result.Capabilities.Select(static item => item.Selector.SelectorCase).Should().Contain([
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.HostConnector,
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation,
        ]);
        result.Capabilities.Single(item => item.Selector.SelectorCase ==
                              ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation)
            .Selector.NyxIdOperation.UserServiceId.Should().Be("us-home-alpha");
        connectorSource.ListCalls.Should().Be(1);
        nyxIdSource.ListCalls.Should().Be(1);
    }

    [Fact]
    public async Task ListAsync_ShouldOrderExplicitRequestsByExactRequestIdentity()
    {
        var source = new StubSource(
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest,
            [
                Descriptor(NyxIdRequestSelector("usvc-zeta", "/api/zeta")),
                Descriptor(NyxIdRequestSelector("usvc-alpha", "/api/alpha")),
            ]);
        var service = new ExternalWorkflowCapabilityReadinessService([source]);

        var result = await service.ListAsync(
            new ListExternalWorkflowCapabilitiesRequest(Access()),
            CancellationToken.None);

        result.Capabilities.Select(static descriptor =>
                descriptor.Selector.NyxIdRequest.UserServiceId)
            .Should().Equal("usvc-alpha", "usvc-zeta");
    }

    [Fact]
    public async Task ListAsync_ShouldFailClosedForUnknownSelectorVariant()
    {
        var source = new StubSource(
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.None,
            [Descriptor(new ExternalWorkflowCapabilitySelector())]);
        var service = new ExternalWorkflowCapabilityReadinessService([source]);

        var action = () => service.ListAsync(
            new ListExternalWorkflowCapabilitiesRequest(Access()),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*selector*");
    }

    [Fact]
    public void DiscoveryDescriptor_ShouldNeverPublishServerDerivedProofAsAuthorInput()
    {
        var descriptor = Descriptor(NyxIdSelector());

        NyxIdOperationSelector.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .Should().BeEquivalentTo(["user_service_id", "endpoint_id"]);
        descriptor.Selector.NyxIdOperation.UserServiceId.Should().Be("us-home-alpha");
        descriptor.Capability.Should().BeNull();
    }

    [Fact]
    public async Task InspectAsync_ShouldRouteOnlyToCapabilityOwnerSource()
    {
        var selector = NyxIdSelector();
        var proof = new ExternalWorkflowCapabilityRef
        {
            NyxIdUserService = new NyxIdUserServiceCapabilityRef
            {
                UserServiceId = "us-home-alpha",
                ServiceSlugSnapshot = "home-assistant",
                EndpointId = "get-state",
                HttpMethod = "GET",
                PathTemplate = "/states/{entity_id}",
                ContractDigest = "nyxid-digest",
            },
        };
        var connectorSource = new StubSource(
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.HostConnector,
            []);
        var nyxIdSource = new StubSource(
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation,
            [],
            new ExternalCapabilityReadiness
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                Status = ExternalCapabilityReadinessStatus.Ready,
                SelectedSelector = selector,
                SelectedCapability = proof,
            });
        var service = new ExternalWorkflowCapabilityReadinessService([connectorSource, nyxIdSource]);

        var result = await service.InspectAsync(
            new InspectExternalWorkflowCapabilityReadinessRequest(
                Access(),
                selector,
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        result.SelectedCapability.NyxIdUserService.UserServiceId.Should().Be("us-home-alpha");
        result.SelectedCapability.NyxIdUserService.PathTemplate.Should().Be("/states/{entity_id}");
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
                new ExternalWorkflowCapabilitySelector(),
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
            NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                "runtime-caller-credential"),
            "runtime-organization-credential");

        access.ToString().Should()
            .Contain("scope-alpha")
            .And.Contain("caller-alpha")
            .And.NotContain("runtime-caller-credential")
            .And.NotContain("runtime-organization-credential");
    }

    private static ExternalWorkflowCapabilityAccessContext Access() =>
        new(
            "scope-alpha",
            "caller-alpha",
            NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                "runtime-caller-credential"));

    private static ExternalWorkflowCapabilitySelector NyxIdSelector() =>
        new()
        {
            NyxIdOperation = new NyxIdOperationSelector
            {
                UserServiceId = "us-home-alpha",
                EndpointId = "get-state",
            },
        };

    private static ExternalWorkflowCapabilitySelector NyxIdRequestSelector(
        string userServiceId,
        string pathTemplate) =>
        new()
        {
            NyxIdRequest = new NyxIdRequestSelector
            {
                UserServiceId = userServiceId,
                Method = NyxIdRequestMethod.Get,
                PathTemplate = pathTemplate,
                BodyMode = NyxIdRequestBodyMode.None,
                ResponseMode = NyxIdRequestResponseMode.Text,
            },
        };

    private static ExternalWorkflowCapabilityDescriptor Descriptor(ExternalWorkflowCapabilitySelector selector) =>
        new()
        {
            Selector = selector,
            DisplayName = selector.SelectorCase.ToString(),
        };

    private sealed class StubSource(
        ExternalWorkflowCapabilitySelector.SelectorOneofCase selectorKind,
        IReadOnlyList<ExternalWorkflowCapabilityDescriptor> descriptors,
        ExternalCapabilityReadiness? readiness = null,
        ExternalWorkflowCapabilityDiscoveryResult? discovery = null) : IExternalWorkflowCapabilitySource
    {
        public ExternalWorkflowCapabilitySelector.SelectorOneofCase SelectorKind => selectorKind;

        public int ListCalls { get; private set; }

        public int InspectCalls { get; private set; }

        public Task<ExternalWorkflowCapabilityDiscoveryResult> ListAsync(
            ExternalWorkflowCapabilityAccessContext access,
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            if (discovery is not null)
                return Task.FromResult(discovery);
            var result = new ExternalWorkflowCapabilityDiscoveryResult
            {
                CandidateCount = descriptors.Count,
            };
            result.Capabilities.Add(descriptors);
            return Task.FromResult(result);
        }

        public Task<ExternalCapabilityReadiness> InspectAsync(
            ExternalWorkflowCapabilityAccessContext access,
            ExternalWorkflowCapabilitySelector selector,
            ExternalCapabilityExecutionMode executionMode,
            CancellationToken cancellationToken = default)
        {
            InspectCalls++;
            return Task.FromResult(readiness ?? new ExternalCapabilityReadiness
            {
                ExecutionMode = executionMode,
                Status = ExternalCapabilityReadinessStatus.Ready,
                SelectedSelector = selector,
            });
        }
    }
}
