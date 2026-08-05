using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowAuthorizationDependenciesTests
{
    [Fact]
    public void WorkflowParser_ShouldMapStepLevelNyxIdOperationSelector()
    {
        const string yaml = """
            name: selector-workflow
            roles: []
            steps:
              - id: read-item
                type: tool_call
                capability:
                  nyxid_operation:
                    user_service_id: us-shop-alpha
                    endpoint_id: endpoint-get-item
                parameters:
                  tool: nyxid_proxy
                  arguments: '{"path_params":{"item_id":"${input}"}}'
            """;

        var step = new Aevatar.Workflow.Core.Primitives.WorkflowParser()
            .Parse(yaml)
            .Steps
            .Should().ContainSingle().Subject;

        step.Capability.Should().NotBeNull();
        step.Capability!.NyxIdOperation.Should().NotBeNull();
        step.Capability.NyxIdOperation!.UserServiceId.Should().Be("us-shop-alpha");
        step.Capability.NyxIdOperation.EndpointId.Should().Be("endpoint-get-item");
        step.Parameters.Should().NotContainKey("capability");
    }

    [Fact]
    public void WorkflowParser_ShouldMapStepLevelNyxIdRequestSelector()
    {
        const string yaml = """
            name: selector-workflow
            roles: []
            steps:
              - id: read-item
                type: tool_call
                capability:
                  nyxid_request:
                    user_service_id: usvc-alpha
                    method: GET
                    path_template: /api/resources/{resource_id}
                    query_parameters: [page_size, filter]
                    header_parameters: [If-Match]
                    body_mode: none
                    response_mode: text
                parameters:
                  tool: nyxid_proxy
                  arguments: '{"path_params":{"resource_id":"${input.resource_id}"},"query":{"page_size":500}}'
            """;

        var dependencies = new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        dependencies.Should().NotBeNull();
        var selector = dependencies!.ExternalInvocations.Should().ContainSingle().Subject.Selector;
        selector.SelectorCase.Should().Be(ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest);
        selector.NyxIdRequest.UserServiceId.Should().Be("usvc-alpha");
        selector.NyxIdRequest.Method.Should().Be(NyxIdRequestMethod.Get);
        selector.NyxIdRequest.PathTemplate.Should().Be("/api/resources/{resource_id}");
        selector.NyxIdRequest.QueryParameters.Should().Equal("filter", "page_size");
        selector.NyxIdRequest.HeaderParameters.Should().Equal("If-Match");
        selector.NyxIdRequest.BodyMode.Should().Be(NyxIdRequestBodyMode.None);
        selector.NyxIdRequest.ResponseMode.Should().Be(NyxIdRequestResponseMode.Text);
    }

    [Fact]
    public void WorkflowParser_ShouldMapNyxIdRequestBodyRequired()
    {
        const string yaml = """
            name: selector-workflow
            roles: []
            steps:
              - id: create-item
                type: tool_call
                capability:
                  nyxid_request:
                    user_service_id: usvc-alpha
                    method: POST
                    path_template: /api/resources
                    body_mode: json
                    body_required: true
                    response_mode: text
                parameters:
                  tool: nyxid_proxy
            """;

        var dependencies = new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        dependencies.Should().NotBeNull();
        dependencies!.ExternalInvocations.Should().ContainSingle().Which
            .Selector.NyxIdRequest.BodyRequired.Should().BeTrue();
    }

    [Fact]
    public void WorkflowParser_ShouldRejectMultipleNyxIdSelectors()
    {
        const string yaml = """
            name: selector-workflow
            roles: []
            steps:
              - id: read-item
                type: tool_call
                capability:
                  nyxid_operation:
                    user_service_id: usvc-alpha
                    endpoint_id: endpoint-alpha
                  nyxid_request:
                    user_service_id: usvc-alpha
                    method: GET
                    path_template: /api/resources
                    body_mode: none
                    response_mode: text
                parameters:
                  tool: nyxid_proxy
            """;

        var act = () => new Aevatar.Workflow.Core.Primitives.WorkflowParser().Parse(yaml);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*exactly one*NyxID capability selector*");
    }

    [Theory]
    [InlineData("TRACE", "/api/resources", "page_size", "If-Match", "none", "text")]
    [InlineData("GET", "https://example.com/api", "page_size", "If-Match", "none", "text")]
    [InlineData("GET", "/api/%252e%252e/secrets", "page_size", "If-Match", "none", "text")]
    [InlineData("GET", "/api/{id}/{id}", "page_size", "If-Match", "none", "text")]
    [InlineData("GET", "/api/resources", "page_size,page_size", "If-Match", "none", "text")]
    [InlineData("GET", "/api/resources", "page_size", "Authorization", "none", "text")]
    [InlineData("POST", "/api/resources", "page_size", "If-Match", "json", "file_artifact")]
    public void EvaluateAuthorizationDependencies_ShouldRejectInvalidExplicitRequestContract(
        string method,
        string pathTemplate,
        string queryParameters,
        string headerParameters,
        string bodyMode,
        string responseMode)
    {
        var queryYaml = string.Join(", ", queryParameters.Split(','));
        var headerYaml = string.Join(", ", headerParameters.Split(','));
        var yaml = $$"""
            name: selector-workflow
            roles: []
            steps:
              - id: request
                type: tool_call
                capability:
                  nyxid_request:
                    user_service_id: usvc-alpha
                    method: {{method}}
                    path_template: {{pathTemplate}}
                    query_parameters: [{{queryYaml}}]
                    header_parameters: [{{headerYaml}}]
                    body_mode: {{bodyMode}}
                    response_mode: {{responseMode}}
                parameters:
                  tool: nyxid_proxy
            """;

        var act = () => new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        act.Should().Throw<WorkflowExternalCapabilityValidationException>();
    }

    [Theory]
    [InlineData("GET", "json", false)]
    [InlineData("HEAD", "json", false)]
    [InlineData("OPTIONS", "json", false)]
    [InlineData("POST", "none", true)]
    public void EvaluateAuthorizationDependencies_ShouldRejectInvalidExplicitRequestBodyPolicy(
        string method,
        string bodyMode,
        bool bodyRequired)
    {
        var yaml = $$"""
            name: selector-workflow
            roles: []
            steps:
              - id: request
                type: tool_call
                capability:
                  nyxid_request:
                    user_service_id: usvc-alpha
                    method: {{method}}
                    path_template: /api/resources
                    body_mode: {{bodyMode}}
                    body_required: {{bodyRequired.ToString().ToLowerInvariant()}}
                    response_mode: text
                parameters:
                  tool: nyxid_proxy
            """;

        var act = () => new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        act.Should().Throw<WorkflowExternalCapabilityValidationException>();
    }

    [Fact]
    public void EvaluateAuthorizationDependencies_ShouldCompileSelectorOnlyInvocation()
    {
        const string yaml = """
            name: selector-workflow
            roles: []
            steps:
              - id: read-item
                type: tool_call
                capability:
                  nyxid_operation:
                    user_service_id: us-shop-alpha
                    endpoint_id: endpoint-get-item
                parameters:
                  tool: nyxid_proxy
                  arguments: '{"path_params":{"item_id":"${input}"}}'
            """;

        var dependencies = new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        dependencies.Should().NotBeNull();
        var invocation = dependencies!.ExternalInvocations.Should().ContainSingle().Subject;
        invocation.CallSiteId.Should().Be("selector-workflow/read-item");
        invocation.ToolName.Should().Be("nyxid_proxy");
        invocation.Selector.NyxIdOperation.UserServiceId.Should().Be("us-shop-alpha");
        invocation.Selector.NyxIdOperation.EndpointId.Should().Be("endpoint-get-item");
        dependencies.ServiceGrantPolicy.Should().Be(WorkflowServiceGrantPolicy.Required);
    }

    [Fact]
    public async Task BindWorkflowDefinition_ShouldRequireServiceGrantForExplicitRequestSelector()
    {
        const string workflowId = "wf-explicit-alpha";
        const string revisionId = "rev-explicit-alpha";
        var yaml = ExactNyxIdRequestWorkflowYaml();
        var agent = NewAgent();
        var dependencies = agent.EvaluateAuthorizationDependencies(yaml)!;
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            ReadyAdmissions(dependencies, workflowId, revisionId),
            ReadyExplicitRequestSourceStamps(),
            workflowId: workflowId,
            revisionId: revisionId);

        await agent.BindWorkflowDefinitionAsync(
            yaml,
            "explicit-workflow",
            inlineWorkflowYamls: null,
            scopeId: null,
            sourceKind: null,
            capabilityAdmissionPlan: plan,
            workflowId: workflowId,
            revisionId: revisionId,
            expectedExecutionMode: ExternalCapabilityExecutionMode.Interactive);

        agent.State.AuthorizationDependencies.ServiceGrantPolicy.Should()
            .Be(WorkflowServiceGrantPolicy.Required);
        agent.State.AuthorizationDependencies.ExternalInvocations.Should().ContainSingle()
            .Which.Selector.NyxIdRequest.UserServiceId.Should().Be("usvc-explicit-alpha");
        agent.State.WorkflowId.Should().Be(workflowId);
        agent.State.RevisionId.Should().Be(revisionId);
        agent.State.ExpectedExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
    }

    [Fact]
    public async Task BindWorkflowDefinition_WhenExplicitIdentityChanges_ShouldRejectAndPreserveAuthority()
    {
        var yaml = ExactNyxIdRequestWorkflowYaml();
        var agent = NewAgent();
        var planAlpha = CreateExplicitRequestPlan(yaml, "wf-alpha", "rev-alpha");
        var planBeta = CreateExplicitRequestPlan(yaml, "wf-beta", "rev-beta");
        await agent.BindWorkflowDefinitionAsync(
            yaml,
            "explicit-workflow",
            inlineWorkflowYamls: null,
            scopeId: null,
            sourceKind: null,
            capabilityAdmissionPlan: planAlpha,
            workflowId: "wf-alpha",
            revisionId: "rev-alpha",
            expectedExecutionMode: ExternalCapabilityExecutionMode.Interactive);

        var act = () => agent.BindWorkflowDefinitionAsync(
            yaml,
            "explicit-workflow",
            inlineWorkflowYamls: null,
            scopeId: null,
            sourceKind: null,
            capabilityAdmissionPlan: planBeta,
            workflowId: "wf-beta",
            revisionId: "rev-beta",
            expectedExecutionMode: ExternalCapabilityExecutionMode.Interactive);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*workflow revision identity*");
        agent.State.WorkflowId.Should().Be("wf-alpha");
        agent.State.RevisionId.Should().Be("rev-alpha");
    }

    [Fact]
    public async Task BindWorkflowDefinition_WhenInvalidBindingClearsPlan_ShouldStillRejectIdentityChange()
    {
        var yaml = ExactNyxIdRequestWorkflowYaml();
        var agent = NewAgent();
        var planAlpha = CreateExplicitRequestPlan(yaml, "wf-alpha", "rev-alpha");
        var planBeta = CreateExplicitRequestPlan(yaml, "wf-beta", "rev-beta");
        await agent.BindWorkflowDefinitionAsync(
            yaml,
            "explicit-workflow",
            inlineWorkflowYamls: null,
            scopeId: null,
            sourceKind: null,
            capabilityAdmissionPlan: planAlpha,
            workflowId: "wf-alpha",
            revisionId: "rev-alpha",
            expectedExecutionMode: ExternalCapabilityExecutionMode.Interactive);
        await agent.BindWorkflowDefinitionAsync(
            "name: explicit-workflow\nroles: [\n",
            "explicit-workflow",
            inlineWorkflowYamls: null,
            scopeId: null,
            sourceKind: null,
            capabilityAdmissionPlan: planAlpha,
            workflowId: "wf-alpha",
            revisionId: "rev-alpha",
            expectedExecutionMode: ExternalCapabilityExecutionMode.Interactive);

        agent.State.Compiled.Should().BeFalse();
        agent.State.CapabilityAdmissionPlan.Should().BeNull();

        var act = () => agent.BindWorkflowDefinitionAsync(
            yaml,
            "explicit-workflow",
            inlineWorkflowYamls: null,
            scopeId: null,
            sourceKind: null,
            capabilityAdmissionPlan: planBeta,
            workflowId: "wf-beta",
            revisionId: "rev-beta",
            expectedExecutionMode: ExternalCapabilityExecutionMode.Interactive);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*workflow revision identity*");
        agent.State.WorkflowId.Should().Be("wf-alpha");
        agent.State.RevisionId.Should().Be("rev-alpha");
    }

    [Fact]
    public async Task BindWorkflowDefinition_WhenExistingRevisionOnlyDiffers_ShouldRejectIdentityChange()
    {
        var explicitYaml = ExactNyxIdRequestWorkflowYaml();
        var agent = NewAgent();
        var plan = CreateExplicitRequestPlan(explicitYaml, "wf-beta", "rev-beta");
        await agent.BindWorkflowDefinitionAsync(
            "name: explicit-workflow\nroles: []\nsteps: []\n",
            "explicit-workflow",
            inlineWorkflowYamls: null,
            scopeId: null,
            sourceKind: null,
            capabilityAdmissionPlan: null,
            workflowId: null,
            revisionId: "rev-alpha",
            expectedExecutionMode: ExternalCapabilityExecutionMode.Interactive);

        var act = () => agent.BindWorkflowDefinitionAsync(
            explicitYaml,
            "explicit-workflow",
            inlineWorkflowYamls: null,
            scopeId: null,
            sourceKind: null,
            capabilityAdmissionPlan: plan,
            workflowId: "wf-beta",
            revisionId: "rev-beta",
            expectedExecutionMode: ExternalCapabilityExecutionMode.Interactive);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*workflow revision identity*");
        agent.State.WorkflowId.Should().BeEmpty();
        agent.State.RevisionId.Should().Be("rev-alpha");
    }

    [Fact]
    public async Task BindWorkflowDefinition_WhenExistingExplicitIdentityIsMissing_ShouldRequireRebind()
    {
        var yaml = ExactNyxIdRequestWorkflowYaml();
        var plan = CreateExplicitRequestPlan(yaml, "wf-alpha", "rev-alpha");
        var agent = NewAgent();
        agent.State.WorkflowName = "explicit-workflow";
        agent.State.WorkflowYaml = yaml;
        agent.State.CapabilityAdmissionPlan = plan.Clone();

        var act = () => agent.BindWorkflowDefinitionAsync(
            yaml,
            "explicit-workflow",
            inlineWorkflowYamls: null,
            scopeId: null,
            sourceKind: null,
            capabilityAdmissionPlan: plan,
            workflowId: "wf-alpha",
            revisionId: "rev-alpha",
            expectedExecutionMode: ExternalCapabilityExecutionMode.Interactive);

        await act.Should().ThrowAsync<WorkflowCapabilityAdmissionRebindRequiredException>();
    }

    [Fact]
    public async Task BindWorkflowDefinition_WithExplicitRequestOnlyInInlineWorkflow_ShouldRequireServiceGrant()
    {
        const string workflowId = "wf-root-alpha";
        const string revisionId = "rev-root-alpha";
        const string rootYaml = """
            name: root-workflow
            roles: []
            steps:
              - id: root-transform
                type: transform
            """;
        var inlineYaml = ExactNyxIdRequestWorkflowYaml();
        var inlineWorkflowYamls = new Dictionary<string, string>
        {
            ["explicit-workflow"] = inlineYaml,
        };
        var agent = NewAgent();
        var inlineDependencies = agent.EvaluateAuthorizationDependencies(inlineYaml)!;
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            rootYaml,
            inlineWorkflowYamls,
            ExternalCapabilityExecutionMode.Interactive,
            ReadyAdmissions(inlineDependencies, workflowId, revisionId),
            ReadyExplicitRequestSourceStamps(),
            workflowId: workflowId,
            revisionId: revisionId);

        await agent.BindWorkflowDefinitionAsync(
            rootYaml,
            "root-workflow",
            inlineWorkflowYamls,
            scopeId: null,
            sourceKind: null,
            capabilityAdmissionPlan: plan,
            workflowId: workflowId,
            revisionId: revisionId,
            expectedExecutionMode: ExternalCapabilityExecutionMode.Interactive);

        agent.State.AuthorizationDependencies.ServiceGrantPolicy.Should()
            .Be(WorkflowServiceGrantPolicy.Required);
        agent.State.AuthorizationDependencies.ExternalInvocations.Should().ContainSingle()
            .Which.Selector.NyxIdRequest.UserServiceId.Should().Be("usvc-explicit-alpha");
    }

    [Theory]
    [InlineData("foreach", "sub_step_type")]
    [InlineData("for_each", "sub_step_type")]
    [InlineData("foreach_llm", "sub_step_type")]
    [InlineData("while", "step")]
    [InlineData("loop", "step")]
    public void EvaluateAuthorizationDependencies_IndirectNyxIdProxy_ShouldNeverBypassAdmission(
        string primitive,
        string subStepTypeKey)
    {
        var yaml = $$$"""
            name: indirect-workflow
            roles: []
            steps:
              - id: invoke-each
                type: {{{primitive}}}
                parameters:
                  {{{subStepTypeKey}}}: tool_call
                  sub_param_tool: nyxid_proxy
                  sub_param_arguments: '{"path_params":{"item_id":"runtime-value"}}'
                  max_iterations: "1"
                  condition: "false"
            """;

        var dependencies = new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        dependencies.Should().NotBeNull();
        var invocation = dependencies!.ExternalInvocations.Should().ContainSingle().Subject;
        invocation.CallSiteId.Should().Be("indirect-workflow/invoke-each/sub-step");
        invocation.ToolName.Should().Be("nyxid_proxy");
        invocation.Selector.SelectorCase.Should()
            .Be(ExternalWorkflowCapabilitySelector.SelectorOneofCase.None);
        dependencies.ServiceGrantPolicy.Should().Be(WorkflowServiceGrantPolicy.Required);
    }

    [Fact]
    public void EvaluateAuthorizationDependencies_DirectNyxIdConnectedServiceTool_ShouldRequireMigration()
    {
        const string yaml = """
            name: legacy-direct-tool
            roles: []
            steps:
              - id: list-records
                type: tool_call
                parameters:
                  tool: nyxid_api-lark-bot-2__bitable_records_list
            """;

        var act = () => new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        var exception = act.Should().Throw<WorkflowExternalCapabilityValidationException>().Which;
        exception.Readiness.Should().NotBeNull();
        exception.Readiness!.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED");
    }

    [Fact]
    public void EvaluateAuthorizationDependencies_SynthesizedDirectNyxIdTool_ShouldRequireMigration()
    {
        const string yaml = """
            name: legacy-indirect-tool
            roles: []
            steps:
              - id: list-each
                type: foreach
                parameters:
                  sub_step_type: tool_call
                  sub_param_tool: nyxid_api-lark-bot-2__bitable_records_list
            """;

        var act = () => new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        var exception = act.Should().Throw<WorkflowExternalCapabilityValidationException>().Which;
        exception.Readiness.Should().NotBeNull();
        exception.Readiness!.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED");
    }

    [Fact]
    public void BindWorkflowDefinitionScopeId_ShouldTrackPresence()
    {
        var scopeField = BindWorkflowDefinitionEvent.Descriptor.FindFieldByNumber(4);

        scopeField.ContainingOneof.Should().NotBeNull();
        scopeField.ContainingOneof!.IsSynthetic.Should().BeTrue();
    }

    [Fact]
    public void WorkflowStateContract_ShouldCarryBoundWorkflowRevisionIdentity()
    {
        WorkflowState.Descriptor.FindFieldByName("workflow_id")!.FieldNumber.Should().Be(20);
        WorkflowState.Descriptor.FindFieldByName("revision_id")!.FieldNumber.Should().Be(21);
        WorkflowState.Descriptor.FindFieldByName("expected_execution_mode")!.FieldNumber.Should().Be(22);
    }

    [Fact]
    public async Task BindWorkflowDefinitionAsync_WhenScopeIsNull_ShouldOmitScopeField()
    {
        var eventSourcing = new InMemoryWorkflowEventSourcingBehaviorFactory();
        var agent = new WorkflowGAgent
        {
            EventSourcingBehaviorFactory = eventSourcing,
        };

        await agent.BindWorkflowDefinitionAsync(
            "name: wf-alpha\nroles: []\nsteps: []\n",
            "wf-alpha",
            inlineWorkflowYamls: null,
            scopeId: null,
            sourceKind: null,
            capabilityAdmissionPlan: null,
            workflowId: null,
            revisionId: null,
            expectedExecutionMode: ExternalCapabilityExecutionMode.Interactive);

        var bind = eventSourcing.CommittedEvents.Should().ContainSingle().Which
            .Should().BeOfType<BindWorkflowDefinitionEvent>().Subject;
        bind.HasScopeId.Should().BeFalse();
        bind.ExpectedExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        agent.State.ExpectedExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
    }

    [Fact]
    public async Task BindWorkflowDefinitionAsync_WhenModeChanges_ShouldRejectAndPreserveFirstMode()
    {
        var agent = NewAgent();
        const string yaml = "name: wf-alpha\nroles: []\nsteps: []\n";
        await agent.BindWorkflowDefinitionAsync(
            yaml,
            "wf-alpha",
            inlineWorkflowYamls: null,
            scopeId: null,
            sourceKind: null,
            capabilityAdmissionPlan: null,
            workflowId: null,
            revisionId: null,
            expectedExecutionMode: ExternalCapabilityExecutionMode.Interactive);

        var act = () => agent.BindWorkflowDefinitionAsync(
            yaml,
            "wf-alpha",
            inlineWorkflowYamls: null,
            scopeId: null,
            sourceKind: null,
            capabilityAdmissionPlan: null,
            workflowId: null,
            revisionId: null,
            expectedExecutionMode: ExternalCapabilityExecutionMode.Durable);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*execution mode*");
        agent.State.ExpectedExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
    }

    [Fact]
    public async Task BindWorkflowDefinition_WhenScopeFieldIsAbsent_ShouldPreserveExistingScope()
    {
        var agent = NewAgent();
        const string yaml = "name: wf-alpha\nroles: []\nsteps: []\n";
        await agent.HandleBindWorkflowDefinition(new BindWorkflowDefinitionEvent
        {
            WorkflowName = "wf-alpha",
            WorkflowYaml = yaml,
            ScopeId = "scope-a",
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
        });

        await agent.HandleBindWorkflowDefinition(new BindWorkflowDefinitionEvent
        {
            WorkflowName = "wf-alpha",
            WorkflowYaml = yaml,
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
        });

        agent.State.ScopeId.Should().Be("scope-a");
    }

    [Fact]
    public async Task BindWorkflowDefinition_WhenScopeFieldIsExplicitlyEmpty_ShouldClearExistingScope()
    {
        var agent = NewAgent();
        const string yaml = "name: wf-alpha\nroles: []\nsteps: []\n";
        await agent.HandleBindWorkflowDefinition(new BindWorkflowDefinitionEvent
        {
            WorkflowName = "wf-alpha",
            WorkflowYaml = yaml,
            ScopeId = "scope-a",
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
        });

        await agent.HandleBindWorkflowDefinition(new BindWorkflowDefinitionEvent
        {
            WorkflowName = "wf-alpha",
            WorkflowYaml = yaml,
            ScopeId = string.Empty,
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
        });

        agent.State.ScopeId.Should().BeEmpty();
    }

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
        var invocation = result!.ExternalInvocations.Should().ContainSingle().Subject;
        invocation.CallSiteId.Should().Be("wf-alpha/nested-call");
        invocation.Selector.SelectorCase.Should()
            .Be(ExternalWorkflowCapabilitySelector.SelectorOneofCase.HostConnector);
        invocation.Selector.HostConnector.Should().BeEquivalentTo(
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
                capability:
                  nyxid_operation:
                    user_service_id: us-home-alpha
                    endpoint_id: list-items
                parameters:
                  tool: nyxid_proxy
                  arguments: '{"query":{}}'
              - id: nested
                type: sequence
                children:
                  - id: proxy-b
                    type: tool_call
                    capability:
                      nyxid_operation:
                        user_service_id: us-home-beta
                        endpoint_id: list-items
                    parameters:
                      tool: nyxid_proxy
                      arguments: '{"query":{}}'
            """;

        var result = new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        result.Should().NotBeNull();
        result!.ExternalInvocations.Should().HaveCount(2);
        result.ExternalInvocations.Select(static invocation =>
                invocation.Selector.NyxIdOperation.UserServiceId)
            .Should().Equal("us-home-alpha", "us-home-beta");
        result.ExternalInvocations.Should().OnlyContain(static invocation =>
            invocation.Selector.NyxIdOperation.EndpointId == "list-items");
        result.ServiceGrantPolicy.Should().Be(WorkflowServiceGrantPolicy.Required);
    }

    [Theory]
    [InlineData("service_id")]
    [InlineData("service")]
    [InlineData("slug")]
    [InlineData("operation_id")]
    [InlineData("endpoint_id")]
    [InlineData("method")]
    [InlineData("path")]
    [InlineData("path_template")]
    [InlineData("contract_digest")]
    [InlineData("source_stamp")]
    public void EvaluateAuthorizationDependencies_ShouldRejectAuthoredServerDerivedProofFields(
        string derivedField)
    {
        var yaml = $$"""
            name: wf-alpha
            roles: []
            steps:
              - id: proxy
                type: tool_call
                capability:
                  nyxid_operation:
                    user_service_id: us-home-alpha
                    endpoint_id: list-items
                parameters:
                  tool: nyxid_proxy
                  arguments: '{"{{derivedField}}":"forged"}'
            """;

        var act = () => new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        act.Should().Throw<WorkflowExternalCapabilityValidationException>()
            .WithMessage($"*{derivedField}*rebind*");
    }

    [Fact]
    public void EvaluateAuthorizationDependencies_ShouldRejectDynamicOrMissingNyxIdSelector()
    {
        const string dynamicSelector = """
            name: wf-alpha
            roles: []
            steps:
              - id: proxy
                type: tool_call
                capability:
                  nyxid_operation:
                    user_service_id: ${service_id}
                    endpoint_id: list-items
                parameters:
                  tool: nyxid_proxy
                  arguments: '{"query":{}}'
            """;

        var dynamicAct = () => new WorkflowGAgent().EvaluateAuthorizationDependencies(dynamicSelector);

        dynamicAct.Should().Throw<WorkflowExternalCapabilityValidationException>()
            .WithMessage("*must be static*");

        const string missingOperation = """
            name: wf-alpha
            roles: []
            steps:
              - id: proxy
                type: tool_call
                capability:
                  nyxid_operation:
                    user_service_id: us-home-alpha
                parameters:
                  tool: nyxid_proxy
                  arguments: '{"query":{}}'
            """;

        var missingAct = () => new WorkflowGAgent().EvaluateAuthorizationDependencies(missingOperation);

        missingAct.Should().Throw<WorkflowExternalCapabilityValidationException>()
            .WithMessage("*exact connected service and operation*");
    }

    [Fact]
    public void EvaluateAuthorizationDependencies_ShouldRejectUnsupportedNyxIdRuntimeArguments()
    {
        const string yaml = """
            name: wf-alpha
            roles: []
            steps:
              - id: proxy
                type: tool_call
                capability:
                  nyxid_operation:
                    user_service_id: us-home-alpha
                    endpoint_id: list-items
                parameters:
                  tool: nyxid_proxy
                  arguments: '{"unsupported_slot":{}}'
            """;

        var act = () => new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        act.Should().Throw<WorkflowExternalCapabilityValidationException>()
            .WithMessage("*unsupported_slot*is not supported*");
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
                capability:
                  nyxid_operation:
                    user_service_id: us-home-alpha
                    endpoint_id: list-items
                parameters:
                  tool: nyxid_proxy
                  arguments: '{"headers":{"{{{headerName}}}":"must-not-enter-workflow"}}'
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
                capability:
                  nyxid_operation:
                    user_service_id: us-home-alpha
                    endpoint_id: list-items
                parameters:
                  tool: nyxid_proxy
                  arguments: '{"query":{}}'
            """;
        var forged = new WorkflowAuthorizationDependencies();
        forged.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = "wf-alpha/proxy",
            ToolName = "nyxid_proxy",
            Selector = new ExternalWorkflowCapabilitySelector
            {
                NyxIdOperation = new NyxIdOperationSelector
                {
                    UserServiceId = "us-forged-beta",
                    EndpointId = "forged-operation",
                },
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
            ReadyAdmissions(actual),
            ReadySourceStamps());

        await agent.HandleBindWorkflowDefinition(new BindWorkflowDefinitionEvent
        {
            WorkflowName = "wf-alpha",
            WorkflowYaml = yaml,
            AuthorizationDependencies = forged,
            CapabilityAdmissionPlan = admissionPlan,
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
        });

        agent.State.AuthorizationDependencies.ExternalInvocations.Should().ContainSingle();
        agent.State.AuthorizationDependencies.ExternalInvocations[0]
            .Selector.NyxIdOperation.UserServiceId.Should().Be("us-home-alpha");
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
            ReadyAdmissions(dependencies),
            ReadySourceStamps());
        var agent = NewAgent();

        var act = () => agent.HandleBindWorkflowDefinition(new BindWorkflowDefinitionEvent
        {
            WorkflowName = "wf-alpha",
            WorkflowYaml = yaml,
            CapabilityAdmissionPlan = plan,
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
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
        var forged = ReadyAdmissions(dependencies).Single();
        forged.Capability.NyxIdUserService.UserServiceId = "us-forged-beta";
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
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
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
        result.ExternalInvocations.Should().BeEmpty();
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
            capability:
              nyxid_operation:
                user_service_id: us-home-alpha
                endpoint_id: get-state
            parameters:
              tool: nyxid_proxy
              arguments: '{"path_params":{"entity_id":"${input}"}}'
        """;

    private static string ExactNyxIdRequestWorkflowYaml() =>
        """
        name: explicit-workflow
        roles: []
        steps:
          - id: request-alpha
            type: tool_call
            capability:
              nyxid_request:
                user_service_id: usvc-explicit-alpha
                method: GET
                path_template: /api/resources/{resource_id}
                body_mode: none
                response_mode: text
            parameters:
              tool: nyxid_proxy
              arguments: '{}'
        """;

    private static WorkflowCapabilityInvocationAdmission[] ReadyAdmissions(
        WorkflowAuthorizationDependencies dependencies,
        string? workflowId = null,
        string? revisionId = null) =>
        dependencies.ExternalInvocations.Select(invocation => new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = invocation.CallSiteId,
            Capability = invocation.Selector.SelectorCase switch
            {
                ExternalWorkflowCapabilitySelector.SelectorOneofCase.HostConnector =>
                    new ExternalWorkflowCapabilityRef
                    {
                        HostConnector = invocation.Selector.HostConnector.Clone(),
                    },
                ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation =>
                    new ExternalWorkflowCapabilityRef
                    {
                        NyxIdUserService = new NyxIdUserServiceCapabilityRef
                        {
                            UserServiceId = invocation.Selector.NyxIdOperation.UserServiceId,
                            ServiceSlugSnapshot = "home-assistant",
                            EndpointId = invocation.Selector.NyxIdOperation.EndpointId,
                            HttpMethod = "GET",
                            PathTemplate = "/states/{entity_id}",
                            ContractDigest = "operation-digest",
                            ExecutionPolicy = new NyxIdOperationExecutionPolicy
                            {
                                Risk = NyxIdOperationRisk.ReadOnly,
                                Approval = NyxIdOperationApproval.None,
                                EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
                                AllowedExecutionModes =
                                {
                                    ExternalCapabilityExecutionMode.Interactive,
                                    ExternalCapabilityExecutionMode.Durable,
                                },
                            },
                        },
                    },
                ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest =>
                    BuildExplicitRequestAdmissionCapability(invocation, workflowId, revisionId),
                _ => throw new InvalidOperationException("A selected capability is required."),
            },
            NyxIdExplicitRequestGrant = invocation.Selector.SelectorCase ==
                                        ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest
                ? BuildExplicitRequestGrant(invocation, workflowId, revisionId)
                : null,
        }).ToArray();

    private static ExternalWorkflowCapabilityRef BuildExplicitRequestAdmissionCapability(
        ExternalToolInvocationSpec invocation,
        string? workflowId,
        string? revisionId)
    {
        var request = invocation.Selector.NyxIdRequest.Clone();
        var requestDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeNyxIdRequestContractDigest(request);
        var grant = BuildExplicitRequestGrant(invocation, workflowId, revisionId);
        return new ExternalWorkflowCapabilityRef
        {
            NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
            {
                Request = request,
                ServiceSlugSnapshot = "explicit-service-alpha",
                ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                    .ComputeNyxIdExplicitRequestProofDigest(requestDigest, "explicit-service-alpha"),
                ExplicitRequestGrantDigest = WorkflowCapabilityAdmissionPlanIntegrity
                    .ComputeNyxIdExplicitRequestGrantDigest(grant),
                ExecutionPolicy = new NyxIdOperationExecutionPolicy
                {
                    Risk = NyxIdOperationRisk.ReadOnly,
                    Approval = NyxIdOperationApproval.None,
                    EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
                    AllowedExecutionModes = { ExternalCapabilityExecutionMode.Interactive },
                },
            },
        };
    }

    private static NyxIdExplicitRequestGrant BuildExplicitRequestGrant(
        ExternalToolInvocationSpec invocation,
        string? workflowId,
        string? revisionId)
    {
        var grant = new NyxIdExplicitRequestGrant
        {
            WorkflowId = workflowId ?? string.Empty,
            RevisionId = revisionId ?? string.Empty,
            CallSiteId = invocation.CallSiteId,
            RequestContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                .ComputeNyxIdRequestContractDigest(invocation.Selector.NyxIdRequest),
            GrantorAuthority = NyxIdExplicitRequestGrantorAuthority.AevatarWorkflowBinder,
            GrantorOwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
            GrantorOwnerSubject = "nyx-user-explicit-alpha",
            Risk = NyxIdOperationRisk.ReadOnly,
        };
        grant.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
        return grant;
    }

    private static ExternalCapabilitySourceStamp[] ReadyExplicitRequestSourceStamps() =>
        [
            new()
            {
                SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
                SourceId = "nyxid-user-services:nyx-user-explicit-alpha",
                SourceVersion = 17,
                ObservedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.Zero)),
                FreshUntil = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 7, 30, 9, 5, 0, TimeSpan.Zero)),
                ContentDigest = "explicit-user-services-digest-alpha",
            },
        ];

    private static WorkflowCapabilityAdmissionPlan CreateExplicitRequestPlan(
        string workflowYaml,
        string workflowId,
        string revisionId)
    {
        var dependencies = new WorkflowGAgent().EvaluateAuthorizationDependencies(workflowYaml)!;
        return WorkflowCapabilityAdmissionPlanIntegrity.Create(
            workflowYaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            ReadyAdmissions(dependencies, workflowId, revisionId),
            ReadyExplicitRequestSourceStamps(),
            workflowId: workflowId,
            revisionId: revisionId);
    }

    private static ExternalCapabilitySourceStamp[] ReadySourceStamps(
        string userServiceId = "us-home-alpha") =>
        [
            new()
            {
                SourceKind = ExternalCapabilitySourceKind.NyxIdMcpConfig,
                SourceId = "nyxid-mcp-config:caller:nyx-user-alpha",
                ObservedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero)),
                FreshUntil = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 7, 21, 10, 5, 0, TimeSpan.Zero)),
                ContentDigest = "mcp-config-digest",
            },
        ];

    private sealed class InMemoryWorkflowEventSourcingBehaviorFactory
        : IEventSourcingBehaviorFactory<WorkflowState>
    {
        public List<IMessage> CommittedEvents { get; } = [];

        public IEventSourcingBehavior<WorkflowState> Create(
            string agentId,
            Type actorType,
            Func<WorkflowState, IMessage, WorkflowState> transitionState)
        {
            _ = actorType;
            return new InMemoryWorkflowEventSourcingBehavior(agentId, transitionState, CommittedEvents);
        }
    }

    private sealed class InMemoryWorkflowEventSourcingBehavior(
        string agentId,
        Func<WorkflowState, IMessage, WorkflowState> transitionState,
        List<IMessage> committedEvents)
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
            var result = new EventStoreCommitResult
            {
                AgentId = agentId,
            };
            foreach (var evt in _pending)
            {
                _state = transitionState(_state, evt);
                committedEvents.Add(evt);
                CurrentVersion++;
                result.CommittedEvents.Add(new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
                    Version = CurrentVersion,
                    EventType = evt.Descriptor.FullName,
                    EventData = Google.Protobuf.WellKnownTypes.Any.Pack(evt),
                    AgentId = agentId,
                });
            }

            result.LatestVersion = CurrentVersion;
            _pending.Clear();
            return Task.FromResult(result);
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
