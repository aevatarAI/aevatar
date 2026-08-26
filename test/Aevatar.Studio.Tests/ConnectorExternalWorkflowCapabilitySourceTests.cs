using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Application.Studio.DependencyInjection;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.ExternalCapabilities;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
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
            [],
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
            : [DeterministicConnector(connectorRef, enabled: false)];
        var source = new ConnectorExternalWorkflowCapabilitySource(
            new StubCatalogQueryPort(new StoredConnectorCatalog(
                string.Empty,
                string.Empty,
                true,
                connectors,
                Version: 4)),
            [new TestDeterministicComputeHandler()],
            new FixedTimeProvider());

        var result = await source.InspectAsync(
            Access(),
            HostRef(connectorRef, TestDeterministicComputeHandler.OperationId, "untrusted-digest"),
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
        var connector = DeterministicConnector("connector-home-alpha");
        var source = new ConnectorExternalWorkflowCapabilitySource(
            new StubCatalogQueryPort(new StoredConnectorCatalog(
                string.Empty,
                string.Empty,
                true,
                [connector],
                Version: 8)),
            [new TestDeterministicComputeHandler()],
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

    [Fact]
    public async Task ListAsync_ShouldPublishAlignedDeterministicHostCallback_AsReadOnly()
    {
        var handler = new TestDeterministicComputeHandler();
        var source = DeterministicSource(handler);

        var descriptor = (await source.ListAsync(Access(), CancellationToken.None))
            .Capabilities.Should().ContainSingle().Subject;

        descriptor.Selector.HostConnector.OperationId.Should().Be(TestDeterministicComputeHandler.OperationId);
        descriptor.Selector.HostConnector.ContractDigest.Should().MatchRegex("^[0-9a-f]{64}$");
        descriptor.ReadOnly.Should().BeTrue();
        descriptor.Destructive.Should().BeFalse();
        var readiness = await source.InspectAsync(
            Access(),
            descriptor.Selector,
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);
        readiness.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
    }

    [Theory]
    [InlineData("different_algorithm")]
    [InlineData("sha256_utf8,different_algorithm")]
    public async Task ListAsync_ShouldNotPublish_WhenCatalogAndRegisteredAlgorithmsDiffer(
        string configuredOperations)
    {
        var handler = new TestDeterministicComputeHandler();
        var connector = DeterministicConnector(
            "deterministic-hash",
            allowedOperations: configuredOperations.Split(','));
        var source = new ConnectorExternalWorkflowCapabilitySource(
            new StubCatalogQueryPort(Catalog(connector)),
            [handler],
            new FixedTimeProvider());

        var discovery = await source.ListAsync(Access(), CancellationToken.None);

        discovery.Capabilities.Should().BeEmpty();
        discovery.CandidateCount.Should().Be(0);
    }

    [Fact]
    public async Task ListAsync_ShouldNotPublish_WhenDeterministicHandlerNameIsMissing()
    {
        var handler = new TestDeterministicComputeHandler();
        var connector = DeterministicConnector("deterministic-hash", handlerName: string.Empty);
        var source = new ConnectorExternalWorkflowCapabilitySource(
            new StubCatalogQueryPort(Catalog(connector)),
            [handler],
            new FixedTimeProvider());

        var discovery = await source.ListAsync(Access(), CancellationToken.None);

        discovery.Capabilities.Should().BeEmpty();
        discovery.CandidateCount.Should().Be(0);
    }

    [Fact]
    public async Task InspectAsync_ShouldReportContractDrift_AfterAlgorithmVersionBump()
    {
        var versionOne = DeterministicSource(new TestDeterministicComputeHandler(version: 1));
        var savedSelector = (await versionOne.ListAsync(Access(), CancellationToken.None))
            .Capabilities.Single().Selector;
        var versionTwo = DeterministicSource(new TestDeterministicComputeHandler(version: 2));

        var readiness = await versionTwo.InspectAsync(
            Access(),
            savedSelector,
            ExternalCapabilityExecutionMode.Durable,
            CancellationToken.None);

        readiness.Status.Should().Be(ExternalCapabilityReadinessStatus.ContractDrift);
        readiness.Blockers.Should().ContainSingle().Which.Code.Should().Be("CONNECTOR_CONTRACT_DRIFT");
    }

    [Fact]
    public async Task WorkflowAdmission_ShouldAcceptListedDeterministicHostCallback()
    {
        var source = DeterministicSource(new TestDeterministicComputeHandler());
        var selector = (await source.ListAsync(Access(), CancellationToken.None))
            .Capabilities.Single().Selector.HostConnector;
        var yaml = $$"""
                   name: deterministic-workflow
                   steps:
                     - id: hash
                       type: connector_call
                       parameters:
                         connector: {{selector.ConnectorCapabilityRef}}
                         operation: {{selector.OperationId}}
                         contract_digest: {{selector.ContractDigest}}
                   """;
        var readiness = new ExternalWorkflowCapabilityReadinessService([source]);
        var admission = new WorkflowExternalCapabilityAdmissionService(
            new RealWorkflowDefinitionParser(),
            readiness,
            new FixedTimeProvider());

        var plan = await admission.AdmitAsync(new WorkflowExternalCapabilityAdmissionRequest(
            Access(),
            yaml,
            new Dictionary<string, string>(),
            "deterministic-test",
            ExternalCapabilityExecutionMode.Interactive));

        var invocation = plan.InvocationAdmissions.Should().ContainSingle().Subject;
        invocation.CallSiteId.Should().Be("deterministic-workflow/hash");
        invocation.Capability.HostConnector.Should().BeEquivalentTo(selector);
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
                []),
            new StoredHostCallbackConnectorConfig(string.Empty, [], []));

    private static ConnectorExternalWorkflowCapabilitySource DeterministicSource(
        IDeterministicComputeHandler handler) =>
        new(
            new StubCatalogQueryPort(Catalog(DeterministicConnector("deterministic-hash"))),
            [handler],
            new FixedTimeProvider());

    private static StoredConnectorCatalog Catalog(params StoredConnectorDefinition[] connectors) =>
        new(string.Empty, string.Empty, true, connectors, Version: 19);

    private static StoredConnectorDefinition DeterministicConnector(
        string name,
        bool enabled = true,
        IReadOnlyList<string>? allowedOperations = null,
        string? handlerName = TestDeterministicComputeHandler.HandlerName) =>
        new(
            name,
            "host_callback",
            enabled,
            30_000,
            0,
            new StoredHttpConnectorConfig(
                string.Empty,
                [],
                [],
                [],
                new Dictionary<string, string>(),
                new StoredConnectorAuthConfig("", "", "", "", "", "", "", "")),
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
                []),
            new StoredHostCallbackConnectorConfig(
                handlerName ?? string.Empty,
                allowedOperations ?? [TestDeterministicComputeHandler.OperationId],
                ["text"]));

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

    private sealed class TestDeterministicComputeHandler(int version = 1) : IDeterministicComputeHandler
    {
        public const string HandlerName = "deterministic-test";
        public const string OperationId = "sha256_utf8";

        public string Name => HandlerName;

        public IReadOnlyList<DeterministicAlgorithmDescriptor> Algorithms { get; } =
        [
            new(
                OperationId,
                version,
                $"sha256:{new string('a', 64)}",
                $"sha256:{new string('b', 64)}"),
        ];

        public Task<HostCallbackConnectorResponse> HandleAsync(
            HostCallbackConnectorRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RealWorkflowDefinitionParser : IWorkflowDefinitionParser
    {
        private readonly WorkflowParser _parser = new();

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var workflow = _parser.Parse(workflowYaml);
                return Task.FromResult(WorkflowYamlParseResult.Success(
                    workflow.Name,
                    WorkflowAuthorizationDependencyEvaluator.Evaluate(workflow)));
            }
            catch (WorkflowExternalCapabilityValidationException exception)
            {
                return Task.FromResult(WorkflowYamlParseResult.Invalid(
                    exception.Message,
                    exception.Readiness));
            }
            catch (Exception exception)
            {
                return Task.FromResult(WorkflowYamlParseResult.Invalid(exception.Message));
            }
        }

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
