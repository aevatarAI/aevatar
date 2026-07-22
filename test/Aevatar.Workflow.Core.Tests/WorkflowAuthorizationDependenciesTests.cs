using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowAuthorizationDependenciesTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("name: [")]
    public void EvaluateAuthorizationDependencies_ShouldFailClosedForInvalidDefinition(string yaml)
    {
        var result = new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        result.Should().BeNull();
    }

    [Fact]
    public void EvaluateAuthorizationDependencies_ShouldCollectExactNestedConnectorOperation()
    {
        var yaml = """
            name: wf-alpha
            roles:
              - id: analyst
                name: Analyst
                connectors: [ "Calendar" ]
            steps:
              - id: parent
                type: sequence
                children:
                  - id: nested-call
                    type: connector_call
                    parameters:
                      connector: connector-home-alpha
                      operation: send-summary
                      contract_digest: sha256:connector-home-v1
            """;

        var result = new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        result.Should().NotBeNull();
        result!.ExternalCapabilities.Should().ContainSingle();
        result.ExternalCapabilities[0].CapabilityCase.Should()
            .Be(ExternalWorkflowCapabilityRef.CapabilityOneofCase.HostConnector);
        result.ExternalCapabilities[0].HostConnector.Should().BeEquivalentTo(
            new HostConnectorCapabilityRef
            {
                ConnectorCapabilityRef = "connector-home-alpha",
                OperationId = "send-summary",
                ContractDigest = "sha256:connector-home-v1",
            });
        result.ServiceGrantPolicy.Should().Be(WorkflowServiceGrantPolicy.NotRequiredNoExternalService);
    }

    [Fact]
    public void EvaluateAuthorizationDependencies_ShouldPreserveExactNyxIdInstancesWithDuplicateSlug()
    {
        var yaml = """
            name: wf-alpha
            roles: []
            steps:
              - id: proxy-a
                type: tool_call
                parameters:
                  tool: nyxid_proxy
                  arguments: '{"service_id":"us-home-alpha","slug":"home-assistant","operation_id":"list-items","method":"GET","path":"/api/items","contract_digest":"sha256:home-v1"}'
              - id: nested
                type: sequence
                children:
                  - id: proxy-b
                    type: tool_call
                    parameters:
                      tool: nyxid_proxy
                      arguments: '{"service_id":"us-home-beta","slug":"home-assistant","operation_id":"list-items","method":"GET","path":"/api/items","contract_digest":"sha256:home-v1"}'
            """;

        var result = new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        result.Should().NotBeNull();
        result!.ExternalCapabilities.Should().HaveCount(2);
        result.ExternalCapabilities.Select(static capability =>
                capability.NyxIdUserService.UserServiceId)
            .Should().Equal("us-home-alpha", "us-home-beta");
        result.ExternalCapabilities.Should().OnlyContain(static capability =>
            capability.NyxIdUserService.ServiceSlugSnapshot == "home-assistant" &&
            capability.NyxIdUserService.OperationId == "list-items" &&
            capability.NyxIdUserService.HttpMethod == "GET" &&
            capability.NyxIdUserService.PathTemplate == "/api/items" &&
            capability.NyxIdUserService.ContractDigest == "sha256:home-v1");
        result.ServiceGrantPolicy.Should().Be(WorkflowServiceGrantPolicy.Required);
    }

    [Theory]
    [InlineData("{\"service_id\":\"${service_id}\",\"slug\":\"home-assistant\",\"operation_id\":\"list-items\",\"method\":\"GET\",\"path\":\"/api/items\",\"contract_digest\":\"sha256:home-v1\"}", "service_id")]
    [InlineData("{\"slug\":\"home-assistant\",\"operation_id\":\"list-items\",\"method\":\"GET\",\"path\":\"/api/items\",\"contract_digest\":\"sha256:home-v1\"}", "service_id")]
    [InlineData("{\"service\":\"us-home-alpha\",\"slug\":\"home-assistant\",\"operation_id\":\"list-items\",\"method\":\"GET\",\"path\":\"/api/items\",\"contract_digest\":\"sha256:home-v1\"}", "service_id")]
    [InlineData("{\"service_id\":\"us-home-alpha\",\"slug\":\"home-assistant\",\"method\":\"GET\",\"path\":\"/api/items\",\"contract_digest\":\"sha256:home-v1\"}", "operation_id")]
    [InlineData("{\"service_id\":\"us-home-alpha\",\"slug\":\"home-assistant\",\"operation_id\":\"list-items\",\"method\":\"${method}\",\"path\":\"/api/items\",\"contract_digest\":\"sha256:home-v1\"}", "method")]
    public void EvaluateAuthorizationDependencies_ShouldRejectUnresolvedNyxIdIdentity(
        string arguments,
        string expectedField)
    {
        var yaml = $$"""
            name: wf-alpha
            roles: []
            steps:
              - id: proxy
                type: tool_call
                parameters:
                  tool: nyxid_proxy
                  arguments: '{{arguments}}'
            """;

        var act = () => new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        act.Should().Throw<WorkflowExternalCapabilityValidationException>()
            .WithMessage($"*{expectedField}*");
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Cookie")]
    [InlineData("X-API-Key")]
    [InlineData("api_token")]
    public void EvaluateAuthorizationDependencies_ShouldRejectSensitiveNyxIdHeaders(string headerName)
    {
        var yaml = $$$"""
            name: wf-alpha
            roles: []
            steps:
              - id: proxy
                type: tool_call
                parameters:
                  tool: nyxid_proxy
                  arguments: '{"service_id":"us-home-alpha","slug":"home-assistant","operation_id":"list-items","method":"GET","path":"/api/items","contract_digest":"sha256:home-v1","headers":{"{{{headerName}}}":"must-not-enter-workflow"}}'
            """;

        var act = () => new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        act.Should().Throw<WorkflowExternalCapabilityValidationException>()
            .WithMessage("*sensitive header*");
    }

    [Fact]
    public async Task BindWorkflowDefinition_ShouldIgnoreCallerSuppliedCapabilityEvidence()
    {
        var yaml = """
            name: wf-alpha
            roles: []
            steps:
              - id: proxy
                type: tool_call
                parameters:
                  tool: nyxid_proxy
                  arguments: '{"service_id":"us-home-alpha","slug":"home-assistant","operation_id":"list-items","method":"GET","path":"/api/items","contract_digest":"sha256:home-v1"}'
            """;
        var forged = new WorkflowAuthorizationDependencies();
        forged.ExternalCapabilities.Add(new ExternalWorkflowCapabilityRef
        {
            NyxIdUserService = new NyxIdUserServiceCapabilityRef
            {
                UserServiceId = "us-forged-beta",
                ServiceSlugSnapshot = "forged-service",
                OperationId = "forged-operation",
                HttpMethod = "DELETE",
                PathTemplate = "/everything",
                ContractDigest = "sha256:forged",
            },
        });
        var agent = new WorkflowGAgent
        {
            EventSourcingBehaviorFactory = new InMemoryWorkflowEventSourcingBehaviorFactory(),
        };
        var actual = agent.EvaluateAuthorizationDependencies(yaml)!;
        var admissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            actual.ExternalCapabilities,
            ReadySourceStamps());

        await agent.HandleBindWorkflowDefinition(new BindWorkflowDefinitionEvent
        {
            WorkflowName = "wf-alpha",
            WorkflowYaml = yaml,
            AuthorizationDependencies = forged,
            CapabilityAdmissionPlan = admissionPlan,
        });

        agent.State.AuthorizationDependencies.ExternalCapabilities.Should().ContainSingle();
        agent.State.AuthorizationDependencies.ExternalCapabilities[0]
            .NyxIdUserService.UserServiceId.Should().Be("us-home-alpha");
    }

    [Fact]
    public async Task BindWorkflowDefinition_ShouldRejectAdmissionPlanDefinitionDigestMismatch()
    {
        var yaml = ExactNyxIdWorkflowYaml();
        var dependencies = new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml)!;
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: another-workflow\nsteps: []\n",
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            dependencies.ExternalCapabilities,
            ReadySourceStamps());
        var agent = NewAgent();

        var act = () => agent.HandleBindWorkflowDefinition(new BindWorkflowDefinitionEvent
        {
            WorkflowName = "wf-alpha",
            WorkflowYaml = yaml,
            CapabilityAdmissionPlan = plan,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*definition digest*");
        agent.State.Version.Should().Be(0);
    }

    [Fact]
    public async Task BindWorkflowDefinition_ShouldRejectAdmissionPlanCapabilityMismatch()
    {
        var yaml = ExactNyxIdWorkflowYaml();
        var dependencies = new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml)!;
        var forged = dependencies.ExternalCapabilities[0].Clone();
        forged.NyxIdUserService.UserServiceId = "us-forged-beta";
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [forged],
            ReadySourceStamps("us-forged-beta"));
        var agent = NewAgent();

        var act = () => agent.HandleBindWorkflowDefinition(new BindWorkflowDefinitionEvent
        {
            WorkflowName = "wf-alpha",
            WorkflowYaml = yaml,
            CapabilityAdmissionPlan = plan,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*capabilit*");
        agent.State.Version.Should().Be(0);
    }

    [Theory]
    [InlineData("llm_call", true)]
    [InlineData("transform", false)]
    public void EvaluateAuthorizationDependencies_ShouldDescribeLlmAndNoExternalServicePolicy(
        string stepType,
        bool ownerLlmRequired)
    {
        var yaml = $$"""
            name: wf-alpha
            roles: []
            steps:
              - id: step-alpha
                type: {{stepType}}
            """;

        var result = new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        result.Should().NotBeNull();
        result!.OwnerLlmRouteRequired.Should().Be(ownerLlmRequired);
        result.ExternalCapabilities.Should().BeEmpty();
        result.ServiceGrantPolicy.Should().Be(WorkflowServiceGrantPolicy.NotRequiredNoExternalService);
    }

    private static WorkflowGAgent NewAgent() =>
        new()
        {
            EventSourcingBehaviorFactory = new InMemoryWorkflowEventSourcingBehaviorFactory(),
        };

    private static string ExactNyxIdWorkflowYaml() =>
        """
        name: wf-alpha
        roles: []
        steps:
          - id: proxy
            type: tool_call
            parameters:
              tool: nyxid_proxy
              arguments: '{"service_id":"us-home-alpha","slug":"home-assistant","operation_id":"get-state","method":"GET","path":"/states/{entity_id}","contract_digest":"operation-digest"}'
        """;

    private static ExternalCapabilitySourceStamp[] ReadySourceStamps(
        string userServiceId = "us-home-alpha") =>
        [
            new()
            {
                SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
                SourceId = "nyxid-user-services:caller",
                ObservedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero)),
                FreshUntil = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 7, 21, 10, 5, 0, TimeSpan.Zero)),
                ContentDigest = "source-digest",
            },
            new()
            {
                SourceKind = ExternalCapabilitySourceKind.NyxIdOpenApi,
                SourceId = userServiceId,
                ObservedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero)),
                FreshUntil = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 7, 21, 10, 5, 0, TimeSpan.Zero)),
                ContentDigest = "openapi-digest",
            },
        ];

    private sealed class InMemoryWorkflowEventSourcingBehaviorFactory
        : IEventSourcingBehaviorFactory<WorkflowState>
    {
        public IEventSourcingBehavior<WorkflowState> Create(
            string agentId,
            Type actorType,
            Func<WorkflowState, IMessage, WorkflowState> transitionState)
        {
            _ = actorType;
            return new InMemoryWorkflowEventSourcingBehavior(agentId, transitionState);
        }
    }

    private sealed class InMemoryWorkflowEventSourcingBehavior(
        string agentId,
        Func<WorkflowState, IMessage, WorkflowState> transitionState)
        : IEventSourcingBehavior<WorkflowState>
    {
        private readonly List<IMessage> _pending = [];
        private WorkflowState _state = new();

        public long CurrentVersion { get; private set; }

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage =>
            _pending.Add(evt);

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var evt in _pending)
            {
                _state = transitionState(_state, evt);
                CurrentVersion++;
            }

            _pending.Clear();
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = CurrentVersion,
            });
        }

        public Task PersistSnapshotAsync(WorkflowState currentState, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<WorkflowState?> ReplayAsync(string replayAgentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<WorkflowState?>(_state.Clone());
        }

        public void DiscardPendingEvents() => _pending.Clear();

        public WorkflowState TransitionState(WorkflowState current, IMessage evt) =>
            transitionState(current, evt);
    }
}
