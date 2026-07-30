using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Application.Studio.DependencyInjection;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class ConnectorExternalWorkflowCapabilitySourceTests
{
    [Fact]
    public void AddStudioApplication_ShouldRegisterConnectorCapabilitySource()
    {
        var services = new ServiceCollection();

        services.AddStudioApplication();

        services.Should().Contain(static descriptor =>
            descriptor.ServiceType == typeof(IExternalWorkflowCapabilitySource) &&
            descriptor.ImplementationType == typeof(ConnectorExternalWorkflowCapabilitySource));
    }

    [Fact]
    public async Task ListAsync_ShouldKeepAllConfiguredAuthModesHostOwned()
    {
        var catalog = new StoredConnectorCatalog(
            string.Empty,
            string.Empty,
            true,
            [
                Connector("connector-public-alpha", authType: string.Empty),
                Connector("connector-client-alpha", authType: "client_credentials"),
                Connector("connector-secret-alpha", authType: "secret_ref_header"),
            ],
            Version: 17);
        var source = new ConnectorExternalWorkflowCapabilitySource(
            new StubCatalogQueryPort(catalog),
            new FixedTimeProvider());

        var discovery = await source.ListAsync(Access(), CancellationToken.None);
        var descriptors = discovery.Capabilities;

        discovery.CandidateCount.Should().Be(3);
        discovery.RejectedCount.Should().Be(0);
        discovery.Diagnostics.Should().BeEmpty();
        descriptors.Should().HaveCount(3);
        descriptors.Should().OnlyContain(static item => item.Selector.SelectorCase ==
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.HostConnector);
        descriptors.Select(static item => item.Selector.HostConnector.ConnectorCapabilityRef)
            .Should().BeEquivalentTo(
                "connector-public-alpha",
                "connector-client-alpha",
                "connector-secret-alpha");
        descriptors.Should().OnlyContain(static item =>
            item.Source.SourceKind == ExternalCapabilitySourceKind.ConnectorCatalog &&
            item.Source.SourceVersion == 17);

        foreach (var descriptor in descriptors)
        {
            var readiness = await source.InspectAsync(
                Access(),
                descriptor.Selector,
                ExternalCapabilityExecutionMode.Durable,
                CancellationToken.None);
            readiness.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        }
    }

    [Theory]
    [InlineData("connector-missing-alpha", true, "CONNECTOR_NOT_FOUND")]
    [InlineData("connector-disabled-alpha", false, "CONNECTOR_DISABLED")]
    public async Task InspectAsync_ShouldFailClosed_ForMissingOrDisabledConnector(
        string connectorRef,
        bool omitFromCatalog,
        string expectedCode)
    {
        var connectors = omitFromCatalog
            ? Array.Empty<StoredConnectorDefinition>()
            : [Connector(connectorRef, enabled: false)];
        var source = new ConnectorExternalWorkflowCapabilitySource(
            new StubCatalogQueryPort(new StoredConnectorCatalog(
                string.Empty,
                string.Empty,
                true,
                connectors,
                Version: 4)),
            new FixedTimeProvider());

        var result = await source.InspectAsync(
            Access(),
            HostRef(connectorRef, "GET", "untrusted-digest"),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.ConnectorNotFound);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be(expectedCode);
        result.Remediations.Should().ContainSingle().Which.ActionKind
            .Should().Be(ExternalCapabilityRemediationActionKind.ConfigureConnector);
    }

    [Fact]
    public async Task InspectAsync_ShouldRejectContractDrift()
    {
        var connector = Connector("connector-home-alpha");
        var source = new ConnectorExternalWorkflowCapabilitySource(
            new StubCatalogQueryPort(new StoredConnectorCatalog(
                string.Empty,
                string.Empty,
                true,
                [connector],
                Version: 8)),
            new FixedTimeProvider());
        var descriptor = (await source.ListAsync(Access(), CancellationToken.None))
            .Capabilities.Single();
        var forged = descriptor.Selector.Clone();
        forged.HostConnector.ContractDigest = "changed-contract-digest";

        var result = await source.InspectAsync(
            Access(),
            forged,
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.ContractDrift);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be("CONNECTOR_CONTRACT_DRIFT");
    }

    private static ExternalWorkflowCapabilityAccessContext Access() =>
        new("scope-alpha", "caller-alpha");

    private static ExternalWorkflowCapabilitySelector HostRef(
        string connectorRef,
        string operationId,
        string digest) =>
        new()
        {
            HostConnector = new HostConnectorCapabilityRef
            {
                ConnectorCapabilityRef = connectorRef,
                OperationId = operationId,
                ContractDigest = digest,
            },
        };

    private static StoredConnectorDefinition Connector(
        string name,
        string authType = "",
        bool enabled = true) =>
        new(
            name,
            "http",
            enabled,
            30_000,
            1,
            new StoredHttpConnectorConfig(
                "https://connector.invalid",
                ["GET"],
                ["/states/{entity_id}"],
                ["entity_id"],
                new Dictionary<string, string>(),
                new StoredConnectorAuthConfig(
                    authType,
                    "https://identity.invalid/oauth/token",
                    "connector-client-alpha",
                    string.Empty,
                    "states.read",
                    "vault://connector/client-credential",
                    "X-Connector-Credential",
                    "Bearer ")),
            new StoredCliConnectorConfig(
                string.Empty,
                [],
                [],
                [],
                string.Empty,
                new Dictionary<string, string>()),
            new StoredMcpConnectorConfig(
                string.Empty,
                string.Empty,
                string.Empty,
                [],
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                new StoredConnectorAuthConfig("", "", "", "", "", "", "", ""),
                string.Empty,
                [],
                []));

    private sealed class StubCatalogQueryPort(StoredConnectorCatalog catalog) : IConnectorCatalogQueryPort
    {
        public Task<StoredConnectorCatalog> GetConnectorCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(catalog);

        public Task<StoredConnectorDraft> GetConnectorDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 7, 21, 10, 0, 0, TimeSpan.Zero);
    }
}
