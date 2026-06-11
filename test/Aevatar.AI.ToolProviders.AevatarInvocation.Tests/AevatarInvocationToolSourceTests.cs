using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.AGUI.Contracts;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.AI.ToolProviders.AevatarInvocation.Tests;

public sealed class AevatarInvocationToolSourceTests
{
    [Fact]
    public async Task AddAevatarInvocationTools_ShouldRegisterFourTaggedToolSources()
    {
        var services = new ServiceCollection();
        var harness = new Harness();
        harness.RegisterDependencies(services);

        services.AddAevatarInvocationTools();
        services.AddAevatarInvocationTools();

        await using var provider = services.BuildServiceProvider();
        var sources = provider.GetServices<IAgentToolSource>().ToList();

        sources.OfType<InvokeGAgentToolSource>().Should().ContainSingle();
        sources.OfType<InvokeTeamToolSource>().Should().ContainSingle();
        sources.OfType<StartWorkflowToolSource>().Should().ContainSingle();
        sources.OfType<ObserveRunToolSource>().Should().ContainSingle();

        var tools = new List<IAgentTool>();
        foreach (var source in sources)
            tools.AddRange(await source.DiscoverToolsAsync());

        tools.Select(static tool => tool.Name).Should().BeEquivalentTo(
            "aevatar_invoke_gagent",
            "aevatar_invoke_team",
            "aevatar_start_workflow",
            "aevatar_observe_run");
        tools.All(static tool => tool is IAevatarInvocationTool invocationTool &&
                                 invocationTool.ToolSetTag == AevatarInvocationToolTags.ToolSet)
            .Should()
            .BeTrue();
        tools.Should().OnlyContain(static tool => HasStrictObjectSchema(tool.ParametersSchema));
    }

