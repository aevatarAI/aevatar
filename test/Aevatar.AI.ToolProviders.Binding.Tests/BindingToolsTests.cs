using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.Binding.Models;
using Aevatar.AI.ToolProviders.Binding.Ports;
using Aevatar.AI.ToolProviders.Binding.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.ToolProviders.Binding.Tests;

public class BindingToolsTests
{
    #region BindingListTool

    [Fact]
    public async Task BindingListTool_ReturnsBindings()
    {
        var queryAdapter = new StubQueryAdapter(
        [
            new ScopeBindingEntry("svc-1", "Service One", "workflow", "rev-1", "actor-1", DateTimeOffset.UtcNow),
            new ScopeBindingEntry("svc-2", "Service Two", "scripting", "rev-2", "actor-2", DateTimeOffset.UtcNow),
        ]);

        var options = new BindingToolOptions();
        var tool = new BindingListTool(queryAdapter, options);

        AgentToolRequestContext.Current = OwnerContext("test-scope");

        try
        {
            var result = await tool.ExecuteAsync("{}");

            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            root.GetProperty("scope_id").GetString().Should().Be("test-scope");
            root.GetProperty("count").GetInt32().Should().Be(2);
            root.GetProperty("total").GetInt32().Should().Be(2);

            var bindings = root.GetProperty("bindings");
            bindings.GetArrayLength().Should().Be(2);
            bindings[0].GetProperty("service_id").GetString().Should().Be("svc-1");
            bindings[1].GetProperty("implementation_kind").GetString().Should().Be("scripting");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task BindingBindTool_RequiresOwnerScope()
    {
        var commandPort = new StubCommandPort();
        var tool = new BindingBindTool(commandPort);

        AgentToolRequestContext.Current = null;

        var result = await tool.ExecuteAsync("""{"kind":"workflow","workflow_yamls":["name: wf1"]}""");

        result.Should().Contain("error");
        result.Should().Contain("owner_scope_id");
    }

    [Fact]
    public async Task BindingBindTool_RejectsCallerScopeWithoutOwnerScope()
    {
        var commandPort = new StubCommandPort();
        var tool = new BindingBindTool(commandPort);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            ["scope_id"] = "caller-scope-1",
        });

        try
        {
            var result = await tool.ExecuteAsync("""{"kind":"workflow","workflow_yamls":["name: wf1"]}""");

            result.Should().Contain("error");
            result.Should().Contain("owner_scope_id not available");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    #endregion

    #region External workflow capability tools

    [Fact]
    public async Task ListExternalWorkflowCapabilitiesTool_UsesCurrentAuthorityAndPreservesExactInstances()
    {
        const string callerBearer = "caller-secret-that-must-not-be-serialized";
        const string organizationBearer = "organization-secret-that-must-not-be-serialized";
        var discovery = new ExternalWorkflowCapabilityDiscoveryResult
        {
            CandidateCount = 3,
            RejectedCount = 1,
        };
        discovery.Capabilities.Add(
        [
            Descriptor(NyxIdSelector("us-home-alpha"), "Home alpha"),
            Descriptor(NyxIdSelector("us-home-beta"), "Home beta"),
        ]);
        discovery.Diagnostics.Add(new ExternalCapabilityDiscoveryDiagnostic
        {
            Code = ExternalCapabilityDiscoveryDiagnosticCode.GenericProxyRejected,
            SafeMessage = "Generic proxy services are not eligible for workflow admission.",
            Count = 1,
        });
        var listPort = new StubExternalWorkflowCapabilityListPort(discovery);
        var tool = new ListExternalWorkflowCapabilitiesTool(listPort);

        tool.Name.Should().Be("list_external_workflow_capabilities");
        tool.IsReadOnly.Should().BeTrue();
        AgentToolRequestContext.Current = CapabilityContext(
            "owner-scope-alpha",
            "caller-subject-alpha",
            callerBearer,
            organizationBearer);

        try
        {
            var result = await tool.ExecuteAsync("{}");

            listPort.Request.Should().NotBeNull();
            listPort.Request!.Access.ScopeId.Should().Be("owner-scope-alpha");
            listPort.Request.Access.CallerId.Should().Be("caller-subject-alpha");
            listPort.Request.Access.NyxIdCallerCredential?.SourceReadableUserBearerToken
                .Should().Be(callerBearer);
            listPort.Request.Access.NyxIdOrganizationBearerToken.Should().Be(organizationBearer);
            tool.Description.Should().Contain("nyxid_operation/nyxid_request");

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("candidate_count").GetInt32().Should().Be(3);
            document.RootElement.GetProperty("rejected_count").GetInt32().Should().Be(1);
            document.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString().Should()
                .Be("EXTERNAL_CAPABILITY_DISCOVERY_DIAGNOSTIC_CODE_GENERIC_PROXY_REJECTED");
            var capabilities = document.RootElement.GetProperty("capabilities");
            capabilities.GetArrayLength().Should().Be(2);
            capabilities[0].GetProperty("selector").GetProperty("nyxid_operation")
                .GetProperty("user_service_id").GetString().Should().Be("us-home-alpha");
            capabilities[0].GetProperty("selector")
                .TryGetProperty("nyx_id_operation", out _).Should().BeFalse();
            capabilities[1].GetProperty("selector").GetProperty("nyxid_operation")
                .GetProperty("user_service_id").GetString().Should().Be("us-home-beta");
            result.Should().NotContain("nyx_id_operation");
            result.Should().NotContain("contract_digest");
            result.Should().NotContain(callerBearer);
            result.Should().NotContain(organizationBearer);
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ListExternalWorkflowCapabilitiesTool_ThroughStreamingExecutor_ShouldKeepVerifiedReadOnlySuccess()
    {
        var discovery = new ExternalWorkflowCapabilityDiscoveryResult
        {
            CandidateCount = 1,
        };
        discovery.Capabilities.Add(Descriptor(
            NyxIdSelector("us-lark-alpha", "endpoint-send-message"),
            "ChronoAI Lark Bot / im_message_create"));
        var tool = new ListExternalWorkflowCapabilitiesTool(
            new StubExternalWorkflowCapabilityListPort(discovery));
        var tools = new ToolManager();
        tools.Register(tool);
        var executionContext = CapabilityContext(
            "owner-scope-alpha",
            "caller-subject-alpha",
            "caller-bearer-alpha",
            "organization-bearer-alpha") with
        {
            Request = new AgentToolRequestIdentity("binding-tools-request", null),
            ExecutionOwner = AgentToolExecutionOwners.HostService(nameof(BindingToolsTests)),
        };
        var executor = new StreamingToolExecutor(
            tools,
            toolContext: executionContext,
            toolExecutionPort: CreateToolExecutionPort());
        using var executionState = executor.CreateExecutionState();

        AgentToolRequestContext.Current = executionContext;

        try
        {
            var prepared = await executor.PrepareBatchAsync(
                "binding-tools-test:tc-list-external-workflow-capabilities",
                round: 0,
                [new ToolCall
                {
                    Id = "tc-list-external-workflow-capabilities",
                    Name = tool.Name,
                    ArgumentsJson = "{}",
                }]);
            executor.AddTool(executionState, prepared.Single());

            var results = new List<ToolExecutionResult>();
            await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
                results.Add(result);

            var toolResult = results.Should().ContainSingle().Which;
            toolResult.IsError.Should().BeFalse();
            toolResult.Result.Should().Contain("\"capabilities\"");
            toolResult.Result.Should().Contain("nyxid_operation");
            toolResult.Result.Should().NotBe("""{"status":"unknown","message":"The tool outcome could not be verified."}""");
            var receipt = toolResult.Receipt;
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
            receipt.ApprovalMode.Should().Be(AgentToolReceiptApprovalMode.NeverRequire);
            receipt.SideEffectKind.Should().BeEmpty();
            receipt.ResultJson.Should().Be(toolResult.Result);
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExternalWorkflowCapabilityReadTools_ShouldEmitTypedResultReceipts()
    {
        IAgentTool listTool = new ListExternalWorkflowCapabilitiesTool(
            new StubExternalWorkflowCapabilityListPort(
                new ExternalWorkflowCapabilityDiscoveryResult()));
        IAgentTool readinessTool = new InspectExternalWorkflowCapabilityReadinessTool(
            new StubExternalWorkflowCapabilityReadinessPort());
        AgentToolRequestContext.Current = CapabilityContext(
            "scope-receipt-alpha",
            "caller-receipt-alpha",
            "caller-bearer-alpha",
            "organization-bearer-alpha");

        try
        {
            var listResult = await listTool.ExecuteAsync("{}");
            var listReceipt = listTool.CreateResultReceipt(
                "call-list-alpha",
                listTool.Name,
                "{}",
                listResult);

            listReceipt.Should().NotBeNull();
            listReceipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
            listReceipt.CallId.Should().Be("call-list-alpha");
            listReceipt.ToolName.Should().Be("list_external_workflow_capabilities");
            listReceipt.ResultJson.Should().Be(listResult);

            const string readinessArguments =
                """
                {
                  "selector": {
                    "nyx_id_operation": {
                      "user_service_id": "us-receipt-alpha",
                      "endpoint_id": "endpoint-receipt-beta"
                    }
                  },
                  "execution_mode": "interactive"
                }
                """;
            var readinessResult = await readinessTool.ExecuteAsync(readinessArguments);
            var readinessReceipt = readinessTool.CreateResultReceipt(
                "call-readiness-beta",
                readinessTool.Name,
                readinessArguments,
                readinessResult);

            readinessReceipt.Should().NotBeNull();
            readinessReceipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
            readinessReceipt.CallId.Should().Be("call-readiness-beta");
            readinessReceipt.ToolName.Should().Be("inspect_external_workflow_capability_readiness");
            readinessReceipt.ResultJson.Should().Be(readinessResult);
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ListExternalWorkflowCapabilitiesTool_EmitsAuthoringSelectorForNyxIdRequest()
    {
        var discovery = new ExternalWorkflowCapabilityDiscoveryResult();
        var requestSelector = new NyxIdRequestSelector
        {
            UserServiceId = "us-lark-alpha",
            Method = NyxIdRequestMethod.Post,
            PathTemplate = "/open-apis/im/v1/messages/{message_id}",
            BodyMode = NyxIdRequestBodyMode.Json,
            BodyRequired = true,
            ResponseMode = NyxIdRequestResponseMode.Text,
        };
        requestSelector.QueryParameters.Add("receive_id_type");
        requestSelector.HeaderParameters.Add("X-Tenant");
        discovery.Capabilities.Add(Descriptor(
            new ExternalWorkflowCapabilitySelector { NyxIdRequest = requestSelector },
            "Lark message request"));
        var tool = new ListExternalWorkflowCapabilitiesTool(
            new StubExternalWorkflowCapabilityListPort(discovery));
        AgentToolRequestContext.Current = CapabilityContext(
            "owner-scope-alpha",
            "caller-subject-alpha",
            "caller-bearer-alpha",
            "organization-bearer-alpha");

        try
        {
            var result = await tool.ExecuteAsync("{}");

            using var document = JsonDocument.Parse(result);
            var selector = document.RootElement.GetProperty("capabilities")[0].GetProperty("selector");
            selector.TryGetProperty("nyx_id_request", out _).Should().BeFalse();
            var request = selector.GetProperty("nyxid_request");
            request.GetProperty("user_service_id").GetString().Should().Be("us-lark-alpha");
            request.GetProperty("method").GetString().Should().Be("POST");
            request.GetProperty("path_template").GetString().Should().Be("/open-apis/im/v1/messages/{message_id}");
            request.GetProperty("query_parameters").EnumerateArray()
                .Select(static item => item.GetString()).Should().Equal("receive_id_type");
            request.GetProperty("header_parameters").EnumerateArray()
                .Select(static item => item.GetString()).Should().Equal("X-Tenant");
            request.GetProperty("body_mode").GetString().Should().Be("json");
            request.GetProperty("body_required").GetBoolean().Should().BeTrue();
            request.GetProperty("response_mode").GetString().Should().Be("text");
            result.Should().NotContain("nyx_id_request");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public void ExternalWorkflowCapabilityReadTools_ShouldPreserveErrorsAndRejectUnstructuredResults()
    {
        IAgentTool tool = new ListExternalWorkflowCapabilitiesTool(
            new StubExternalWorkflowCapabilityListPort(
                new ExternalWorkflowCapabilityDiscoveryResult()));

        const string errorResult = """{"error":"safe discovery failure"}""";
        var errorReceipt = tool.CreateResultReceipt(
            "call-error-alpha",
            tool.Name,
            "{}",
            errorResult);

        errorReceipt.Should().NotBeNull();
        errorReceipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        errorReceipt.ErrorCode.Should().Be("external_workflow_capability_query_failed");
        errorReceipt.ResultJson.Should().Be(errorResult);
        tool.CreateResultReceipt("call-invalid-beta", tool.Name, "{}", "not-json")
            .Should().BeNull();
        tool.CreateResultReceipt("call-unverified-gamma", tool.Name, "{}", "{}")
            .Should().BeNull();
    }

    [Fact]
    public async Task ExternalWorkflowCapabilityToolSupport_UsesSupplementalSourceCredentialForDelegatedCaller()
    {
        var listPort = new StubExternalWorkflowCapabilityListPort(
            new ExternalWorkflowCapabilityDiscoveryResult());
        var tool = new ListExternalWorkflowCapabilitiesTool(listPort);
        AgentToolRequestContext.Current = CapabilityContext(
            "owner-scope-m-alpha",
            "caller-subject-wf-alpha",
            "delegation-svc-alpha",
            "organization-bearer-alpha") with
        {
            Credentials = new AgentToolCredentials(
                "delegation-svc-alpha",
                "organization-bearer-alpha",
                null,
                AgentToolNyxIdCredentialKind.ProxyDelegation,
                "source-readable-wf-alpha"),
        };

        try
        {
            await tool.ExecuteAsync("{}");

            var credential = listPort.Request!.Access.NyxIdCallerCredential!;
            credential.Kind.Should().Be(NyxIdCallerCredentialKind.SourceReadableUserBearer);
            credential.SourceReadableUserBearerToken.Should().Be("source-readable-wf-alpha");
            credential.ProxyDelegationToken.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExternalWorkflowCapabilityToolSupport_PreservesDelegationWhenSupplementalSourceCredentialIsAbsent()
    {
        var listPort = new StubExternalWorkflowCapabilityListPort(
            new ExternalWorkflowCapabilityDiscoveryResult());
        var tool = new ListExternalWorkflowCapabilitiesTool(listPort);
        AgentToolRequestContext.Current = CapabilityContext(
            "owner-scope-m-beta",
            "caller-subject-wf-beta",
            "delegation-svc-beta",
            "organization-bearer-beta") with
        {
            Credentials = new AgentToolCredentials(
                "delegation-svc-beta",
                "organization-bearer-beta",
                null,
                AgentToolNyxIdCredentialKind.ProxyDelegation),
        };

        try
        {
            await tool.ExecuteAsync("{}");

            var credential = listPort.Request!.Access.NyxIdCallerCredential!;
            credential.Kind.Should().Be(NyxIdCallerCredentialKind.ProxyDelegation);
            credential.ProxyDelegationToken.Should().Be("delegation-svc-beta");
            credential.SourceReadableUserBearerToken.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExternalWorkflowCapabilityToolSupport_DoesNotPromoteUnspecifiedCredentialKind()
    {
        var listPort = new StubExternalWorkflowCapabilityListPort(
            new ExternalWorkflowCapabilityDiscoveryResult());
        var tool = new ListExternalWorkflowCapabilitiesTool(listPort);
        AgentToolRequestContext.Current = CapabilityContext(
            "owner-scope-alpha",
            "caller-subject-alpha",
            "legacy-alpha",
            "organization-bearer-alpha") with
        {
            Credentials = new AgentToolCredentials("legacy-alpha", "organization-bearer-alpha", null),
        };

        try
        {
            await tool.ExecuteAsync("{}");

            listPort.Request!.Access.NyxIdCallerCredential.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ListExternalWorkflowCapabilitiesTool_RejectsOwnerSubjectWithoutNyxIdAuthority()
    {
        var listPort = new StubExternalWorkflowCapabilityListPort(
            new ExternalWorkflowCapabilityDiscoveryResult());
        var tool = new ListExternalWorkflowCapabilitiesTool(listPort);
        AgentToolRequestContext.Current = OwnerContext("scope-owner-alpha") with
        {
            Caller = new AgentToolCallerContext(
                "scope-owner-alpha",
                "scope-owner-alpha",
                ResponseId: null,
                OwnerScopeId: "scope-owner-alpha"),
            Credentials = new AgentToolCredentials("caller-bearer-alpha", null, null),
        };

        try
        {
            var result = await tool.ExecuteAsync("{}");

            result.Should().Contain("verified caller identity not available");
            listPort.Request.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task InspectExternalWorkflowCapabilityReadinessTool_UsesExactTypedCandidate()
    {
        var readinessPort = new StubExternalWorkflowCapabilityReadinessPort();
        var tool = new InspectExternalWorkflowCapabilityReadinessTool(readinessPort);

        tool.Name.Should().Be("inspect_external_workflow_capability_readiness");
        tool.IsReadOnly.Should().BeTrue();
        AgentToolRequestContext.Current = CapabilityContext(
            "owner-scope-alpha",
            "caller-subject-alpha",
            "caller-bearer-alpha",
            "organization-bearer-alpha");

        try
        {
            var result = await tool.ExecuteAsync(
                """
                {
                  "selector": {
                    "nyx_id_operation": {
                      "user_service_id": "us-home-alpha",
                      "endpoint_id": "read_states"
                    }
                  },
                  "execution_mode": "interactive"
                }
                """);

            readinessPort.Request.Should().NotBeNull();
            readinessPort.Request!.Access.ScopeId.Should().Be("owner-scope-alpha");
            readinessPort.Request.Access.CallerId.Should().Be("caller-subject-alpha");
            readinessPort.Request.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
            readinessPort.Request.Selector.NyxIdOperation.UserServiceId.Should().Be("us-home-alpha");
            readinessPort.Request.Selector.NyxIdOperation.EndpointId.Should().Be("read_states");
            tool.ParametersSchema.Should().Contain("endpoint_id");
            tool.ParametersSchema.Should().NotContain("operation_id");
            tool.ParametersSchema.Should().NotContain("contract_digest");

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should()
                .Be("EXTERNAL_CAPABILITY_READINESS_STATUS_READY");
            document.RootElement.GetProperty("selected_capability")
                .GetProperty("nyx_id_user_service")
                .GetProperty("user_service_id").GetString().Should().Be("us-home-alpha");
            result.Should().NotContain("caller-bearer-alpha");
            result.Should().NotContain("organization-bearer-alpha");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task InspectExternalWorkflowCapabilityReadinessTool_AcceptsAuthoringNyxIdOperationSelector()
    {
        var readinessPort = new StubExternalWorkflowCapabilityReadinessPort();
        var tool = new InspectExternalWorkflowCapabilityReadinessTool(readinessPort);
        AgentToolRequestContext.Current = CapabilityContext(
            "owner-scope-alpha",
            "caller-subject-alpha",
            "caller-bearer-alpha",
            "organization-bearer-alpha");

        try
        {
            var result = await tool.ExecuteAsync(
                """
                {
                  "selector": {
                    "nyxid_operation": {
                      "user_service_id": "us-home-alpha",
                      "endpoint_id": "read_states"
                    }
                  },
                  "execution_mode": "interactive"
                }
                """);

            readinessPort.Request.Should().NotBeNull();
            readinessPort.Request!.Selector.NyxIdOperation.UserServiceId.Should().Be("us-home-alpha");
            readinessPort.Request.Selector.NyxIdOperation.EndpointId.Should().Be("read_states");
            tool.ParametersSchema.Should().Contain("nyxid_operation");
            tool.ParametersSchema.Should().Contain("nyxid_request");
            tool.ParametersSchema.Should().NotContain("nyx_id_operation");
            tool.ParametersSchema.Should().NotContain("nyx_id_request");

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should()
                .Be("EXTERNAL_CAPABILITY_READINESS_STATUS_READY");
            var selectedSelector = document.RootElement.GetProperty("selected_selector");
            selectedSelector.TryGetProperty("nyx_id_operation", out _).Should().BeFalse();
            selectedSelector.GetProperty("nyxid_operation")
                .GetProperty("user_service_id").GetString().Should().Be("us-home-alpha");
            result.Should().NotContain("nyx_id_operation");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task InspectExternalWorkflowCapabilityReadinessTool_AcceptsAuthoringNyxIdRequestSelector()
    {
        var readinessPort = new StubExternalWorkflowCapabilityReadinessPort();
        var tool = new InspectExternalWorkflowCapabilityReadinessTool(readinessPort);
        AgentToolRequestContext.Current = CapabilityContext(
            "owner-scope-alpha",
            "caller-subject-alpha",
            "caller-bearer-alpha",
            "organization-bearer-alpha");

        try
        {
            var result = await tool.ExecuteAsync(
                """
                {
                  "selector": {
                    "nyxid_request": {
                      "user_service_id": "us-lark-alpha",
                      "method": "POST",
                      "path_template": "/open-apis/im/v1/messages",
                      "query_parameters": ["receive_id_type"],
                      "header_parameters": [],
                      "body_mode": "json",
                      "body_required": true,
                      "response_mode": "text"
                    }
                  },
                  "execution_mode": "durable"
                }
                """);

            readinessPort.Request.Should().NotBeNull();
            readinessPort.Request!.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Durable);
            var request = readinessPort.Request.Selector.NyxIdRequest;
            request.UserServiceId.Should().Be("us-lark-alpha");
            request.Method.Should().Be(NyxIdRequestMethod.Post);
            request.PathTemplate.Should().Be("/open-apis/im/v1/messages");
            request.QueryParameters.Should().Equal("receive_id_type");
            request.BodyMode.Should().Be(NyxIdRequestBodyMode.Json);
            request.BodyRequired.Should().BeTrue();
            request.ResponseMode.Should().Be(NyxIdRequestResponseMode.Text);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should()
                .Be("EXTERNAL_CAPABILITY_READINESS_STATUS_READY");
            var selectedSelector = document.RootElement.GetProperty("selected_selector");
            selectedSelector.TryGetProperty("nyx_id_request", out _).Should().BeFalse();
            selectedSelector.GetProperty("nyxid_request")
                .GetProperty("user_service_id").GetString().Should().Be("us-lark-alpha");
            result.Should().NotContain("nyx_id_request");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task InspectExternalWorkflowCapabilityReadinessTool_AcceptsCanonicalCodeExecutionSelector()
    {
        var readinessPort = new StubExternalWorkflowCapabilityReadinessPort();
        var tool = new InspectExternalWorkflowCapabilityReadinessTool(readinessPort);
        AgentToolRequestContext.Current = CapabilityContext(
            "owner-scope-alpha",
            "caller-subject-alpha",
            "caller-bearer-alpha",
            "organization-bearer-alpha");

        try
        {
            var result = await tool.ExecuteAsync(
                """
                {
                  "selector": { "code_execution": {} },
                  "execution_mode": "interactive"
                }
                """);

            readinessPort.Request.Should().NotBeNull();
            readinessPort.Request!.Selector.SelectorCase.Should()
                .Be(ExternalWorkflowCapabilitySelector.SelectorOneofCase.CodeExecution);
            tool.ParametersSchema.Should().Contain("code_execution");

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("selected_selector")
                .GetProperty("code_execution").ValueKind.Should().Be(JsonValueKind.Object);
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task WorkflowExternalCapabilityAuthoringToolSource_RegistersOnlyAuthoringReadTools()
    {
        var source = new WorkflowExternalCapabilityAuthoringToolSource(
            new WorkflowExternalCapabilityToolOptions(),
            new StubExternalWorkflowCapabilityListPort(
                new ExternalWorkflowCapabilityDiscoveryResult()),
            new StubExternalWorkflowCapabilityReadinessPort(),
            new StubWorkflowExplicitRequestPreviewService());

        var tools = await source.DiscoverToolsAsync();

        tools.Should().HaveCount(3);
        tools.Should().ContainSingle(tool => tool is ListExternalWorkflowCapabilitiesTool);
        tools.Should().ContainSingle(tool => tool is InspectExternalWorkflowCapabilityReadinessTool);
        tools.Should().ContainSingle(tool => tool is PreviewWorkflowExplicitRequestsTool);
        tools.Should().OnlyContain(static tool => tool.IsReadOnly);
    }

    #endregion

    #region BindingStatusTool

    [Fact]
    public async Task BindingStatusTool_ReturnsStatus()
    {
        var queryAdapter = new StubQueryAdapter([], new ScopeBindingHealthStatus(
            "svc-1", "Service One", "workflow", "healthy", "actor-1", "actor-1", null, DateTimeOffset.UtcNow));
        var tool = new BindingStatusTool(queryAdapter);

        AgentToolRequestContext.Current = OwnerContext("test-scope");

        try
        {
            var result = await tool.ExecuteAsync("""{"service_id":"svc-1"}""");

            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            root.GetProperty("service_id").GetString().Should().Be("svc-1");
            root.GetProperty("status").GetString().Should().Be("healthy");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    #endregion

    #region BindingBindTool

    [Fact]
    public async Task BindingBindTool_WorkflowKind_CallsUpsert()
    {
        ScopeBindingUpsertRequest? captured = null;
        var commandPort = new StubCommandPort(captureRequest: r => captured = r);
        var tool = new BindingBindTool(commandPort);

        AgentToolRequestContext.Current = CapabilityContext(
            "owner-scope-1",
            "caller-alpha",
            "caller-bearer-alpha",
            "organization-bearer-alpha");

        try
        {
            var result = await tool.ExecuteAsync(
                """{"kind":"workflow","workflow_yamls":["name: wf1\nsteps:\n  - id: s1"],"display_name":"My WF"}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

            captured.Should().NotBeNull();
            captured!.ScopeId.Should().Be("owner-scope-1");
            captured.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Workflow);
            captured.Workflow.Should().NotBeNull();
            captured.Workflow!.WorkflowYamls.Should().HaveCount(1);
            captured.DisplayName.Should().Be("My WF");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task BindingBindTool_ScriptingKind_CallsUpsert()
    {
        ScopeBindingUpsertRequest? captured = null;
        var commandPort = new StubCommandPort(captureRequest: r => captured = r);
        var tool = new BindingBindTool(commandPort);

        AgentToolRequestContext.Current = OwnerContext("scope-2");

        try
        {
            var result = await tool.ExecuteAsync(
                """{"kind":"scripting","script_id":"script-abc","script_revision":"v2"}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

            captured.Should().NotBeNull();
            captured!.ScopeId.Should().Be("scope-2");
            captured.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Scripting);
            captured.Script.Should().NotBeNull();
            captured.Script!.ScriptId.Should().Be("script-abc");
            captured.Script.ScriptRevision.Should().Be("v2");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task BindingBindTool_GAgentKind_UsesAgentKind()
    {
        ScopeBindingUpsertRequest? captured = null;
        var commandPort = new StubCommandPort(captureRequest: r => captured = r);
        var tool = new BindingBindTool(commandPort);

        AgentToolRequestContext.Current = OwnerContext("scope-gagent");

        try
        {
            var result = await tool.ExecuteAsync(
                """{"kind":"gagent","agent_kind":"orders.assistant","display_name":"Orders Assistant"}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

            captured.Should().NotBeNull();
            captured!.ScopeId.Should().Be("scope-gagent");
            captured.ImplementationKind.Should().Be(ScopeBindingImplementationKind.GAgent);
            captured.GAgent.Should().NotBeNull();
            captured.GAgent!.AgentKind.Should().Be("orders.assistant");
            captured.DisplayName.Should().Be("Orders Assistant");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task BindingBindTool_GAgentKind_RejectsGAgentTypeAlias()
    {
        var commandPort = new StubCommandPort();
        var tool = new BindingBindTool(commandPort);

        AgentToolRequestContext.Current = OwnerContext("scope-gagent");

        try
        {
            var result = await tool.ExecuteAsync(
                """{"kind":"gagent","gagent_type":"OrdersGAgent","agent_kind":"orders.assistant"}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("error").GetString().Should().Contain("agent_kind");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    #endregion

    #region ScopeWorkflows tools

    [Fact]
    public async Task ScopeWorkflowsUpsertTool_CallsCommandPort()
    {
        ScopeWorkflowUpsertRequest? captured = null;
        var tool = new ScopeWorkflowsUpsertTool(new StubScopeWorkflowCommandPort(
            captureRequest: r => captured = r));

        AgentToolRequestContext.Current = CapabilityContext(
            "scope-workflows",
            "caller-alpha",
            "caller-bearer-alpha",
            "organization-bearer-alpha");

        try
        {
            var result = await tool.ExecuteAsync(
                """
                {
                  "workflow_id":"summary-digest",
                  "workflow_yaml":"name: summary-digest\nsteps: []\n",
                  "workflow_name":"daily_digest",
                  "display_name":"Summary Digest",
                  "inline_workflow_yamls": { "child": "name: child\nsteps: []\n" },
                  "revision_id":"rev-input"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("accepted").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("workflow_id").GetString().Should().Be("summary-digest");
            doc.RootElement.GetProperty("revision_id").GetString().Should().Be("rev-result");
            doc.RootElement.GetProperty("read_model_url").GetString().Should().Be("/api/scopes/scope-workflows/workflows/summary-digest");
            doc.RootElement.GetProperty("acceptance_stage").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("propagation_stage").GetString().Should().Be("readmodel_propagating");
            doc.RootElement.GetProperty("command_handles").GetArrayLength().Should().Be(1);
            doc.RootElement.TryGetProperty("workflow", out _).Should().BeFalse();

            captured.Should().NotBeNull();
            captured!.ScopeId.Should().Be("scope-workflows");
            captured.WorkflowId.Should().Be("summary-digest");
            captured.WorkflowYaml.Should().Contain("summary-digest");
            captured.WorkflowName.Should().Be("daily_digest");
            captured.DisplayName.Should().Be("Summary Digest");
            captured.InlineWorkflowYamls.Should().ContainKey("child");
            captured.RevisionId.Should().Be("rev-input");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ScopeWorkflowsUpsertTool_ValidatesTypedParameters()
    {
        var tool = new ScopeWorkflowsUpsertTool(new StubScopeWorkflowCommandPort());

        AgentToolRequestContext.Current = OwnerContext("scope-workflows");

        try
        {
            var missingWorkflowId = await tool.ExecuteAsync("""{"workflow_yaml":"name: wf"}""");
            ReadError(missingWorkflowId).Should().Be("'workflow_id' is required");

            var missingYaml = await tool.ExecuteAsync("""{"workflow_id":"wf"}""");
            ReadError(missingYaml).Should().Be("'workflow_yaml' is required");

            var invalidInlineMap = await tool.ExecuteAsync(
                """{"workflow_id":"wf","workflow_yaml":"name: wf","inline_workflow_yamls":{"child":42}}""");
            invalidInlineMap.Should().Contain("inline_workflow_yamls");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ScopeWorkflowsUpsertTool_ReturnsErrorOnCommandFailure()
    {
        var tool = new ScopeWorkflowsUpsertTool(new StubScopeWorkflowCommandPort(
            exception: new InvalidOperationException("bad request")));

        AgentToolRequestContext.Current = CapabilityContext(
            "scope-workflows",
            "caller-alpha",
            "caller-bearer-alpha",
            "organization-bearer-alpha");

        try
        {
            var result = await tool.ExecuteAsync("""{"workflow_id":"wf","workflow_yaml":"name: wf"}""");

            result.Should().Contain("Workflow upsert failed");
            result.Should().Contain("InvalidOperationException");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ScopeWorkflowsListTool_ReturnsWorkflows()
    {
        var workflows = new[]
        {
            BuildWorkflowSummary("scope-workflows", "wf-1"),
            BuildWorkflowSummary("scope-workflows", "wf-2"),
        };
        var tool = new ScopeWorkflowsListTool(
            new StubScopeWorkflowQueryPort(listResult: workflows),
            new BindingToolOptions { MaxListResults = 1 });

        AgentToolRequestContext.Current = OwnerContext("scope-workflows");

        try
        {
            var result = await tool.ExecuteAsync("""{"max_results":2}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("scope_id").GetString().Should().Be("scope-workflows");
            doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
            doc.RootElement.GetProperty("total").GetInt32().Should().Be(2);
            doc.RootElement.GetProperty("workflows")[0].GetProperty("workflow_id").GetString().Should().Be("wf-1");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ScopeWorkflowsListTool_ShouldEmitTypedResultReceipt()
    {
        IAgentTool tool = new ScopeWorkflowsListTool(
            new StubScopeWorkflowQueryPort(listResult:
            [
                BuildWorkflowSummary("scope-workflows", "wf-1"),
            ]),
            new BindingToolOptions());

        AgentToolRequestContext.Current = OwnerContext("scope-workflows");

        try
        {
            var result = await tool.ExecuteAsync("{}");
            var receipt = tool.CreateResultReceipt(
                "call-scope-workflows-list",
                tool.Name,
                "{}",
                result);

            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
            receipt.CallId.Should().Be("call-scope-workflows-list");
            receipt.ToolName.Should().Be("scope_workflows_list");
            receipt.ResultJson.Should().Be(result);
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ScopeWorkflowsListTool_ValidatesScopeContext()
    {
        var tool = new ScopeWorkflowsListTool(
            new StubScopeWorkflowQueryPort(),
            new BindingToolOptions());

        AgentToolRequestContext.Current = null;

        var result = await tool.ExecuteAsync("{}");

        result.Should().Contain("owner_scope_id not available");
    }

    [Fact]
    public async Task ScopeWorkflowsListTool_ReturnsErrorOnQueryFailure()
    {
        var tool = new ScopeWorkflowsListTool(
            new StubScopeWorkflowQueryPort(exception: new InvalidOperationException("query failed")),
            new BindingToolOptions());

        AgentToolRequestContext.Current = OwnerContext("scope-workflows");

        try
        {
            var result = await tool.ExecuteAsync("{}");

            result.Should().Contain("Workflow list failed");
            result.Should().Contain("InvalidOperationException");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ScopeWorkflowsGetTool_ReturnsWorkflow()
    {
        var tool = new ScopeWorkflowsGetTool(new StubScopeWorkflowQueryPort(
            getResult: BuildWorkflowSummary("scope-workflows", "wf-1")));

        AgentToolRequestContext.Current = OwnerContext("scope-workflows");

        try
        {
            var result = await tool.ExecuteAsync("""{"workflow_id":"wf-1"}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("available").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("scope_id").GetString().Should().Be("scope-workflows");
            doc.RootElement.GetProperty("workflow").GetProperty("workflow_id").GetString().Should().Be("wf-1");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ScopeWorkflowsGetTool_ShouldEmitTypedResultReceipt()
    {
        IAgentTool tool = new ScopeWorkflowsGetTool(new StubScopeWorkflowQueryPort(
            getResult: BuildWorkflowSummary("scope-workflows", "wf-1")));

        AgentToolRequestContext.Current = OwnerContext("scope-workflows");

        try
        {
            var result = await tool.ExecuteAsync("""{"workflow_id":"wf-1"}""");
            var receipt = tool.CreateResultReceipt(
                "call-scope-workflows-get",
                tool.Name,
                """{"workflow_id":"wf-1"}""",
                result);

            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
            receipt.CallId.Should().Be("call-scope-workflows-get");
            receipt.ToolName.Should().Be("scope_workflows_get");
            receipt.ResultJson.Should().Be(result);
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ScopeWorkflowsGetTool_ShouldKeepVerifiedReadOnlySuccessThroughStreamingExecutor()
    {
        var tool = new ScopeWorkflowsGetTool(new StubScopeWorkflowQueryPort(
            getResult: BuildWorkflowSummary("scope-workflows", "wf-1")));
        var tools = new ToolManager();
        tools.Register(tool);
        var executionContext = OwnerContext("scope-workflows") with
        {
            Request = new AgentToolRequestIdentity("scope-workflows-get-request", "tc-scope-workflows-get"),
            ExecutionOwner = AgentToolExecutionOwners.HostService(nameof(BindingToolsTests)),
        };
        var executor = new StreamingToolExecutor(
            tools,
            toolContext: executionContext,
            toolExecutionPort: CreateToolExecutionPort());
        using var executionState = executor.CreateExecutionState();

        AgentToolRequestContext.Current = executionContext;

        try
        {
            var prepared = await executor.PrepareBatchAsync(
                "binding-tools-test:tc-scope-workflows-get",
                round: 0,
                [new ToolCall
                {
                    Id = "tc-scope-workflows-get",
                    Name = tool.Name,
                    ArgumentsJson = """{"workflow_id":"wf-1"}""",
                }]);
            executor.AddTool(executionState, prepared.Single());

            var results = new List<ToolExecutionResult>();
            await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
                results.Add(result);

            var toolResult = results.Should().ContainSingle().Which;
            toolResult.IsError.Should().BeFalse();
            toolResult.Result.Should().Contain("\"workflow\"");
            toolResult.Result.Should().NotBe("""{"status":"unknown","message":"The tool outcome could not be verified."}""");
            var receipt = toolResult.Receipt;
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
            receipt.ApprovalMode.Should().Be(AgentToolReceiptApprovalMode.NeverRequire);
            receipt.SideEffectKind.Should().BeEmpty();
            receipt.ResultJson.Should().Be(toolResult.Result);
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ScopeWorkflowsGetTool_ValidatesWorkflowId()
    {
        var tool = new ScopeWorkflowsGetTool(new StubScopeWorkflowQueryPort());

        AgentToolRequestContext.Current = OwnerContext("scope-workflows");

        try
        {
            var result = await tool.ExecuteAsync("{}");

            ReadError(result).Should().Be("'workflow_id' is required");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ScopeWorkflowsGetTool_ReturnsUnavailableWhenWorkflowMissing()
    {
        var tool = new ScopeWorkflowsGetTool(new StubScopeWorkflowQueryPort(getResult: null));

        AgentToolRequestContext.Current = OwnerContext("scope-workflows");

        try
        {
            var result = await tool.ExecuteAsync("""{"workflow_id":"missing"}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("available").GetBoolean().Should().BeFalse();
            doc.RootElement.GetProperty("workflow_id").GetString().Should().Be("missing");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ScopeWorkflowsGetTool_ShouldVerifyUnavailableAndErrorResults()
    {
        IAgentTool missingTool = new ScopeWorkflowsGetTool(new StubScopeWorkflowQueryPort(getResult: null));

        AgentToolRequestContext.Current = OwnerContext("scope-workflows");

        try
        {
            var missingResult = await missingTool.ExecuteAsync("""{"workflow_id":"missing"}""");
            var missingReceipt = missingTool.CreateResultReceipt(
                "call-scope-workflows-missing",
                missingTool.Name,
                """{"workflow_id":"missing"}""",
                missingResult);

            missingReceipt.Should().NotBeNull();
            missingReceipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            missingReceipt.ErrorCode.Should().Be("scope_workflow_get_failed");
            missingReceipt.ResultJson.Should().Be(missingResult);

            IAgentTool invalidTool = new ScopeWorkflowsGetTool(new StubScopeWorkflowQueryPort());
            var invalidResult = await invalidTool.ExecuteAsync("{}");
            var invalidReceipt = invalidTool.CreateResultReceipt(
                "call-scope-workflows-invalid",
                invalidTool.Name,
                "{}",
                invalidResult);

            invalidReceipt.Should().NotBeNull();
            invalidReceipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            invalidReceipt.ErrorCode.Should().Be("scope_workflow_get_failed");
            invalidReceipt.ResultJson.Should().Be(invalidResult);
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ScopeWorkflowsGetTool_ReturnsErrorOnQueryFailure()
    {
        var tool = new ScopeWorkflowsGetTool(new StubScopeWorkflowQueryPort(
            exception: new InvalidOperationException("query failed")));

        AgentToolRequestContext.Current = OwnerContext("scope-workflows");

        try
        {
            var result = await tool.ExecuteAsync("""{"workflow_id":"wf-1"}""");

            result.Should().Contain("Workflow get failed");
            result.Should().Contain("InvalidOperationException");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task BindingAgentToolSource_RegistersScopeWorkflowToolsConditionally()
    {
        var source = new BindingAgentToolSource(
            new BindingToolOptions(),
            scopeWorkflowCommandPort: new StubScopeWorkflowCommandPort(),
            scopeWorkflowQueryPort: new StubScopeWorkflowQueryPort());

        var tools = await source.DiscoverToolsAsync();

        tools.Should().HaveCount(3);
        tools.Should().Contain(t => t is ScopeWorkflowsUpsertTool);
        tools.Should().Contain(t => t is ScopeWorkflowsListTool);
        tools.Should().Contain(t => t is ScopeWorkflowsGetTool);
    }

    #endregion

    #region BindingUnbindTool

    [Fact]
    public async Task BindingUnbindTool_CallsUnbind()
    {
        var unbindAdapter = new StubUnbindAdapter(
            new ScopeBindingUnbindResult(true, "svc-remove"));

        var tool = new BindingUnbindTool(unbindAdapter);

        AgentToolRequestContext.Current = OwnerContext("scope-unbind");

        try
        {
            var result = await tool.ExecuteAsync("""{"service_id":"svc-remove"}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("service_id").GetString().Should().Be("svc-remove");
            doc.RootElement.GetProperty("scope_id").GetString().Should().Be("scope-unbind");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    #endregion

    #region BindingAgentToolSource conditional registration

    [Fact]
    public async Task BindingAgentToolSource_ConditionalRegistration()
    {
        // All ports provided -> all 4 tools
        var sourceAll = new BindingAgentToolSource(
            new BindingToolOptions(),
            commandPort: new StubCommandPort(),
            queryAdapter: new StubQueryAdapter([]),
            unbindAdapter: new StubUnbindAdapter(new ScopeBindingUnbindResult(true, "")));

        var toolsAll = await sourceAll.DiscoverToolsAsync();
        toolsAll.Should().HaveCount(4);
        toolsAll.Should().Contain(t => t is BindingListTool);
        toolsAll.Should().Contain(t => t is BindingStatusTool);
        toolsAll.Should().Contain(t => t is BindingBindTool);
        toolsAll.Should().Contain(t => t is BindingUnbindTool);

        // No ports -> empty
        var sourceNone = new BindingAgentToolSource(new BindingToolOptions());
        var toolsNone = await sourceNone.DiscoverToolsAsync();
        toolsNone.Should().BeEmpty();

        // Only query adapter -> 2 read tools
        var sourceQueryOnly = new BindingAgentToolSource(
            new BindingToolOptions(),
            queryAdapter: new StubQueryAdapter([]));
        var toolsQueryOnly = await sourceQueryOnly.DiscoverToolsAsync();
        toolsQueryOnly.Should().HaveCount(2);
        toolsQueryOnly.Should().Contain(t => t is BindingListTool);
        toolsQueryOnly.Should().Contain(t => t is BindingStatusTool);
    }

    #endregion

    #region Stubs

    private sealed class StubQueryAdapter : IScopeBindingQueryAdapter
    {
        private readonly IReadOnlyList<ScopeBindingEntry> _entries;
        private readonly ScopeBindingHealthStatus? _healthStatus;

        public StubQueryAdapter(
            IReadOnlyList<ScopeBindingEntry> entries,
            ScopeBindingHealthStatus? healthStatus = null)
        {
            _entries = entries;
            _healthStatus = healthStatus;
        }

        public Task<IReadOnlyList<ScopeBindingEntry>> ListAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult(_entries);

        public Task<ScopeBindingHealthStatus?> GetStatusAsync(string scopeId, string serviceId, CancellationToken ct = default) =>
            Task.FromResult(_healthStatus);
    }

    private sealed class StubCommandPort : IScopeBindingCommandPort
    {
        private readonly Action<ScopeBindingUpsertRequest>? _captureRequest;

        public StubCommandPort(Action<ScopeBindingUpsertRequest>? captureRequest = null)
        {
            _captureRequest = captureRequest;
        }

        public Task<ScopeBindingUpsertResult> UpsertAsync(ScopeBindingUpsertRequest request, CancellationToken ct = default)
        {
            _captureRequest?.Invoke(request);

            return Task.FromResult(new ScopeBindingUpsertResult(
                ScopeId: request.ScopeId,
                ServiceId: request.ServiceId ?? "auto-generated-id",
                DisplayName: request.DisplayName ?? "Unnamed",
                RevisionId: "rev-stub",
                ImplementationKind: request.ImplementationKind,
                ExpectedActorId: "actor-stub"));
        }
    }

    private sealed class StubUnbindAdapter : IScopeBindingUnbindAdapter
    {
        private readonly ScopeBindingUnbindResult _result;

        public StubUnbindAdapter(ScopeBindingUnbindResult result)
        {
            _result = result;
        }

        public Task<ScopeBindingUnbindResult> UnbindAsync(string scopeId, string serviceId, CancellationToken ct = default) =>
            Task.FromResult(_result);
    }

    private sealed class StubScopeWorkflowCommandPort : IScopeWorkflowCommandPort
    {
        private readonly Action<ScopeWorkflowUpsertRequest>? _captureRequest;
        private readonly Exception? _exception;

        public StubScopeWorkflowCommandPort(
            Action<ScopeWorkflowUpsertRequest>? captureRequest = null,
            Exception? exception = null)
        {
            _captureRequest = captureRequest;
            _exception = exception;
        }

        public Task<ScopeWorkflowUpsertResult> UpsertAsync(
            ScopeWorkflowUpsertRequest request,
            CancellationToken ct = default)
        {
            if (_exception is not null)
                throw _exception;

            _captureRequest?.Invoke(request);
            return Task.FromResult(new ScopeWorkflowUpsertResult(
                request.ScopeId,
                request.WorkflowId,
                $"service-key-{request.WorkflowId}",
                "rev-result",
                "definition-prefix",
                "expected-actor",
                "expected-deployment",
                DateTimeOffset.UtcNow,
                [new ScopeWorkflowCommandAcceptedHandle("create_revision", "target-actor", "cmd-1", "corr-1")],
                $"/api/scopes/{request.ScopeId}/workflows/{request.WorkflowId}",
                DisplayName: $"Display {request.WorkflowId}",
                WorkflowName: $"workflow-name-{request.WorkflowId}"));
        }
    }

    private sealed class StubScopeWorkflowQueryPort : IScopeWorkflowQueryPort
    {
        private readonly IReadOnlyList<ScopeWorkflowSummary> _listResult;
        private readonly ScopeWorkflowSummary? _getResult;
        private readonly Exception? _exception;

        public StubScopeWorkflowQueryPort(
            IReadOnlyList<ScopeWorkflowSummary>? listResult = null,
            ScopeWorkflowSummary? getResult = null,
            Exception? exception = null)
        {
            _listResult = listResult ?? [];
            _getResult = getResult;
            _exception = exception;
        }

        public Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(string scopeId, CancellationToken ct = default)
        {
            if (_exception is not null)
                throw _exception;

            return Task.FromResult(_listResult);
        }

        public Task<ScopeWorkflowLookupResult> LookupByWorkflowIdAsync(
            string scopeId,
            string workflowId,
            CancellationToken ct = default)
        {
            if (_exception is not null)
                throw _exception;

            return Task.FromResult(_getResult is null
                ? new ScopeWorkflowLookupResult(ScopeWorkflowLookupStatus.NotFound, null, "test_not_found")
                : new ScopeWorkflowLookupResult(ScopeWorkflowLookupStatus.Runnable, _getResult, "test_runnable"));
        }

        public Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(
            string scopeId,
            string workflowId,
            CancellationToken ct = default)
        {
            if (_exception is not null)
                throw _exception;

            return Task.FromResult(_getResult);
        }

        public Task<ScopeWorkflowSummary?> GetByActorIdAsync(
            string scopeId,
            string actorId,
            CancellationToken ct = default) =>
            Task.FromResult<ScopeWorkflowSummary?>(null);
    }

    private sealed class StubExternalWorkflowCapabilityListPort(
        ExternalWorkflowCapabilityDiscoveryResult discovery) : IExternalWorkflowCapabilityListPort
    {
        public ListExternalWorkflowCapabilitiesRequest? Request { get; private set; }

        public Task<ExternalWorkflowCapabilityDiscoveryResult> ListAsync(
            ListExternalWorkflowCapabilitiesRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(discovery.Clone());
        }
    }

    private sealed class StubExternalWorkflowCapabilityReadinessPort : IExternalWorkflowCapabilityReadinessPort
    {
        public InspectExternalWorkflowCapabilityReadinessRequest? Request { get; private set; }

        public Task<ExternalCapabilityReadiness> InspectAsync(
            InspectExternalWorkflowCapabilityReadinessRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            var readiness = new ExternalCapabilityReadiness
            {
                ExecutionMode = request.ExecutionMode,
                Status = ExternalCapabilityReadinessStatus.Ready,
                SelectedSelector = request.Selector.Clone(),
            };
            readiness.SelectedCapability = request.Selector.SelectorCase switch
            {
                ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation => new ExternalWorkflowCapabilityRef
                {
                    NyxIdUserService = new NyxIdUserServiceCapabilityRef
                    {
                        UserServiceId = request.Selector.NyxIdOperation.UserServiceId,
                        ServiceSlugSnapshot = "home-assistant",
                        EndpointId = request.Selector.NyxIdOperation.EndpointId,
                        HttpMethod = "GET",
                        PathTemplate = "/api/states",
                        ContractDigest = "server-derived-contract-digest",
                    },
                },
                ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest => new ExternalWorkflowCapabilityRef
                {
                    NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
                    {
                        Request = request.Selector.NyxIdRequest.Clone(),
                        ServiceSlugSnapshot = "lark",
                        ContractDigest = "server-derived-request-contract-digest",
                    },
                },
                ExternalWorkflowCapabilitySelector.SelectorOneofCase.CodeExecution =>
                    new ExternalWorkflowCapabilityRef
                    {
                        CodeExecution = new CodeExecutionCapabilityRef(),
                    },
                _ => new ExternalWorkflowCapabilityRef(),
            };
            return Task.FromResult(readiness);
        }
    }

    private sealed class StubWorkflowExplicitRequestPreviewService : IWorkflowExplicitRequestPreviewService
    {
        public Task<WorkflowExplicitRequestPreviewResult> PreviewAsync(
            WorkflowExplicitRequestPreviewRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowExplicitRequestPreviewResult(
                request.WorkflowId ?? string.Empty,
                request.RevisionId ?? string.Empty,
                []));
    }

    private static ExternalWorkflowCapabilityDescriptor Descriptor(
        ExternalWorkflowCapabilitySelector selector,
        string displayName) =>
        new()
        {
            Selector = selector,
            DisplayName = displayName,
            ReadOnly = true,
        };

    private static ExternalWorkflowCapabilitySelector NyxIdSelector(
        string userServiceId,
        string endpointId = "read_states") =>
        new()
        {
            NyxIdOperation = new NyxIdOperationSelector
            {
                UserServiceId = userServiceId,
                EndpointId = endpointId,
            },
        };

    private static ScopeWorkflowSummary BuildWorkflowSummary(
        string scopeId,
        string workflowId) =>
        new(
            scopeId,
            workflowId,
            $"Display {workflowId}",
            $"service-key-{workflowId}",
            $"workflow-name-{workflowId}",
            $"actor-{workflowId}",
            "rev-active",
            "deployment-1",
            "active",
            DateTimeOffset.Parse("2026-05-25T00:00:00Z"));

    private static AgentToolExecutionContext OwnerContext(string ownerScopeId, string? scopeId = null) =>
        global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            ["scope_id"] = scopeId ?? ownerScopeId,
            [LLMRequestMetadataKeys.OwnerScopeId] = ownerScopeId,
        });

    private static AgentToolExecutionContext CapabilityContext(
        string ownerScopeId,
        string callerSubject,
        string callerBearer,
        string organizationBearer) =>
        OwnerContext(ownerScopeId) with
        {
            Caller = new AgentToolCallerContext(
                ownerScopeId,
                callerSubject,
                ResponseId: "response-alpha",
                OwnerScopeId: ownerScopeId),
            Credentials = new AgentToolCredentials(
                callerBearer,
                organizationBearer,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", callerSubject),
        };

    private static IAgentToolExecutionPort CreateToolExecutionPort() =>
        new AdmittedAgentToolExecutor(
            new StartingAdmissionLedger(),
            new AppendedAuditTrail(),
            new StableAuditIdentityHasher());

    private sealed class StartingAdmissionLedger : IAgentToolAdmissionLedger
    {
        public Task<AgentToolAdmissionResult> TryStartAsync(
            AgentToolAdmissionFact fact,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentToolAdmissionResult(AgentToolAdmissionStatus.Started));
    }

    private sealed class AppendedAuditTrail : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
    }

    private sealed class StableAuditIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }

    private static string? ReadError(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("error").GetString();
    }

    #endregion
}
