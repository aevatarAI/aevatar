using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.AGUI.Contracts;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
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
    public async Task AddAevatarInvocationTools_ShouldRegisterSixTaggedToolSources()
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
        sources.OfType<InvokeMemberToolSource>().Should().ContainSingle();
        sources.OfType<StartWorkflowToolSource>().Should().ContainSingle();
        sources.OfType<ObserveRunToolSource>().Should().ContainSingle();
        sources.OfType<ReadWorkflowRunArtifactToolSource>().Should().ContainSingle();

        var tools = new List<IAgentTool>();
        foreach (var source in sources)
            tools.AddRange(await source.DiscoverToolsAsync());

        tools.Select(static tool => tool.Name).Should().BeEquivalentTo(
            "aevatar_invoke_gagent",
            "aevatar_invoke_team",
            "aevatar_invoke_member",
            "aevatar_start_workflow",
            "aevatar_observe_run",
            "aevatar_read_workflow_run_artifact");
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
        doc.RootElement.GetProperty("properties").TryGetProperty("agent_kind", out _).Should().BeTrue();
        doc.RootElement.GetProperty("properties").TryGetProperty("actor_name", out _).Should().BeFalse();
    }

    [Fact]
    public async Task StartWorkflowToolDescription_ShouldTreatInlineWorkflowYamlsAsFallback()
    {
        var tool = await DiscoverSingleAsync(new StartWorkflowToolSource(new Harness().CreateDispatcher()));

        tool.Description.Should().Contain("mounted/imported Aevatar Scope Workflow");
        tool.Description.Should().Contain("run_id is the workflow run actor id");
        tool.Description.Should().Contain("command_id is the start command/tool-call id");
        tool.Description.Should().Contain("workflow_yamls");
        tool.Description.Should().Contain("explicit fallback");
        tool.Description.Should().Contain("templates/import sources");
        tool.Description.Should().Contain("read-only scoped connected-service tools");
        tool.Description.Should().Contain("inputs.prompt");
        tool.Description.Should().NotContain("pass that bundle in workflow_yamls");
    }

    [Fact]
    public async Task StartWorkflowSchema_ShouldDescribeJsonInputContractOnPrompt()
    {
        // Field regression (2026-08-13): agents passed the user's raw sentence
        // or an empty string as inputs.prompt because the proto-derived schema
        // carried zero semantics. The contract must be visible at the model
        // interface.
        var tool = await DiscoverSingleAsync(new StartWorkflowToolSource(new Harness().CreateDispatcher()));
        using var doc = JsonDocument.Parse(tool.ParametersSchema);

        var prompt = doc.RootElement
            .GetProperty("properties")
            .GetProperty("inputs")
            .GetProperty("properties")
            .GetProperty("prompt");
        var description = prompt.GetProperty("description").GetString();
        description.Should().Contain("serialized JSON");
        description.Should().Contain("read-only scoped connected-service");
        description.Should().Contain("leave unavailable fields explicit");
        description.Should().Contain("Never pass an empty string");

        var workflowId = doc.RootElement
            .GetProperty("properties")
            .GetProperty("workflow_id");
        workflowId.GetProperty("description").GetString().Should().Contain("Never guess");
        doc.RootElement.GetProperty("properties").TryGetProperty("actor_id", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ObserveRunSchema_ShouldRequireTypedTarget()
    {
        var tool = await DiscoverSingleAsync(new ObserveRunToolSource(new Harness().CreateDispatcher()));
        using var doc = JsonDocument.Parse(tool.ParametersSchema);

        var properties = doc.RootElement
            .GetProperty("properties")
            .EnumerateObject()
            .Select(static item => item.Name)
            .ToArray();

        properties.Should().BeEquivalentTo(
            "service_run",
            "gagent_terminal_correlation",
            "gagent_terminal_session",
            "workflow_current_state");
        doc.RootElement.TryGetProperty("oneOf", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("anyOf", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("allOf", out _).Should().BeFalse();
    }

    [Fact]
    public async Task InvokeMemberSchema_ShouldNotRequireEndpointId()
    {
        var tool = await DiscoverSingleAsync(new InvokeMemberToolSource(new Harness().CreateDispatcher()));
        using var doc = JsonDocument.Parse(tool.ParametersSchema);

        var required = doc.RootElement.GetProperty("required")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();

        required.Should().BeEquivalentTo("member_id", "payload");
        doc.RootElement.GetProperty("properties").TryGetProperty("endpoint_id", out _).Should().BeTrue();
        doc.RootElement.GetProperty("properties").GetProperty("wait").GetProperty("enum")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .Should().BeEquivalentTo("ack", "stream");
        tool.Description.Should().Contain("defaults to chat");
        tool.Description.Should().Contain("acceptance only");
        tool.Description.Should().Contain("aevatar_observe_run");
        tool.Description.Should().Contain("not complete");
        tool.SideEffectKind.Should().Be("studio.member.run-dispatch");
        tool.TurnReusePolicy.Should().Be(AgentToolTurnReusePolicy.RetireAfterSuccess);
    }

    [Fact]
    public async Task InvokeMember_WaitComplete_ShouldRejectBeforeDispatch()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_member");

        using var _ = PushContext(callId: "call-member-complete");
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "m-alpha",
              "payload": { "prompt": "run once" },
              "wait": "complete"
            }
            """);

        ErrorCodeOrNull(output).Should().Be("unsupported_wait_mode");
        output.Should().Contain("aevatar_observe_run");
        harness.MemberResolver.LastMemberId.Should().BeNull();
        harness.ServiceInvocationDispatcher.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadWorkflowRunArtifactTool_ShouldExposeStrictReadOnlySchema()
    {
        var harness = new Harness();
        var tool = await DiscoverSingleAsync(new ReadWorkflowRunArtifactToolSource(harness.WorkflowQuery, harness.RunBindingReader));
        using var doc = JsonDocument.Parse(tool.ParametersSchema);

        tool.Name.Should().Be("aevatar_read_workflow_run_artifact");
        tool.IsReadOnly.Should().BeTrue();
        tool.Description.Should().Contain("aevatar_start_workflow");
        tool.Description.Should().Contain("run-binding projection proves");
        tool.Description.Should().Contain("pending");
        doc.RootElement.GetProperty("type").GetString().Should().Be("object");
        doc.RootElement.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("required")[0].GetString().Should().Be("workflow_run_id");
        doc.RootElement.GetProperty("properties").TryGetProperty("workflow_run_id", out _).Should().BeTrue();
        doc.RootElement.GetProperty("properties").TryGetProperty("actor_id", out _).Should().BeTrue();
        doc.RootElement.GetProperty("properties").TryGetProperty("wait_ms", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("aevatar_invoke_gagent", "{}")]
    [InlineData("aevatar_invoke_team", """{"team_id":"team"}""")]
    [InlineData("aevatar_invoke_member", """{"member_id":"member"}""")]
    [InlineData("aevatar_start_workflow", """{"workflow_id":"wf"}""")]
    [InlineData("aevatar_observe_run", "{}")]
    [InlineData("aevatar_read_workflow_run_artifact", "{}")]
    public async Task Tools_ShouldReturnStructuredValidationError(string toolName, string argumentsJson)
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync(toolName);

        using var _ = PushContext(callId: $"call-{toolName}");
        var output = await tool.ExecuteAsync(argumentsJson);

        ErrorCode(output).Should().Be("invalid_arguments");
    }

    [Fact]
    public async Task InvokeGAgent_ShouldResolveAgentKindAndDispatchEnvelopeThroughPort()
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
              "agent_kind": "RoleGAgent",
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
    public async Task InvokeGAgent_ShouldReturnAmbiguousAgentKind_WhenKindHasMultipleActors()
    {
        var harness = new Harness();
        harness.ActorRegistry.Snapshot = new GAgentActorRegistrySnapshot(
            "scope-1",
            [new GAgentActorGroup("RoleGAgent", ["actor-1", "actor-2"])],
            7,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_gagent");

        using var _ = PushContext(callId: "call-gagent-ambiguous");
        var output = await tool.ExecuteAsync("""
            {
              "agent_kind": "RoleGAgent",
              "payload": { "prompt": "hello" },
              "wait": "ack"
            }
            """);

        ErrorCode(output).Should().Be("agent_kind_ambiguous");
        output.Should().Contain("actor_id");
        harness.ActorDispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeGAgent_ShouldRejectActorNameAlias()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_gagent");

        using var _ = PushContext(callId: "call-gagent-legacy-alias");
        var output = await tool.ExecuteAsync("""
            {
              "actor_name": "RoleGAgent",
              "payload": { "prompt": "hello" }
            }
            """);

        ErrorCode(output).Should().Be("invalid_arguments");
        output.Should().Contain("agent_kind");
        harness.ActorDispatch.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("agent_kind", "RoleGAgent")]
    [InlineData("actor_id", "actor-1")]
    public async Task InvokeGAgent_ShouldRejectActorNameAliasEvenWhenPairedWithValidSelector(
        string selectorField,
        string selectorValue)
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_gagent");

        using var _ = PushContext(callId: $"call-gagent-legacy-alias-{selectorField}");
        var output = await tool.ExecuteAsync($$"""
            {
              "actor_name": "LegacyRoleGAgent",
              "{{selectorField}}": "{{selectorValue}}",
              "payload": { "prompt": "hello" }
            }
            """);

        ErrorCode(output).Should().Be("invalid_arguments");
        output.Should().Contain("actor_name");
        harness.ActorRegistry.LastScopeId.Should().BeNull();
        harness.ActorDispatch.Calls.Should().BeEmpty();
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
    public async Task InvokeGAgent_ShouldResolveActorInOwnerScope_WhenLarkChannelCarriesOwnerScope()
    {
        var harness = new Harness();
        harness.ActorRegistry.Snapshot = new GAgentActorRegistrySnapshot(
            "owner-scope-1",
            [new GAgentActorGroup("RoleGAgent", ["owner-actor-1"])],
            7,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_gagent");

        using var _ = PushContext(
            callId: "call-gagent-lark-owner-scope",
            scopeId: "registration-scope-1",
            ownerScopeId: "owner-scope-1",
            channelPlatform: "lark",
            channelRegistrationScopeId: "registration-scope-1");
        var output = await tool.ExecuteAsync("""
            {
              "actor_id": "owner-actor-1",
              "payload": { "prompt": "hello" }
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.ActorRegistry.LastScopeId.Should().Be("owner-scope-1");
        harness.ActorDispatch.Calls.Should().ContainSingle();
        var payload = harness.ActorDispatch.Calls.Single().Envelope.Payload.Unpack<ChatRequestEvent>();
        payload.ScopeId.Should().Be("owner-scope-1");
    }

    [Fact]
    public async Task InvokeGAgent_ShouldResolveActorInOwnerScope_WhenFeishuChannelCarriesOwnerScope()
    {
        var harness = new Harness();
        harness.ActorRegistry.Snapshot = new GAgentActorRegistrySnapshot(
            "owner-scope-1",
            [new GAgentActorGroup("RoleGAgent", ["owner-actor-1"])],
            7,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_gagent");

        using var _ = PushContext(
            callId: "call-gagent-feishu-owner-scope",
            scopeId: "registration-scope-1",
            ownerScopeId: "owner-scope-1",
            channelPlatform: "feishu",
            channelRegistrationScopeId: "registration-scope-1");
        var output = await tool.ExecuteAsync("""
            {
              "actor_id": "owner-actor-1",
              "payload": { "prompt": "hello" }
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.ActorRegistry.LastScopeId.Should().Be("owner-scope-1");
        harness.ActorDispatch.Calls.Should().ContainSingle();
        var payload = harness.ActorDispatch.Calls.Single().Envelope.Payload.Unpack<ChatRequestEvent>();
        payload.ScopeId.Should().Be("owner-scope-1");
    }

    [Fact]
    public async Task InvokeGAgent_ShouldKeepCallerScope_WhenLarkChannelHasNoOwnerScope()
    {
        var harness = new Harness();
        harness.ActorRegistry.Snapshot = new GAgentActorRegistrySnapshot(
            "registration-scope-1",
            [new GAgentActorGroup("RoleGAgent", ["registration-actor-1"])],
            7,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_gagent");

        using var _ = PushContext(
            callId: "call-gagent-lark-no-owner-scope",
            scopeId: "registration-scope-1",
            ownerScopeId: null,
            channelPlatform: "lark",
            channelRegistrationScopeId: "registration-scope-1");
        var output = await tool.ExecuteAsync("""
            {
              "actor_id": "registration-actor-1",
              "payload": { "prompt": "hello" }
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.ActorRegistry.LastScopeId.Should().Be("registration-scope-1");
        harness.ActorDispatch.Calls.Should().ContainSingle();
        var payload = harness.ActorDispatch.Calls.Single().Envelope.Payload.Unpack<ChatRequestEvent>();
        payload.ScopeId.Should().Be("registration-scope-1");
    }

    [Fact]
    public async Task InvokeGAgent_ShouldUseOwnerScope_WhenContextCarriesOwnerScope()
    {
        var harness = new Harness();
        harness.ActorRegistry.Snapshot = new GAgentActorRegistrySnapshot(
            "registration-scope-1",
            [new GAgentActorGroup("RoleGAgent", ["registration-actor-1"])],
            7,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_gagent");

        using var _ = PushContext(
            callId: "call-gagent-api-registration-scope",
            scopeId: "registration-scope-1",
            ownerScopeId: "owner-scope-1",
            channelPlatform: null,
            channelRegistrationScopeId: null);
        var output = await tool.ExecuteAsync("""
            {
              "actor_id": "registration-actor-1",
              "payload": { "prompt": "hello" }
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.ActorRegistry.LastScopeId.Should().Be("owner-scope-1");
        harness.ActorDispatch.Calls.Should().ContainSingle();
        var payload = harness.ActorDispatch.Calls.Single().Envelope.Payload.Unpack<ChatRequestEvent>();
        payload.ScopeId.Should().Be("owner-scope-1");
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
                  "{{LLMRequestMetadataKeys.SenderNyxUserId}}": "evil-sender-user",
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
    public async Task InvokeTeam_WhenEntryServiceIsWorkflow_DispatchesServiceInvocationAndUsesAcceptedReceipt()
    {
        var harness = new Harness();
        harness.TeamResolver.Resolution = new TeamEntryMemberResolution(
            "scope-1",
            "team-1",
            "member-1",
            "workflow-service");
        harness.ConfigureServiceTarget(
            ServiceImplementationKind.Workflow,
            serviceId: "workflow-service",
            endpointId: "chat",
            primaryActorId: "workflow-definition-actor");
        harness.ServiceInvocationDispatcher.Receipt = new ServiceInvocationAcceptedReceipt
        {
            RequestId = "workflow-command",
            ServiceKey = "tenant:aevatar-service:default:workflow-service",
            DeploymentId = "deployment-workflow-service",
            TargetActorId = "workflow-run-actor",
            EndpointId = "chat",
            CommandId = "workflow-command",
            CorrelationId = "workflow-correlation",
            RunId = "workflow-service-run",
        };
        harness.WorkflowRunDelivery.DeliveryActorId = "delivery-team-alpha";
        harness.ServiceInvocationDispatcher.OnDispatch = invocation =>
        {
            harness.WorkflowRunDelivery.Reservations.Should().ContainSingle();
            harness.WorkflowRunDelivery.Registrations.Should().BeEmpty();
            invocation.CommandId.Should().Be("cmd-team-alpha");
            invocation.CorrelationId.Should().Be("corr-team-alpha");
            invocation.WorkflowCompletionNotificationTarget.Should().NotBeNull();
            invocation.WorkflowCompletionNotificationTarget.ActorId.Should().Be("delivery-team-alpha");
            var reservation = harness.WorkflowRunDelivery.Reservations.Single();
            invocation.WorkflowCompletionNotificationTarget.DeliveryId.Should().Be(reservation.DeliveryId);
            reservation.DeliveryId.Should().StartWith("workflow-delivery:");
            reservation.DeliveryId.Should().NotContain("cmd-team-alpha");
            invocation.WorkflowCompletionNotificationTarget.ActorId.Should().NotBe(reservation.DeliveryId);
        };
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(
            callId: "cmd-team-alpha",
            requestId: "corr-team-alpha",
            nyxIdCredentialKind: AgentToolNyxIdCredentialKind.ProxyDelegation,
            sourceReadableAccessToken: "source-token",
            executionOwner: AgentToolExecutionOwners.ChannelRegistration("bot-reg-1"));
        var request = BuildChatRunRequest(
            "response-team-workflow",
            "call-team-workflow-tool",
            "aevatar_invoke_team",
            """
            {
              "team_id": "team-1",
              "endpoint_id": "chat",
              "payload": {
                "prompt": "run team workflow",
                "input_parts": [
                  { "kind": "text", "text": "typed input" }
                ],
                "headers": { "x-workflow": "yes" }
              },
              "wait": "stream"
            }
            """);

        var result = await dispatcher.InvokeTeamForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().BeEmpty(result.ToolExecutionResultJson);
        result.RunId.Should().Be("workflow-service-run");
        result.ScopeId.Should().Be("scope-1");
        result.WaitMode.Should().Be(ChatRunSubRunWaitMode.Stream);
        result.Status.Should().Be("streaming");
        result.ActorId.Should().Be("workflow-run-actor");
        result.ServiceId.Should().Be("workflow-service");
        result.EndpointId.Should().Be("chat");
        result.StreamTopic.Should().Be("aevatar://scopes/scope-1/services/workflow-service/runs/workflow-service-run");

        harness.TeamInvocation.Request.Should().BeNull();
        harness.WorkflowDispatch.Command.Should().BeNull();
        harness.ServiceInvocationDispatcher.Calls.Should().ContainSingle();
        var serviceDispatch = harness.ServiceInvocationDispatcher.Calls.Single();
        serviceDispatch.Target.Service.PrimaryActorId.Should().Be("workflow-definition-actor");
        serviceDispatch.Request.Identity!.TenantId.Should().Be("scope-1");
        serviceDispatch.Request.Identity.ServiceId.Should().Be("workflow-service");
        serviceDispatch.Request.EndpointId.Should().Be("chat");
        var chatPayload = serviceDispatch.Request.Payload!.Unpack<ChatRequestEvent>();
        chatPayload.Prompt.Should().Be("run team workflow");
        chatPayload.SessionId.Should().Be("response-1");
        chatPayload.ScopeId.Should().Be("scope-1");
        chatPayload.Headers.Should().Contain("x-workflow", "yes");
        chatPayload.Metadata.Should().Contain("x-workflow", "yes");
        chatPayload.InputParts.Should().ContainSingle();
        chatPayload.InputParts[0].Text.Should().Be("typed input");
        chatPayload.ToolContext.Caller.ScopeId.Should().Be("scope-1");
        chatPayload.ToolContext.Caller.OwnerSubject.Should().Be("owner-1");
        chatPayload.ToolContext.Credentials.NyxIdAccessToken.Should().BeEmpty();
        chatPayload.ToolContext.Credentials.NyxIdOrgToken.Should().BeEmpty();
        chatPayload.ToolContext.Credentials.SenderNyxIdAccessToken.Should().BeEmpty();
        chatPayload.ToolContext.Credentials.SourceReadableNyxIdAccessToken.Should().BeEmpty();
        chatPayload.ConnectorHttpAuthorization.Should().BeEmpty();
        chatPayload.CallerNyxIdCredentialKind.Should().Be(
            AgentToolNyxIdCredentialKindPayload.Unspecified);
        chatPayload.CallerSourceReadableNyxIdBearerToken.Should().BeEmpty();
        chatPayload.CallerDurableCredential.Should().NotBeNull();
        chatPayload.CallerDurableCredential.Ref.Should().Be("secrets://nyx/default-reply");
        chatPayload.CallerDurableCredential.Purpose.Should().Be(
            CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey);
        chatPayload.CallerDurableCredential.OwnerScopeKey.Should().Be("registration-scope-1");
        chatPayload.CallerDurableCredential.SubjectId.Should().Be("nyx-api-key-1");
        chatPayload.CallerDurableCredential.SourceKind.Should().Be(
            DurableCallerCredentialSourceKind.ChannelRegistration);
        chatPayload.LlmControl.ModelOverride.Should().Be("model-1");
        chatPayload.LlmControl.NyxIdRoutePreference.Should().Be("route-1");
        chatPayload.LlmControl.SenderNyxIdAccessToken.Should().BeEmpty();
        ShouldNotCarryTrustedCallerValues(chatPayload.Headers);
        ShouldNotCarryTrustedCallerValues(chatPayload.Metadata);

        harness.AdmissionAuthorizer.Calls.Should().ContainSingle();
        harness.AdmissionAuthorizer.Calls[0].ServiceKey.Should().Contain("workflow-service");
        harness.AdmissionAuthorizer.Calls[0].Endpoint.EndpointId.Should().Be("chat");
        harness.ServiceRunRegistration.Records.Should().BeEmpty();
        harness.WorkflowRunDelivery.Registrations.Should().ContainSingle();
        harness.WorkflowRunDelivery.Registrations[0].StreamTopic.Should().Be(result.StreamTopic);
        harness.WorkflowRunDelivery.Registrations[0].WorkflowActorId.Should().Be("workflow-run-actor");
        harness.WorkflowRunDelivery.Registrations[0].WorkflowRunId.Should().Be("workflow-service-run");
        harness.WorkflowRunDelivery.Registrations[0].WorkflowCommandId.Should().Be("cmd-team-alpha");
        harness.WorkflowRunDelivery.Registrations[0].WorkflowCorrelationId.Should().Be("corr-team-alpha");

        using var output = JsonDocument.Parse(result.ToolExecutionResultJson);
        output.RootElement.GetProperty("run_id").GetString().Should().Be("workflow-service-run");
        output.RootElement.GetProperty("actor_id").GetString().Should().Be("workflow-run-actor");
        output.RootElement.GetProperty("command_id").GetString().Should().Be("cmd-team-alpha");
        output.RootElement.GetProperty("correlation_id").GetString().Should().Be("corr-team-alpha");
        output.RootElement.GetProperty("service_id").GetString().Should().Be("workflow-service");
        output.RootElement.GetProperty("stream_topic").GetString().Should().Be(result.StreamTopic);
    }

    [Fact]
    public async Task InvokeMember_WhenEndpointIsOmitted_DefaultsToChatAndDispatchesPublishedService()
    {
        var harness = new Harness();
        harness.MemberResolver.Resolution = new MemberPublishedServiceResolution(
            "scope-1",
            "m-alpha",
            "svc-alpha");
        harness.ConfigureServiceTarget(
            ServiceImplementationKind.Workflow,
            serviceId: "svc-alpha",
            endpointId: "chat",
            primaryActorId: "workflow-definition-actor");
        harness.ServiceInvocationDispatcher.Receipt = new ServiceInvocationAcceptedReceipt
        {
            RequestId = "member-workflow-command",
            ServiceKey = "tenant:aevatar-service:default:svc-alpha",
            DeploymentId = "deployment-member-workflow",
            TargetActorId = "workflow-run-actor",
            EndpointId = "chat",
            CommandId = "member-workflow-command",
            CorrelationId = "member-workflow-correlation",
            RunId = "member-workflow-service-run",
        };
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(callId: "call-member-workflow");
        var request = BuildChatRunRequest(
            "response-member-workflow",
            "call-member-workflow-tool",
            "aevatar_invoke_member",
            """
            {
              "member_id": "m-alpha",
              "payload": {
                "prompt": "run member workflow",
                "input_parts": [
                  {
                    "kind": "file",
                    "file_ref": {
                      "file_id": "file-lark-1",
                      "artifact_id": "workflow-file://file-lark-1",
                      "source_kind": 3,
                      "source_message_id": "om_lark_1",
                      "source_resource_key": "file_key_1",
                      "file_name": "document.pdf",
                      "media_type": "application/pdf",
                      "size_bytes": 1234
                    }
                  }
                ],
                "headers": { "x-workflow": "yes" }
              },
              "wait": "stream"
            }
            """);

        var result = await dispatcher.InvokeMemberForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().BeEmpty(result.ToolExecutionResultJson);
        result.RunId.Should().Be("member-workflow-service-run");
        result.ScopeId.Should().Be("scope-1");
        result.ActorId.Should().Be("workflow-run-actor");
        result.ServiceId.Should().Be("svc-alpha");
        result.EndpointId.Should().Be("chat");
        result.StreamTopic.Should().Be("aevatar://scopes/scope-1/services/svc-alpha/runs/member-workflow-service-run");

        harness.MemberResolver.LastScopeId.Should().Be("scope-1");
        harness.MemberResolver.LastMemberId.Should().Be("m-alpha");
        harness.ActorRegistry.LastScopeId.Should().BeNull();
        harness.ActorDispatch.Calls.Should().BeEmpty();
        harness.TeamResolver.LastTeamId.Should().BeNull();
        harness.TeamInvocation.Request.Should().BeNull();
        harness.WorkflowDispatch.Command.Should().BeNull();
        harness.ServiceInvocationDispatcher.Calls.Should().ContainSingle();
        var dispatch = harness.ServiceInvocationDispatcher.Calls.Single();
        dispatch.Request.Identity!.TenantId.Should().Be("scope-1");
        dispatch.Request.Identity.ServiceId.Should().Be("svc-alpha");
        dispatch.Request.EndpointId.Should().Be("chat");
        var chatPayload = dispatch.Request.Payload!.Unpack<ChatRequestEvent>();
        chatPayload.Prompt.Should().Be("run member workflow");
        chatPayload.InputParts.Should().ContainSingle();
        var inputPart = chatPayload.InputParts.Single();
        inputPart.Kind.Should().Be(ChatContentPartKind.Text);
        inputPart.DataBase64.Should().BeEmpty();
        inputPart.FileRef.Should().NotBeNull();
        var fileRef = inputPart.FileRef!;
        fileRef.FileId.Should().Be("file-lark-1");
        fileRef.ArtifactId.Should().Be("workflow-file://file-lark-1");
        fileRef.SourceKind.Should().Be(Aevatar.AI.Abstractions.ChatFileSourceKind.ConnectedServiceResource);
        fileRef.SourceMessageId.Should().Be("om_lark_1");
        fileRef.SourceResourceKey.Should().Be("file_key_1");
        fileRef.FileName.Should().Be("document.pdf");
        fileRef.MediaType.Should().Be("application/pdf");
        fileRef.SizeBytes.Should().Be(1234);
        harness.AdmissionAuthorizer.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task InvokeTeam_WhenPublishedWorkflowPayloadOmitsFileRef_ShouldForwardAmbientInputFileRefs()
    {
        var harness = new Harness();
        harness.TeamResolver.Resolution = new TeamEntryMemberResolution(
            "scope-1",
            "team-1",
            "m-alpha",
            "svc-team-workflow");
        harness.ConfigureServiceTarget(
            ServiceImplementationKind.Workflow,
            serviceId: "svc-team-workflow",
            endpointId: "chat",
            primaryActorId: "workflow-definition-actor");
        harness.ServiceInvocationDispatcher.Receipt = new ServiceInvocationAcceptedReceipt
        {
            RequestId = "team-ambient-command",
            ServiceKey = "tenant:aevatar-service:default:svc-team-workflow",
            DeploymentId = "deployment-team-workflow",
            TargetActorId = "workflow-run-actor",
            EndpointId = "chat",
            CommandId = "team-ambient-command",
            CorrelationId = "team-ambient-correlation",
            RunId = "team-ambient-run",
        };
        var dispatcher = harness.CreateDispatcher();
        var ambientFileRef = BuildInputFileRef(
            "file-team-ambient",
            "workflow-file://file-team-ambient",
            "team-ambient.pdf");

        using var _ = PushContext(
            callId: "call-team-ambient-file-ref",
            inputFileRefs: [ambientFileRef]);
        var request = BuildChatRunRequest(
            "response-team-ambient-file-ref",
            "tool-call-team-ambient-file-ref",
            "aevatar_invoke_team",
            """
            {
              "team_id": "team-1",
              "endpoint_id": "chat",
              "payload": { "prompt": "process ambient document" },
              "wait": "stream"
            }
            """);

        var result = await dispatcher.InvokeTeamForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().BeEmpty(result.ToolExecutionResultJson);
        var dispatch = harness.ServiceInvocationDispatcher.Calls.Should().ContainSingle().Subject;
        var chatPayload = dispatch.Request.Payload!.Unpack<ChatRequestEvent>();
        var inputPart = chatPayload.InputParts.Should().ContainSingle().Subject;
        inputPart.FileRef.Should().NotBeNull();
        inputPart.FileRef!.FileId.Should().Be("file-team-ambient");
        inputPart.FileRef.ArtifactId.Should().Be("workflow-file://file-team-ambient");
        inputPart.FileRef.SourceMessageId.Should().Be("om_file-team-ambient");
        inputPart.FileRef.SourceResourceKey.Should().Be("resource_file-team-ambient");
        inputPart.FileRef.FileName.Should().Be("team-ambient.pdf");
        inputPart.FileRef.MediaType.Should().Be("application/pdf");
        inputPart.FileRef.SizeBytes.Should().Be(789);
        harness.ServiceInvocationResolution.LastRequest!.Payload!.Unpack<ChatRequestEvent>()
            .InputParts.Should().BeEmpty("service resolution must not mutate the explicit payload");
        harness.AdmissionAuthorizer.Calls.Should().ContainSingle().Subject.Request.Payload!
            .Unpack<ChatRequestEvent>().InputParts.Should().ContainSingle();
    }

    [Fact]
    public async Task InvokeMember_WhenPublishedWorkflowPayloadOmitsFileRef_ShouldForwardAmbientInputFileRefs()
    {
        var harness = new Harness();
        harness.MemberResolver.Resolution = new MemberPublishedServiceResolution(
            "scope-1",
            "m-alpha",
            "svc-member-workflow");
        harness.ConfigureServiceTarget(
            ServiceImplementationKind.Workflow,
            serviceId: "svc-member-workflow",
            endpointId: "chat",
            primaryActorId: "workflow-definition-actor");
        harness.ServiceInvocationDispatcher.Receipt = new ServiceInvocationAcceptedReceipt
        {
            RequestId = "member-ambient-command",
            ServiceKey = "tenant:aevatar-service:default:svc-member-workflow",
            DeploymentId = "deployment-member-workflow",
            TargetActorId = "workflow-run-actor",
            EndpointId = "chat",
            CommandId = "member-ambient-command",
            CorrelationId = "member-ambient-correlation",
            RunId = "member-ambient-run",
        };
        var dispatcher = harness.CreateDispatcher();
        var ambientFileRef = BuildInputFileRef(
            "file-member-ambient",
            "workflow-file://file-member-ambient",
            "member-ambient.pdf");

        using var _ = PushContext(
            callId: "call-member-ambient-file-ref",
            inputFileRefs: [ambientFileRef]);
        var request = BuildChatRunRequest(
            "response-member-ambient-file-ref",
            "tool-call-member-ambient-file-ref",
            "aevatar_invoke_member",
            """
            {
              "member_id": "m-alpha",
              "payload": { "prompt": "process ambient document" },
              "wait": "stream"
            }
            """);

        var result = await dispatcher.InvokeMemberForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().BeEmpty(result.ToolExecutionResultJson);
        var dispatch = harness.ServiceInvocationDispatcher.Calls.Should().ContainSingle().Subject;
        var chatPayload = dispatch.Request.Payload!.Unpack<ChatRequestEvent>();
        var inputPart = chatPayload.InputParts.Should().ContainSingle().Subject;
        inputPart.FileRef.Should().NotBeNull();
        inputPart.FileRef!.FileId.Should().Be("file-member-ambient");
        inputPart.FileRef.ArtifactId.Should().Be("workflow-file://file-member-ambient");
        inputPart.FileRef.SourceMessageId.Should().Be("om_file-member-ambient");
        inputPart.FileRef.SourceResourceKey.Should().Be("resource_file-member-ambient");
        inputPart.FileRef.FileName.Should().Be("member-ambient.pdf");
        inputPart.FileRef.MediaType.Should().Be("application/pdf");
        inputPart.FileRef.SizeBytes.Should().Be(789);
        harness.ServiceInvocationResolution.LastRequest!.Payload!.Unpack<ChatRequestEvent>()
            .InputParts.Should().BeEmpty("service resolution must not mutate the explicit payload");
        harness.AdmissionAuthorizer.Calls.Should().ContainSingle().Subject.Request.Payload!
            .Unpack<ChatRequestEvent>().InputParts.Should().ContainSingle();
    }

    [Theory]
    [InlineData("aevatar_invoke_team")]
    [InlineData("aevatar_invoke_member")]
    public async Task InvokePublishedWorkflow_WhenPayloadProvidesExplicitFileRef_ShouldNotAppendAmbientInputFileRefs(
        string toolName)
    {
        var harness = new Harness();
        const string publishedServiceId = "svc-explicit-file-workflow";
        if (string.Equals(toolName, "aevatar_invoke_team", StringComparison.Ordinal))
        {
            harness.TeamResolver.Resolution = new TeamEntryMemberResolution(
                "scope-1",
                "team-1",
                "m-alpha",
                publishedServiceId);
        }
        else
        {
            harness.MemberResolver.Resolution = new MemberPublishedServiceResolution(
                "scope-1",
                "m-alpha",
                publishedServiceId);
        }

        harness.ConfigureServiceTarget(
            ServiceImplementationKind.Workflow,
            serviceId: publishedServiceId,
            endpointId: "chat",
            primaryActorId: "workflow-definition-actor");
        harness.ServiceInvocationDispatcher.Receipt = new ServiceInvocationAcceptedReceipt
        {
            RequestId = "explicit-file-command",
            ServiceKey = "tenant:aevatar-service:default:svc-explicit-file-workflow",
            DeploymentId = "deployment-explicit-file-workflow",
            TargetActorId = "workflow-run-actor",
            EndpointId = "chat",
            CommandId = "explicit-file-command",
            CorrelationId = "explicit-file-correlation",
            RunId = "explicit-file-run",
        };
        var dispatcher = harness.CreateDispatcher();
        var ambientFileRef = BuildInputFileRef(
            "file-ambient-suppressed",
            "workflow-file://file-ambient-suppressed",
            "ambient-suppressed.pdf");
        var argumentsJson = string.Equals(toolName, "aevatar_invoke_team", StringComparison.Ordinal)
            ? """
              {
                "team_id": "team-1",
                "endpoint_id": "chat",
                "payload": {
                  "prompt": "process explicit document",
                  "input_parts": [
                    {
                      "kind": "file",
                      "file_ref": {
                        "file_id": "file-explicit",
                        "artifact_id": "workflow-file://file-explicit",
                        "source_kind": 3,
                        "file_name": "explicit.pdf",
                        "media_type": "application/pdf"
                      }
                    }
                  ]
                },
                "wait": "stream"
              }
              """
            : """
              {
                "member_id": "m-alpha",
                "payload": {
                  "prompt": "process explicit document",
                  "input_parts": [
                    {
                      "kind": "file",
                      "file_ref": {
                        "file_id": "file-explicit",
                        "artifact_id": "workflow-file://file-explicit",
                        "source_kind": 3,
                        "file_name": "explicit.pdf",
                        "media_type": "application/pdf"
                      }
                    }
                  ]
                },
                "wait": "stream"
              }
              """;

        using var _ = PushContext(
            callId: $"call-{toolName}-explicit-file-ref",
            inputFileRefs: [ambientFileRef]);
        var request = BuildChatRunRequest(
            $"response-{toolName}-explicit-file-ref",
            $"tool-call-{toolName}-explicit-file-ref",
            toolName,
            argumentsJson);

        var result = string.Equals(toolName, "aevatar_invoke_team", StringComparison.Ordinal)
            ? await dispatcher.InvokeTeamForChatRunAsync(request, request.ArgumentsJson)
            : await dispatcher.InvokeMemberForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().BeEmpty(result.ToolExecutionResultJson);
        var dispatch = harness.ServiceInvocationDispatcher.Calls.Should().ContainSingle().Subject;
        var chatPayload = dispatch.Request.Payload!.Unpack<ChatRequestEvent>();
        var inputPart = chatPayload.InputParts.Should().ContainSingle().Subject;
        inputPart.FileRef.Should().NotBeNull();
        inputPart.FileRef!.FileId.Should().Be("file-explicit");
        inputPart.FileRef.ArtifactId.Should().Be("workflow-file://file-explicit");
        inputPart.FileRef.FileName.Should().Be("explicit.pdf");
    }

    [Fact]
    public async Task InvokeMember_WhenEndpointIsExplicit_UsesRequestedEndpoint()
    {
        var harness = new Harness();
        harness.MemberResolver.Resolution = new MemberPublishedServiceResolution(
            "scope-1",
            "m-alpha",
            "svc-alpha");
        harness.ConfigureServiceTarget(
            ServiceImplementationKind.Workflow,
            serviceId: "svc-alpha",
            endpointId: "diagnose",
            primaryActorId: "workflow-definition-actor");
        harness.ServiceInvocationDispatcher.Receipt = new ServiceInvocationAcceptedReceipt
        {
            RequestId = "member-workflow-command",
            ServiceKey = "tenant:aevatar-service:default:svc-alpha",
            DeploymentId = "deployment-member-workflow",
            TargetActorId = "workflow-run-actor",
            EndpointId = "diagnose",
            CommandId = "member-workflow-command",
            CorrelationId = "member-workflow-correlation",
            RunId = "member-workflow-service-run",
        };
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_member");

        using var _ = PushContext(callId: "call-member-explicit-endpoint");
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "m-alpha",
              "endpoint_id": "diagnose",
              "payload": { "prompt": "run member workflow" },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        var dispatch = harness.ServiceInvocationDispatcher.Calls.Should().ContainSingle().Subject;
        dispatch.Request.EndpointId.Should().Be("diagnose");
        harness.AdmissionAuthorizer.Calls.Should().ContainSingle();
        harness.AdmissionAuthorizer.Calls.Single().Endpoint.EndpointId.Should().Be("diagnose");
    }

    [Fact]
    public async Task InvokeMember_ShouldResolveMemberInOwnerScope_WhenLarkChannelCarriesOwnerScope()
    {
        var harness = new Harness();
        harness.MemberResolver.Resolution = new MemberPublishedServiceResolution(
            "owner-scope-1",
            "m-lark",
            "svc-lark");
        harness.ConfigureServiceTarget(
            ServiceImplementationKind.Workflow,
            serviceId: "svc-lark",
            endpointId: "chat",
            primaryActorId: "workflow-definition-actor");
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_member");

        using var _ = PushContext(
            callId: "call-member-lark",
            scopeId: "registration-scope-1",
            ownerScopeId: "owner-scope-1",
            channelPlatform: "lark");
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "m-lark",
              "endpoint_id": "chat",
              "payload": { "prompt": "go" },
              "wait": "ack"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.MemberResolver.LastScopeId.Should().Be("owner-scope-1");
        harness.ServiceInvocationDispatcher.Calls.Should().ContainSingle();
        harness.ServiceInvocationDispatcher.Calls.Single().Request.Identity!.TenantId.Should().Be("owner-scope-1");
    }

    [Fact]
    public async Task InvokeMember_ShouldUseOwnerScope_WhenContextCarriesOwnerScope()
    {
        var harness = new Harness();
        harness.MemberResolver.Resolution = new MemberPublishedServiceResolution(
            "owner-scope-1",
            "m-api",
            "svc-api");
        harness.ConfigureServiceTarget(
            ServiceImplementationKind.Workflow,
            serviceId: "svc-api",
            endpointId: "chat",
            primaryActorId: "workflow-definition-actor");
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_member");

        using var _ = PushContext(
            callId: "call-member-api",
            scopeId: "registration-scope-1",
            ownerScopeId: "owner-scope-1",
            channelPlatform: "api-chat");
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "m-api",
              "endpoint_id": "chat",
              "payload": { "prompt": "go" },
              "wait": "ack"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.MemberResolver.LastScopeId.Should().Be("owner-scope-1");
        harness.ServiceInvocationDispatcher.Calls.Should().ContainSingle();
        harness.ServiceInvocationDispatcher.Calls.Single().Request.Identity!.TenantId.Should().Be("owner-scope-1");
    }

    [Fact]
    public async Task InvokeTeam_WhenAcceptedWorkflowCommandIdentityDiffers_ShouldReturnAcceptedWithoutAbandoning()
    {
        var harness = new Harness();
        harness.TeamResolver.Resolution = new TeamEntryMemberResolution(
            "scope-1",
            "team-1",
            "member-team-mismatch",
            "workflow-service-mismatch");
        harness.ConfigureServiceTarget(
            ServiceImplementationKind.Workflow,
            serviceId: "workflow-service-mismatch",
            endpointId: "chat",
            primaryActorId: "workflow-definition-mismatch");
        harness.ServiceInvocationDispatcher.HonorRequestIdentitySeeds = false;
        harness.ServiceInvocationDispatcher.Receipt = new ServiceInvocationAcceptedReceipt
        {
            RequestId = "accepted-team-command-mismatch",
            ServiceKey = "tenant:aevatar-service:default:workflow-service-mismatch",
            DeploymentId = "deployment-workflow-service-mismatch",
            TargetActorId = "workflow-run-team-mismatch",
            EndpointId = "chat",
            CommandId = "accepted-team-command-mismatch",
            CorrelationId = "accepted-team-correlation-mismatch",
            RunId = "service-run-team-mismatch",
        };
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(
            callId: "reserved-team-command-mismatch",
            requestId: "reserved-team-correlation-mismatch");
        var request = BuildChatRunRequest(
            "response-team-mismatch",
            "tool-call-team-mismatch",
            "aevatar_invoke_team",
            """
            {
              "team_id": "team-1",
              "endpoint_id": "chat",
              "payload": { "prompt": "run team workflow" },
              "wait": "stream"
            }
            """);

        var result = await dispatcher.InvokeTeamForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().BeEmpty();
        result.Status.Should().Be("streaming");
        result.RunId.Should().Be("service-run-team-mismatch");
        harness.WorkflowRunDelivery.Registrations.Should().ContainSingle();
        harness.WorkflowRunDelivery.Abandonments.Should().BeEmpty();
        using var resultJson = JsonDocument.Parse(result.ToolExecutionResultJson);
        resultJson.RootElement.GetProperty("command_id").GetString()
            .Should().Be("accepted-team-command-mismatch");
    }

    [Fact]
    public async Task InvokeTeam_WhenEntryServiceIsWorkflow_UsesServiceInvocationDispatcherArtifactAuthority()
    {
        var harness = new Harness();
        harness.TeamResolver.Resolution = new TeamEntryMemberResolution(
            "scope-1",
            "team-1",
            "member-1",
            "workflow-service");
        harness.ConfigureServiceTarget(
            ServiceImplementationKind.Workflow,
            serviceId: "workflow-service",
            endpointId: "chat",
            primaryActorId: "deployed-workflow-definition-actor");
        var target = harness.ServiceInvocationResolution.Result!;
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "published-workflow",
            WorkflowYaml = "name: published-workflow",
            DefinitionActorId = "published-definition-actor",
            InlineWorkflowYamls =
            {
                ["helper"] = "name: helper",
            },
        };
        harness.ServiceInvocationDispatcher.Receipt = new ServiceInvocationAcceptedReceipt
        {
            RequestId = "service-command",
            ServiceKey = target.Service.ServiceKey,
            DeploymentId = target.Service.DeploymentId,
            TargetActorId = "service-workflow-run-actor",
            EndpointId = "chat",
            CommandId = "service-command",
            CorrelationId = "service-correlation",
            RunId = "service-run",
        };
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(callId: "call-team-workflow-authority");
        var request = BuildChatRunRequest(
            "response-team-workflow-authority",
            "call-team-workflow-authority-tool",
            "aevatar_invoke_team",
            """
            {
              "team_id": "team-1",
              "endpoint_id": "chat",
              "payload": {
                "prompt": "run published workflow",
                "headers": { "x-workflow": "yes" }
              },
              "wait": "stream"
            }
            """);

        var result = await dispatcher.InvokeTeamForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().BeEmpty(result.ToolExecutionResultJson);
        result.RunId.Should().Be("service-run");
        result.ActorId.Should().Be("service-workflow-run-actor");
        result.StreamTopic.Should().Be("aevatar://scopes/scope-1/services/workflow-service/runs/service-run");
        harness.WorkflowDispatch.Command.Should().BeNull();
        harness.ServiceRunRegistration.Records.Should().BeEmpty();
        harness.ServiceInvocationDispatcher.Calls.Should().ContainSingle();
        var dispatch = harness.ServiceInvocationDispatcher.Calls.Single();
        dispatch.Target.Artifact.DeploymentPlan.WorkflowPlan.WorkflowName.Should().Be("published-workflow");
        dispatch.Target.Artifact.DeploymentPlan.WorkflowPlan.WorkflowYaml.Should().Be("name: published-workflow");
        dispatch.Target.Artifact.DeploymentPlan.WorkflowPlan.DefinitionActorId.Should().Be("published-definition-actor");
        dispatch.Target.Artifact.DeploymentPlan.WorkflowPlan.InlineWorkflowYamls.Should().Contain("helper", "name: helper");
        dispatch.Request.Identity!.ServiceId.Should().Be("workflow-service");
        dispatch.Request.EndpointId.Should().Be("chat");
        dispatch.Request.Payload!.Unpack<ChatRequestEvent>().Prompt.Should().Be("run published workflow");

        using var output = JsonDocument.Parse(result.ToolExecutionResultJson);
        output.RootElement.GetProperty("command_id").GetString().Should().Be("call-team-workflow-authority");
        output.RootElement.GetProperty("correlation_id").GetString().Should().Be("request-1");
    }

    [Fact]
    public async Task InvokeTeam_WhenEntryServiceIsStatic_KeepsStaticInvocationPathAfterServiceResolution()
    {
        var harness = new Harness();
        harness.ConfigureServiceTarget(
            ServiceImplementationKind.Static,
            serviceId: "service-1",
            endpointId: "entry",
            primaryActorId: "static-actor");
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(callId: "call-team-static");
        var request = BuildChatRunRequest(
            "response-team-static",
            "call-team-static-tool",
            "aevatar_invoke_team",
            """
            {
              "team_id": "team-1",
              "endpoint_id": "entry",
              "payload": {
                "prompt": "go",
                "input_parts": [
                  {
                    "kind": "file",
                    "file_ref": {
                      "file_id": "file-static-1",
                      "artifact_id": "workflow-file://file-static-1",
                      "source_kind": 3,
                      "source_message_id": "om_static_1",
                      "source_resource_key": "file_key_static_1",
                      "file_name": "document.pdf",
                      "media_type": "application/pdf",
                      "size_bytes": 1234
                    }
                  }
                ]
              },
              "wait": "stream"
            }
            """);

        var result = await dispatcher.InvokeTeamForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().BeEmpty(result.ToolExecutionResultJson);
        result.RunId.Should().Be("team-command");
        result.ServiceId.Should().Be("service-1");
        result.StreamTopic.Should().Be("aevatar://scopes/scope-1/services/service-1/runs/team-command");
        harness.TeamInvocation.Request.Should().NotBeNull();
        var inputPart = harness.TeamInvocation.Request!.Input.InputParts.Should().ContainSingle().Which;
        inputPart.Kind.Should().Be(GAgentDraftRunInputPartKind.Text);
        inputPart.FileRef.Should().NotBeNull();
        inputPart.FileRef!.FileId.Should().Be("file-static-1");
        inputPart.FileRef.ArtifactId.Should().Be("workflow-file://file-static-1");
        inputPart.FileRef.SourceKind.Should().Be(Aevatar.AI.Abstractions.ChatFileSourceKind.ConnectedServiceResource);
        inputPart.FileRef.SourceMessageId.Should().Be("om_static_1");
        inputPart.FileRef.SourceResourceKey.Should().Be("file_key_static_1");
        inputPart.FileRef.FileName.Should().Be("document.pdf");
        inputPart.FileRef.MediaType.Should().Be("application/pdf");
        inputPart.FileRef.SizeBytes.Should().Be(1234);
        harness.WorkflowDispatch.Command.Should().BeNull();
        harness.ServiceRunRegistration.Records.Should().BeEmpty();
        harness.AdmissionAuthorizer.Calls.Should().ContainSingle();
        harness.AdmissionAuthorizer.Calls[0].Artifact.ImplementationKind.Should().Be(ServiceImplementationKind.Static);
    }

    [Fact]
    public async Task InvokeTeam_WhenEntryServiceIsStatic_ShouldKeepAmbientFileRefsOutOfAdmissionAndExecution()
    {
        var harness = new Harness();
        harness.ConfigureServiceTarget(
            ServiceImplementationKind.Static,
            serviceId: "service-static-ambient",
            endpointId: "entry",
            primaryActorId: "static-actor");
        var dispatcher = harness.CreateDispatcher();
        var ambientFileRef = BuildInputFileRef(
            "file-static-ambient",
            "workflow-file://file-static-ambient",
            "ambient.pdf");

        using var _ = PushContext(
            callId: "call-team-static-ambient",
            inputFileRefs: [ambientFileRef]);
        var request = BuildChatRunRequest(
            "response-team-static-ambient",
            "call-team-static-ambient-tool",
            "aevatar_invoke_team",
            """
            {
              "team_id": "team-1",
              "endpoint_id": "entry",
              "payload": { "prompt": "go" },
              "wait": "stream"
            }
            """);

        var result = await dispatcher.InvokeTeamForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().BeEmpty(result.ToolExecutionResultJson);
        var resolutionPayload = harness.ServiceInvocationResolution.LastRequest!.Payload!
            .Unpack<ChatRequestEvent>();
        resolutionPayload.InputParts.Should().BeEmpty();
        var admittedPayload = harness.AdmissionAuthorizer.Calls.Should().ContainSingle().Subject
            .Request.Payload!.Unpack<ChatRequestEvent>();
        admittedPayload.InputParts.Should().BeEmpty();
        harness.TeamInvocation.Request.Should().NotBeNull();
        harness.TeamInvocation.Request!.Input.InputParts.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task InvokeTeam_WhenEntryServiceIsScripting_ReturnsUnsupportedKind()
    {
        var harness = new Harness();
        harness.TeamResolver.Resolution = new TeamEntryMemberResolution(
            "scope-1",
            "team-1",
            "member-1",
            "script-service");
        harness.ConfigureServiceTarget(
            ServiceImplementationKind.Scripting,
            serviceId: "script-service",
            endpointId: "chat",
            primaryActorId: "script-runtime-actor");
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(callId: "call-team-scripting");
        var request = BuildChatRunRequest(
            "response-team-scripting",
            "call-team-scripting-tool",
            "aevatar_invoke_team",
            """
            {
              "team_id": "team-1",
              "endpoint_id": "chat",
              "payload": { "prompt": "go" },
              "wait": "stream"
            }
            """);

        var result = await dispatcher.InvokeTeamForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().Be("unsupported_team_entry_service_kind");
        result.ToolExecutionResultJson.Should().Contain("currently supports Static and Workflow");
        result.ToolExecutionResultJson.Should().NotContain("Only static GAgent services support stream invocation");
        harness.TeamInvocation.Request.Should().BeNull();
        harness.WorkflowDispatch.Command.Should().BeNull();
        harness.ServiceRunRegistration.Records.Should().BeEmpty();
        harness.AdmissionAuthorizer.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task InvokeTeam_ShouldResolveTeamInOwnerScope_WhenChannelOwnerScopeIdIsPresentWithoutSenderNyxUserId()
    {
        var harness = new Harness();
        harness.TeamResolver.Resolution = new TeamEntryMemberResolution(
            "owner-scope-1",
            "team-1",
            "member-1",
            "service-1");
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_team");

        using var _ = PushContext(
            callId: "call-team-bound-sender",
            scopeId: "registration-scope-1",
            ownerScopeId: "owner-scope-1",
            senderBindingId: "binding-1");
        var output = await tool.ExecuteAsync("""
            {
              "team_id": "team-1",
              "endpoint_id": "entry",
              "payload": { "prompt": "go" },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.TeamResolver.LastScopeId.Should().Be("owner-scope-1");
        harness.TeamInvocation.Request.Should().NotBeNull();
        harness.TeamInvocation.Request!.Identity.TenantId.Should().Be("owner-scope-1");
        harness.TeamInvocation.Request.Input.Caller!.TenantId.Should().Be("owner-scope-1");
        harness.TeamInvocation.Request.Input.ToolContext!.Caller.ScopeId.Should().Be("registration-scope-1");
        harness.TeamInvocation.Request.Input.ToolContext.Caller.OwnerScopeId.Should().Be("owner-scope-1");
        harness.TeamInvocation.Request.Input.ToolContext.SenderBinding.BindingId.Should().Be("binding-1");
        harness.TeamInvocation.Request.Input.ToolContext.SenderBinding.NyxUserId.Should().BeNull();

        var result = Read(output);
        result.GetProperty("stream_topic").GetString().Should().Be("aevatar://scopes/owner-scope-1/services/service-1/runs/team-command");
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
                  "{{LLMRequestMetadataKeys.SenderNyxUserId}}": "evil-sender-user",
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
            GAgentDraftRunStartError.ActorKindMismatch,
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

        ErrorCode(output).Should().Be("actorkindmismatch");
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
                "input_parts": [
                  {
                    "kind": "file",
                    "file_ref": {
                      "file_id": "file-lark-1",
                      "artifact_id": "workflow-file://file-lark-1",
                      "source_kind": "chat_file_source_kind_connected_service_resource",
                      "source_message_id": "om_lark_1",
                      "source_resource_key": "file_key_1",
                      "file_name": "document.pdf",
                      "media_type": "application/pdf",
                      "size_bytes": 1234
                    }
                  }
                ],
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
        harness.WorkflowDispatch.Command.CallerCredential!.BearerToken.Should().Be("access-token");
        harness.WorkflowDispatch.Command.InputParts.Should().ContainSingle();
        var inputPart = harness.WorkflowDispatch.Command.InputParts!.Single();
        inputPart.Kind.Should().Be(Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPartKind.File);
        inputPart.DataBase64.Should().BeNull();
        inputPart.FileRef.Should().NotBeNull();
        var fileRef = inputPart.FileRef!;
        fileRef.FileId.Should().Be("file-lark-1");
        fileRef.ArtifactId.Should().Be("workflow-file://file-lark-1");
        fileRef.SourceKind.Should().Be(FileArtifactSourceKind.ConnectedServiceResource);
        fileRef.SourceMessageId.Should().Be("om_lark_1");
        fileRef.SourceResourceKey.Should().Be("file_key_1");
        fileRef.FileName.Should().Be("document.pdf");
        fileRef.MediaType.Should().Be("application/pdf");
        fileRef.SizeBytes.Should().Be(1234);
        harness.WorkflowDispatch.Command.Metadata.Should().Contain("x-workflow", "yes");
        ShouldNotCarryTrustedCallerValues(harness.WorkflowDispatch.Command.Metadata);
        ShouldCarryWorkflowLlmControlValues(harness.WorkflowDispatch.Command.LlmControl);
        ShouldCarryTypedTrustedCallerValues(harness.WorkflowDispatch.Command);

        var result = Read(output);
        result.GetProperty("run_id").GetString().Should().Be("workflow-actor");
        result.GetProperty("actor_id").GetString().Should().Be("workflow-actor");
        result.GetProperty("command_id").GetString().Should().Be("call-workflow");
        result.GetProperty("stream_topic").GetString().Should().Be("aevatar://actors/workflow-actor/runs/call-workflow");
    }

    [Fact]
    public async Task StartWorkflow_FromNyxIdAssistant_ShouldBindRecoveryToCurrentConversation()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-workflow-chat-recovery",
            chatContext: new AgentChatInvocationContext(
                AgentChatInvocationSurface.NyxIdAssistant,
                "nyxid-chat-conversation-1",
                "turn-1",
                "task-1",
                null,
                null));
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowChatHistoryDelivery.Reservations.Should().ContainSingle();
        var reservation = harness.WorkflowChatHistoryDelivery.Reservations.Single();
        reservation.ScopeId.Should().Be("scope-1");
        reservation.Conversation.Intent.Should().Be(WorkflowChatConversationIntentKind.Create);
        reservation.Conversation.ConversationId.Should().Be("nyxid-chat-conversation-1");
        reservation.WorkflowActorId.Should().Be("workflow-actor");
        reservation.WorkflowCommandId.Should().Be("call-workflow-chat-recovery");
        reservation.RequestFingerprint.Should().NotBeNullOrWhiteSpace();
        harness.WorkflowChatHistoryDelivery.Bindings.Should().ContainSingle();
        harness.WorkflowChatHistoryDelivery.Bindings.Single().Receipt.ActorId.Should().Be("workflow-actor");
    }

    [Fact]
    public async Task StartWorkflow_CreateResultReceipt_WithAcceptedStreamAck_ShouldReturnVerifiedReceipt()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        var receipt = tool.CreateResultReceipt(
            "call-workflow",
            "aevatar_start_workflow",
            "{}",
            """{"run_id":"call-workflow","status":"streaming","stream_topic":"aevatar://actors/workflow-actor/runs/call-workflow","actor_id":"workflow-actor","command_id":"call-workflow","correlation_id":"call-workflow","wait":"stream"}""");

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.SideEffectKind.Should().Be("workflow.managed-child-start");
        receipt.SubjectKind.Should().Be("aevatar.invocation_run");
        receipt.SubjectId.Should().Be("call-workflow");
        receipt.ResultJson.Should().Contain("workflow-actor");
    }

    [Fact]
    public async Task StartWorkflow_ThroughStreamingExecutorWithoutChannel_ShouldKeepBoundCommandIdentity()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-workflow-executor",
            channelPlatform: null,
            channelRegistrationScopeId: null,
            durableReplyCredentialRef: null);
        var result = await ExecuteToolThroughExecutorAsync(
            tool,
            "call-workflow-executor",
            "aevatar_start_workflow",
            """
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "stream"
            }
            """);

        result.Result.Should().Contain("\"run_id\":\"workflow-actor\"");
        result.Result.Should().Contain("\"command_id\":\"call-workflow-executor\"");
        result.Result.Should().Contain("\"status\":\"streaming\"");
        result.Result.Should().NotContain("tool outcome could not be verified");
        result.Receipt.Should().NotBeNull();
        result.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        result.Receipt.CallId.Should().Be("call-workflow-executor");
        result.Receipt.SubjectId.Should().Be("workflow-actor");
        using var receiptResult = JsonDocument.Parse(result.Receipt.ResultJson);
        receiptResult.RootElement.GetProperty("command_id").GetString().Should().Be(result.Receipt.CallId);
        harness.WorkflowRunDelivery.Reservations.Should().BeEmpty();
        harness.WorkflowRunDelivery.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeMemberWorkflowService_ThroughStreamingExecutor_ShouldKeepVerifiedAcceptedAck()
    {
        var harness = new Harness();
        harness.MemberResolver.Resolution = new MemberPublishedServiceResolution(
            "scope-1",
            "member-workflow",
            "workflow-service");
        harness.ConfigureServiceTarget(
            ServiceImplementationKind.Workflow,
            serviceId: "workflow-service",
            endpointId: "chat",
            primaryActorId: "workflow-definition-actor");
        harness.ServiceInvocationDispatcher.Receipt = new ServiceInvocationAcceptedReceipt
        {
            RequestId = "call-member-workflow",
            ServiceKey = "tenant:aevatar-service:default:workflow-service",
            DeploymentId = "deployment-workflow-service",
            RunId = "workflow-service-run",
            CommandId = "workflow-command",
            CorrelationId = "workflow-correlation",
            TargetActorId = "workflow-actor",
            EndpointId = "chat",
        };
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_member");

        using var _ = PushContext(callId: "call-member-workflow");
        var result = await ExecuteToolThroughExecutorAsync(
            tool,
            "call-member-workflow",
            "aevatar_invoke_member",
            """
            {
              "member_id": "member-workflow",
              "payload": { "prompt": "process pdf" },
              "wait": "stream"
            }
            """);

        result.Result.Should().Contain("\"run_id\":\"workflow-service-run\"");
        result.Result.Should().Contain("\"service_id\":\"workflow-service\"");
        result.Result.Should().NotContain("tool outcome could not be verified");
        result.Receipt.Should().NotBeNull();
        result.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        result.Receipt.SubjectId.Should().Be("workflow-service-run");
    }

    [Fact]
    public async Task StartWorkflow_ShouldPopulateInputPartsFromAmbientInputFileRefs_WhenExplicitFileRefsAreAbsent()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");
        var ambientFileRef = new Aevatar.AI.Abstractions.ChatFileRef
        {
            FileId = "file-ambient-1",
            ArtifactId = "workflow-file://file-ambient-1",
            SourceKind = Aevatar.AI.Abstractions.ChatFileSourceKind.ConnectedServiceResource,
            SourceMessageId = "om_ambient_1",
            SourceResourceKey = "file_key_ambient_1",
            FileName = "ambient.pdf",
            MediaType = "application/pdf",
            SizeBytes = 789,
        };

        using var _ = PushContext(
            callId: "call-workflow-ambient-file-ref",
            inputFileRefs: [ambientFileRef]);
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": {
                "prompt": "run workflow"
              },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        harness.WorkflowDispatch.Command!.InputParts.Should().ContainSingle();
        var inputPart = harness.WorkflowDispatch.Command.InputParts!.Single();
        inputPart.Kind.Should().Be(Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPartKind.File);
        inputPart.Name.Should().Be("ambient.pdf");
        inputPart.MediaType.Should().Be("application/pdf");
        inputPart.FileRef.Should().NotBeNull();
        var fileRef = inputPart.FileRef!;
        fileRef.FileId.Should().Be("file-ambient-1");
        fileRef.ArtifactId.Should().Be("workflow-file://file-ambient-1");
        fileRef.SourceKind.Should().Be(FileArtifactSourceKind.ConnectedServiceResource);
        fileRef.SourceMessageId.Should().Be("om_ambient_1");
        fileRef.SourceResourceKey.Should().Be("file_key_ambient_1");
        fileRef.FileName.Should().Be("ambient.pdf");
        fileRef.MediaType.Should().Be("application/pdf");
        fileRef.SizeBytes.Should().Be(789);
    }

    [Fact]
    public async Task StartWorkflow_ShouldAcceptAmbientInputFileRefs_WhenInputsObjectIsEmpty()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");
        var ambientFileRef = new Aevatar.AI.Abstractions.ChatFileRef
        {
            FileId = "file-empty-inputs-1",
            ArtifactId = "workflow-file://file-empty-inputs-1",
            SourceKind = Aevatar.AI.Abstractions.ChatFileSourceKind.ConnectedServiceResource,
            FileName = "empty-inputs.pdf",
            MediaType = "application/pdf",
        };

        using var _ = PushContext(
            callId: "call-workflow-empty-inputs-file-ref",
            inputFileRefs: [ambientFileRef]);
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": {},
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        var inputPart = harness.WorkflowDispatch.Command!.InputParts.Should().ContainSingle().Subject;
        inputPart.FileRef.Should().NotBeNull();
        inputPart.FileRef!.FileId.Should().Be("file-empty-inputs-1");
        inputPart.FileRef.ArtifactId.Should().Be("workflow-file://file-empty-inputs-1");
    }

    [Fact]
    public async Task StartWorkflow_ShouldNotAppendAmbientInputFileRefs_WhenExplicitFileRefExists()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");
        var ambientFileRef = new Aevatar.AI.Abstractions.ChatFileRef
        {
            FileId = "file-ambient-1",
            ArtifactId = "workflow-file://file-ambient-1",
            SourceKind = Aevatar.AI.Abstractions.ChatFileSourceKind.ConnectedServiceResource,
            FileName = "ambient.pdf",
            MediaType = "application/pdf",
        };

        using var _ = PushContext(
            callId: "call-workflow-explicit-file-ref",
            inputFileRefs: [ambientFileRef]);
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": {
                "prompt": "run workflow",
                "input_parts": [
                  {
                    "kind": "file",
                    "file_ref": {
                      "file_id": "file-explicit-1",
                      "artifact_id": "workflow-file://file-explicit-1",
                      "source_kind": 3,
                      "file_name": "explicit.pdf",
                      "media_type": "application/pdf"
                    }
                  }
                ]
              },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        harness.WorkflowDispatch.Command!.InputParts.Should().ContainSingle();
        var fileRef = harness.WorkflowDispatch.Command.InputParts!.Single().FileRef;
        fileRef.Should().NotBeNull();
        fileRef!.FileId.Should().Be("file-explicit-1");
        fileRef.ArtifactId.Should().Be("workflow-file://file-explicit-1");
        fileRef.FileName.Should().Be("explicit.pdf");
    }

    [Fact]
    public async Task StartWorkflow_ShouldResolveScopeWorkflowIdToDefinitionActorSource()
    {
        var harness = new Harness();
        harness.ScopeWorkflowQuery.Workflows["98a81d707d4f4294b9b06f61a9fa8ac0"] = new ScopeWorkflowSummary(
            "scope-1",
            "98a81d707d4f4294b9b06f61a9fa8ac0",
            "Document Workflow",
            "scope-1:aevatar:workflows:98a81d707d4f4294b9b06f61a9fa8ac0",
            "document_workflow",
            "workflow-definition-actor-98a81d707d4f4294b9b06f61a9fa8ac0",
            "revision-1",
            "deployment-1",
            "Active",
            DateTimeOffset.UtcNow);
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-run-actor", "document_workflow", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(callId: "call-scope-workflow");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "98a81d707d4f4294b9b06f61a9fa8ac0",
              "inputs": { "prompt": "process pdf" },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.ScopeWorkflowQuery.Lookups.Should().ContainSingle()
            .Which.Should().Be(("scope-1", "98a81d707d4f4294b9b06f61a9fa8ac0"));
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        harness.WorkflowDispatch.Command!.Source.Kind.Should().Be(WorkflowChatSourceKind.DefinitionActor);
        harness.WorkflowDispatch.Command.Source.ActorId.Should()
            .Be("workflow-definition-actor-98a81d707d4f4294b9b06f61a9fa8ac0");
        harness.WorkflowDispatch.Command.Source.WorkflowName.Should().Be("document_workflow");
    }

    [Fact]
    public async Task StartWorkflow_WhenConfiguredScopeWorkflowTemplateIsMissing_ShouldEnsureBeforeLookup()
    {
        var harness = new Harness();
        harness.ScopeWorkflowQuery.Workflows["wf-configured"] = new ScopeWorkflowSummary(
            "scope-1",
            "wf-configured",
            "Configured Workflow",
            "scope-1:aevatar:workflows:wf-configured",
            "configured_workflow",
            "workflow-definition-actor-configured",
            "rev-configured",
            "deployment-configured",
            "Active",
            DateTimeOffset.UtcNow);
        harness.ScopeWorkflowTemplateEnsure.Result = ScopeWorkflowTemplateEnsureResult.SaveAndBindAccepted(
            new ScopeWorkflowSaveAndBindResult(
                "scope-1",
                "wf-configured",
                "rev-configured",
                new ScopeWorkflowUpsertResult(
                    "scope-1",
                    "wf-configured",
                    "scope-1:aevatar:workflows:wf-configured",
                    "rev-configured",
                    "scope-workflow",
                    "workflow-definition-actor-configured",
                    "deployment-configured",
                    DateTimeOffset.UtcNow,
                    [],
                    "/api/scopes/scope-1/workflows/wf-configured"),
                new ScopeBindingUpsertResult(
                    "scope-1",
                    "default",
                    "Configured Workflow",
                    "rev-configured",
                    ScopeBindingImplementationKind.Workflow,
                    "binding-actor")),
            "workflow_template_missing");
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-configured-scope-workflow",
            accessToken: "source-token",
            nyxIdCredentialKind: AgentToolNyxIdCredentialKind.SourceReadableUserBearer);
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-configured",
              "inputs": { "prompt": "process pdf" },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        var ensureRequest = harness.ScopeWorkflowTemplateEnsure.Requests.Should().ContainSingle().Subject;
        ensureRequest.ScopeId.Should().Be("scope-1");
        ensureRequest.WorkflowId.Should().Be("wf-configured");
        ensureRequest.CapabilityAdmission.Should().NotBeNull();
        ensureRequest.CapabilityAdmission!.CallerId.Should().Be("owner-1");
        ensureRequest.CapabilityAdmission.NyxIdCallerCredential.Should().NotBeNull();
        ensureRequest.CapabilityAdmission.NyxIdCallerCredential!.SourceReadableUserBearerToken.Should()
            .Be("source-token");
        harness.ScopeWorkflowQuery.Lookups.Should().ContainSingle()
            .Which.Should().Be(("scope-1", "wf-configured"));
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        harness.WorkflowDispatch.Command!.Source.Kind.Should().Be(WorkflowChatSourceKind.DefinitionActor);
        harness.WorkflowDispatch.Command.Source.ActorId.Should().Be("workflow-definition-actor-configured");
    }

    [Fact]
    public async Task StartWorkflow_WithInlineYamlBundle_ShouldNotEnsureScopeTemplate()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(callId: "call-inline-workflow");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-inline",
              "workflow_yamls": ["name: wf_inline\nsteps: []"],
              "inputs": { "prompt": "process pdf" },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.ScopeWorkflowTemplateEnsure.Requests.Should().BeEmpty();
        harness.ScopeWorkflowQuery.Lookups.Should().BeEmpty();
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        harness.WorkflowDispatch.Command!.Source.Kind.Should().Be(WorkflowChatSourceKind.InlineYamlBundle);
    }

    [Fact]
    public async Task StartWorkflow_WhenScopeWorkflowIsMissing_ShouldNotFallbackToCatalog()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(callId: "call-scope-workflow-missing");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "document-file-extraction-workflow",
              "inputs": { "prompt": "process pdf" },
              "wait": "stream"
            }
            """);

        ErrorCode(output).Should().Be("scope_workflow_not_found");
        ErrorMessage(output).Should().Contain("document-file-extraction-workflow");
        ErrorMessage(output).Should().Contain("scope-1");
        ErrorMessage(output).Should().Contain("service_catalog_missing");
        harness.ScopeWorkflowQuery.Lookups.Should().ContainSingle()
            .Which.Should().Be(("scope-1", "document-file-extraction-workflow"));
        harness.WorkflowDispatch.Command.Should().BeNull();
    }

    [Fact]
    public async Task StartWorkflow_WhenScopeWorkflowLookupFails_ShouldNotFallbackToCatalog()
    {
        var harness = new Harness();
        harness.ScopeWorkflowQuery.Failure = new InvalidOperationException("lookup unavailable");
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(callId: "call-scope-workflow-lookup-failed");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "document-file-extraction-workflow",
              "inputs": { "prompt": "process pdf" },
              "wait": "stream"
            }
            """);

        ErrorCode(output).Should().Be("scope_workflow_lookup_failed");
        ErrorMessage(output).Should().Contain("document-file-extraction-workflow");
        ErrorMessage(output).Should().Contain("scope-1");
        harness.ScopeWorkflowQuery.Lookups.Should().ContainSingle()
            .Which.Should().Be(("scope-1", "document-file-extraction-workflow"));
        harness.WorkflowDispatch.Command.Should().BeNull();
    }

    [Theory]
    [InlineData(ScopeWorkflowLookupStatus.NotReady, "deployment_readmodel_missing")]
    [InlineData(ScopeWorkflowLookupStatus.Stale, "workflow_actor_binding_mismatched")]
    public async Task StartWorkflow_WhenScopeWorkflowIsNotRunnable_ShouldNotFallbackToCatalog(
        ScopeWorkflowLookupStatus status,
        string reason)
    {
        var harness = new Harness();
        harness.ScopeWorkflowQuery.LookupResults["98a81d707d4f4294b9b06f61a9fa8ac0"] =
            new ScopeWorkflowLookupResult(status, null, reason);
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(callId: "call-scope-workflow-not-runnable");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "98a81d707d4f4294b9b06f61a9fa8ac0",
              "inputs": { "prompt": "process pdf" },
              "wait": "stream"
            }
            """);

        ErrorCode(output).Should().Be("scope_workflow_not_runnable");
        ErrorMessage(output).Should().Contain(status.ToString());
        ErrorMessage(output).Should().Contain(reason);
        harness.ScopeWorkflowQuery.Lookups.Should().ContainSingle()
            .Which.Should().Be(("scope-1", "98a81d707d4f4294b9b06f61a9fa8ac0"));
        harness.WorkflowDispatch.Command.Should().BeNull();
    }

    [Fact]
    public async Task StartWorkflow_WithWaitComplete_ShouldSkipBackgroundDeliveryRegistration()
    {
        // wait=complete is the caller's promise to observe the run in-turn and
        // compose the user-facing reply itself; the background channel relay
        // would post the raw final output as a second, unformatted message.
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        harness.WorkflowQuery.Snapshot = new WorkflowActorSnapshot
        {
            ScopeId = "scope-1",
            ActorId = "workflow-actor",
            LastCommandId = "call-workflow-wait-complete",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            StateVersion = 7,
            LastOutput = "done",
        };
        harness.WorkflowRunDelivery.DeliveryActorId = "delivery-wait-complete";
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-workflow-wait-complete",
            channelPlatform: "lark",
            channelRegistrationScopeId: "registration-scope-1");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "{\"submit\":false}" },
              "wait": "complete"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowRunDelivery.Reservations.Should().BeEmpty();
        harness.WorkflowRunDelivery.Registrations.Should().BeEmpty();
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        harness.WorkflowDispatch.Command!.CompletionNotificationTarget.Should().BeNull();
        harness.WorkflowDispatch.Command.CommandIdSeed.Should().Be("call-workflow-wait-complete");
        using var result = JsonDocument.Parse(output);
        result.RootElement.GetProperty("run_id").GetString().Should().Be("workflow-actor");
        result.RootElement.GetProperty("command_id").GetString()
            .Should().Be("call-workflow-wait-complete");
        result.RootElement.GetProperty("result").GetProperty("run_id").GetString()
            .Should().Be("workflow-actor");
        result.RootElement.GetProperty("result").GetProperty("command_id").GetString()
            .Should().Be("call-workflow-wait-complete");
    }

    [Fact]
    public async Task StartWorkflow_WithWaitComplete_ShouldUseConfiguredCompletionObservationTimeout()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var context = PushContext(
            callId: "call-workflow-wait-complete-unobserved",
            channelPlatform: null,
            channelRegistrationScopeId: null,
            durableReplyCredentialRef: null);
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "complete"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowQuery.LastCurrentStateActorId.Should().Be("workflow-actor");
        using var result = JsonDocument.Parse(output);
        result.RootElement.GetProperty("status").GetString().Should().Be("streaming");
        result.RootElement.TryGetProperty("result", out _).Should().BeFalse();
    }

    [Fact]
    public async Task StartWorkflow_WithWaitComplete_ShouldReturnObservedPartialOutputWhenRunIsStillActive()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        harness.WorkflowQuery.Snapshot = new WorkflowActorSnapshot
        {
            ScopeId = "scope-1",
            ActorId = "workflow-actor",
            LastCommandId = "call-workflow-wait-complete-partial",
            CompletionStatus = WorkflowRunCompletionStatus.Running,
            StateVersion = 7,
            LastOutput = "{\"workflow_status\":\"needs_input\",\"message\":\"choose one\"}",
        };
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var context = PushContext(
            callId: "call-workflow-wait-complete-partial",
            channelPlatform: null,
            channelRegistrationScopeId: null,
            durableReplyCredentialRef: null);
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "complete"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        using var result = JsonDocument.Parse(output);
        result.RootElement.GetProperty("status").GetString().Should().Be("streaming");
        result.RootElement.GetProperty("result").GetProperty("partial_output").GetString()
            .Should().Be("{\"workflow_status\":\"needs_input\",\"message\":\"choose one\"}");
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("ack")]
    [InlineData("complete")]
    public async Task StartWorkflow_WithoutChannel_ShouldBindWorkflowCommandToToolCallForAllWaitModes(
        string wait)
    {
        var harness = new Harness();
        var dispatcher = harness.CreateDispatcher();
        var callId = $"call-workflow-no-channel-{wait}";
        var argumentsJson = $$"""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "{{wait}}"
            }
            """;
        var request = BuildChatRunRequest(
            $"response-workflow-no-channel-{wait}",
            $"tool-call-workflow-no-channel-{wait}",
            "aevatar_start_workflow",
            argumentsJson);

        using var _ = PushContext(
            callId: callId,
            channelPlatform: null,
            channelRegistrationScopeId: null,
            durableReplyCredentialRef: null);
        var completion = await dispatcher.StartWorkflowForChatRunAsync(request, argumentsJson);

        completion.ErrorCode.Should().BeEmpty(completion.ToolExecutionResultJson);
        harness.WorkflowRunDelivery.Reservations.Should().BeEmpty();
        harness.WorkflowRunDelivery.Registrations.Should().BeEmpty();
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        harness.WorkflowDispatch.Command!.CommandIdSeed.Should().Be(callId);
        harness.WorkflowDispatch.Command.CompletionNotificationTarget.Should().BeNull();
        using var result = JsonDocument.Parse(completion.ToolExecutionResultJson);
        result.RootElement.GetProperty("command_id").GetString().Should().Be(callId);
    }

    [Fact]
    public async Task StartWorkflow_ShouldUseOwnerScope_WhenLarkChannelCarriesOwnerScope()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-workflow-lark-owner-scope",
            scopeId: "registration-scope-1",
            ownerScopeId: "owner-scope-1",
            channelPlatform: "lark",
            channelRegistrationScopeId: "registration-scope-1");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        harness.WorkflowDispatch.Command!.ScopeId.Should().Be("owner-scope-1");
    }

    [Fact]
    public async Task StartWorkflow_ShouldUseOwnerScope_WhenLarkChannelCarriesOwnerScopeWithoutOwnerSubject()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-workflow-lark-owner-scope-no-owner-subject",
            scopeId: "registration-scope-1",
            ownerSubject: null,
            ownerScopeId: "owner-scope-1",
            channelPlatform: "lark",
            channelRegistrationScopeId: "registration-scope-1");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        harness.WorkflowDispatch.Command!.ScopeId.Should().Be("owner-scope-1");
    }

    [Fact]
    public async Task StartWorkflow_ShouldUseOwnerScope_WhenContextCarriesOwnerScope()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-workflow-api-registration-scope",
            scopeId: "registration-scope-1",
            ownerScopeId: "owner-scope-1",
            channelPlatform: null,
            channelRegistrationScopeId: null);
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        harness.WorkflowDispatch.Command!.ScopeId.Should().Be("owner-scope-1");
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
        result.RunId.Should().Be("workflow-actor");
        using var invocationResultJson = JsonDocument.Parse(result.ToolExecutionResultJson);
        invocationResultJson.RootElement.GetProperty("command_id").GetString().Should().Be("call-workflow-typed");
        result.ScopeId.Should().Be("scope-1");
        result.WaitMode.Should().Be(ChatRunSubRunWaitMode.Stream);
        result.Status.Should().Be("streaming");
        result.ActorId.Should().Be("workflow-actor");
        result.StreamTopic.Should().Be("aevatar://actors/workflow-actor/runs/call-workflow-typed");
        result.CompletionObserved.Should().BeFalse();
        result.CompletionResultJson.Should().BeEmpty();
        result.ErrorCode.Should().BeEmpty();
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        ShouldCarryTypedTrustedCallerValues(harness.WorkflowDispatch.Command!);
        ShouldNotCarryTrustedCallerValues(harness.WorkflowDispatch.Command!.Metadata);
    }

    [Fact]
    public async Task StartWorkflowForChatRun_WaitStream_ShouldRegisterBackgroundDeliveryAndAckFast()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(
            callId: "call-workflow-delivery",
            durableReplyCredentialRef: "secrets://nyx/reply-1",
            externalMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["channel.durable_reply_credential_ref"] = "secrets://nyx/forged-reply",
            });
        var request = BuildChatRunRequest(
            "response-workflow",
            "call-workflow-delivery-tool",
            "aevatar_start_workflow",
            """
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "stream"
            }
            """);

        var result = await dispatcher.StartWorkflowForChatRunAsync(request, request.ArgumentsJson);

        result.WaitMode.Should().Be(ChatRunSubRunWaitMode.Stream);
        result.Status.Should().Be("streaming");
        result.ErrorCode.Should().BeEmpty();
        result.CompletionObserved.Should().BeFalse();
        result.CompletionResultJson.Should().BeEmpty();
        harness.WorkflowRunDelivery.Registrations.Should().ContainSingle();
        var registration = harness.WorkflowRunDelivery.Registrations.Single();
        registration.DeliveryId.Should().Be(harness.WorkflowRunDelivery.Reservations.Single().DeliveryId);
        registration.WorkflowActorId.Should().Be("workflow-actor");
        registration.WorkflowRunId.Should().Be("call-workflow-delivery");
        registration.WorkflowCommandId.Should().Be("call-workflow-delivery");
        registration.WorkflowCorrelationId.Should().Be("request-1");
        registration.StreamTopic.Should().Be("aevatar://actors/workflow-actor/runs/call-workflow-delivery");
        registration.ChannelPlatform.Should().Be("telegram");
        registration.ReplyMessageId.Should().Be("message-1");
        registration.PlatformMessageId.Should().Be("platform-message-1");
        registration.WorkflowResultDeliveryCredential.SecretReference.Ref.Should().Be("secrets://nyx/reply-1");
        registration.RegistrationScopeId.Should().Be("registration-scope-1");
        registration.BotRegistrationId.Should().Be("bot-reg-1");

        using var resultJson = JsonDocument.Parse(result.ToolExecutionResultJson);
        var delivery = resultJson.RootElement.GetProperty("workflow_run_delivery");
        delivery.GetProperty("delivery_actor_id").GetString().Should().Be("reserved-delivery-actor");
        delivery.GetProperty("workflow_actor_id").GetString().Should().Be("workflow-actor");
        delivery.GetProperty("workflow_command_id").GetString().Should().Be("call-workflow-delivery");
        // The boundary JSON must not echo any credential handle.
        delivery.TryGetProperty("durable_reply_credential_ref", out var credentialProperty).Should().BeFalse();
        credentialProperty.ValueKind.Should().Be(JsonValueKind.Undefined);
        result.ToolExecutionResultJson.Should().NotContain("secrets://nyx/reply-1");
    }

    [Fact]
    public async Task StartWorkflowForChatRun_DefaultWaitWithChannelCredential_ShouldReserveBeforeDispatchAndRegisterAfterAcceptance()
    {
        var credentialExpiresAtUnixMs = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
        var harness = new Harness();
        harness.WorkflowRunDelivery.DeliveryActorId = "delivery-actor-alpha";
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt(
                "workflow-actor-alpha",
                "wf-main",
                "ignored-command",
                "ignored-correlation"));
        harness.WorkflowDispatch.OnDispatch = command =>
        {
            harness.WorkflowRunDelivery.Reservations.Should().ContainSingle();
            harness.WorkflowRunDelivery.Registrations.Should().BeEmpty();
            command.CommandIdSeed.Should().Be("command-alpha");
            command.CorrelationIdSeed.Should().Be("correlation-alpha");
            command.CompletionNotificationTarget.Should().NotBeNull();
            command.CompletionNotificationTarget!.ActorId.Should().Be("delivery-actor-alpha");
            var reservation = harness.WorkflowRunDelivery.Reservations.Single();
            command.CompletionNotificationTarget.DeliveryId.Should().Be(reservation.DeliveryId);
            reservation.DeliveryId.Should().StartWith("workflow-delivery:");
            reservation.DeliveryId.Should().NotContain("command-alpha");
            command.CompletionNotificationTarget.ActorId.Should().NotBe(reservation.DeliveryId);
            command.CompletionNotificationTarget.ExpiresAtUnixMs.Should().Be(credentialExpiresAtUnixMs);
        };
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(
            callId: "command-alpha",
            requestId: "correlation-alpha",
            durableReplyCredentialRef: "secrets://nyx/reply-alpha",
            durableReplyCredentialExpiresAtUnixMs: credentialExpiresAtUnixMs);
        var request = BuildChatRunRequest(
            "response-alpha",
            "tool-call-alpha",
            "aevatar_start_workflow",
            """
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" }
            }
            """);

        var result = await dispatcher.StartWorkflowForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().BeEmpty(result.ToolExecutionResultJson);
        result.RunId.Should().Be("workflow-actor-alpha");
        using var invocationResultJson = JsonDocument.Parse(result.ToolExecutionResultJson);
        invocationResultJson.RootElement.GetProperty("command_id").GetString().Should().Be("command-alpha");
        result.ActorId.Should().Be("workflow-actor-alpha");
        harness.WorkflowRunDelivery.Reservations.Should().ContainSingle()
            .Which.ExpectedWorkflowCommandId.Should().Be("command-alpha");
        var registration = harness.WorkflowRunDelivery.Registrations.Should().ContainSingle().Which;
        registration.DeliveryId.Should().Be(harness.WorkflowRunDelivery.Reservations.Single().DeliveryId);
        registration.WorkflowCorrelationId.Should().Be("correlation-alpha");
        harness.WorkflowRunDelivery.Abandonments.Should().BeEmpty();
    }

    [Fact]
    public async Task StartWorkflowForChatRun_WhenDeliveryReservationFails_ShouldNotDispatch()
    {
        var harness = new Harness();
        harness.WorkflowRunDelivery.ReserveFailure = new InvalidOperationException("reservation boom");
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(
            callId: "command-reserve-failure",
            durableReplyCredentialRef: "secrets://nyx/reply-reserve-failure");
        var request = BuildChatRunRequest(
            "response-reserve-failure",
            "tool-call-reserve-failure",
            "aevatar_start_workflow",
            """
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" }
            }
            """);

        var result = await dispatcher.StartWorkflowForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().Be("workflow_background_delivery_reservation_failed");
        result.ToolExecutionResultJson.Should().NotContain("reservation boom");
        harness.WorkflowRunDelivery.Reservations.Should().ContainSingle();
        harness.WorkflowDispatch.Command.Should().BeNull();
        harness.WorkflowRunDelivery.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task StartWorkflowForChatRun_WhenDispatchThrows_ShouldAbandonReservation()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Failure = new InvalidOperationException("dispatch boom");
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(
            callId: "command-dispatch-failure",
            durableReplyCredentialRef: "secrets://nyx/reply-dispatch-failure");
        var request = BuildChatRunRequest(
            "response-dispatch-failure",
            "tool-call-dispatch-failure",
            "aevatar_start_workflow",
            """
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" }
            }
            """);

        var act = () => dispatcher.StartWorkflowForChatRunAsync(request, request.ArgumentsJson);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("dispatch boom");
        harness.WorkflowRunDelivery.Reservations.Should().ContainSingle();
        harness.WorkflowRunDelivery.Registrations.Should().BeEmpty();
        harness.WorkflowRunDelivery.Abandonments.Should().ContainSingle();
        harness.WorkflowRunDelivery.Abandonments[0].Receipt.WorkflowCommandId
            .Should().Be("command-dispatch-failure");
    }

    [Fact]
    public async Task StartWorkflowForChatRun_WhenDispatchAdmissionIsRejected_ShouldAbandonWithoutAcknowledging()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(
                new WorkflowChatRunAcceptedReceipt(
                    "workflow-actor-rejected",
                    "wf-main",
                    "ignored-command",
                    "ignored-correlation"),
                new DispatchAdmission(
                    false,
                    "command-admission-rejected",
                    DateTimeOffset.UtcNow,
                    "workflow-actor-rejected",
                    "correlation-admission-rejected"));
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(
            callId: "command-admission-rejected",
            requestId: "correlation-admission-rejected",
            durableReplyCredentialRef: "secrets://nyx/reply-admission-rejected");
        var request = BuildChatRunRequest(
            "response-admission-rejected",
            "tool-call-admission-rejected",
            "aevatar_start_workflow",
            """
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" }
            }
            """);

        var result = await dispatcher.StartWorkflowForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().Be("dispatch_not_accepted");
        result.RunId.Should().BeEmpty();
        result.Status.Should().BeEmpty();
        harness.WorkflowRunDelivery.Reservations.Should().ContainSingle();
        harness.WorkflowRunDelivery.Registrations.Should().BeEmpty();
        harness.WorkflowRunDelivery.Abandonments.Should().ContainSingle();
    }

    [Fact]
    public async Task StartWorkflowForChatRun_WhenAcceptedCommandIdentityDiffers_ShouldReturnAcceptedWithoutAbandoning()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.HonorCommandSeeds = false;
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt(
                "workflow-actor-mismatch",
                "wf-main",
                "accepted-command-mismatch",
                "accepted-correlation-mismatch"));
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(
            callId: "reserved-command-mismatch",
            requestId: "reserved-correlation-mismatch",
            durableReplyCredentialRef: "secrets://nyx/reply-command-mismatch");
        var request = BuildChatRunRequest(
            "response-command-mismatch",
            "tool-call-command-mismatch",
            "aevatar_start_workflow",
            """
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" }
            }
            """);

        var result = await dispatcher.StartWorkflowForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().BeEmpty();
        result.Status.Should().Be("streaming");
        result.RunId.Should().Be("workflow-actor-mismatch");
        harness.WorkflowRunDelivery.Registrations.Should().ContainSingle();
        harness.WorkflowRunDelivery.Abandonments.Should().BeEmpty();
        using var resultJson = JsonDocument.Parse(result.ToolExecutionResultJson);
        resultJson.RootElement.GetProperty("workflow_run_delivery")
            .GetProperty("workflow_command_id").GetString()
            .Should().Be("accepted-command-mismatch");
    }

    [Fact]
    public async Task StartWorkflowForChatRun_WaitStreamWithoutRegistrationPort_ShouldNotStartWorkflow()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var dispatcher = harness.CreateDispatcher(withWorkflowRunDeliveryRegistrationPort: false);

        using var _ = PushContext(
            callId: "call-workflow-delivery-no-port",
            durableReplyCredentialRef: "secrets://nyx/reply-1");
        var request = BuildChatRunRequest(
            "response-workflow",
            "call-workflow-delivery-no-port-tool",
            "aevatar_start_workflow",
            """
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "stream"
            }
            """);

        var result = await dispatcher.StartWorkflowForChatRunAsync(request, request.ArgumentsJson);

        result.RunId.Should().BeEmpty();
        result.ActorId.Should().BeEmpty();
        result.Status.Should().BeEmpty();
        result.StreamTopic.Should().BeEmpty();
        result.ErrorCode.Should().Be("channel_workflow_delivery_unavailable");
        harness.WorkflowDispatch.Command.Should().BeNull(
            "a workflow run without a registered delivery path would lose the terminal channel result");
        harness.WorkflowRunDelivery.Registrations.Should().BeEmpty();
        using var resultJson = JsonDocument.Parse(result.ToolExecutionResultJson);
        resultJson.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("channel_workflow_delivery_unavailable");
        var errorMessage = resultJson.RootElement.GetProperty("error").GetProperty("message").GetString();
        errorMessage.Should().NotBeNull();
        errorMessage!.ToLowerInvariant()
            .Should().NotContain("durable")
            .And.NotContain("credential");
        resultJson.RootElement.TryGetProperty("workflow_run_delivery", out var missingDelivery).Should().BeFalse();
        missingDelivery.ValueKind.Should().Be(JsonValueKind.Undefined);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartWorkflowForChatRun_WhenRegistrationFailsOrIsCanceled_ShouldReturnAcceptedWorkflowWithFallbackReceipt(
        bool canceled)
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        harness.WorkflowRunDelivery.Failure = canceled
            ? new OperationCanceledException("registration canceled")
            : new InvalidOperationException("registration boom");
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(
            callId: "call-workflow-delivery-throws",
            durableReplyCredentialRef: "secrets://nyx/reply-1");
        var request = BuildChatRunRequest(
            "response-workflow",
            "call-workflow-delivery-throws-tool",
            "aevatar_start_workflow",
            """
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "stream"
            }
            """);

        var result = await dispatcher.StartWorkflowForChatRunAsync(request, request.ArgumentsJson);

        result.RunId.Should().Be("workflow-actor");
        result.Status.Should().Be("streaming");
        result.StreamTopic.Should().Be("aevatar://actors/workflow-actor/runs/call-workflow-delivery-throws");
        result.ErrorCode.Should().BeEmpty();
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        harness.WorkflowRunDelivery.Registrations.Should().ContainSingle();
        harness.WorkflowRunDelivery.Abandonments.Should().BeEmpty(
            "the accepted workflow still owns the typed completion target and can deliver without the late registration bind");
        using var resultJson = JsonDocument.Parse(result.ToolExecutionResultJson);
        resultJson.RootElement.TryGetProperty("error", out var ignoredError).Should().BeFalse();
        ignoredError.ValueKind.Should().Be(JsonValueKind.Undefined);
        var delivery = resultJson.RootElement.GetProperty("workflow_run_delivery");
        delivery.GetProperty("delivery_actor_id").GetString().Should().Be("reserved-delivery-actor");
        delivery.GetProperty("workflow_command_id").GetString().Should().Be("call-workflow-delivery-throws");
    }

    [Fact]
    public async Task StartWorkflow_WhenBackgroundDeliveryRegistered_ShouldCreateReceiptFromRegistrationReceipt()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-workflow-delivery-receipt",
            durableReplyCredentialRef: "secrets://nyx/reply-1");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "stream"
            }
            """);
        var receipt = tool.CreateSuccessReceipt("call-workflow-delivery-receipt", tool.Name, output);

        receipt.Should().NotBeNull();
        receipt!.WorkflowRunDelivery.Should().NotBeNull();
        receipt.WorkflowRunDelivery.DeliveryActorId.Should().Be("reserved-delivery-actor");
        receipt.WorkflowRunDelivery.WorkflowActorId.Should().Be("workflow-actor");
        receipt.WorkflowRunDelivery.WorkflowCommandId.Should().Be("call-workflow-delivery-receipt");
        // The receipt intentionally carries no credential handle.
        receipt.WorkflowRunDelivery.ToString().Should().NotContain("secrets://nyx/reply-1");
    }

    [Fact]
    public async Task StartWorkflowForChatRun_WaitStreamWithoutChannelDelivery_ShouldReturnProductFailure()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(
            callId: "call-workflow-delivery-missing-credential",
            durableReplyCredentialRef: string.Empty,
            externalMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["channel.durable_reply_credential_ref"] = "secrets://nyx/forged-reply",
            });
        var request = BuildChatRunRequest(
            "response-workflow",
            "call-workflow-delivery-missing-credential-tool",
            "aevatar_start_workflow",
            """
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "stream"
            }
            """);

        var result = await dispatcher.StartWorkflowForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().Be("channel_workflow_delivery_unavailable");
        result.RunId.Should().BeEmpty();
        harness.WorkflowDispatch.Command.Should().BeNull();
        harness.WorkflowRunDelivery.Registrations.Should().BeEmpty();
        using var resultJson = JsonDocument.Parse(result.ToolExecutionResultJson);
        var errorMessage = resultJson.RootElement.GetProperty("error").GetProperty("message").GetString();
        errorMessage.Should().NotBeNull();
        errorMessage!.ToLowerInvariant()
            .Should().NotContain("durable")
            .And.NotContain("credential");
    }

    [Fact]
    public async Task StartWorkflowForChatRun_DefaultWaitWithoutChannelDelivery_ShouldNotStartWorkflow()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(
            callId: "call-workflow-default-wait-no-channel-delivery",
            durableReplyCredentialRef: string.Empty);
        var request = BuildChatRunRequest(
            "response-workflow",
            "call-workflow-default-wait-no-channel-delivery-tool",
            "aevatar_start_workflow",
            """
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" }
            }
            """);

        var result = await dispatcher.StartWorkflowForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().Be("channel_workflow_delivery_unavailable");
        result.RunId.Should().BeEmpty();
        harness.WorkflowDispatch.Command.Should().BeNull(
            "a channel workflow run without a result delivery path would silently lose the terminal result");
        harness.WorkflowRunDelivery.Registrations.Should().BeEmpty();
        using var resultJson = JsonDocument.Parse(result.ToolExecutionResultJson);
        var errorMessage = resultJson.RootElement.GetProperty("error").GetProperty("message").GetString();
        errorMessage.Should().NotBeNull();
        errorMessage!.ToLowerInvariant()
            .Should().NotContain("durable")
            .And.NotContain("credential");
    }

    [Fact]
    public async Task InvokeTeamForChatRun_DefaultWaitWorkflowEntryWithoutChannelDelivery_ShouldNotDispatch()
    {
        // The workflow-team path shares the pre-dispatch delivery gate with aevatar_start_workflow:
        // a channel invocation without a delivery credential must fail closed before the service
        // invocation dispatch, with the same product-level error.
        var harness = new Harness();
        harness.TeamResolver.Resolution = new TeamEntryMemberResolution(
            "scope-1",
            "team-1",
            "member-1",
            "workflow-service");
        harness.ConfigureServiceTarget(
            ServiceImplementationKind.Workflow,
            serviceId: "workflow-service",
            endpointId: "chat",
            primaryActorId: "deployed-workflow-definition-actor");
        harness.ServiceInvocationResolution.Result!.Artifact.DeploymentPlan.WorkflowPlan =
            new WorkflowServiceDeploymentPlan
            {
                WorkflowName = "published-workflow",
                WorkflowYaml = "name: published-workflow",
                DefinitionActorId = "published-definition-actor",
            };
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(
            callId: "call-team-workflow-no-channel-delivery",
            durableReplyCredentialRef: string.Empty);
        var request = BuildChatRunRequest(
            "response-team-workflow-no-channel-delivery",
            "call-team-workflow-no-channel-delivery-tool",
            "aevatar_invoke_team",
            """
            {
              "team_id": "team-1",
              "endpoint_id": "chat",
              "payload": { "prompt": "run published workflow" }
            }
            """);

        var result = await dispatcher.InvokeTeamForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().Be("channel_workflow_delivery_unavailable");
        result.RunId.Should().BeEmpty();
        harness.ServiceInvocationDispatcher.Calls.Should().BeEmpty(
            "a channel workflow-team run without a result delivery path would silently lose the terminal result");
        harness.WorkflowRunDelivery.Registrations.Should().BeEmpty();
        using var resultJson = JsonDocument.Parse(result.ToolExecutionResultJson);
        var errorMessage = resultJson.RootElement.GetProperty("error").GetProperty("message").GetString();
        errorMessage.Should().NotBeNull();
        errorMessage!.ToLowerInvariant()
            .Should().NotContain("durable")
            .And.NotContain("credential");
    }

    [Fact]
    public async Task InvokeTeam_WhenChannelWorkflowDeliveryIsUnavailable_ShouldCreateProviderOwnedErrorReceipt()
    {
        var harness = new Harness();
        harness.TeamResolver.Resolution = new TeamEntryMemberResolution(
            "scope-1",
            "team-1",
            "member-1",
            "workflow-service");
        harness.ConfigureServiceTarget(
            ServiceImplementationKind.Workflow,
            serviceId: "workflow-service",
            endpointId: "chat",
            primaryActorId: "deployed-workflow-definition-actor");
        harness.ServiceInvocationResolution.Result!.Artifact.DeploymentPlan.WorkflowPlan =
            new WorkflowServiceDeploymentPlan
            {
                WorkflowName = "published-workflow",
                WorkflowYaml = "name: published-workflow",
                DefinitionActorId = "published-definition-actor",
            };
        var tool = await harness.DiscoverToolAsync("aevatar_invoke_team");
        const string arguments = """
            {
              "team_id": "team-1",
              "endpoint_id": "chat",
              "payload": { "prompt": "run published workflow" }
            }
            """;

        using var _ = PushContext(
            callId: "call-team-workflow-no-channel-delivery-receipt",
            durableReplyCredentialRef: string.Empty);
        var output = await tool.ExecuteAsync(arguments);
        var receipt = tool.CreateResultReceipt(
            "call-team-workflow-no-channel-delivery-receipt",
            tool.Name,
            arguments,
            output);

        receipt.Should().NotBeNull();
        receipt!.CallId.Should().Be("call-team-workflow-no-channel-delivery-receipt");
        receipt.ToolName.Should().Be("aevatar_invoke_team");
        receipt.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ApprovalMode.Should().Be(AgentToolReceiptApprovalMode.NeverRequire);
        receipt.ErrorCode.Should().Be(
            AgentToolFailureCodes.ChannelWorkflowResultDeliveryUnavailable);
        receipt.ErrorMessage.Should().Contain("Repair workflow replies");
        receipt.ErrorMessage.Should().Contain("provider webhook settings usually do not need changes");
        receipt.ErrorMessage.Should().NotContain("Lark");
        receipt.ErrorMessage.Should().NotContain("lark");
        receipt.ErrorMessage.Should().NotContain("developer-console");
        receipt.ErrorMessage.Should().NotContain("developer console");
        receipt.ResultJson.Should().Be(output);
        receipt.ResultJson.Should().NotContain("secrets://");
        harness.ServiceInvocationDispatcher.Calls.Should().BeEmpty();

        tool.CreateResultReceipt(
                "call-team-success",
                tool.Name,
                arguments,
                """{"run_id":"run-alpha","status":"accepted"}""")
            .Should().BeNull("successful invoke-team results retain their existing receipt behavior");
    }

    [Fact]
    public async Task StartWorkflow_WhenServerSetCallerCredentialIsMalformed_ShouldReturnStructuredError()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(callId: "call-workflow-invalid-credential", accessToken: "Bearer access-token");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "ack"
            }
            """);

        ErrorCode(output).Should().Be("invalidcallercredential");
        harness.WorkflowDispatch.Command.Should().BeNull();
    }

    [Fact]
    public async Task StartWorkflow_WhenSourceReadableCredentialHasNoExecutionCredential_ShouldReturnStructuredError()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-workflow-source-only",
            accessToken: null,
            nyxIdCredentialKind: AgentToolNyxIdCredentialKind.ProxyDelegation,
            sourceReadableAccessToken: "source-token");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "ack"
            }
            """);

        ErrorCode(output).Should().Be("invalidcallercredential");
        harness.WorkflowDispatch.Command.Should().BeNull();
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
    public async Task InvokeGAgentForChatRun_ShouldPreserveWorkflowRuntimeInCanonicalToolContextPayload()
    {
        var harness = new Harness();
        var dispatcher = harness.CreateDispatcher();

        using var _ = PushContext(
            callId: "call-gagent-runtime",
            workflowRuntime: new AgentWorkflowRuntimeContext(
                "parent-actor",
                "parent-run",
                "parent-step",
                "root-run",
                2));
        var request = BuildChatRunRequest(
            "response-gagent",
            "call-gagent-runtime-tool",
            "aevatar_invoke_gagent",
            """
            {
              "actor_id": "actor-1",
              "payload": { "prompt": "run gagent" }
            }
            """);

        var result = await dispatcher.InvokeGAgentForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().BeEmpty();
        harness.ActorDispatch.Calls.Should().ContainSingle();
        var chatRequest = harness.ActorDispatch.Calls.Single().Envelope.Payload.Unpack<ChatRequestEvent>();
        chatRequest.ToolContext.WorkflowRuntime.ParentActorId.Should().Be("parent-actor");
        chatRequest.ToolContext.WorkflowRuntime.ParentRunId.Should().Be("parent-run");
        chatRequest.ToolContext.WorkflowRuntime.ParentStepId.Should().Be("parent-step");
        chatRequest.ToolContext.WorkflowRuntime.RootRunId.Should().Be("root-run");
        chatRequest.ToolContext.WorkflowRuntime.Depth.Should().Be(2);
        chatRequest.ToolContext.SkillRecovery.Should().NotBeNull();
    }

    [Fact]
    public async Task InvokeGAgentForChatRun_ShouldPreserveInputFileRefsInCanonicalToolContextPayload()
    {
        var harness = new Harness();
        var dispatcher = harness.CreateDispatcher();
        var inputFileRef = new Aevatar.AI.Abstractions.ChatFileRef
        {
            FileId = "file-current-1",
            ArtifactId = "workflow-file://file-current-1",
            SourceKind = Aevatar.AI.Abstractions.ChatFileSourceKind.ConnectedServiceResource,
            SourceMessageId = "om_current_1",
            SourceResourceKey = "file_key_current_1",
            FileName = "Invoice-O3XHKQSN-0004.pdf",
            MediaType = "application/pdf",
            SizeBytes = 4321,
        };

        using var _ = PushContext(
            callId: "call-gagent-input-file-ref",
            inputFileRefs: [inputFileRef]);
        var request = BuildChatRunRequest(
            "response-gagent",
            "call-gagent-input-file-ref-tool",
            "aevatar_invoke_gagent",
            """
            {
              "actor_id": "actor-1",
              "payload": { "prompt": "run gagent" }
            }
            """);

        var result = await dispatcher.InvokeGAgentForChatRunAsync(request, request.ArgumentsJson);

        result.ErrorCode.Should().BeEmpty();
        harness.ActorDispatch.Calls.Should().ContainSingle();
        var chatRequest = harness.ActorDispatch.Calls.Single().Envelope.Payload.Unpack<ChatRequestEvent>();
        chatRequest.InputParts.Should().BeEmpty();
        var fileRef = chatRequest.ToolContext.InputFileRefs.Should().ContainSingle().Subject;
        fileRef.FileId.Should().Be("file-current-1");
        fileRef.ArtifactId.Should().Be("workflow-file://file-current-1");
        fileRef.SourceKind.Should().Be(Aevatar.AI.Abstractions.ChatFileSourceKind.ConnectedServiceResource);
        fileRef.SourceMessageId.Should().Be("om_current_1");
        fileRef.SourceResourceKey.Should().Be("file_key_current_1");
        fileRef.FileName.Should().Be("Invoice-O3XHKQSN-0004.pdf");
        fileRef.MediaType.Should().Be("application/pdf");
        fileRef.SizeBytes.Should().Be(4321);
    }

    [Fact]
    public async Task StartWorkflow_WhenTrustedWorkflowRuntimeExists_ShouldCreateManagedHandoffReceipt()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-managed-workflow",
            workflowRuntime: new AgentWorkflowRuntimeContext(
                "parent-actor",
                "parent-run",
                "parent-step",
                "root-run",
                2));

        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "child-flow",
              "inputs": {
                "prompt": "run child",
                "input_parts": [
                  {
                    "kind": "file",
                    "file_ref": {
                      "file_id": "file-child-1",
                      "artifact_id": "workflow-file://file-child-1",
                      "source_kind": 3,
                      "source_message_id": "om_child_1",
                      "source_resource_key": "file_key_child_1",
                      "file_name": "child.pdf",
                      "media_type": "application/pdf",
                      "size_bytes": 456
                    }
                  }
                ]
              },
              "wait": "stream"
            }
            """);
        var receipt = tool.CreateSuccessReceipt("call-managed-workflow", tool.Name, output);

        receipt.Should().NotBeNull();
        receipt!.ManagedWorkflowHandoff.Should().NotBeNull();
        receipt.ManagedWorkflowHandoff.ParentActorId.Should().Be("parent-actor");
        receipt.ManagedWorkflowHandoff.ParentRunId.Should().Be("parent-run");
        receipt.ManagedWorkflowHandoff.ParentStepId.Should().Be("parent-step");
        receipt.ManagedWorkflowHandoff.InvocationId.Should().Be("parent-run:workflow_tool:parent-step:call-managed-workflow");
        receipt.ManagedWorkflowHandoff.ChildRunId.Should().Be("parent-run:workflow_tool:parent-step:call-managed-workflow");
    }

    [Fact]
    public async Task StartWorkflow_CreateResultReceipt_WhenCanonicalRunWasObserved_ShouldPreserveTypedStage()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");
        const string result = """
            {
              "run_id": "workflow-actor",
              "status": "accepted",
              "actor_id": "workflow-actor",
              "command_id": "workflow-command",
              "mutation_stage": "read_model_observed"
            }
            """;

        var receipt = tool.CreateResultReceipt(
            "call-workflow-observed",
            tool.Name,
            "{}",
            result);

        receipt.Should().NotBeNull();
        receipt!.Effect.Should().Be(AgentToolReceiptEffect.Mutating);
        receipt.MutationStage.Should().Be(AgentToolReceiptMutationStage.ReadModelObserved);
        receipt.SubjectKind.Should().Be(AevatarInvocationReceiptJson.InvocationRunSubjectKind);
        receipt.SubjectId.Should().Be("workflow-actor");
    }

    [Fact]
    public async Task StartWorkflow_CreateResultReceipt_WhenOnlyDispatchWasAccepted_ShouldNotClaimObservation()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");
        const string result = """
            {
              "run_id": "workflow-actor",
              "status": "accepted",
              "actor_id": "workflow-actor",
              "command_id": "workflow-command",
              "mutation_stage": "accepted"
            }
            """;

        var receipt = tool.CreateResultReceipt(
            "call-workflow-accepted",
            tool.Name,
            "{}",
            result);

        receipt.Should().NotBeNull();
        receipt!.Effect.Should().Be(AgentToolReceiptEffect.Mutating);
        receipt.MutationStage.Should().Be(AgentToolReceiptMutationStage.Accepted);
        receipt.MutationStage.Should().NotBe(AgentToolReceiptMutationStage.ReadModelObserved);
    }

    [Fact]
    public async Task StartWorkflow_WhenExactCanonicalRunIsVisible_ShouldReturnObservedReceipt()
    {
        var harness = new Harness();
        harness.WorkflowQuery.Snapshot = new WorkflowActorSnapshot
        {
            ScopeId = "scope-1",
            ActorId = "workflow-actor",
            LastCommandId = "call-workflow-observation",
            StateVersion = 1,
        };
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-workflow-observation",
            channelPlatform: null,
            channelRegistrationScopeId: null,
            durableReplyCredentialRef: null);
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "ack"
            }
            """);
        var receipt = tool.CreateResultReceipt(
            "call-workflow-observation",
            tool.Name,
            "{}",
            output);

        Read(output).GetProperty("mutation_stage").GetString().Should().Be("read_model_observed");
        Read(output).GetProperty("command_id").GetString().Should().Be("call-workflow-observation");
        harness.WorkflowQuery.LastCurrentStateActorId.Should().Be("workflow-actor");
        receipt.Should().NotBeNull();
        receipt!.MutationStage.Should().Be(AgentToolReceiptMutationStage.ReadModelObserved);
    }

    [Fact]
    public async Task WorkflowStartReadModelObserver_WhenAcceptedRunIsNotVisible_ShouldRemainUnobserved()
    {
        var harness = new Harness();
        var observer = new WorkflowStartReadModelObserver(
            harness.WorkflowQuery,
            observationTimeout: TimeSpan.Zero);

        var observed = await observer.ObserveAsync(
            "scope-1",
            "workflow-actor",
            "workflow-command",
            CancellationToken.None);

        observed.Should().BeFalse();
        harness.WorkflowQuery.LastCurrentStateActorId.Should().Be("workflow-actor");
    }

    [Fact]
    public async Task WorkflowStartReadModelObserver_WhenSnapshotIsStale_ShouldRemainUnobserved()
    {
        var harness = new Harness();
        harness.WorkflowQuery.Snapshot = new WorkflowActorSnapshot
        {
            ScopeId = "scope-1",
            ActorId = "workflow-actor",
            LastCommandId = "older-command",
            StateVersion = 9,
        };
        var observer = new WorkflowStartReadModelObserver(
            harness.WorkflowQuery,
            observationTimeout: TimeSpan.Zero);

        var observed = await observer.ObserveAsync(
            "scope-1",
            "workflow-actor",
            "workflow-command",
            CancellationToken.None);

        observed.Should().BeFalse();
    }

    [Fact]
    public async Task WorkflowStartReadModelObserver_WhenExactCommittedSnapshotIsVisible_ShouldObserve()
    {
        var harness = new Harness();
        harness.WorkflowQuery.Snapshot = new WorkflowActorSnapshot
        {
            ScopeId = "scope-1",
            ActorId = "workflow-actor",
            LastCommandId = "workflow-command",
            StateVersion = 9,
        };
        var observer = new WorkflowStartReadModelObserver(
            harness.WorkflowQuery,
            observationTimeout: TimeSpan.Zero);

        var observed = await observer.ObserveAsync(
            "scope-1",
            "workflow-actor",
            "workflow-command",
            CancellationToken.None);

        observed.Should().BeTrue();
    }

    [Fact]
    public async Task StartWorkflow_WhenManagedRuntimeExists_ShouldPopulateInputFileRefsFromAmbientRefs()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");
        var ambientFileRef = new Aevatar.AI.Abstractions.ChatFileRef
        {
            FileId = "file-ambient-child-1",
            ArtifactId = "workflow-file://file-ambient-child-1",
            SourceKind = Aevatar.AI.Abstractions.ChatFileSourceKind.ConnectedServiceResource,
            SourceMessageId = "om_ambient_child_1",
            SourceResourceKey = "file_key_ambient_child_1",
            FileName = "ambient-child.pdf",
            MediaType = "application/pdf",
            SizeBytes = 654,
        };

        using var _ = PushContext(
            callId: "call-managed-workflow-ambient-file-ref",
            workflowRuntime: new AgentWorkflowRuntimeContext(
                "parent-actor",
                "parent-run",
                "parent-step",
                "root-run",
                2),
            inputFileRefs: [ambientFileRef]);

        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "child-flow",
              "inputs": { "prompt": "run child" },
              "wait": "stream"
            }
            """);
        var receipt = tool.CreateSuccessReceipt("call-managed-workflow-ambient-file-ref", tool.Name, output);

        receipt.Should().NotBeNull();
        receipt!.ManagedWorkflowHandoff.Should().NotBeNull();
        var requested = harness.ActorDispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload
            .Unpack<SubWorkflowInvokeRequestedEvent>();
        var fileRef = requested.InputFileRefs.Should().ContainSingle().Subject;
        fileRef.FileId.Should().Be("file-ambient-child-1");
        fileRef.ArtifactId.Should().Be("workflow-file://file-ambient-child-1");
        fileRef.SourceKind.Should().Be(Aevatar.Workflow.Abstractions.WorkflowFileSourceKind.ConnectedServiceResource);
        fileRef.SourceMessageId.Should().Be("om_ambient_child_1");
        fileRef.SourceResourceKey.Should().Be("file_key_ambient_child_1");
        fileRef.FileName.Should().Be("ambient-child.pdf");
        fileRef.MediaType.Should().Be("application/pdf");
        fileRef.SizeBytes.Should().Be(654);
    }

    [Fact]
    public async Task StartWorkflow_LegacyActorIdCannotOverrideScopeWorkflowTarget()
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
        harness.ScopeWorkflowQuery.Lookups.Should().ContainSingle()
            .Which.Should().Be(("scope-1", "wf-main"));
        harness.WorkflowDispatch.Command.Source.ActorId.Should().Be("workflow-definition-actor-wf-main");
        harness.WorkflowDispatch.Command.Source.WorkflowName.Should().Be("wf-main");
        harness.WorkflowDispatch.Command.CommandIdSeed.Should().Be("call-workflow-actor");
        harness.WorkflowDispatch.Command.CorrelationIdSeed.Should().Be("request-1");
        harness.WorkflowDispatch.Command.CompletionNotificationTarget.Should().NotBeNull();
        harness.WorkflowRunDelivery.Reservations.Should().ContainSingle();
        harness.WorkflowRunDelivery.Registrations.Should().ContainSingle();
        var result = Read(output);
        result.GetProperty("status").GetString().Should().Be("accepted");
        result.GetProperty("wait").GetString().Should().Be("ack");
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
        harness.WorkflowDispatch.Command.Source.ActorId.Should().BeNull();
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
        harness.WorkflowDispatch.Command.CallerCredential!.BearerToken.Should().Be("access-token");
    }

    [Theory]
    [InlineData(
        AgentToolNyxIdCredentialKind.SourceReadableUserBearer,
        NyxIdCallerCredentialKind.SourceReadableUserBearer)]
    [InlineData(
        AgentToolNyxIdCredentialKind.ProxyDelegation,
        NyxIdCallerCredentialKind.ProxyDelegation)]
    public async Task StartWorkflow_ShouldPreserveCallerCredentialKind(
        AgentToolNyxIdCredentialKind toolCredentialKind,
        NyxIdCallerCredentialKind expectedWorkflowCredentialKind)
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-workflow-credential-kind",
            nyxIdCredentialKind: toolCredentialKind);
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowDispatch.Command.Should().NotBeNull();
        harness.WorkflowDispatch.Command!.CallerCredential.Should().NotBeNull();
        harness.WorkflowDispatch.Command.CallerCredential!.BearerToken.Should().Be("access-token");
        harness.WorkflowDispatch.Command.CallerCredential.Kind.Should().Be(expectedWorkflowCredentialKind);
    }

    [Fact]
    public async Task StartWorkflow_WhenSourceReadableCredentialHasAuthority_ShouldProjectRefreshableDelegation()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-workflow-source-readable-authority",
            channelPlatform: "lark",
            senderBindingId: "binding-source-readable",
            nyxIdCredentialKind: AgentToolNyxIdCredentialKind.SourceReadableUserBearer,
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext(
                "lark",
                "tenant-1",
                "external-user-1"));
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        var callerCredential = harness.WorkflowDispatch.Command!.CallerCredential;
        callerCredential.Should().NotBeNull();
        callerCredential!.BearerToken.Should().Be("access-token");
        callerCredential.SourceReadableUserBearerToken.Should().Be("access-token");
        callerCredential.Kind.Should().Be(NyxIdCallerCredentialKind.ProxyDelegation);
        callerCredential.NyxIdAuthority.Should().NotBeNull();
        callerCredential.NyxIdAuthority!.Platform.Should().Be("lark");
        callerCredential.NyxIdAuthority.Tenant.Should().Be("tenant-1");
        callerCredential.NyxIdAuthority.ExternalUserId.Should().Be("external-user-1");
        callerCredential.NyxIdAuthority.Scope.Should().Be("proxy");
        callerCredential.NyxIdAuthority.BindingId.Should().Be("binding-source-readable");
    }

    [Fact]
    public async Task StartWorkflow_FromChannelRegistration_ShouldUseBotAgentKeyWithoutUserBearer()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-workflow-channel-agent-key",
            channelPlatform: "lark",
            nyxIdCredentialKind: AgentToolNyxIdCredentialKind.ProxyDelegation,
            sourceReadableAccessToken: "short-lived-user-token",
            executionOwner: AgentToolExecutionOwners.ChannelRegistration("bot-reg-1"));
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "complete"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        var callerCredential = harness.WorkflowDispatch.Command!.CallerCredential;
        callerCredential.Should().NotBeNull();
        callerCredential!.BearerToken.Should().BeNull();
        callerCredential.SourceReadableUserBearerToken.Should().BeNull();
        callerCredential.NyxIdAuthority.Should().BeNull();
        callerCredential.Kind.Should().Be(NyxIdCallerCredentialKind.AgentKey);
        callerCredential.DurableCallerCredential.Should().NotBeNull();
        callerCredential.DurableCallerCredential!.Ref.Should().Be("secrets://nyx/default-reply");
        callerCredential.DurableCallerCredential.SourceKind.Should().Be(
            DurableCallerCredentialSourceKind.ChannelRegistration);
        harness.ChannelAgentKeyReadiness.Credentials.Should().ContainSingle();
        harness.ChannelAgentKeyReadiness.Credentials[0].SubjectId.Should().Be("nyx-api-key-1");
        harness.ChannelAgentKeyReadiness.Credentials[0].SourceKind.Should().Be(
            DurableCallerCredentialSourceKind.ChannelRegistration);
    }

    [Theory]
    [InlineData(
        DurableCallerCredentialSourceKind.WebhookBinding,
        CredentialSecretPurposes.WorkflowWebhookBindingAgentKey)]
    [InlineData(
        DurableCallerCredentialSourceKind.ScheduledDispatch,
        CredentialSecretPurposes.ScheduledInvocationAgentKey)]
    public async Task StartWorkflow_FromDurableAgentKey_ShouldPreserveVaultHandleWithoutRawKey(
        DurableCallerCredentialSourceKind sourceKind,
        string purpose)
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");
        var descriptor = new SecretReference
        {
            Ref = "secrets://nyx/workflow-agent-key",
            Purpose = purpose,
            OwnerScopeKey = "scope-workflow-agent-key",
            Version = 1,
            Fingerprint = "fingerprint-workflow-agent-key",
            CreatedAtUnixMs = 1,
        };
        var durable = new DurableCallerCredentialRef
        {
            Ref = descriptor.Ref,
            Purpose = descriptor.Purpose,
            OwnerScopeKey = descriptor.OwnerScopeKey,
            SubjectId = "agent-key-workflow",
            SourceKind = sourceKind,
            SecretReference = descriptor,
            ProviderCredentialId = sourceKind == DurableCallerCredentialSourceKind.WebhookBinding
                ? "provider-key-workflow"
                : string.Empty,
        };

        using var _ = PushContext(
            callId: "call-workflow-durable-agent-key",
            accessToken: "nyxid_ag_workflow_secret",
            nyxIdCredentialKind: AgentToolNyxIdCredentialKind.AgentKey,
            durableNyxIdCredential: durable);
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "complete"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        var callerCredential = harness.WorkflowDispatch.Command!.CallerCredential;
        callerCredential.Should().NotBeNull();
        callerCredential!.BearerToken.Should().BeNull();
        callerCredential.Kind.Should().Be(NyxIdCallerCredentialKind.AgentKey);
        callerCredential.DurableCallerCredential.Should().NotBeNull();
        callerCredential.DurableCallerCredential!.Ref.Should().Be(descriptor.Ref);
        callerCredential.DurableCallerCredential.SourceKind.Should().Be(sourceKind);
    }

    [Fact]
    public async Task StartWorkflow_WhenChannelAgentKeyIsNotReady_ShouldFailClosedWithoutUserTokenFallback()
    {
        var harness = new Harness();
        harness.ChannelAgentKeyReadiness.Result =
            ChannelNyxIdAgentKeyReadinessResult.Failed("channel_agent_key_scope_update_failed");
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-workflow-channel-agent-key-not-ready",
            accessToken: "short-lived-user-token",
            sourceReadableAccessToken: "short-lived-source-token",
            channelPlatform: "lark",
            executionOwner: AgentToolExecutionOwners.ChannelRegistration("bot-reg-1"));
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "complete"
            }
            """);

        ErrorCodeOrNull(output).Should().Be("channel_agent_key_scope_update_failed");
        ErrorMessage(output).Should().Contain("will not fall back");
        harness.ChannelAgentKeyReadiness.Credentials.Should().ContainSingle();
        harness.WorkflowDispatch.Command.Should().BeNull();
        output.Should().NotContain("short-lived-user-token");
        output.Should().NotContain("short-lived-source-token");
    }

    [Fact]
    public async Task StartWorkflow_FromChannelRegistrationWithoutAgentKey_ShouldFailClosed()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-workflow-channel-agent-key-missing",
            channelPlatform: "lark",
            durableReplyCredentialRef: null,
            executionOwner: AgentToolExecutionOwners.ChannelRegistration("bot-reg-1"));
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "complete"
            }
            """);

        ErrorCodeOrNull(output).Should().Be("channel_agent_key_unavailable");
        harness.WorkflowDispatch.Command.Should().BeNull();
    }

    [Fact]
    public async Task StartWorkflow_WhenChannelAgentKeyClassRequiresRebind_ShouldNotOfferInPlaceRepair()
    {
        var harness = new Harness();
        harness.ChannelAgentKeyReadiness.Result =
            ChannelNyxIdAgentKeyReadinessResult.Failed("channel_agent_key_rebind_required");
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-workflow-channel-agent-key-rebind",
            accessToken: "short-lived-user-token",
            sourceReadableAccessToken: "short-lived-source-token",
            channelPlatform: "lark",
            executionOwner: AgentToolExecutionOwners.ChannelRegistration("bot-reg-1"));
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "complete"
            }
            """);

        ErrorCodeOrNull(output).Should().Be("channel_agent_key_rebind_required");
        ErrorMessage(output).Should().Contain("remove this registration");
        ErrorMessage(output).Should().Contain("add the bot again");
        ErrorMessage(output).Should().Contain("cannot change an Agent Key security class");
        harness.WorkflowDispatch.Command.Should().BeNull();
    }

    [Fact]
    public async Task StartWorkflow_WhenNyxIdAssistantCredentialHasNoSenderBinding_ShouldNotProjectRefreshableAuthority()
    {
        var harness = new Harness();
        harness.WorkflowDispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "wf-main", "wf-command", "wf-correlation"));
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-workflow-nyxid-assistant-no-binding",
            senderBindingId: null,
            nyxIdCredentialKind: AgentToolNyxIdCredentialKind.SourceReadableUserBearer,
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext(
                "nyxid",
                string.Empty,
                "owner-scope-1",
                "proxy"),
            chatContext: new AgentChatInvocationContext(
                AgentChatInvocationSurface.NyxIdAssistant,
                "conversation-1",
                "turn-1",
                "task-1",
                null,
                null));
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        var callerCredential = harness.WorkflowDispatch.Command!.CallerCredential;
        callerCredential.Should().NotBeNull();
        callerCredential!.BearerToken.Should().Be("access-token");
        callerCredential.Kind.Should().Be(NyxIdCallerCredentialKind.SourceReadableUserBearer);
        callerCredential.NyxIdAuthority.Should().BeNull();
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
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" }
            }
            """);

        ErrorCode(output).Should().Be("workflownotfound");
        ErrorMessage(output).Should().Be(WorkflowChatRunStartErrorGuidance.WorkflowNotFound);
        harness.WorkflowRunDelivery.Reservations.Should().ContainSingle();
        harness.WorkflowRunDelivery.Registrations.Should().BeEmpty();
        harness.WorkflowRunDelivery.Abandonments.Should().ContainSingle();
    }

    [Theory]
    [InlineData("parent_actor_id")]
    [InlineData("parent_run_id")]
    [InlineData("parent_step_id")]
    [InlineData("root_run_id")]
    [InlineData("depth")]
    [InlineData("requested_depth")]
    [InlineData("workflow_runtime_context")]
    [InlineData("workflow_call_context")]
    public async Task StartWorkflow_ShouldRejectPublicWorkflowRuntimeFields(string forbiddenField)
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(callId: "call-workflow-forged");
        var output = await tool.ExecuteAsync($$"""
            {
              "workflow_id": "wf-main",
              "inputs": { "prompt": "run workflow" },
              "{{forbiddenField}}": "forged"
            }
            """);

        ErrorCode(output).Should().Be("invalid_arguments");
        output.Should().Contain(forbiddenField);
        harness.WorkflowDispatch.Command.Should().BeNull();
        harness.ActorDispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task StartWorkflow_WhenTrustedWorkflowRuntimeExists_ShouldDispatchManagedChildStartToParentActor()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-managed-workflow",
            workflowRuntime: new AgentWorkflowRuntimeContext(
                "parent-actor",
                "parent-run",
                "parent-step",
                "root-run",
                2));
        var output = await tool.ExecuteAsync("""
            {
              "workflow_id": "child-flow",
              "inputs": {
                "prompt": "run child",
                "input_parts": [
                  {
                    "kind": "file",
                    "file_ref": {
                      "file_id": "file-child-1",
                      "artifact_id": "workflow-file://file-child-1",
                      "source_kind": 3,
                      "source_message_id": "om_child_1",
                      "source_resource_key": "file_key_child_1",
                      "file_name": "child.pdf",
                      "media_type": "application/pdf",
                      "size_bytes": 456
                    }
                  }
                ]
              },
              "wait": "stream"
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowDispatch.Command.Should().BeNull();
        harness.ActorDispatch.Calls.Should().ContainSingle();
        var call = harness.ActorDispatch.Calls.Single();
        call.ActorId.Should().Be("parent-actor");
        call.Envelope.Route.PublisherActorId.Should().Be("parent-actor");
        call.Envelope.Route.GetTopologyAudience().Should().Be(TopologyAudience.Self);
        call.Envelope.Propagation.CorrelationId.Should().Be("call-managed-workflow");

        var requested = call.Envelope.Payload.Unpack<SubWorkflowInvokeRequestedEvent>();
        requested.InvocationId.Should().Be("parent-run:workflow_tool:parent-step:call-managed-workflow");
        requested.ParentRunId.Should().Be("parent-run");
        requested.ParentStepId.Should().Be("parent-step");
        requested.WorkflowName.Should().Be("child-flow");
        requested.Input.Should().Be("run child");
        requested.RequestedByActorId.Should().Be("parent-actor");
        requested.RootRunId.Should().Be("root-run");
        requested.RequestedDepth.Should().Be(3);
        requested.InputFileRefs.Should().ContainSingle();
        requested.InputFileRefs[0].FileId.Should().Be("file-child-1");
        requested.InputFileRefs[0].ArtifactId.Should().Be("workflow-file://file-child-1");
        requested.InputFileRefs[0].SourceKind.Should().Be(WorkflowFileSourceKind.ConnectedServiceResource);
        requested.InputFileRefs[0].SourceMessageId.Should().Be("om_child_1");
        requested.InputFileRefs[0].SourceResourceKey.Should().Be("file_key_child_1");
        requested.InputFileRefs[0].FileName.Should().Be("child.pdf");
        requested.InputFileRefs[0].MediaType.Should().Be("application/pdf");
        requested.InputFileRefs[0].SizeBytes.Should().Be(456);

        var result = Read(output);
        result.GetProperty("run_id").GetString().Should().Be(requested.InvocationId);
        result.GetProperty("actor_id").GetString().Should().Be("parent-actor");
        result.GetProperty("status").GetString().Should().Be("accepted");
        result.GetProperty("stream_topic").GetString()
            .Should()
            .Be("aevatar://actors/parent-actor/runs/parent-run%3Aworkflow_tool%3Aparent-step%3Acall-managed-workflow");
    }

    [Theory]
    [InlineData(
        """
        {
          "workflow_id": "child-flow",
          "workflow_yamls": ["name: child_flow\nsteps: []"],
          "inputs": { "prompt": "run child" }
        }
        """,
        "workflow_yamls")]
    public async Task StartWorkflow_WhenTrustedWorkflowRuntimeExists_ShouldRejectTopLevelStartOptions(
        string argumentsJson,
        string field)
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_start_workflow");

        using var _ = PushContext(
            callId: "call-managed-workflow-invalid",
            workflowRuntime: new AgentWorkflowRuntimeContext(
                "parent-actor",
                "parent-run",
                "parent-step",
                "root-run",
                1));
        var output = await tool.ExecuteAsync(argumentsJson);

        ErrorCode(output).Should().Be("invalid_arguments");
        output.Should().Contain(field);
        harness.WorkflowDispatch.Command.Should().BeNull();
        harness.ActorDispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ObserveRun_ShouldReadTerminalCorrelationTargetOnly()
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
              "gagent_terminal_correlation": {
                "actor_id": "actor-1",
                "correlation_id": "run-1"
              }
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.TerminalQuery.LastActorId.Should().Be("actor-1");
        harness.TerminalQuery.LastCorrelationId.Should().Be("run-1");
        harness.TerminalQuery.LastSessionId.Should().BeNull();
        harness.ServiceRunQuery.LastRunId.Should().BeNull();
        harness.WorkflowQuery.LastCurrentStateActorId.Should().BeNull();
        var result = Read(output);
        result.GetProperty("run_id").GetString().Should().Be("run-1");
        result.GetProperty("status").GetString().Should().Be(nameof(GAgentRunTerminalStatus.RunFinished));
        result.GetProperty("recent_events").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task ObserveRun_ShouldReadTerminalSessionTargetOnly()
    {
        var harness = new Harness();
        harness.TerminalQuery.BySessionId = new GAgentRunTerminalSnapshot(
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

        using var _ = PushContext(callId: "call-observe-session");
        var output = await tool.ExecuteAsync("""
            {
              "gagent_terminal_session": {
                "actor_id": "actor-1",
                "session_id": "session-1"
              }
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.TerminalQuery.LastActorId.Should().Be("actor-1");
        harness.TerminalQuery.LastCorrelationId.Should().BeNull();
        harness.TerminalQuery.LastSessionId.Should().Be("session-1");
        harness.ServiceRunQuery.LastRunId.Should().BeNull();
        harness.WorkflowQuery.LastCurrentStateActorId.Should().BeNull();
        var result = Read(output);
        result.GetProperty("run_id").GetString().Should().Be("session-1");
        result.GetProperty("status").GetString().Should().Be(nameof(GAgentRunTerminalStatus.RunFinished));
    }

    [Fact]
    public async Task ObserveRun_ServiceRun_WhenReadModelIsMissing_ShouldReturnNotFoundWithoutFallback()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_observe_run");

        using var _ = PushContext(callId: "call-observe-service-run-missing");
        var output = await tool.ExecuteAsync("""
            {
              "service_run": {
                "service_id": "service-1",
                "run_id": "missing-run"
              }
            }
            """);

        ErrorCode(output).Should().Be("service_run_not_found");
        harness.ServiceRunQuery.LastScopeId.Should().Be("scope-1");
        harness.ServiceRunQuery.LastServiceId.Should().Be("service-1");
        harness.ServiceRunQuery.LastRunId.Should().Be("missing-run");
        harness.ServiceRunQuery.LastCommandId.Should().BeNull();
        harness.ServiceRunQuery.LastQuery.Should().BeNull();
        harness.TerminalQuery.LastActorId.Should().BeNull();
        harness.WorkflowQuery.LastCurrentStateActorId.Should().BeNull();
    }

    [Fact]
    public async Task ObserveRun_GAgentTerminalCorrelation_WhenReadModelIsMissing_ShouldReturnNotFoundWithoutFallback()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_observe_run");

        using var _ = PushContext(callId: "call-observe-terminal-correlation-missing");
        var output = await tool.ExecuteAsync("""
            {
              "gagent_terminal_correlation": {
                "actor_id": "actor-1",
                "correlation_id": "missing-correlation"
              }
            }
            """);

        ErrorCode(output).Should().Be("gagent_terminal_not_found");
        harness.TerminalQuery.LastActorId.Should().Be("actor-1");
        harness.TerminalQuery.LastCorrelationId.Should().Be("missing-correlation");
        harness.TerminalQuery.LastSessionId.Should().BeNull();
        harness.ServiceRunQuery.LastRunId.Should().BeNull();
        harness.WorkflowQuery.LastCurrentStateActorId.Should().BeNull();
    }

    [Fact]
    public async Task ObserveRun_GAgentTerminalSession_WhenReadModelIsMissing_ShouldReturnNotFoundWithoutFallback()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_observe_run");

        using var _ = PushContext(callId: "call-observe-terminal-session-missing");
        var output = await tool.ExecuteAsync("""
            {
              "gagent_terminal_session": {
                "actor_id": "actor-1",
                "session_id": "missing-session"
              }
            }
            """);

        ErrorCode(output).Should().Be("gagent_terminal_not_found");
        harness.TerminalQuery.LastActorId.Should().Be("actor-1");
        harness.TerminalQuery.LastCorrelationId.Should().BeNull();
        harness.TerminalQuery.LastSessionId.Should().Be("missing-session");
        harness.ServiceRunQuery.LastRunId.Should().BeNull();
        harness.WorkflowQuery.LastCurrentStateActorId.Should().BeNull();
    }

    [Fact]
    public async Task ObserveRun_ShouldReadServiceRunTargetWithCallerScopeOnly()
    {
        var harness = new Harness();
        harness.ServiceRunQuery.ByRunId = BuildServiceRun("scope-1", "service-1", "run-1", "command-1");
        var tool = await harness.DiscoverToolAsync("aevatar_observe_run");

        using var _ = PushContext(callId: "call-observe-service-run");
        var output = await tool.ExecuteAsync("""
            {
              "service_run": {
                "service_id": "service-1",
                "run_id": "run-1"
              }
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.ServiceRunQuery.LastScopeId.Should().Be("scope-1");
        harness.ServiceRunQuery.LastServiceId.Should().Be("service-1");
        harness.ServiceRunQuery.LastRunId.Should().Be("run-1");
        harness.ServiceRunQuery.LastCommandId.Should().BeNull();
        harness.TerminalQuery.LastActorId.Should().BeNull();
        harness.WorkflowQuery.LastCurrentStateActorId.Should().BeNull();
        var result = Read(output);
        result.GetProperty("run_id").GetString().Should().Be("run-1");
        result.GetProperty("command_id").GetString().Should().Be("command-1");
    }

    [Fact]
    public async Task ObserveRun_ServiceRun_WhenCallerScopeIsUnavailable_ShouldFastFail()
    {
        var harness = new Harness();
        harness.ServiceRunQuery.ByRunId = BuildServiceRun("scope-1", "service-1", "run-1", "command-1");
        var tool = await harness.DiscoverToolAsync("aevatar_observe_run");

        using var _ = PushContext(callId: "call-observe-no-scope", scopeId: null);
        var output = await tool.ExecuteAsync("""
            {
              "service_run": {
                "service_id": "service-1",
                "run_id": "run-1"
              }
            }
            """);

        ErrorCode(output).Should().Be("caller_scope_unavailable");
        harness.ServiceRunQuery.LastScopeId.Should().BeNull();
        harness.TerminalQuery.LastActorId.Should().BeNull();
    }

    [Fact]
    public async Task ObserveRun_ShouldReadWorkflowCurrentStateTargetOnly()
    {
        var harness = new Harness();
        harness.WorkflowQuery.Snapshot = new WorkflowActorSnapshot
        {
            ActorId = "workflow-actor",
            WorkflowName = "wf-main",
            LastCommandId = "workflow-command",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            StateVersion = 9,
            LastOutput = "done",
            LastUpdatedAt = DateTimeOffset.UtcNow,
        };
        var tool = await harness.DiscoverToolAsync("aevatar_observe_run");

        using var _ = PushContext(callId: "call-observe-workflow");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_current_state": {
                "actor_id": "workflow-actor",
                "command_id": "workflow-command"
              }
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowQuery.LastCurrentStateActorId.Should().Be("workflow-actor");
        harness.TerminalQuery.LastActorId.Should().BeNull();
        harness.ServiceRunQuery.LastRunId.Should().BeNull();
        var result = Read(output);
        result.GetProperty("run_id").GetString().Should().Be("workflow-command");
        result.GetProperty("actor_id").GetString().Should().Be("workflow-actor");
        result.GetProperty("status").GetString().Should().Be(nameof(WorkflowRunCompletionStatus.Completed));
    }

    [Fact]
    public async Task ObserveRun_WorkflowCurrentState_ThroughStreamingExecutor_ShouldKeepVerifiedReadResult()
    {
        var harness = new Harness();
        harness.WorkflowQuery.Snapshot = new WorkflowActorSnapshot
        {
            ActorId = "workflow-actor",
            LastCommandId = "workflow-command",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            StateVersion = 9,
            LastOutput = "done",
        };
        var tool = await harness.DiscoverToolAsync("aevatar_observe_run");

        using var _ = PushContext(callId: "call-observe-workflow-executor");
        var result = await ExecuteToolThroughExecutorAsync(
            tool,
            "call-observe-workflow-executor",
            "aevatar_observe_run",
            """
            {
              "workflow_current_state": {
                "actor_id": "workflow-actor",
                "command_id": "workflow-command"
              }
            }
            """);

        result.Result.Should().Contain("\"run_id\":\"workflow-command\"");
        result.Result.Should().Contain("\"status\":\"Completed\"");
        result.Result.Should().NotContain("tool outcome could not be verified");
        result.Receipt.Should().NotBeNull();
        result.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        result.Receipt.SubjectId.Should().Be("workflow-command");
    }

    [Fact]
    public async Task ObserveRun_WorkflowCurrentState_WhenCommandIdIsOmitted_ShouldReturnSnapshotCommandId()
    {
        var harness = new Harness();
        harness.WorkflowQuery.Snapshot = new WorkflowActorSnapshot
        {
            ActorId = "workflow-actor",
            LastCommandId = "snapshot-command",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
        };
        var tool = await harness.DiscoverToolAsync("aevatar_observe_run");

        using var _ = PushContext(callId: "call-observe-workflow-current");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_current_state": {
                "actor_id": "workflow-actor"
              }
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowQuery.LastCurrentStateActorId.Should().Be("workflow-actor");
        harness.TerminalQuery.LastActorId.Should().BeNull();
        harness.ServiceRunQuery.LastRunId.Should().BeNull();
        Read(output).GetProperty("run_id").GetString().Should().Be("snapshot-command");
    }

    [Fact]
    public async Task ObserveRun_WorkflowCurrentState_WhenCommandIdDiffers_ShouldNotFallback()
    {
        var harness = new Harness();
        harness.WorkflowQuery.Snapshot = new WorkflowActorSnapshot
        {
            ActorId = "workflow-actor",
            LastCommandId = "newer-command",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
        };
        var tool = await harness.DiscoverToolAsync("aevatar_observe_run");

        using var _ = PushContext(callId: "call-observe-workflow-stale");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_current_state": {
                "actor_id": "workflow-actor",
                "command_id": "expected-command"
              }
            }
            """);

        ErrorCode(output).Should().Be("workflow_current_state_not_found");
        harness.WorkflowQuery.LastCurrentStateActorId.Should().Be("workflow-actor");
        harness.TerminalQuery.LastActorId.Should().BeNull();
        harness.ServiceRunQuery.LastRunId.Should().BeNull();
    }

    [Fact]
    public async Task ObserveRun_WhenTargetIsMissing_ShouldReturnStructuredError()
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_observe_run");

        using var _ = PushContext(callId: "call-observe-missing-target");
        var output = await tool.ExecuteAsync("{}");

        ErrorCode(output).Should().Be("invalid_arguments");
    }

    [Theory]
    [InlineData(
        """
        {
          "service_run": {
            "run_id": "run-1"
          }
        }
        """)]
    [InlineData(
        """
        {
          "gagent_terminal_correlation": {
            "correlation_id": "correlation-1"
          }
        }
        """)]
    [InlineData(
        """
        {
          "gagent_terminal_session": {
            "actor_id": "actor-1"
          }
        }
        """)]
    [InlineData(
        """
        {
          "workflow_current_state": {
            "command_id": "command-1"
          }
        }
        """)]
    public async Task ObserveRun_WhenNestedRequiredFieldIsMissing_ShouldReturnStructuredErrorWithoutFallback(
        string argumentsJson)
    {
        var harness = new Harness();
        var tool = await harness.DiscoverToolAsync("aevatar_observe_run");

        using var _ = PushContext(callId: "call-observe-nested-missing");
        var output = await tool.ExecuteAsync(argumentsJson);

        ErrorCode(output).Should().Be("invalid_arguments");
        harness.ServiceRunQuery.LastRunId.Should().BeNull();
        harness.TerminalQuery.LastActorId.Should().BeNull();
        harness.WorkflowQuery.LastCurrentStateActorId.Should().BeNull();
    }

    [Fact]
    public async Task ReadWorkflowRunArtifact_ShouldReadReportArtifactWithoutCurrentStateQuery()
    {
        var harness = new Harness();
        harness.WorkflowQuery.Report = new WorkflowRunReport
        {
            RootActorId = "workflow-run-actor",
            WorkflowName = "demo-dinner-workflow",
            CommandId = "run-1",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            StateVersion = 17,
            Success = true,
            StartedAt = new DateTimeOffset(2026, 6, 13, 3, 36, 47, TimeSpan.Zero),
            EndedAt = new DateTimeOffset(2026, 6, 13, 3, 36, 58, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 6, 13, 3, 36, 58, TimeSpan.Zero),
            FinalOutput = "Dinner is ready.",
            Summary = new WorkflowRunStatistics
            {
                TotalSteps = 2,
                RequestedSteps = 2,
                CompletedSteps = 2,
                RoleReplyCount = 1,
            },
            Steps =
            [
                new WorkflowRunStepTrace
                {
                    StepId = "plan",
                    StepType = "llm",
                    TargetRole = "dinner_assistant",
                    Success = true,
                    OutputPreview = "Plan dinner.",
                },
            ],
            RoleReplies =
            [
                new WorkflowRunRoleReply
                {
                    Timestamp = new DateTimeOffset(2026, 6, 13, 3, 36, 58, TimeSpan.Zero),
                    RoleId = "dinner_assistant",
                    Content = "Dinner is ready.",
                    ContentLength = 16,
                },
            ],
        };
        var tool = await harness.DiscoverToolAsync("aevatar_read_workflow_run_artifact");

        var output = await tool.ExecuteAsync("""{"workflow_run_id":"run-1"}""");

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowQuery.LastReportWorkflowRunId.Should().Be("run-1");
        harness.RunBindingReader.LastRunId.Should().Be("run-1");
        harness.WorkflowQuery.LastCurrentStateActorId.Should().BeNull();
        var result = Read(output);
        result.GetProperty("workflow_run_id").GetString().Should().Be("run-1");
        result.GetProperty("artifact_actor_id").GetString().Should().Be("workflow-run-actor");
        result.GetProperty("artifact").GetString().Should().Be("report");
        result.GetProperty("status").GetString().Should().Be(nameof(WorkflowRunCompletionStatus.Completed));
        result.GetProperty("final_output").GetString().Should().Be("Dinner is ready.");
        result.GetProperty("final_output_bytes").GetInt32().Should()
            .Be(Encoding.UTF8.GetByteCount("Dinner is ready."));
        result.GetProperty("final_output_sha256").GetString().Should()
            .Be(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Dinner is ready.")))
                .ToLowerInvariant());
        result.GetProperty("summary").GetProperty("completed_steps").GetInt32().Should().Be(2);
    }

    [Theory]
    [InlineData(WorkflowRunCompletionStatus.Running)]
    [InlineData(WorkflowRunCompletionStatus.AwaitingToolApproval)]
    [InlineData(WorkflowRunCompletionStatus.WaitingForSignal)]
    public async Task ReadWorkflowRunArtifact_MaterializedNonTerminalReport_ShouldOmitNullableSuccess(
        WorkflowRunCompletionStatus status)
    {
        var harness = new Harness();
        harness.WorkflowQuery.Report = new WorkflowRunReport
        {
            RootActorId = "workflow-run-actor",
            WorkflowName = "non-terminal-workflow",
            CommandId = "command-alpha",
            CompletionStatus = status,
            StateVersion = 18,
            Success = null,
        };
        var tool = await harness.DiscoverToolAsync("aevatar_read_workflow_run_artifact");

        var output = await tool.ExecuteAsync("""{"workflow_run_id":"run-1"}""");

        var result = Read(output);
        result.GetProperty("status").GetString().Should().Be(status.ToString());
        result.TryGetProperty("success", out _).Should().BeFalse();
        result.GetProperty("state_version").GetInt64().Should().Be(18);
    }

    [Fact]
    public async Task ReadWorkflowRunArtifact_TimedOutReport_ShouldSerializeTypedTerminalFailure()
    {
        var harness = new Harness();
        harness.WorkflowQuery.Report = new WorkflowRunReport
        {
            RootActorId = "workflow-run-actor",
            WorkflowName = "timed-out-workflow",
            CommandId = "command-alpha",
            CompletionStatus = WorkflowRunCompletionStatus.TimedOut,
            StateVersion = 19,
            Success = false,
            FinalError = "deadline exceeded",
        };
        var tool = await harness.DiscoverToolAsync("aevatar_read_workflow_run_artifact");

        var output = await tool.ExecuteAsync("""{"workflow_run_id":"run-1"}""");

        var result = Read(output);
        result.GetProperty("status").GetString().Should().Be(nameof(WorkflowRunCompletionStatus.TimedOut));
        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("state_version").GetInt64().Should().Be(19);
    }

    [Fact]
    public async Task ReadWorkflowRunArtifact_ReportDigest_ShouldCoverFullUnicodeOutputWithoutTrimming()
    {
        var harness = new Harness();
        var finalOutput = $" \n{new string('x', 2_100)}\u4E2D\u6587\U0001F642\n ";
        harness.WorkflowQuery.Report = new WorkflowRunReport
        {
            RootActorId = "workflow-run-actor",
            WorkflowName = "long-output-workflow",
            CommandId = "command-alpha",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            StateVersion = 18,
            Success = true,
            FinalOutput = finalOutput,
        };
        var tool = await harness.DiscoverToolAsync("aevatar_read_workflow_run_artifact");

        var output = await tool.ExecuteAsync("""{"workflow_run_id":"run-1"}""");

        var result = Read(output);
        result.GetProperty("final_output").GetString().Should().NotBe(finalOutput);
        result.GetProperty("final_output").GetString().Should().EndWith("...");
        var fullBytes = Encoding.UTF8.GetBytes(finalOutput);
        result.GetProperty("final_output_bytes").GetInt32().Should().Be(fullBytes.Length);
        result.GetProperty("final_output_sha256").GetString().Should()
            .Be(Convert.ToHexString(SHA256.HashData(fullBytes)).ToLowerInvariant());
    }

    [Fact]
    public async Task ReadWorkflowRunArtifact_ReportDigest_ShouldHandleEmojiAcrossBufferBoundary()
    {
        var harness = new Harness();
        var tail = new string('z', 8_193);
        var finalOutput = new string('a', 4_095) + "\U0001F642" + tail;
        harness.WorkflowQuery.Report = new WorkflowRunReport
        {
            RootActorId = "workflow-run-actor",
            WorkflowName = "buffer-boundary-workflow",
            CommandId = "command-boundary",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            StateVersion = 19,
            Success = true,
            FinalOutput = finalOutput,
        };
        var tool = await harness.DiscoverToolAsync("aevatar_read_workflow_run_artifact");

        var output = await tool.ExecuteAsync("""{"workflow_run_id":"run-1"}""");

        var result = Read(output);
        var fullBytes = Encoding.UTF8.GetBytes(finalOutput);
        fullBytes.Length.Should().Be(4_095 + 4 + tail.Length);
        result.GetProperty("final_output_bytes").GetInt64().Should().Be(fullBytes.Length);
        result.GetProperty("final_output_sha256").GetString().Should()
            .Be(Convert.ToHexString(SHA256.HashData(fullBytes)).ToLowerInvariant());
    }

    [Fact]
    public async Task ReadWorkflowRunArtifact_EmptyReportOutput_ShouldExposeEmptyDigest()
    {
        var harness = new Harness();
        harness.WorkflowQuery.Report = new WorkflowRunReport
        {
            RootActorId = "workflow-run-actor",
            WorkflowName = "empty-output-workflow",
            CommandId = "command-alpha",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            StateVersion = 19,
            Success = true,
            FinalOutput = string.Empty,
        };
        var tool = await harness.DiscoverToolAsync("aevatar_read_workflow_run_artifact");

        var output = await tool.ExecuteAsync("""{"workflow_run_id":"run-1"}""");

        var result = Read(output);
        result.TryGetProperty("final_output", out _).Should().BeFalse();
        result.GetProperty("final_output_bytes").GetInt32().Should().Be(0);
        result.GetProperty("final_output_sha256").GetString().Should()
            .Be(Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant());
    }

    [Fact]
    public async Task ReadWorkflowRunArtifact_Report_ThroughStreamingExecutor_ShouldKeepVerifiedReadResult()
    {
        var harness = new Harness();
        harness.WorkflowQuery.Report = new WorkflowRunReport
        {
            RootActorId = "workflow-run-actor",
            WorkflowName = "document-file-extraction-workflow",
            CommandId = "run-1",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            StateVersion = 17,
            Success = true,
            FinalOutput = "{\"document_type\":\"receipt\"}",
        };
        var tool = await harness.DiscoverToolAsync("aevatar_read_workflow_run_artifact");

        using var _ = PushContext(callId: "call-read-workflow-artifact-executor");
        var result = await ExecuteToolThroughExecutorAsync(
            tool,
            "call-read-workflow-artifact-executor",
            "aevatar_read_workflow_run_artifact",
            """{"workflow_run_id":"run-1"}""");

        result.Result.Should().Contain("\"workflow_run_id\":\"run-1\"");
        result.Result.Should().Contain("\"artifact\":\"report\"");
        result.Result.Should().Contain("document_type");
        result.Result.Should().NotContain("tool outcome could not be verified");
        result.Receipt.Should().NotBeNull();
        result.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        result.Receipt.SubjectId.Should().Be("run-1");
    }

    [Fact]
    public async Task ReadWorkflowRunArtifact_ShouldResolveShortRunIdThroughBindingProjection()
    {
        var harness = new Harness();
        harness.WorkflowQuery.ReportsByWorkflowRunId["workflow-run-actor"] = new WorkflowRunReport
        {
            RootActorId = "workflow-run-actor",
            CommandId = "run-1",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            Success = true,
        };
        harness.RunBindingReader.BindingsByRunId["run-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "workflow-run-actor",
                "workflow-definition-actor",
                "run-1",
                "demo-dinner-workflow",
                string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Interactive),
        ];
        var tool = await harness.DiscoverToolAsync("aevatar_read_workflow_run_artifact");

        var output = await tool.ExecuteAsync("""{"workflow_run_id":"run-1"}""");

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowQuery.ReportCalls.Should().Equal("run-1", "workflow-run-actor");
        harness.WorkflowQuery.LastCurrentStateActorId.Should().BeNull();
        var result = Read(output);
        result.GetProperty("workflow_run_id").GetString().Should().Be("run-1");
        result.GetProperty("artifact_actor_id").GetString().Should().Be("workflow-run-actor");
        result.GetProperty("root_actor_id").GetString().Should().Be("workflow-run-actor");
    }

    [Fact]
    public async Task ReadWorkflowRunArtifact_ShouldUseExplicitActorIdWhenRunBindingMatches()
    {
        var harness = new Harness();
        harness.RunBindingReader.BindingsByRunId["run-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "workflow-run-actor",
                "workflow-definition-actor",
                "run-1",
                "demo-dinner-workflow",
                string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Interactive),
        ];
        harness.WorkflowQuery.ReportsByWorkflowRunId["workflow-run-actor"] = new WorkflowRunReport
        {
            RootActorId = "workflow-run-actor",
            CommandId = "run-1",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            StateVersion = 11,
            Success = true,
            FinalOutput = "Completed through explicit actor identity.",
        };
        var tool = await harness.DiscoverToolAsync("aevatar_read_workflow_run_artifact");

        var output = await tool.ExecuteAsync(
            """{"workflow_run_id":"run-1","actor_id":"workflow-run-actor"}""");

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.RunBindingReader.ListByRunIdCalls.Should().Equal("run-1");
        harness.WorkflowQuery.ReportCalls.Should().Equal("run-1", "workflow-run-actor");
        var result = Read(output);
        result.GetProperty("workflow_run_id").GetString().Should().Be("run-1");
        result.GetProperty("artifact_actor_id").GetString().Should().Be("workflow-run-actor");
        result.GetProperty("status").GetString().Should().Be(nameof(WorkflowRunCompletionStatus.Completed));
        result.GetProperty("final_output").GetString().Should().Be("Completed through explicit actor identity.");
    }

    [Fact]
    public async Task ReadWorkflowRunArtifact_ExplicitForeignActorWithoutRunBinding_ShouldNotReadItsReport()
    {
        var harness = new Harness();
        harness.WorkflowQuery.ReportsByWorkflowRunId["foreign-workflow-run-actor"] = new WorkflowRunReport
        {
            RootActorId = "foreign-workflow-run-actor",
            WorkflowName = "foreign-workflow",
            CommandId = "foreign-command",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            StateVersion = 12,
            Success = true,
            FinalOutput = "Foreign committed output.",
        };
        var tool = await harness.DiscoverToolAsync("aevatar_read_workflow_run_artifact");

        var output = await tool.ExecuteAsync(
            """{"workflow_run_id":"run-1","actor_id":"foreign-workflow-run-actor","wait_ms":0}""");

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.RunBindingReader.ListByRunIdCalls.Should().Equal("run-1");
        harness.WorkflowQuery.ReportCalls.Should().Equal("run-1");
        var result = Read(output);
        result.GetProperty("workflow_run_id").GetString().Should().Be("run-1");
        result.GetProperty("artifact_actor_id").GetString().Should().Be("run-1");
        result.GetProperty("status").GetString().Should().Be("pending");
        result.GetProperty("pending").GetBoolean().Should().BeTrue();
        result.TryGetProperty("success", out _).Should().BeFalse();
        result.TryGetProperty("root_actor_id", out _).Should().BeFalse();
        result.TryGetProperty("final_output_sha256", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ReadWorkflowRunArtifact_ExplicitActorId_ShouldNotOverrideCommittedReportActor()
    {
        var harness = new Harness();
        harness.WorkflowQuery.ReportsByWorkflowRunId["run-1"] = new WorkflowRunReport
        {
            RootActorId = "workflow-run-actor",
            WorkflowName = "actor-lineage-workflow",
            CommandId = "command-alpha",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            StateVersion = 12,
            Success = true,
            FinalOutput = "Committed actor identity wins.",
        };
        var tool = await harness.DiscoverToolAsync("aevatar_read_workflow_run_artifact");

        var output = await tool.ExecuteAsync(
            """{"workflow_run_id":"run-1","actor_id":"caller-supplied-actor"}""");

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowQuery.ReportCalls.Should().Equal("run-1");
        var result = Read(output);
        result.GetProperty("artifact_actor_id").GetString().Should().Be("workflow-run-actor");
        result.GetProperty("root_actor_id").GetString().Should().Be("workflow-run-actor");
    }

    [Fact]
    public async Task ReadWorkflowRunArtifact_ShouldWaitForReportMaterializationAndReResolveBinding()
    {
        var harness = new Harness();
        var delayCalls = new List<TimeSpan>();
        var binding = new WorkflowActorBinding(
            WorkflowActorKind.Run,
            "workflow-run-actor",
            "workflow-definition-actor",
            "run-1",
            "demo-dinner-workflow",
            string.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ExternalCapabilityExecutionMode.Interactive);
        harness.WorkflowQuery.ReportsByWorkflowRunId["workflow-run-actor"] = new WorkflowRunReport
        {
            RootActorId = "workflow-run-actor",
            CommandId = "run-1",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            StateVersion = 7,
            Success = true,
            FinalOutput = "Dinner is ready.",
        };
        var tool = new ReadWorkflowRunArtifactTool(
            harness.WorkflowQuery,
            harness.RunBindingReader,
            (delay, _) =>
            {
                delayCalls.Add(delay);
                harness.RunBindingReader.BindingsByRunId["run-1"] = [binding];
                return Task.CompletedTask;
            });

        var output = await tool.ExecuteAsync("""{"workflow_run_id":"run-1","wait_ms":1000}""");

        ErrorCodeOrNull(output).Should().BeNull(output);
        delayCalls.Should().ContainSingle();
        harness.RunBindingReader.ListByRunIdCalls.Should().Equal("run-1", "run-1");
        harness.WorkflowQuery.ReportCalls.Should().Equal("run-1", "run-1", "workflow-run-actor");
        harness.WorkflowQuery.LastCurrentStateActorId.Should().BeNull();
        var result = Read(output);
        result.GetProperty("workflow_run_id").GetString().Should().Be("run-1");
        result.GetProperty("artifact_actor_id").GetString().Should().Be("workflow-run-actor");
        result.GetProperty("status").GetString().Should().Be(nameof(WorkflowRunCompletionStatus.Completed));
        result.GetProperty("final_output").GetString().Should().Be("Dinner is ready.");
    }

    [Fact]
    public async Task ReadWorkflowRunArtifact_WhenReportStillMissing_ShouldReturnPendingWithoutError()
    {
        var harness = new Harness();
        var tool = new ReadWorkflowRunArtifactTool(
            harness.WorkflowQuery,
            harness.RunBindingReader,
            (_, _) => throw new InvalidOperationException("wait_ms=0 should not delay"));

        var output = await tool.ExecuteAsync("""{"workflow_run_id":"run-1","wait_ms":0}""");

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowQuery.ReportCalls.Should().Equal("run-1");
        harness.WorkflowQuery.LastCurrentStateActorId.Should().BeNull();
        var result = Read(output);
        result.GetProperty("workflow_run_id").GetString().Should().Be("run-1");
        result.GetProperty("artifact_actor_id").GetString().Should().Be("run-1");
        result.GetProperty("artifact").GetString().Should().Be("report");
        result.GetProperty("status").GetString().Should().Be("pending");
        result.GetProperty("pending").GetBoolean().Should().BeTrue();
        result.GetProperty("waited_ms").GetInt32().Should().Be(0);
        result.GetProperty("retry_after_ms").GetInt32().Should().Be(1000);
    }

    [Fact]
    public async Task ReadWorkflowRunArtifact_Timeline_ShouldReadTimelineExport()
    {
        var harness = new Harness();
        harness.RunBindingReader.BindingsByRunId["run-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "workflow-run-actor",
                "workflow-definition-actor",
                "run-1",
                "demo-dinner-workflow",
                string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Interactive),
        ];
        harness.WorkflowQuery.Timeline =
        [
            new WorkflowRunTimelineExportItem
            {
                Timestamp = new DateTimeOffset(2026, 6, 13, 3, 36, 58, TimeSpan.Zero),
                Stage = "completed",
                Message = "Workflow completed",
                StepId = "final",
                EventType = "type.googleapis.com/aevatar.workflow.WorkflowCompletedEvent",
            },
        ];
        var tool = await harness.DiscoverToolAsync("aevatar_read_workflow_run_artifact");

        var output = await tool.ExecuteAsync("""{"workflow_run_id":"run-1","view":"timeline","take":25}""");

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowQuery.LastTimelineWorkflowRunId.Should().Be("workflow-run-actor");
        harness.WorkflowQuery.LastTimelineTake.Should().Be(25);
        harness.WorkflowQuery.LastCurrentStateActorId.Should().BeNull();
        var result = Read(output);
        result.GetProperty("artifact").GetString().Should().Be("timeline");
        result.GetProperty("artifact_actor_id").GetString().Should().Be("workflow-run-actor");
        result.GetProperty("events")[0].GetProperty("step_id").GetString().Should().Be("final");
    }

    [Fact]
    public async Task ReadWorkflowRunArtifact_GraphEdges_ShouldReadGraphExport()
    {
        var harness = new Harness();
        harness.WorkflowQuery.GraphEdges =
        [
            new WorkflowRunGraphExportEdge
            {
                EdgeId = "edge-1",
                FromNodeId = "run-1",
                ToNodeId = "role-1",
                EdgeType = "OWNS",
                UpdatedAt = new DateTimeOffset(2026, 6, 13, 3, 36, 58, TimeSpan.Zero),
            },
        ];
        var tool = await harness.DiscoverToolAsync("aevatar_read_workflow_run_artifact");

        var output = await tool.ExecuteAsync("""
            {
              "workflow_run_id": "workflow-run-actor",
              "view": "graph_edges",
              "take": 7,
              "edge_types": ["OWNS"]
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowQuery.LastGraphEdgesWorkflowRunId.Should().Be("workflow-run-actor");
        harness.WorkflowQuery.LastGraphEdgesTake.Should().Be(7);
        harness.WorkflowQuery.LastGraphEdgesOptions!.EdgeTypes.Should().Equal("OWNS");
        var result = Read(output);
        result.GetProperty("artifact").GetString().Should().Be("graph_edges");
        result.GetProperty("count").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ReadWorkflowRunArtifact_GraphSubgraph_ShouldReadGraphExport()
    {
        var harness = new Harness();
        harness.WorkflowQuery.GraphSubgraph = new WorkflowRunGraphExportSubgraph
        {
            RootNodeId = "run-1",
            Nodes =
            {
                new WorkflowRunGraphExportNode
                {
                    NodeId = "run-1",
                    NodeType = "workflow-run",
                    UpdatedAt = new DateTimeOffset(2026, 6, 13, 3, 36, 58, TimeSpan.Zero),
                },
            },
            Edges =
            {
                new WorkflowRunGraphExportEdge
                {
                    EdgeId = "edge-1",
                    FromNodeId = "run-1",
                    ToNodeId = "role-1",
                    EdgeType = "OWNS",
                    UpdatedAt = new DateTimeOffset(2026, 6, 13, 3, 36, 58, TimeSpan.Zero),
                },
            },
        };
        var tool = await harness.DiscoverToolAsync("aevatar_read_workflow_run_artifact");

        var output = await tool.ExecuteAsync("""
            {
              "workflow_run_id": "run-1",
              "view": "graph_subgraph",
              "graph_depth": 3,
              "take": 11,
              "edge_types": ["OWNS"]
            }
            """);

        ErrorCodeOrNull(output).Should().BeNull(output);
        harness.WorkflowQuery.LastGraphSubgraphWorkflowRunId.Should().Be("run-1");
        harness.WorkflowQuery.LastGraphSubgraphDepth.Should().Be(3);
        harness.WorkflowQuery.LastGraphSubgraphTake.Should().Be(11);
        harness.WorkflowQuery.LastGraphSubgraphOptions!.EdgeTypes.Should().Equal("OWNS");
        var result = Read(output);
        result.GetProperty("artifact").GetString().Should().Be("graph_subgraph");
        result.GetProperty("node_count").GetInt32().Should().Be(1);
        result.GetProperty("edge_count").GetInt32().Should().Be(1);
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

    private static async Task<ToolExecutionResult> ExecuteToolThroughExecutorAsync(
        IAgentTool tool,
        string toolCallId,
        string toolName,
        string argumentsJson)
    {
        var tools = new ToolManager();
        tools.Register(tool);
        var executor = new StreamingToolExecutor(
            tools,
            toolExecutionPort: CreateToolExecutionPort());
        using var executionState = executor.CreateExecutionState();
        var prepared = await executor.PrepareBatchAsync(
            $"tool-source-test:{toolCallId}",
            round: 0,
            [new ToolCall
            {
                Id = toolCallId,
                Name = toolName,
                ArgumentsJson = argumentsJson,
            }]);
        executor.AddTool(executionState, prepared.Single());

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        return results.Should().ContainSingle().Subject;
    }

    private static IAgentToolExecutionPort CreateToolExecutionPort() =>
        new AdmittedAgentToolExecutor(
            new StartingAdmissionLedger(),
            new AppendedAuditTrail(),
            new StableAuditIdentityHasher());

    private static JsonElement Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string ErrorCode(string json) =>
        ErrorCodeOrNull(json) ?? throw new InvalidOperationException($"Expected an error result: {json}");

    private static string ErrorMessage(string json)
    {
        var root = Read(json);
        return root.TryGetProperty("error", out var error) &&
               error.TryGetProperty("message", out var message)
            ? message.GetString() ?? string.Empty
            : throw new InvalidOperationException($"Expected an error message: {json}");
    }

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
        command.CallerCredential.Should().NotBeNull();
        command.CallerCredential!.BearerToken.Should().Be("access-token");
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
        values.Should().NotContainKey(LLMRequestMetadataKeys.SenderNyxUserId);
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
        llmControl.RoutePreference.Should().Be("route-1");
    }

    private static Aevatar.AI.Abstractions.ChatFileRef BuildInputFileRef(
        string fileId,
        string artifactId,
        string fileName) =>
        new()
        {
            FileId = fileId,
            ArtifactId = artifactId,
            SourceKind = Aevatar.AI.Abstractions.ChatFileSourceKind.ConnectedServiceResource,
            SourceMessageId = $"om_{fileId}",
            SourceResourceKey = $"resource_{fileId}",
            FileName = fileName,
            MediaType = "application/pdf",
            SizeBytes = 789,
        };

    private static AgentToolContextScope PushContext(
        string callId,
        string requestId = "request-1",
        string? scopeId = "scope-1",
        string? accessToken = "access-token",
        string? senderNyxUserId = null,
        string? senderBindingId = "binding-1",
        string? ownerSubject = "owner-1",
        string? ownerScopeId = null,
        string? channelPlatform = "telegram",
        string? channelRegistrationScopeId = "registration-scope-1",
        AgentWorkflowRuntimeContext? workflowRuntime = null,
        string? durableReplyCredentialRef = "secrets://nyx/default-reply",
        long durableReplyCredentialExpiresAtUnixMs = 0,
        IReadOnlyDictionary<string, string>? externalMetadata = null,
        IReadOnlyList<Aevatar.AI.Abstractions.ChatFileRef>? inputFileRefs = null,
        AgentToolNyxIdCredentialKind nyxIdCredentialKind = AgentToolNyxIdCredentialKind.Unspecified,
        string? organizationAccessToken = "org-token",
        string? senderAccessToken = "sender-token",
        string? sourceReadableAccessToken = null,
        AgentToolNyxIdAuthorityContext? nyxIdAuthority = null,
        AgentToolExecutionOwner? executionOwner = null,
        DurableCallerCredentialRef? durableNyxIdCredential = null,
        AgentChatInvocationContext? chatContext = null) =>
        AgentToolContextScope.Push(new AgentToolExecutionContext(
            new AgentToolRequestIdentity(requestId, callId),
            new AgentToolCredentials(
                accessToken,
                organizationAccessToken,
                senderAccessToken,
                nyxIdCredentialKind,
                sourceReadableAccessToken),
            new AgentToolCallerContext(scopeId, ownerSubject, "response-1", ownerScopeId),
            new AgentToolChannelContext(
                channelPlatform,
                "sender-1",
                channelRegistrationScopeId,
                "message-1",
                "platform-message-1",
                null,
                ToDeliveryCredential(durableReplyCredentialRef, durableReplyCredentialExpiresAtUnixMs),
                "bot-reg-1"),
            new AgentToolSenderBindingContext(senderBindingId, senderNyxUserId),
            new LLMRequestRoutingContext("model-1", "route-1", 4, "memory"),
            new AgentToolConnectedServicesContext("""{"service":"ctx"}"""),
            workflowRuntime ?? AgentWorkflowRuntimeContext.Empty,
            AgentSkillRecoveryContext.Empty,
            BuildExternalMetadata(externalMetadata)) with
        {
            InputFileRefs = inputFileRefs ?? [],
            ExecutionOwner = executionOwner ??
                             AgentToolExecutionOwners.HostService(nameof(AevatarInvocationToolSourceTests)),
            NyxIdAuthority = nyxIdAuthority ?? AgentToolNyxIdAuthorityContext.Empty,
            DurableNyxIdCredential = durableNyxIdCredential,
            Chat = chatContext ?? AgentChatInvocationContext.Empty,
        });

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

    private static ChannelWorkflowResultDeliveryCredential? ToDeliveryCredential(
        string? secretRef,
        long expiresAtUnixMs) =>
        string.IsNullOrWhiteSpace(secretRef)
            ? null
            : new ChannelWorkflowResultDeliveryCredential
            {
                SecretReference = new Aevatar.Foundation.Abstractions.Credentials.SecretReference
                {
                    Ref = secretRef,
                    Purpose = Aevatar.Foundation.Abstractions.Credentials.CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
                    OwnerScopeKey = "registration-scope-1",
                    ExpiresAtUnixMs = expiresAtUnixMs,
                },
                SubjectId = "nyx-api-key-1",
            };

    private static IReadOnlyDictionary<string, string> BuildExternalMetadata(
        IReadOnlyDictionary<string, string>? externalMetadata)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["external"] = "value",
        };
        if (externalMetadata is not null)
        {
            foreach (var pair in externalMetadata)
                metadata[pair.Key] = pair.Value;
        }

        return metadata;
    }

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
            string.Empty,
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
            DateTimeOffset.UtcNow,
            string.Empty,
            string.Empty);

    private sealed class Harness
    {
        public RecordingActorDispatchPort ActorDispatch { get; } = new();
        public RecordingActorRegistryQueryPort ActorRegistry { get; } = new();
        public RecordingTeamEntryMemberResolver TeamResolver { get; } = new();
        public RecordingMemberPublishedServiceResolver MemberResolver { get; } = new();
        public RecordingStaticGAgentInvocationPort TeamInvocation { get; } = new();
        public RecordingWorkflowDispatchService WorkflowDispatch { get; } = new();
        public RecordingServiceInvocationDispatcher ServiceInvocationDispatcher { get; } = new();
        public RecordingWorkflowRunBackgroundDeliveryRegistrationPort WorkflowRunDelivery { get; } = new();
        public RecordingWorkflowChatHistoryTerminalDeliveryPort WorkflowChatHistoryDelivery { get; } = new();
        public RecordingChannelAgentKeyReadinessPort ChannelAgentKeyReadiness { get; } = new();
        public RecordingServiceInvocationResolutionPort ServiceInvocationResolution { get; } = new();
        public RecordingInvokeAdmissionAuthorizer AdmissionAuthorizer { get; } = new();
        public RecordingServiceRunRegistrationPort ServiceRunRegistration { get; } = new();
        public RecordingServiceRunQueryPort ServiceRunQuery { get; } = new();
        public RecordingTerminalQueryPort TerminalQuery { get; } = new();
        public StubWorkflowExecutionQueryService WorkflowQuery { get; } = new();
        public RecordingScopeWorkflowQueryPort ScopeWorkflowQuery { get; } = new();
        public RecordingScopeWorkflowTemplateEnsurePort ScopeWorkflowTemplateEnsure { get; } = new();
        public RecordingWorkflowRunBindingReader RunBindingReader { get; } = new();

        public Harness()
        {
            ScopeWorkflowQuery.Workflows["wf-main"] = new ScopeWorkflowSummary(
                "scope-1",
                "wf-main",
                "Workflow Main",
                "scope-1:aevatar:workflows:wf-main",
                "wf-main",
                "workflow-definition-actor-wf-main",
                "revision-wf-main",
                "deployment-wf-main",
                "Active",
                DateTimeOffset.UtcNow);
            ConfigureServiceTarget(
                ServiceImplementationKind.Static,
                serviceId: "service-1",
                endpointId: "entry",
                primaryActorId: "team-actor");
        }

        public AevatarInvocationDispatcher CreateDispatcher(bool withWorkflowRunDeliveryRegistrationPort = true) =>
            new(
                ActorDispatch,
                ActorRegistry,
                TeamResolver,
                MemberResolver,
                TeamInvocation,
                WorkflowDispatch,
                ServiceInvocationResolution,
                ServiceInvocationDispatcher,
                AdmissionAuthorizer,
                ServiceRunQuery,
                TerminalQuery,
                WorkflowQuery,
                withWorkflowRunDeliveryRegistrationPort ? WorkflowRunDelivery : null,
                scopeWorkflowQueryPort: ScopeWorkflowQuery,
                workflowStartObservationTimeout: TimeSpan.Zero,
                channelAgentKeyReadinessPort: ChannelAgentKeyReadiness,
                scopeWorkflowTemplateEnsurePort: ScopeWorkflowTemplateEnsure,
                workflowChatHistoryTerminalDeliveryPort: WorkflowChatHistoryDelivery);

        public void ConfigureServiceTarget(
            ServiceImplementationKind implementationKind,
            string serviceId,
            string endpointId,
            string primaryActorId)
        {
            var identity = new ServiceIdentity
            {
                TenantId = "scope-1",
                AppId = ScopeServiceIdentityDefaults.ServiceAppId,
                Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
                ServiceId = serviceId,
            };
            var serviceKey = ServiceKeys.Build(identity);
            var revisionId = $"revision-{serviceId}";
            var deploymentId = $"deployment-{serviceId}";
            var endpoint = new ServiceEndpointDescriptor
            {
                EndpointId = endpointId,
                DisplayName = endpointId,
                Kind = ServiceEndpointKind.Chat,
                RequestTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                ResponseTypeUrl = Any.Pack(new AGUIEvent()).TypeUrl,
                Description = "chat endpoint",
            };
            var artifact = new PreparedServiceRevisionArtifact
            {
                Identity = identity.Clone(),
                RevisionId = revisionId,
                ImplementationKind = implementationKind,
                ArtifactHash = $"artifact-{serviceId}",
                DeploymentPlan = new ServiceDeploymentPlan(),
            };
            artifact.Endpoints.Add(endpoint);
            switch (implementationKind)
            {
                case ServiceImplementationKind.Static:
                    artifact.DeploymentPlan.StaticPlan = new StaticServiceDeploymentPlan
                    {
                        PreferredActorId = primaryActorId,
                        AgentKind = "RoleGAgent",
                    };
                    break;
                case ServiceImplementationKind.Scripting:
                    artifact.DeploymentPlan.ScriptingPlan = new ScriptingServiceDeploymentPlan
                    {
                        Revision = revisionId,
                        DefinitionActorId = "script-definition-actor",
                    };
                    break;
                case ServiceImplementationKind.Workflow:
                    artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
                    {
                        WorkflowName = "wf-main",
                        DefinitionActorId = primaryActorId,
                    };
                    break;
            }

            ServiceInvocationResolution.Result = new ServiceInvocationResolvedTarget(
                new ServiceInvocationResolvedService(
                    serviceKey,
                    revisionId,
                    deploymentId,
                    primaryActorId,
                    ServiceServingState.Active.ToString(),
                    []),
                artifact,
                endpoint);
        }

        public void RegisterDependencies(IServiceCollection services)
        {
            services.AddSingleton<IActorDispatchPort>(ActorDispatch);
            services.AddSingleton<IGAgentActorRegistryQueryPort>(ActorRegistry);
            services.AddSingleton<ITeamEntryMemberResolver>(TeamResolver);
            services.AddSingleton<IMemberPublishedServiceResolver>(MemberResolver);
            services.AddSingleton<IStaticGAgentStreamInvocationPort<AGUIEvent>>(TeamInvocation);
            services.AddSingleton<ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>(WorkflowDispatch);
            services.AddSingleton<IServiceInvocationDispatcher>(ServiceInvocationDispatcher);
            services.AddSingleton<IServiceInvocationResolutionPort>(ServiceInvocationResolution);
            services.AddSingleton<IInvokeAdmissionAuthorizer>(AdmissionAuthorizer);
            services.AddSingleton<IServiceRunRegistrationPort>(ServiceRunRegistration);
            services.AddSingleton<IServiceRunQueryPort>(ServiceRunQuery);
            services.AddSingleton<IGAgentRunTerminalQueryPort>(TerminalQuery);
            services.AddSingleton<IWorkflowExecutionQueryApplicationService>(WorkflowQuery);
            services.AddSingleton<IScopeWorkflowQueryPort>(ScopeWorkflowQuery);
            services.AddSingleton<IScopeWorkflowTemplateEnsurePort>(ScopeWorkflowTemplateEnsure);
            services.AddSingleton<IWorkflowRunBindingReader>(RunBindingReader);
            services.AddSingleton<IWorkflowRunBackgroundDeliveryRegistrationPort>(WorkflowRunDelivery);
        }

        public async Task<IAgentTool> DiscoverToolAsync(string toolName)
        {
            IAgentToolSource source = toolName switch
            {
                "aevatar_invoke_gagent" => new InvokeGAgentToolSource(CreateDispatcher()),
                "aevatar_invoke_team" => new InvokeTeamToolSource(CreateDispatcher()),
                "aevatar_invoke_member" => new InvokeMemberToolSource(CreateDispatcher()),
                "aevatar_start_workflow" => new StartWorkflowToolSource(CreateDispatcher()),
                "aevatar_observe_run" => new ObserveRunToolSource(CreateDispatcher()),
                "aevatar_read_workflow_run_artifact" => new ReadWorkflowRunArtifactToolSource(WorkflowQuery, RunBindingReader),
                _ => throw new ArgumentOutOfRangeException(nameof(toolName), toolName, null),
            };
            var tools = await source.DiscoverToolsAsync();
            return tools.Single(tool => tool.Name == toolName);
        }
    }

    private sealed class RecordingChannelAgentKeyReadinessPort : IChannelNyxIdAgentKeyReadinessPort
    {
        public List<DurableCallerCredentialRef> Credentials { get; } = [];

        public ChannelNyxIdAgentKeyReadinessResult Result { get; set; } =
            ChannelNyxIdAgentKeyReadinessResult.Succeeded;

        public Task<ChannelNyxIdAgentKeyReadinessResult> EnsureReadyAsync(
            DurableCallerCredentialRef credential,
            CancellationToken ct = default)
        {
            Credentials.Add(credential.Clone());
            return Task.FromResult(Result);
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

    private sealed class RecordingMemberPublishedServiceResolver : IMemberPublishedServiceResolver
    {
        public string? LastScopeId { get; private set; }
        public string? LastMemberId { get; private set; }
        public MemberPublishedServiceResolution Resolution { get; set; } = new("scope-1", "member-1", "service-1");
        public Exception? Failure { get; set; }

        public Task<MemberPublishedServiceResolution> ResolveAsync(
            MemberPublishedServiceResolveRequest request,
            CancellationToken ct = default)
        {
            LastScopeId = request.ScopeId;
            LastMemberId = request.MemberId;
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

    private sealed class RecordingScopeWorkflowQueryPort : IScopeWorkflowQueryPort
    {
        public Dictionary<string, ScopeWorkflowSummary> Workflows { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ScopeWorkflowLookupResult> LookupResults { get; } = new(StringComparer.Ordinal);
        public List<(string ScopeId, string WorkflowId)> Lookups { get; } = [];
        public Exception? Failure { get; set; }

        public Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(
            string scopeId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScopeWorkflowSummary>>(Workflows.Values
                .Where(workflow => string.Equals(workflow.ScopeId, scopeId, StringComparison.Ordinal))
                .ToArray());

        public Task<ScopeWorkflowLookupResult> LookupByWorkflowIdAsync(
            string scopeId,
            string workflowId,
            CancellationToken ct = default)
        {
            Lookups.Add((scopeId, workflowId));
            if (Failure is not null)
                throw Failure;

            if (LookupResults.TryGetValue(workflowId, out var lookupResult))
                return Task.FromResult(lookupResult);

            if (Workflows.TryGetValue(workflowId, out var workflow) &&
                string.Equals(workflow.ScopeId, scopeId, StringComparison.Ordinal))
            {
                return Task.FromResult(new ScopeWorkflowLookupResult(
                    ScopeWorkflowLookupStatus.Runnable,
                    workflow,
                    "runnable"));
            }

            if (string.Equals(workflowId, "wf-main", StringComparison.Ordinal))
            {
                return Task.FromResult(new ScopeWorkflowLookupResult(
                    ScopeWorkflowLookupStatus.Runnable,
                    BuildWorkflow(scopeId, workflowId),
                    "runnable"));
            }

            return Task.FromResult(new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.NotFound,
                null,
                "service_catalog_missing"));
        }

        public Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(
            string scopeId,
            string workflowId,
            CancellationToken ct = default) =>
            Task.FromResult(Workflows.TryGetValue(workflowId, out var workflow) &&
                            string.Equals(workflow.ScopeId, scopeId, StringComparison.Ordinal)
                ? workflow
                : null);

        public Task<ScopeWorkflowSummary?> GetByActorIdAsync(
            string scopeId,
            string actorId,
            CancellationToken ct = default) =>
            Task.FromResult<ScopeWorkflowSummary?>(Workflows.Values.FirstOrDefault(workflow =>
                string.Equals(workflow.ScopeId, scopeId, StringComparison.Ordinal) &&
                string.Equals(workflow.ActorId, actorId, StringComparison.Ordinal)));

        private static ScopeWorkflowSummary BuildWorkflow(string scopeId, string workflowId) =>
            new(
                scopeId,
                workflowId,
                "Workflow Main",
                $"{scopeId}:aevatar:workflows:{workflowId}",
                workflowId,
                $"workflow-definition-actor-{scopeId}-{workflowId}",
                $"revision-{workflowId}",
                $"deployment-{workflowId}",
                "Active",
                DateTimeOffset.UtcNow);
    }

    private sealed class RecordingScopeWorkflowTemplateEnsurePort : IScopeWorkflowTemplateEnsurePort
    {
        public List<ScopeWorkflowTemplateEnsureRequest> Requests { get; } = [];

        public ScopeWorkflowTemplateEnsureResult Result { get; set; } =
            ScopeWorkflowTemplateEnsureResult.NotConfigured("scope-1", "wf-main");

        public Exception? Failure { get; set; }

        public Task<ScopeWorkflowTemplateEnsureResult> EnsureAsync(
            ScopeWorkflowTemplateEnsureRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Failure is not null)
                throw Failure;

            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingWorkflowDispatchService
        : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public WorkflowChatRunRequest? Command { get; private set; }
        public Action<WorkflowChatRunRequest>? OnDispatch { get; set; }
        public Exception? Failure { get; set; }
        public bool HonorCommandSeeds { get; set; } = true;

        public CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> Result { get; set; } =
            CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
                .Success(new WorkflowChatRunAcceptedReceipt("workflow-actor", "workflow", "workflow-command", "workflow-correlation"));

        public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            Command = command;
            OnDispatch?.Invoke(command);
            if (Failure != null)
                throw Failure;

            if (HonorCommandSeeds && Result.Succeeded && Result.Receipt != null &&
                !string.IsNullOrWhiteSpace(command.CommandIdSeed))
            {
                var accepted = Result.Receipt;
                return Task.FromResult(CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
                    .Success(new WorkflowChatRunAcceptedReceipt(
                        accepted.ActorId,
                        accepted.WorkflowName,
                        command.CommandIdSeed,
                        command.CorrelationIdSeed ?? command.CommandIdSeed),
                        Result.Admission));
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingServiceInvocationDispatcher : IServiceInvocationDispatcher
    {
        public List<(ServiceInvocationResolvedTarget Target, ServiceInvocationRequest Request)> Calls { get; } = [];
        public Action<ServiceInvocationRequest>? OnDispatch { get; set; }
        public Exception? Failure { get; set; }
        public bool HonorRequestIdentitySeeds { get; set; } = true;

        public ServiceInvocationAcceptedReceipt Receipt { get; set; } = new()
        {
            RequestId = "service-command",
            ServiceKey = "tenant:app:default:service-1",
            DeploymentId = "deployment-service-1",
            TargetActorId = "service-target-actor",
            EndpointId = "chat",
            CommandId = "service-command",
            CorrelationId = "service-correlation",
            RunId = "service-run",
        };

        public Task<ServiceInvocationAcceptedReceipt> DispatchAsync(
            ServiceInvocationResolvedTarget target,
            ServiceInvocationRequest request,
            CancellationToken ct = default)
        {
            Calls.Add((CloneTarget(target), request.Clone()));
            OnDispatch?.Invoke(request);
            if (Failure != null)
                throw Failure;

            var receipt = Receipt.Clone();
            if (HonorRequestIdentitySeeds && !string.IsNullOrWhiteSpace(request.CommandId))
            {
                receipt.RequestId = request.CommandId;
                receipt.CommandId = request.CommandId;
                receipt.CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                    ? request.CommandId
                    : request.CorrelationId;
            }

            return Task.FromResult(receipt);
        }

        private static ServiceInvocationResolvedTarget CloneTarget(ServiceInvocationResolvedTarget target) =>
            new(
                target.Service,
                target.Artifact.Clone(),
                target.Endpoint.Clone());
    }

    private sealed class RecordingWorkflowChatHistoryTerminalDeliveryPort : IWorkflowChatHistoryTerminalDeliveryPort
    {
        public List<WorkflowChatHistoryTerminalDeliveryReservationRequest> Reservations { get; } = [];
        public List<(WorkflowChatHistoryTerminalDeliveryReservation Reservation, WorkflowChatRunAcceptedReceipt Receipt)> Bindings { get; } = [];
        public List<(WorkflowChatHistoryTerminalDeliveryReservation Reservation, string Reason)> Abandons { get; } = [];

        public Task<WorkflowChatHistoryTerminalDeliveryReservationResult> ReserveAsync(
            WorkflowChatHistoryTerminalDeliveryReservationRequest request,
            CancellationToken ct = default)
        {
            Reservations.Add(request);
            var reservation = new WorkflowChatHistoryTerminalDeliveryReservation(
                "chat-history-delivery-actor",
                request.DeliveryId,
                request.WorkflowActorId,
                request.WorkflowCommandId);
            var chatContext = new WorkflowChatContext(
                request.ScopeId,
                request.Conversation.ConversationId ?? "generated-conversation",
                "generated-turn",
                1);
            return Task.FromResult(WorkflowChatHistoryTerminalDeliveryReservationResult.Success(reservation, chatContext));
        }

        public Task BindAcceptedAsync(
            WorkflowChatHistoryTerminalDeliveryReservation reservation,
            WorkflowChatRunAcceptedReceipt receipt,
            CancellationToken ct = default)
        {
            Bindings.Add((reservation, receipt));
            return Task.CompletedTask;
        }

        public Task AbandonAsync(
            WorkflowChatHistoryTerminalDeliveryReservation reservation,
            string reason,
            CancellationToken ct = default)
        {
            Abandons.Add((reservation, reason));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWorkflowRunBackgroundDeliveryRegistrationPort
        : IWorkflowRunBackgroundDeliveryRegistrationPort
    {
        public List<WorkflowRunBackgroundDeliveryReservation> Reservations { get; } = [];
        public List<WorkflowRunBackgroundDeliveryRegistration> Registrations { get; } = [];
        public List<(WorkflowRunBackgroundDeliveryReservationReceipt Receipt, string Reason)> Abandonments { get; } = [];
        public Exception? ReserveFailure { get; set; }
        public Exception? Failure { get; set; }
        public Exception? AbandonFailure { get; set; }
        public string DeliveryActorId { get; set; } = "reserved-delivery-actor";

        public Task<WorkflowRunBackgroundDeliveryReservationReceipt> ReserveAsync(
            WorkflowRunBackgroundDeliveryReservation reservation,
            CancellationToken ct = default)
        {
            Reservations.Add(reservation);
            if (ReserveFailure != null)
                throw ReserveFailure;

            return Task.FromResult(new WorkflowRunBackgroundDeliveryReservationReceipt(
                DeliveryActorId,
                reservation.DeliveryId,
                reservation.ExpectedWorkflowCommandId));
        }

        public Task<WorkflowRunBackgroundDeliveryReceipt> RegisterAsync(
            WorkflowRunBackgroundDeliveryReservationReceipt reservationReceipt,
            WorkflowRunBackgroundDeliveryRegistration registration,
            CancellationToken ct = default)
        {
            Registrations.Add(registration);
            if (Failure != null)
                throw Failure;

            return Task.FromResult(new WorkflowRunBackgroundDeliveryReceipt
            {
                DeliveryActorId = reservationReceipt.DeliveryActorId,
                WorkflowActorId = registration.WorkflowActorId,
                WorkflowRunId = registration.WorkflowRunId,
                WorkflowCommandId = registration.WorkflowCommandId,
                WorkflowCorrelationId = registration.WorkflowCorrelationId,
                StreamTopic = registration.StreamTopic,
                ChannelPlatform = registration.ChannelPlatform,
                ReplyMessageId = registration.ReplyMessageId,
                PlatformMessageId = registration.PlatformMessageId,
                RegistrationScopeId = registration.RegistrationScopeId,
            });
        }

        public Task AbandonAsync(
            WorkflowRunBackgroundDeliveryReservationReceipt reservationReceipt,
            string reason,
            CancellationToken ct = default)
        {
            Abandonments.Add((reservationReceipt, reason));
            if (AbandonFailure != null)
                throw AbandonFailure;

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingServiceInvocationResolutionPort : IServiceInvocationResolutionPort
    {
        public ServiceIdentity? LastIdentity { get; private set; }
        public ServiceInvocationRequest? LastRequest { get; private set; }
        public ServiceInvocationResolvedTarget? Result { get; set; }
        public Exception? Failure { get; set; }

        public Task<bool> HasServiceAsync(
            ServiceIdentity identity,
            CancellationToken ct = default)
        {
            LastIdentity = identity.Clone();
            return Task.FromResult(Result != null);
        }

        public Task<ServiceInvocationResolvedTarget> ResolveAsync(
            ServiceInvocationRequest request,
            CancellationToken ct = default)
        {
            if (Failure != null)
                throw Failure;

            LastRequest = request.Clone();
            LastIdentity = request.Identity?.Clone();
            return Task.FromResult(Result ?? throw new InvalidOperationException("No service invocation target configured."));
        }
    }

    private sealed class RecordingInvokeAdmissionAuthorizer : IInvokeAdmissionAuthorizer
    {
        public List<AdmissionCall> Calls { get; } = [];
        public Exception? Failure { get; set; }

        public Task AuthorizeAsync(
            string serviceKey,
            string deploymentId,
            PreparedServiceRevisionArtifact artifact,
            ServiceEndpointDescriptor endpoint,
            ServiceInvocationRequest request,
            CancellationToken ct = default)
        {
            if (Failure != null)
                throw Failure;

            Calls.Add(new AdmissionCall(
                serviceKey,
                deploymentId,
                artifact.Clone(),
                endpoint.Clone(),
                request.Clone()));
            return Task.CompletedTask;
        }
    }

    private sealed record AdmissionCall(
        string ServiceKey,
        string DeploymentId,
        PreparedServiceRevisionArtifact Artifact,
        ServiceEndpointDescriptor Endpoint,
        ServiceInvocationRequest Request);

    private sealed class RecordingServiceRunRegistrationPort : IServiceRunRegistrationPort
    {
        public List<ServiceRunRecord> Records { get; } = [];
        public List<(string RunActorId, string RunId, ServiceRunStatus Status)> StatusUpdates { get; } = [];
        public Exception? Failure { get; set; }

        public Task<ServiceRunRegistrationResult> RegisterAsync(
            ServiceRunRecord record,
            CancellationToken ct = default)
        {
            if (Failure != null)
                throw Failure;

            Records.Add(record.Clone());
            return Task.FromResult(new ServiceRunRegistrationResult(record.TargetActorId, record.RunId));
        }

        public Task UpdateStatusAsync(
            string runActorId,
            string runId,
            ServiceRunStatus status,
            CancellationToken ct = default)
        {
            StatusUpdates.Add((runActorId, runId, status));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingServiceRunQueryPort : IServiceRunQueryPort
    {
        public ServiceRunQuery? LastQuery { get; private set; }
        public string? LastScopeId { get; private set; }
        public string? LastServiceId { get; private set; }
        public string? LastRunId { get; private set; }
        public string? LastCommandId { get; private set; }
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
            CancellationToken ct = default)
        {
            LastScopeId = scopeId;
            LastServiceId = serviceId;
            LastCommandId = commandId;
            return Task.FromResult(ByCommandId);
        }
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

    private sealed class RecordingWorkflowRunBindingReader : IWorkflowRunBindingReader
    {
        public Dictionary<string, IReadOnlyList<WorkflowActorBinding>> BindingsByRunId { get; } =
            new(StringComparer.Ordinal);

        public string? LastRunId { get; private set; }
        public int? LastTake { get; private set; }
        public WorkflowRunBindingQuery? LastQuery { get; private set; }
        public List<string> ListByRunIdCalls { get; } = [];

        public Task<IReadOnlyList<WorkflowActorBinding>> ListByRunIdAsync(
            string runId,
            int take = 20,
            CancellationToken ct = default)
        {
            LastRunId = runId;
            LastTake = take;
            ListByRunIdCalls.Add(runId);
            BindingsByRunId.TryGetValue(runId, out var bindings);
            return Task.FromResult(bindings ?? []);
        }

        public Task<IReadOnlyList<WorkflowActorBinding>> QueryAsync(
            WorkflowRunBindingQuery query,
            CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<WorkflowActorBinding>>([]);
        }
    }

    private sealed class StubWorkflowExecutionQueryService : IWorkflowExecutionQueryApplicationService
    {
        public bool WorkflowActorCurrentStateQueryEnabled => true;
        public WorkflowActorSnapshot? Snapshot { get; set; }
        public WorkflowRunReport? Report { get; set; }
        public Dictionary<string, WorkflowRunReport> ReportsByWorkflowRunId { get; } = new(StringComparer.Ordinal);
        public List<string> ReportCalls { get; } = [];
        public IReadOnlyList<WorkflowRunTimelineExportItem> Timeline { get; set; } = [];
        public IReadOnlyList<WorkflowRunGraphExportEdge> GraphEdges { get; set; } = [];
        public WorkflowRunGraphExportSubgraph GraphSubgraph { get; set; } = new();
        public string? LastCurrentStateActorId { get; private set; }
        public string? LastReportWorkflowRunId { get; private set; }
        public string? LastTimelineWorkflowRunId { get; private set; }
        public int? LastTimelineTake { get; private set; }
        public string? LastGraphEdgesWorkflowRunId { get; private set; }
        public int? LastGraphEdgesTake { get; private set; }
        public WorkflowRunGraphExportQueryOptions? LastGraphEdgesOptions { get; private set; }
        public string? LastGraphSubgraphWorkflowRunId { get; private set; }
        public int? LastGraphSubgraphDepth { get; private set; }
        public int? LastGraphSubgraphTake { get; private set; }
        public WorkflowRunGraphExportQueryOptions? LastGraphSubgraphOptions { get; private set; }

        public Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowAgentSummary>>([]);

        public IReadOnlyList<string> ListWorkflows() => [];

        public Task<IReadOnlyList<WorkflowCatalogItem>> ListWorkflowCatalogAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowCatalogItem>>([]);

        public Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(string workflowName, CancellationToken ct = default) =>
            Task.FromResult<WorkflowCatalogItemDetail?>(null);

        public Task<WorkflowCapabilitiesDocument> GetCapabilitiesAsync(CancellationToken ct = default) =>
            Task.FromResult(new WorkflowCapabilitiesDocument());

        public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(string actorId, CancellationToken ct = default)
        {
            LastCurrentStateActorId = actorId;
            return Task.FromResult(Snapshot);
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
            WorkflowActorCurrentStateListQuery query,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>([]);

        public Task<WorkflowRunReport?> GetWorkflowRunReportArtifactAsync(string actorId, CancellationToken ct = default)
        {
            LastReportWorkflowRunId = actorId;
            ReportCalls.Add(actorId);
            if (ReportsByWorkflowRunId.TryGetValue(actorId, out var report))
                return Task.FromResult<WorkflowRunReport?>(report);

            return Task.FromResult(Report);
        }

        public Task<IReadOnlyList<WorkflowRunTimelineExportItem>> ListWorkflowRunTimelineExportAsync(
            string actorId,
            int take = 200,
            CancellationToken ct = default)
        {
            LastTimelineWorkflowRunId = actorId;
            LastTimelineTake = take;
            return Task.FromResult(Timeline);
        }

        public Task<IReadOnlyList<WorkflowRunGraphExportEdge>> ListWorkflowRunGraphExportEdgesAsync(
            string actorId,
            int take = 200,
            WorkflowRunGraphExportQueryOptions? options = null,
            CancellationToken ct = default)
        {
            LastGraphEdgesWorkflowRunId = actorId;
            LastGraphEdgesTake = take;
            LastGraphEdgesOptions = options;
            return Task.FromResult(GraphEdges);
        }

        public Task<WorkflowRunGraphExportSubgraph> GetWorkflowRunGraphExportSubgraphAsync(
            string actorId,
            int depth = 2,
            int take = 200,
            WorkflowRunGraphExportQueryOptions? options = null,
            CancellationToken ct = default)
        {
            LastGraphSubgraphWorkflowRunId = actorId;
            LastGraphSubgraphDepth = depth;
            LastGraphSubgraphTake = take;
            LastGraphSubgraphOptions = options;
            return Task.FromResult(GraphSubgraph);
        }
    }
}