    [Fact]
    public async Task InvokeGAgentSchema_ShouldAvoidTopLevelCompositionKeywords()
    {
        var tool = await DiscoverSingleAsync(new InvokeGAgentToolSource(new Harness().CreateDispatcher()));
        using var doc = JsonDocument.Parse(tool.ParametersSchema);

        doc.RootElement.TryGetProperty("oneOf", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("anyOf", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("allOf", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("not", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("enum", out _).Should().BeFalse();
        doc.RootElement.GetProperty("properties").TryGetProperty("actor_id", out _).Should().BeTrue();
        doc.RootElement.GetProperty("properties").TryGetProperty("actor_name", out _).Should().BeTrue();
    }

    [Fact]
    public async Task StartWorkflowToolDescription_ShouldMentionInlineWorkflowYamls()
    {
        var tool = await DiscoverSingleAsync(new StartWorkflowToolSource(new Harness().CreateDispatcher()));

        tool.Description.Should().Contain("workflow_yamls");
        tool.Description.Should().Contain("use_skill");
    }

    [Theory]
    [InlineData("aevatar_invoke_gagent", "{}")]
    [InlineData("aevatar_invoke_team", """{"team_id":"team"}""")]
    [InlineData("aevatar_start_workflow", """{"workflow_id":"wf"}""")]
    [InlineData("aevatar_observe_run", "{}")]
    public async Task Tools_ShouldReturnStructuredValidationError(string toolName, string argumentsJson)
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync(toolName);

        using var _ = PushContext(callId: $"call-{toolName}");
        var output = await tool.ExecuteAsync(argumentsJson);

        ErrorCode(output).Should().Be("invalid_arguments");
    }

    [Fact]
    public async Task InvokeGAgent_ShouldResolveActorNameAndDispatchEnvelopeThroughPort()
    {
        var harness = new Harness();
        harness.ActorRegistry.Snapshot = new GAgentActorRegistrySnapshot(
            "scope-1",
            [new GAgentActorGroup("RoleGAgent", ["actor-1"])],
            7,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_gagent");

        using var _ = PushContext(callId: "call-gagent");
        var output = await tool.ExecuteAsync("""
            {
              "actor_name": "RoleGAgent",
              "payload": {
                "prompt": "hello",
                "input_parts": [
                  { "kind": "text", "text": "typed part" }
                ],
                "headers": { "x-test": "yes" }
              },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.ActorRegistry.LastScopeId.Should().Be("scope-1");
        harness.ActorDispatch.Calls.Should().ContainSingle();
        var call = harness.ActorDispatch.Calls.Single();
        call.ActorId.Should().Be("actor-1");
        call.Envelope.Route.GetTargetActorId().Should().Be("actor-1");
        call.Envelope.Propagation.CorrelationId.Should().Be("call-gagent");

        var payload = call.Envelope.Payload.Unpack<ChatRequestEvent>();
        payload.Prompt.Should().Be("hello");
        payload.SessionId.Should().Be("call-gagent");
        payload.ScopeId.Should().Be("scope-1");
        payload.Headers["x-test"].Should().Be("yes");
        payload.Headers.Should().NotContainKey(LLMRequestMetadataKeys.ScopeId);
        payload.Metadata["x-test"].Should().Be("yes");
        ShouldNotCarryTrustedCallerValues(payload.Headers);
        ShouldNotCarryTrustedCallerValues(payload.Metadata);
        payload.InputParts.Should().ContainSingle();
        payload.InputParts[0].Kind.Should().Be(ChatContentPartKind.Text);
        payload.InputParts[0].Text.Should().Be("typed part");
        payload.ToolContext.Caller.OwnerSubject.Should().Be("owner-1");
        payload.ToolContext.Credentials.NyxIdAccessToken.Should().Be("access-token");
        payload.LlmControl.NyxIdAccessToken.Should().Be("access-token");
        payload.LlmControl.ModelOverride.Should().Be("model-1");

        var result = Read(output);
        result.GetProperty("run_id").GetString().Should().Be("call-gagent");
        result.GetProperty("stream_topic").GetString().Should().Be("aevatar://actors/actor-1/runs/call-gagent");
        result.GetProperty("wait").GetString().Should().Be("stream");
    }

    [Fact]
    public async Task InvokeGAgentForChatRun_ShouldMapTypedControlFields()
    {
        var harness = new Harness();
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(callId: "call-gagent-typed");
        var request = BuildChatRunRequest(
            "response-gagent",
            "call-gagent-typed-tool",
            "aevatar_invoke_gagent",
            """
            {
              "actor_id": "actor-1",
              "payload": { "prompt": "hello" },
              "wait": "stream"
            }
            """);
        var result = await dispatcher.InvokeGAgentForChatRunAsync(request, request.ArgumentsJson);

        result.ResponseId.Should().Be("response-gagent");
        result.ToolCall.Should().BeSameAs(request.ToolCall);
        result.ArgumentsJson.Should().Be(request.ArgumentsJson);
        result.ToolExecutionResultJson.Should().NotBeNullOrWhiteSpace();
        result.RunId.Should().Be("call-gagent-typed");
        result.ScopeId.Should().Be("scope-1");
        result.WaitMode.Should().Be(ChatRunSubRunWaitMode.Stream);
        result.Status.Should().Be("streaming");
        result.ActorId.Should().Be("actor-1");
        result.StreamTopic.Should().Be("aevatar://actors/actor-1/runs/call-gagent-typed");
        result.CompletionObserved.Should().BeFalse();
        result.CompletionResultJson.Should().BeEmpty();
        result.ErrorCode.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeGAgent_ShouldRejectActorIdOutsideCallerScope()
    {
        var harness = new Harness();
        harness.ActorRegistry.Snapshot = new GAgentActorRegistrySnapshot(
            "scope-1",
            [new GAgentActorGroup("RoleGAgent", ["actor-1"])],
            7,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_gagent");

        using var _ = PushContext(callId: "call-gagent-outside-scope");
        var output = await tool.ExecuteAsync("""
            {
              "actor_id": "actor-2",
              "payload": {
                "prompt": "hello"
              }
            }
            """);

        ErrorCode(output).Should().Be("actor_not_found");
        harness.ActorRegistry.LastScopeId.Should().Be("scope-1");
        harness.ActorDispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeGAgent_ShouldNotResolveCallerScopeFromExternalMetadata()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_gagent");

        using var _ = AgentToolContextScope.Push(new AgentToolExecutionContext(
            new AgentToolRequestIdentity("request-1", "call-gagent-no-scope"),
            new AgentToolCredentials("access-token", "org-token", "sender-token"),
            new AgentToolCallerContext(null, "owner-1", "response-1"),
            new AgentToolChannelContext("telegram", "sender-1", "registration-scope-1", "message-1", "platform-message-1"),
            new AgentToolSenderBindingContext("binding-1"),
            new LLMRequestRoutingContext("model-1", "route-1", 4, "memory"),
            new AgentToolConnectedServicesContext("""{"service":"ctx"}"""),
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["scope_id"] = "metadata-scope",
                [LLMRequestMetadataKeys.ScopeId] = "metadata-aevatar-scope",
                ["external"] = "value",
            }));

        var output = await tool.ExecuteAsync("""
            {
              "actor_id": "actor-1",
              "payload": { "prompt": "hello" },
              "wait": "ack"
            }
            """);

        ErrorCode(output).Should().Be("caller_scope_unavailable");
        harness.ActorRegistry.LastScopeId.Should().BeNull();
        harness.ActorDispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeGAgent_ShouldRejectPayloadHeaderCredentialOverrides()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_gagent");

        using var _ = PushContext(callId: "call-gagent-credentials");
        var output = await tool.ExecuteAsync($$"""
            {
              "actor_id": "actor-1",
              "payload": {
                "prompt": "hello",
                "headers": {
                  "{{LLMRequestMetadataKeys.OwnerSubject}}": "evil-owner",
                  "{{LLMRequestMetadataKeys.NyxIdAccessToken}}": "evil-access-token",
                  "{{LLMRequestMetadataKeys.SenderNyxIdAccessToken}}": "evil-sender-token",
                  "{{LLMRequestMetadataKeys.ScopeId}}": "evil-scope",
                  "scope_id": "evil-legacy-scope"
                }
              },
              "wait": "ack"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        var payload = harness.ActorDispatch.Calls.Single().Envelope.Payload.Unpack<ChatRequestEvent>();
        ShouldNotCarryTrustedCallerValues(payload.Headers);
        ShouldNotCarryTrustedCallerValues(payload.Metadata);
        payload.ScopeId.Should().Be("scope-1");
        payload.ToolContext.Caller.OwnerSubject.Should().Be("owner-1");
        payload.ToolContext.Credentials.NyxIdAccessToken.Should().Be("access-token");
        payload.LlmControl.NyxIdAccessToken.Should().Be("access-token");
        payload.LlmControl.NyxIdRoutePreference.Should().Be("route-1");
    }

    [Fact]
    public async Task InvokeGAgent_WhenDispatchFails_ShouldReturnStructuredError()
    {
        var harness = new Harness();
        harness.ActorDispatch.Failure = new InvalidOperationException("dispatch broke");
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_gagent");

        using var _ = PushContext(callId: "call-fail");
        var output = await tool.ExecuteAsync("""
            {
              "actor_id": "actor-1",
              "payload": { "prompt": "hello" },
              "wait": "ack"
            }
            """);

        ErrorCode(output).Should().Be("dispatch_failed");
    }

    [Fact]
    public async Task InvokeTeam_ShouldResolveMemberAndReturnStreamTopic()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_team");

        using var _ = PushContext(callId: "call-team");
        var output = await tool.ExecuteAsync("""
            {
              "team_id": "team-1",
              "endpoint_id": "entry",
              "payload": {
                "prompt": "go",
                "headers": { "h": "v" }
              },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.TeamResolver.LastScopeId.Should().Be("scope-1");
        harness.TeamResolver.LastTeamId.Should().Be("team-1");
        harness.TeamResolver.LastEndpointId.Should().Be("entry");
        harness.TeamInvocation.Request.Should().NotBeNull();
        harness.TeamInvocation.Request!.Identity.TenantId.Should().Be("scope-1");
        harness.TeamInvocation.Request.Identity.ServiceId.Should().Be("service-1");
        harness.TeamInvocation.Request.EndpointId.Should().Be("entry");
        harness.TeamInvocation.Request.Input.Prompt.Should().Be("go");
        harness.TeamInvocation.Request.Input.Headers.Should().Contain("h", "v");
        ShouldNotCarryTrustedCallerValues(harness.TeamInvocation.Request.Input.Headers);
        ShouldCarryTypedToolControlValues(
            harness.TeamInvocation.Request.Input.ToolContext,
            harness.TeamInvocation.Request.Input.LlmControl);
        harness.TeamInvocation.Request.Input.Caller!.TenantId.Should().Be("scope-1");

        var result = Read(output);
        result.GetProperty("run_id").GetString().Should().Be("team-command");
        result.GetProperty("service_id").GetString().Should().Be("service-1");
        result.GetProperty("stream_topic").GetString().Should().Be("aevatar://scopes/scope-1/services/service-1/runs/team-command");
    }

    [Fact]
    public async Task InvokeTeamForChatRun_WaitComplete_ShouldReturnAcceptedReceiptWithoutFoldedCompletion()
    {
        var harness = new Harness();
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(callId: "call-team-typed");
        var request = BuildChatRunRequest(
            "response-team",
            "call-team-typed-tool",
            "aevatar_invoke_team",
            """
            {
              "team_id": "team-1",
              "endpoint_id": "entry",
              "payload": { "prompt": "go" },
              "wait": "complete"
            }
            """);
        var result = await dispatcher.InvokeTeamForChatRunAsync(request, request.ArgumentsJson);

        result.ResponseId.Should().Be("response-team");
        result.ToolCall.Should().BeSameAs(request.ToolCall);
        result.ArgumentsJson.Should().Be(request.ArgumentsJson);
        result.ToolExecutionResultJson.Should().NotBeNullOrWhiteSpace();
        result.RunId.Should().Be("team-command");
        result.ScopeId.Should().Be("scope-1");
        result.WaitMode.Should().Be(ChatRunSubRunWaitMode.Complete);
        result.Status.Should().Be("accepted");
        result.StreamTopic.Should().BeEmpty();
        result.ActorId.Should().Be("team-actor");
        result.ServiceId.Should().Be("service-1");
        result.EndpointId.Should().Be("entry");
        result.CompletionObserved.Should().BeFalse();
        result.CompletionResultJson.Should().BeEmpty();
        result.ErrorCode.Should().BeEmpty();

        using var output = JsonDocument.Parse(result.ToolExecutionResultJson);
        output.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        output.RootElement.GetProperty("wait").GetString().Should().Be("complete");
        output.RootElement.TryGetProperty("result", out var foldedResult).Should().BeFalse();
        foldedResult.ValueKind.Should().Be(JsonValueKind.Undefined);
    }

    [Fact]
    public async Task InvokeTeam_ShouldIgnoreMigratedScopeIdArgument()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_team");

        using var _ = PushContext(callId: "call-team-scope-prefill");
        var output = await tool.ExecuteAsync("""
            {
              "team_id": "team-1",
              "endpoint_id": "entry",
              "scope_id": "legacy-policy-scope",
              "payload": { "prompt": "go" },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.TeamResolver.LastScopeId.Should().Be("scope-1");
        harness.TeamInvocation.Request!.Identity.TenantId.Should().Be("scope-1");
        ShouldNotCarryTrustedCallerValues(harness.TeamInvocation.Request.Input.Headers);
        harness.TeamInvocation.Request.Input.ToolContext!.Caller.ScopeId.Should().Be("scope-1");
    }

    [Fact]
    public async Task InvokeGAgent_WaitComplete_ShouldDispatchAndReturnCompletionReceipt()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_gagent");

        using var _ = PushContext(callId: "call-gagent-complete");
        var output = await tool.ExecuteAsync("""
            {
              "actor_id": "actor-1",
              "payload": { "prompt": "hello" },
              "wait": "complete"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        var result = Read(output);
        result.GetProperty("run_id").GetString().Should().Be("call-gagent-complete");
        result.GetProperty("status").GetString().Should().Be("streaming");
        result.GetProperty("wait").GetString().Should().Be("complete");
    }

    [Fact]
    public async Task InvokeTeam_ShouldRejectPayloadHeaderCredentialOverrides()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_team");

        using var _ = PushContext(callId: "call-team-credentials");
        var output = await tool.ExecuteAsync($$"""
            {
              "team_id": "team-1",
              "endpoint_id": "entry",
              "payload": {
                "prompt": "go",
                "headers": {
                  "{{LLMRequestMetadataKeys.OwnerSubject}}": "evil-owner",
                  "{{LLMRequestMetadataKeys.NyxIdAccessToken}}": "evil-access-token",
                  "{{LLMRequestMetadataKeys.SenderNyxIdAccessToken}}": "evil-sender-token",
                  "{{LLMRequestMetadataKeys.ScopeId}}": "evil-scope",
                  "scope_id": "evil-legacy-scope"
                }
              },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        ShouldNotCarryTrustedCallerValues(harness.TeamInvocation.Request!.Input.Headers);
        ShouldCarryTypedToolControlValues(
            harness.TeamInvocation.Request.Input.ToolContext,
            harness.TeamInvocation.Request.Input.LlmControl);
    }

    [Fact]
    public async Task InvokeTeam_ShouldKeepProtectedTrustedKeysOutOfStaticInvocationHeaders()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_team");

        using var _ = PushContext(callId: "call-team-protected-headers");
        var output = await tool.ExecuteAsync($$"""
            {
              "team_id": "team-1",
              "endpoint_id": "entry",
              "payload": {
                "prompt": "go",
                "headers": {
                  "{{LLMRequestMetadataKeys.RequestId}}": "evil-request",
                  "{{LLMRequestMetadataKeys.CallId}}": "evil-call",
                  "{{LLMRequestMetadataKeys.OwnerSubject}}": "evil-owner",
                  "{{LLMRequestMetadataKeys.ResponseId}}": "evil-response",
                  "{{LLMRequestMetadataKeys.NyxIdAccessToken}}": "evil-access-token",
                  "{{LLMRequestMetadataKeys.NyxIdOrgToken}}": "evil-org-token",
                  "{{LLMRequestMetadataKeys.SenderNyxIdAccessToken}}": "evil-sender-token",
                  "{{LLMRequestMetadataKeys.SenderBindingId}}": "evil-binding",
                  "{{LLMRequestMetadataKeys.ScopeId}}": "evil-scope",
                  "{{LLMRequestMetadataKeys.ModelOverride}}": "evil-model",
                  "{{LLMRequestMetadataKeys.NyxIdRoutePreference}}": "evil-route",
                  "{{LLMRequestMetadataKeys.MaxToolRoundsOverride}}": "99",
                  "{{LLMRequestMetadataKeys.ConnectedServicesContext}}": "evil-services",
                  "scope_id": "evil-legacy-scope",
                  "client-note": "open-extension"
                }
              },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.TeamInvocation.Request.Should().NotBeNull();
        var request = harness.TeamInvocation.Request!;
        request.Input.Headers.Should().Contain("client-note", "open-extension");
        request.Identity.TenantId.Should().Be("scope-1");
        request.Input.Caller!.TenantId.Should().Be("scope-1");
        ShouldNotCarryTrustedCallerValues(request.Input.Headers);
    }

    [Fact]
    public async Task InvokeTeam_WhenInvocationReturnsFailureBeforeAcceptance_ShouldReturnStructuredError()
    {
        var harness = new Harness();
        harness.TeamInvocation.Result = new StaticGAgentStreamInvocationResult(
            null,
            GAgentDraftRunStartError.ActorTypeMismatch,
            GAgentDraftRunCompletionStatus.Unknown,
            false);
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_team");

        using var _ = PushContext(callId: "call-team-failed-before-acceptance");
        var output = await tool.ExecuteAsync("""
            {
              "team_id": "team-1",
              "endpoint_id": "entry",
              "payload": { "prompt": "go" },
              "wait": "ack"
            }
            """);

        ErrorCode(output).Should().Be("actortypemismatch");
        harness.TeamInvocation.Request.Should().NotBeNull();
    }

    [Fact]
    public async Task InvokeTeam_WhenResolverFails_ShouldReturnStructuredError()
    {
        var harness = new Harness();
        harness.TeamResolver.Failure = new TeamEntryMemberResolutionException(
            TeamEntryMemberErrorCodes.TeamNotFound,
            "scope-1",
            "team-missing",
            "team missing");
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_team");

        using var _ = PushContext(callId: "call-team-fail");
        var output = await tool.ExecuteAsync("""
            {
              "team_id": "team-missing",
              "endpoint_id": "entry",
              "payload": { "prompt": "go" }
            }
            """);

        ErrorCode(output).Should().Be("team_not_found");
    }

    [Fact]
    public async Task StartWorkflow_ShouldPropagateScopeAndReturnStreamReceipt()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(callId: "call-workflow");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": {
                "prompt": "run workflow",
                "headers": { "x-workflow": "yes" }
              },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        harness.WorkflowDispatch.Command!.Source.WorkflowName.Should().Be("wf-main");
        harness.WorkflowDispatch.Command.Prompt.Should().Be("run workflow");
        harness.WorkflowDispatch.Command.ScopeId.Should().Be("scope-1");
        harness.WorkflowDispatch.Command.Metadata.Should().Contain("x-workflow", "yes");
        ShouldNotCarryTrustedCallerValues(harness.WorkflowDispatch.Command.Metadata);
        ShouldCarryWorkflowLlmControlValues(harness.WorkflowDispatch.Command.LlmControl);
        ShouldCarryTypedTrustedCallerValues(harness.WorkflowDispatch.Command);

        var result = Read(output);
        result.GetProperty("run_id").GetString().Should().Be("wf-command");
        result.GetProperty("actor_id").GetString().Should().Be("workflow-actor");
        result.GetProperty("stream_topic").GetString().Should().Be("aevatar://actors/workflow-actor/runs/wf-command");
    }

    [Fact]
    public async Task StartWorkflowForChatRun_ShouldMapTypedControlFields()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(callId: "call-workflow-typed");
        var request = BuildChatRunRequest(
            "response-workflow",
            "call-workflow-typed-tool",
            "aevatar_start_workflow",
            """
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "stream"
            }
            """);
        var result = await dispatcher.StartWorkflowForChatRunAsync(request, request.ArgumentsJson);

        result.ResponseId.Should().Be("response-workflow");
        result.ToolCall.Should().BeSameAs(request.ToolCall);
        result.ArgumentsJson.Should().Be(request.ArgumentsJson);
        result.ToolExecutionResultJson.Should().NotBeNullOrWhiteSpace();
        result.RunId.Should().Be("wf-command");
        result.ScopeId.Should().Be("scope-1");
        result.WaitMode.Should().Be(ChatRunSubRunWaitMode.Stream);
        result.Status.Should().Be("streaming");
        result.ActorId.Should().Be("workflow-actor");
        result.StreamTopic.Should().Be("aevatar://actors/workflow-actor/runs/wf-command");
        result.CompletionObserved.Should().BeFalse();
        result.CompletionResultJson.Should().BeEmpty();
        result.ErrorCode.Should().BeEmpty();
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        ShouldCarryTypedTrustedCallerValues(harness.WorkflowDispatch.Command!);
        ShouldNotCarryTrustedCallerValues(harness.WorkflowDispatch.Command!.Metadata);
    }

    [Fact]
    public async Task InvokeGAgentForChatRun_WhenValidationFails_ShouldMapTypedErrorCodeAndPreserveRequest()
    {
        var harness = new Harness();
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(callId: "call-gagent-invalid");
        var request = BuildChatRunRequest(
            "response-invalid",
            "call-invalid-tool",
            "aevatar_invoke_gagent",
            """{"actor_id":"actor-1"}""");
        var result = await dispatcher.InvokeGAgentForChatRunAsync(request, request.ArgumentsJson);

        result.ResponseId.Should().Be("response-invalid");
        result.ToolCall.Should().BeSameAs(request.ToolCall);
        result.ArgumentsJson.Should().Be(request.ArgumentsJson);
        result.ToolExecutionResultJson.Should().NotBeNullOrWhiteSpace();
        result.ErrorCode.Should().Be("invalid_arguments");
        result.RunId.Should().BeEmpty();
        result.ScopeId.Should().BeEmpty();
        result.WaitMode.Should().Be(ChatRunSubRunWaitMode.Unspecified);
        result.CompletionObserved.Should().BeFalse();
    }

    [Fact]
    public async Task aevatar_start_workflow_with_actor_id_dispatches_definition_actor_source()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(callId: "call-workflow-actor");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": " wf-main ",
              "actor_id": " workflow-definition-actor ",
              "inputs": {
                "prompt": "run workflow"
              },
              "wait": "ack"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        harness.WorkflowDispatch.Command!.Source.Kind.Should().Be(WorkflowChatSourceKind.DefinitionActor);
        harness.WorkflowDispatch.Command.Source.ActorId.Should().Be("workflow-definition-actor");
        harness.WorkflowDispatch.Command.Source.WorkflowName.Should().Be("wf-main");
    }

    [Fact]
    public async Task aevatar_start_workflow_with_workflow_yamls_dispatches_inline_yaml_bundle_source_and_trims_blank_entries()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(callId: "call-workflow-yamls");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": " wf-main ",
              "actor_id": " workflow-definition-actor ",
              "workflow_yamls": [
                "  name: first\nsteps: []  ",
                "   ",
                "",
                "name: second\nsteps: []\n"
              ],
              "inputs": {
                "prompt": "run workflow"
              },
              "wait": "ack"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        harness.WorkflowDispatch.Command!.Source.Kind.Should().Be(WorkflowChatSourceKind.InlineYamlBundle);
        harness.WorkflowDispatch.Command.Source.ActorId.Should().Be("workflow-definition-actor");
        harness.WorkflowDispatch.Command.Source.WorkflowName.Should().Be("wf-main");
        harness.WorkflowDispatch.Command.Source.WorkflowYamls.Should().Equal(
            "name: first\nsteps: []",
            "name: second\nsteps: []");
    }

    [Fact]
    public async Task StartWorkflow_ShouldRejectPayloadHeaderCredentialOverrides()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(callId: "call-workflow-credentials");
        var output = await tool.ExecuteAsync($$"""
            {
              "workflow_id": "wf-main",
              "inputs": {
                "prompt": "run workflow",
                "headers": {
                  "{{LLMRequestMetadataKeys.OwnerSubject}}": "evil-owner",
                  "{{LLMRequestMetadataKeys.NyxIdAccessToken}}": "evil-access-token",
                  "{{LLMRequestMetadataKeys.SenderNyxIdAccessToken}}": "evil-sender-token",
                  "{{LLMRequestMetadataKeys.ScopeId}}": "evil-scope",
                  "scope_id": "evil-legacy-scope"
                }
              },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        ShouldNotCarryTrustedCallerValues(harness.WorkflowDispatch.Command!.Metadata);
        ShouldCarryWorkflowLlmControlValues(harness.WorkflowDispatch.Command.LlmControl);
        ShouldCarryTypedTrustedCallerValues(harness.WorkflowDispatch.Command);
    }

    [Fact]
    public async Task StartWorkflow_ShouldKeepTrustedControlInTypedFields_NotMetadataBag()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(callId: "call-workflow-trusted-control");
        var output = await tool.ExecuteAsync($$"""
            {
              "workflow_id": "wf-main",
              "inputs": {
                "prompt": "run workflow",
                "headers": {
                  "{{LLMRequestMetadataKeys.RequestId}}": "evil-request",
                  "{{LLMRequestMetadataKeys.CallId}}": "evil-call",
                  "{{LLMRequestMetadataKeys.OwnerSubject}}": "evil-owner",
                  "{{LLMRequestMetadataKeys.ResponseId}}": "evil-response",
                  "{{LLMRequestMetadataKeys.NyxIdAccessToken}}": "evil-access-token",
                  "{{LLMRequestMetadataKeys.NyxIdOrgToken}}": "evil-org-token",
                  "{{LLMRequestMetadataKeys.SenderNyxIdAccessToken}}": "evil-sender-token",
                  "{{LLMRequestMetadataKeys.SenderBindingId}}": "evil-binding",
                  "{{LLMRequestMetadataKeys.ScopeId}}": "evil-scope",
                  "{{LLMRequestMetadataKeys.ModelOverride}}": "evil-model",
                  "{{LLMRequestMetadataKeys.NyxIdRoutePreference}}": "evil-route",
                  "{{LLMRequestMetadataKeys.MaxToolRoundsOverride}}": "99",
                  "{{LLMRequestMetadataKeys.ConnectedServicesContext}}": "evil-services",
                  "scope_id": "evil-legacy-scope",
                  "client-note": "open-extension"
                }
              },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        var command = harness.WorkflowDispatch.Command!;
        command.ScopeId.Should().Be("scope-1");
        command.LlmControl.Should().NotBeNull("trusted LLM routing must use the typed workflow control object");
        command.Metadata.Should().Contain("client-note", "open-extension");
        ShouldNotCarryTrustedCallerValues(command.Metadata);
        ShouldCarryTypedTrustedCallerValues(command);
    }

    [Fact]
    public async Task StartWorkflow_WhenDispatchRejects_ShouldReturnStructuredError()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Failure(WorkflowChatRunStartError.WorkflowNotFound);
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(callId: "call-workflow-fail");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "missing",
              "inputs": { "prompt": "run workflow" }
            }
            """);

        ErrorCode(output).Should().Be("workflownotfound");
    }

    [Fact]
    public async Task ObserveRun_ShouldReadExistingTerminalSnapshot()
    {
        var harness = new Harness();
        harness.TerminalQuery.ByCorrelationId = new GAgentRunTerminalSnapshot(
            "actor-1",
            "session-1",
            "run-1",
            GAgentRunTerminalInteractionKind.DraftRun,
            GAgentRunTerminalStatus.RunFinished,
            "done",
            "finished",
            3,
            "event-3",
            DateTimeOffset.UtcNow);
        var tool = await harness.DiscoverToolAsync("aevatar_observe_run");

        using var _ = PushContext(callId: "call-observe");
        var output = await tool.ExecuteAsync("""
            {
              "run_id": "run-1",
              "actor_id": "actor-1"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.TerminalQuery.LastActorId.Should().Be("actor-1");
        harness.TerminalQuery.LastCorrelationId.Should().Be("run-1");
        var result = Read(output);
        result.GetProperty("run_id").GetString().Should().Be("run-1");
        result.GetProperty("status").GetString().Should().Be(nameof(GAgentRunTerminalStatus.RunFinished));
        result.GetProperty("recent_events").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task ObserveRun_WhenCallerScopeIsUnavailable_ShouldFastFail()
    {
        var harness = new Harness();
        harness.TerminalQuery.ByCorrelationId = new GAgentRunTerminalSnapshot(
            "actor-1",
            "session-1",
            "run-1",
            GAgentRunTerminalInteractionKind.DraftRun,
            GAgentRunTerminalStatus.RunFinished,
            "done",
            "finished",
            3,
            "event-3",
            DateTimeOffset.UtcNow);
        var tool = await harness.DiscoverToolAsync("aevatar_observe_run");

        using var _ = PushContext(callId: "call-observe-no-scope", scopeId: null);
        var output = await tool.ExecuteAsync("""
            {
              "run_id": "run-1",
              "actor_id": "actor-1"
            }
            """);

        ErrorCode(output).Should().Be("caller_scope_unavailable");
        harness.TerminalQuery.LastActorId.Should().BeNull();
    }

    [Fact]
    public async Task ObserveRun_WhenNoReadModelMatches_ShouldReturnStructuredError()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_observe_run");

        using var _ = PushContext(callId: "call-observe-missing");
        var output = await tool.ExecuteAsync("""{"run_id":"missing","actor_id":"actor-1"}""");

        ErrorCode(output).Should().Be("run_not_found");
    }

    [Fact]
    public async Task AevatarInvocationTools_ShouldNotExposeGenericReadModelQueryWrapper()
    {
        var harness = new Harness();

        var tools = await harness.DiscoverAllToolsAsync();

        tools.Select(static tool => tool.Name).Should().NotContain("aevatar_query_readmodel");
    }

    private static bool HasStrictObjectSchema(string schema)
    {
        using var doc = JsonDocument.Parse(schema);
        return doc.RootElement.GetProperty("type").GetString() == "object" &&
               doc.RootElement.GetProperty("additionalProperties").ValueKind == JsonValueKind.False;
    }

    private static async Task<IAgentTool> DiscoverSingleAsync(IAgentToolSource source)
    {
        var tools = await source.DiscoverToolsAsync();
        return tools.Should().ContainSingle().Subject;
    }

    private static JsonElement Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string ErrorCode(string json) =>
        ErrorCodeOrNull(json) ?? throw new InvalidOperationException($"Expected an error result: {json}");

    private static string? ErrorCodeOrNull(string json)
    {
        var root = Read(json);
        return root.TryGetProperty("error", out var error) &&
               error.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }

    private static void ShouldCarryTypedTrustedCallerValues(WorkflowChatRunRequest command)
    {
        command.ScopeId.Should().Be("scope-1");
        ShouldCarryWorkflowLlmControlValues(command.LlmControl);
    }

    private static void ShouldNotCarryTrustedCallerValues(IEnumerable<KeyValuePair<string, string>>? metadata)
    {
        metadata.Should().NotBeNull();
        var values = metadata!.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
        values.Should().NotContainKey(LLMRequestMetadataKeys.RequestId);
        values.Should().NotContainKey(LLMRequestMetadataKeys.CallId);
        values.Should().NotContainKey(LLMRequestMetadataKeys.OwnerSubject);
        values.Should().NotContainKey(LLMRequestMetadataKeys.ResponseId);
        values.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        values.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdOrgToken);
        values.Should().NotContainKey(LLMRequestMetadataKeys.SenderNyxIdAccessToken);
        values.Should().NotContainKey(LLMRequestMetadataKeys.SenderBindingId);
        values.Should().NotContainKey(LLMRequestMetadataKeys.ScopeId);
        values.Should().NotContainKey(LLMRequestMetadataKeys.ModelOverride);
        values.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdRoutePreference);
        values.Should().NotContainKey(LLMRequestMetadataKeys.MaxToolRoundsOverride);
        values.Should().NotContainKey(LLMRequestMetadataKeys.ConnectedServicesContext);
        values.Should().NotContainKey("scope_id");
        values.Should().NotContainKey("external");
    }

    private static void ShouldCarryTypedToolControlValues(
        AgentToolExecutionContext? toolContext,
        LLMControlContext? llmControl)
    {
        toolContext.Should().NotBeNull();
        toolContext!.Caller.ScopeId.Should().Be("scope-1");
        toolContext.Caller.OwnerSubject.Should().Be("owner-1");
        toolContext.Credentials.NyxIdAccessToken.Should().Be("access-token");
        toolContext.Credentials.SenderNyxIdAccessToken.Should().Be("sender-token");
        toolContext.Routing.ModelOverride.Should().Be("model-1");
        toolContext.Routing.NyxIdRoutePreference.Should().Be("route-1");

        llmControl.Should().NotBeNull();
        llmControl!.NyxIdAccessToken.Should().Be("access-token");
        llmControl.SenderNyxIdAccessToken.Should().Be("sender-token");
        llmControl.ModelOverride.Should().Be("model-1");
        llmControl.NyxIdRoutePreference.Should().Be("route-1");
    }

    private static void ShouldCarryWorkflowLlmControlValues(WorkflowLlmControl? llmControl)
    {
        llmControl.Should().NotBeNull();
        llmControl!.ModelOverride.Should().Be("model-1");
        llmControl.MaxToolRoundsOverride.Should().Be(4);
        llmControl.UserMemoryPrompt.Should().Be("memory");
    }

    private static AgentToolContextScope PushContext(string callId, string? scopeId = "scope-1") =>
        AgentToolContextScope.Push(new AgentToolExecutionContext(
            new AgentToolRequestIdentity("request-1", callId),
            new AgentToolCredentials("access-token", "org-token", "sender-token"),
            new AgentToolCallerContext(scopeId, "owner-1", "response-1"),
            new AgentToolChannelContext("telegram", "sender-1", "registration-scope-1", "message-1", "platform-message-1"),
            new AgentToolSenderBindingContext("binding-1"),
            new LLMRequestRoutingContext("model-1", "route-1", 4, "memory"),
            new AgentToolConnectedServicesContext("""{"service":"ctx"}"""),
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["external"] = "value",
            }));

    private static ChatRunToolCompletionRequest BuildChatRunRequest(
        string responseId,
        string toolCallId,
        string toolName,
        string argumentsJson)
    {
        var toolCall = new ToolCall
        {
            Id = toolCallId,
            Name = toolName,
            ArgumentsJson = argumentsJson,
        };
        return new ChatRunToolCompletionRequest(
            responseId,
            "model-test",
            [ChatMessage.User("run tool")],
            toolCall,
            argumentsJson,
            string.Empty,
            3);
    }

    private static ServiceRunSnapshot BuildServiceRun(
        string scopeId,
        string serviceId,
        string runId,
        string commandId) =>
        new(
            scopeId,
            serviceId,
            "service-key",
            runId,
            commandId,
            commandId,
            "entry",
            ServiceImplementationKind.Static,
            "target-actor",
            "revision-1",
            "deployment-1",
            ServiceRunStatus.Accepted,
            "actor-1",
            scopeId,
            ScopeServiceIdentityDefaults.ServiceAppId,
            ScopeServiceIdentityDefaults.ServiceNamespace,
            1,
            "event-1",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private sealed class Harness
    {
        public RecordingActorDispatchPort ActorDispatch { get; } = new();
        public RecordingActorRegistryQueryPort ActorRegistry { get; } = new();
        public RecordingTeamEntryMemberResolver TeamResolver { get; } = new();
        public RecordingStaticGAgentInvocationPort TeamInvocation { get; } = new();
        public RecordingWorkflowDispatchService WorkflowDispatch { get; } = new();
        public RecordingServiceRunQueryPort ServiceRunQuery { get; } = new();
        public RecordingTerminalQueryPort TerminalQuery { get; } = new();
        public StubWorkflowExecutionQueryService WorkflowQuery { get; } = new();

        public AevatarInvocationDispatcher CreateDispatcher() =>
            new(
                ActorDispatch,
                ActorRegistry,
                TeamResolver,
                TeamInvocation,
                WorkflowDispatch,
                ServiceRunQuery,
                TerminalQuery,
                WorkflowQuery);

        public void RegisterDependencies(IServiceCollection services)
        {
            services.AddSingleton<IActorDispatchPort>(ActorDispatch);
            services.AddSingleton<IGAgentActorRegistryQueryPort>(ActorRegistry);
            services.AddSingleton<ITeamEntryMemberResolver>(TeamResolver);
            services.AddSingleton<IStaticGAgentStreamInvocationPort<AGUIEvent>>(TeamInvocation);
            services.AddSingleton<ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>(WorkflowDispatch);
            services.AddSingleton<IServiceRunQueryPort>(ServiceRunQuery);
            services.AddSingleton<IGAgentRunTerminalQueryPort>(TerminalQuery);
            services.AddSingleton<IWorkflowExecutionQueryApplicationService>(WorkflowQuery);
        }

        public async Task<IAgentTool> DiscoverToolAsync(string toolName)
        {
            IAgentToolSource source = toolName switch
            {
                "aevatar_invoke_gagent" => new InvokeGAgentToolSource(CreateDispatcher()),
                "aevatar_invoke_team" => new InvokeTeamToolSource(CreateDispatcher()),
                "aevatar_start_workflow" => new StartWorkflowToolSource(CreateDispatcher()),
                "aevatar_observe_run" => new ObserveRunToolSource(CreateDispatcher()),
                _ => throw new ArgumentOutOfRangeException(nameof(toolName), toolName, null),
            };
            var tools = await source.DiscoverToolsAsync();
            return tools.Single(tool => tool.Name == toolName);
        }

        public async Task<IReadOnlyList<IAgentTool>> DiscoverAllToolsAsync()
        {
            IAgentToolSource[] sources =
            [
                new InvokeGAgentToolSource(CreateDispatcher()),
                new InvokeTeamToolSource(CreateDispatcher()),
                new StartWorkflowToolSource(CreateDispatcher()),
                new ObserveRunToolSource(CreateDispatcher()),
            ];
            var tools = new List<IAgentTool>();
            foreach (var source in sources)
                tools.AddRange(await source.DiscoverToolsAsync());
            return tools;
        }
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];
        public Exception? Failure { get; set; }

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            if (Failure != null)
                throw Failure;

            Calls.Add((actorId, envelope.Clone()));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingActorRegistryQueryPort : IGAgentActorRegistryQueryPort
    {
        public string? LastScopeId { get; private set; }
        public GAgentActorRegistrySnapshot Snapshot { get; set; } = new(
            "scope-1",
            [new GAgentActorGroup("RoleGAgent", ["actor-1"])],
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        public Task<GAgentActorRegistrySnapshot> ListActorsAsync(
            string scopeId,
            CancellationToken cancellationToken = default)
        {
            LastScopeId = scopeId;
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class RecordingTeamEntryMemberResolver : ITeamEntryMemberResolver
    {
        public string? LastScopeId { get; private set; }
        public string? LastTeamId { get; private set; }
        public string? LastEndpointId { get; private set; }
        public TeamEntryMemberResolution Resolution { get; set; } = new("scope-1", "team-1", "member-1", "service-1");
        public TeamEntryMemberResolutionException? Failure { get; set; }

        public Task<TeamEntryMemberResolution> ResolveAsync(
            string scopeId,
            string teamId,
            string endpointId,
            CancellationToken ct = default)
        {
            LastScopeId = scopeId;
            LastTeamId = teamId;
            LastEndpointId = endpointId;
            if (Failure != null)
                throw Failure;

            return Task.FromResult(Resolution);
        }
    }

    private sealed class RecordingStaticGAgentInvocationPort : IStaticGAgentStreamInvocationPort<AGUIEvent>
    {
        public StaticGAgentStreamInvocationRequest? Request { get; private set; }
        public StaticGAgentStreamInvocationResult? Result { get; set; }
        public Exception? Failure { get; set; }

        public async Task<StaticGAgentStreamInvocationResult> InvokeAsync(
            StaticGAgentStreamInvocationRequest request,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<StaticGAgentStreamAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            if (Failure != null)
                throw Failure;

            Request = request;
            if (Result is { Accepted: null })
                return Result;

            var accepted = Result?.Accepted ?? new StaticGAgentStreamAcceptedReceipt(
                new ServiceInvocationAcceptedReceipt
                {
                    RequestId = "request-team",
                    ServiceKey = "service-key",
                    DeploymentId = "deployment-1",
                    TargetActorId = "team-actor",
                    EndpointId = request.EndpointId,
                    CommandId = "team-command",
                    CorrelationId = "team-correlation",
                },
                new GAgentDraftRunAcceptedReceipt(
                    "team-actor",
                    "RoleGAgent",
                    "team-command",
                    "team-correlation",
                    request.Input.SessionId ?? string.Empty));

            if (onAcceptedAsync != null)
                await onAcceptedAsync(accepted, ct);

            return Result ?? new StaticGAgentStreamInvocationResult(
                accepted,
                GAgentDraftRunStartError.None,
                GAgentDraftRunCompletionStatus.RunFinished,
                true);
        }
    }

    private sealed class RecordingWorkflowDispatchService
        : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public WorkflowChatRunRequest? Command { get; private set; }

        public CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> Result { get; set; } =
            CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
                .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "workflow", "workflow-command", "workflow-correlation"));

        public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            Command = command;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingServiceRunQueryPort : IServiceRunQueryPort
    {
        public ServiceRunQuery? LastQuery { get; private set; }
        public string? LastScopeId { get; private set; }
        public string? LastServiceId { get; private set; }
        public string? LastRunId { get; private set; }
        public IReadOnlyList<ServiceRunSnapshot> ListResult { get; set; } = [];
        public ServiceRunSnapshot? ByRunId { get; set; }
        public ServiceRunSnapshot? ByCommandId { get; set; }

        public Task<IReadOnlyList<ServiceRunSnapshot>> ListAsync(
            ServiceRunQuery query,
            CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(ListResult);
        }

        public Task<ServiceRunSnapshot?> GetByRunIdAsync(
            string scopeId,
            string serviceId,
            string runId,
            CancellationToken ct = default)
        {
            LastScopeId = scopeId;
            LastServiceId = serviceId;
            LastRunId = runId;
            return Task.FromResult(ByRunId);
        }

        public Task<ServiceRunSnapshot?> GetByCommandIdAsync(
            string scopeId,
            string serviceId,
            string commandId,
            CancellationToken ct = default) =>
            Task.FromResult(ByCommandId);
    }

    private sealed class RecordingTerminalQueryPort : IGAgentRunTerminalQueryPort
    {
        public string? LastActorId { get; private set; }
        public string? LastCorrelationId { get; private set; }
        public string? LastSessionId { get; private set; }
        public GAgentRunTerminalSnapshot? ByCorrelationId { get; set; }
        public GAgentRunTerminalSnapshot? BySessionId { get; set; }

        public Task<GAgentRunTerminalSnapshot?> GetByCorrelationIdAsync(
            string actorId,
            string correlationId,
            CancellationToken ct = default)
        {
            LastActorId = actorId;
            LastCorrelationId = correlationId;
            return Task.FromResult(ByCorrelationId);
        }

        public Task<GAgentRunTerminalSnapshot?> GetBySessionIdAsync(
            string actorId,
            string sessionId,
            CancellationToken ct = default)
        {
            LastActorId = actorId;
            LastSessionId = sessionId;
            return Task.FromResult(BySessionId);
        }
    }

    private sealed class StubWorkflowExecutionQueryService : IWorkflowExecutionQueryApplicationService
    {
        public bool WorkflowActorCurrentStateQueryEnabled => true;
        public WorkflowActorSnapshot? Snapshot { get; set; }
        public IReadOnlyList<WorkflowRunTimelineExportItem> Timeline { get; set; } = [];

        public Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowAgentSummary>>([]);

        public IReadOnlyList<string> ListWorkflows() => [];

        public Task<IReadOnlyList<WorkflowCatalogItem>> ListWorkflowCatalogAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowCatalogItem>>([]);

        public Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(string workflowName, CancellationToken ct = default) =>
            Task.FromResult<WorkflowCatalogItemDetail?>(null);

        public Task<WorkflowCapabilitiesDocument> GetCapabilitiesAsync(CancellationToken ct = default) =>
            Task.FromResult(new WorkflowCapabilitiesDocument());

        public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(string actorId, CancellationToken ct = default) =>
            Task.FromResult(Snapshot);

        public Task<WorkflowRunReport?> GetWorkflowRunReportArtifactAsync(string actorId, CancellationToken ct = default) =>
            Task.FromResult<WorkflowRunReport?>(null);

        public Task<IReadOnlyList<WorkflowRunTimelineExportItem>> ListWorkflowRunTimelineExportAsync(
            string actorId,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult(Timeline);

        public Task<IReadOnlyList<WorkflowRunGraphExportEdge>> ListWorkflowRunGraphExportEdgesAsync(
            string actorId,
            int take = 200,
            WorkflowRunGraphExportQueryOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowRunGraphExportEdge>>([]);

        public Task<WorkflowRunGraphExportSubgraph> GetWorkflowRunGraphExportSubgraphAsync(
            string actorId,
            int depth = 2,
            int take = 200,
            WorkflowRunGraphExportQueryOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new WorkflowRunGraphExportSubgraph());
    }
}
