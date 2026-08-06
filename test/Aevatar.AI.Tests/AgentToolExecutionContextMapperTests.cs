using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.Tests;

public sealed class AgentToolExecutionContextMapperTests
{
    [Fact]
    public void FromRequest_WhenTypedFieldsAndLegacyMetadataOverlap_ShouldUseOnlyTypedControlAndScrubMetadata()
    {
        var request = new LLMRequest
        {
            Messages = [],
            RequestId = "typed-request",
            CallerContext = new LLMRequestCallerContext(
                ScopeId: "typed-scope",
                OwnerSubject: "typed-owner",
                ResponseId: "typed-response",
                Credentials: new LLMRequestCallerCredentials("typed-access")),
            RoutingContext = new LLMRequestRoutingContext(
                ModelOverride: "typed-model",
                NyxIdRoutePreference: "typed-route",
                MaxToolRoundsOverride: 9,
                UserMemoryPrompt: "typed-memory"),
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LLMRequestMetadataKeys.RequestId] = "legacy-request",
                [LLMRequestMetadataKeys.CallId] = "legacy-call",
                [LLMRequestMetadataKeys.ScopeId] = "legacy-scope",
                [LLMRequestMetadataKeys.OwnerSubject] = "legacy-owner",
                [LLMRequestMetadataKeys.ResponseId] = "legacy-response",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "legacy-access",
                [LLMRequestMetadataKeys.NyxIdOrgToken] = "legacy-org",
                [LLMRequestMetadataKeys.ModelOverride] = "legacy-model",
                [LLMRequestMetadataKeys.NyxIdRoutePreference] = "legacy-route",
                [LLMRequestMetadataKeys.MaxToolRoundsOverride] = "4",
                [LLMRequestMetadataKeys.UserMemoryPrompt] = "legacy-memory",
                ["workflow.parent_actor_id"] = "forged-parent",
                ["workflow.root_run_id"] = "forged-root",
                ["external-trace"] = "trace-1",
            },
        };

        var context = AgentToolExecutionContextMapper.FromRequest(request);

        context.Request.RequestId.Should().Be("typed-request");
        context.Request.CallId.Should().BeNull();
        context.Caller.ScopeId.Should().Be("typed-scope");
        context.Caller.OwnerSubject.Should().Be("typed-owner");
        context.Caller.ResponseId.Should().Be("typed-response");
        context.Credentials.NyxIdAccessToken.Should().Be("typed-access");
        context.Credentials.NyxIdOrgToken.Should().BeNull();
        context.Routing.ModelOverride.Should().Be("typed-model");
        context.Routing.NyxIdRoutePreference.Should().Be("typed-route");
        context.Routing.MaxToolRoundsOverride.Should().Be(9);
        context.Routing.UserMemoryPrompt.Should().Be("typed-memory");
        context.WorkflowRuntime.Should().Be(AgentWorkflowRuntimeContext.Empty);
        context.ExternalMetadata.Should().ContainSingle();
        context.ExternalMetadata["external-trace"].Should().Be("trace-1");
    }

    [Fact]
    public void FromRequest_WhenTypedContextExists_ShouldMergeExternalMetadataWithoutClobberingExplicitContextMetadata()
    {
        var request = new LLMRequest
        {
            Messages = [],
            RequestId = "request-1",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["channel.lark.operator_user_id"] = "lark-user-from-receive-flow",
                ["channel.lark.operator_open_id"] = "ou_operator_1",
                ["explicit"] = "from-request",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "metadata-token",
            },
            ToolContext = AgentToolExecutionContext.Empty with
            {
                Credentials = new AgentToolCredentials("typed-token", null, null),
                ExternalMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["explicit"] = "from-tool-context",
                    ["tool-only"] = "kept",
                },
            },
        };

        var context = AgentToolExecutionContextMapper.FromRequest(request);

        context.Credentials.NyxIdAccessToken.Should().Be("typed-token");
        context.ExternalMetadata["channel.lark.operator_user_id"].Should().Be("lark-user-from-receive-flow");
        context.ExternalMetadata["channel.lark.operator_open_id"].Should().Be("ou_operator_1");
        context.ExternalMetadata["explicit"].Should().Be("from-tool-context");
        context.ExternalMetadata["tool-only"].Should().Be("kept");
        context.ExternalMetadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
    }

    [Fact]
    public void FromRequest_WhenOnlyMetadataContainsOwnedControlKeys_ShouldNotPromoteThemToControlContext()
    {
        var request = new LLMRequest
        {
            Messages = [],
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LLMRequestMetadataKeys.RequestId] = "legacy-request",
                [LLMRequestMetadataKeys.CallId] = "legacy-call",
                [LLMRequestMetadataKeys.ScopeId] = "legacy-scope",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "legacy-access",
                [LLMRequestMetadataKeys.NyxIdOrgToken] = "legacy-org",
                [LLMRequestMetadataKeys.ModelOverride] = "legacy-model",
                [LLMRequestMetadataKeys.NyxIdRoutePreference] = "legacy-route",
                [LLMRequestMetadataKeys.MaxToolRoundsOverride] = "4",
                ["external-trace"] = "trace-1",
            },
        };

        var context = AgentToolExecutionContextMapper.FromRequest(request);

        context.Request.RequestId.Should().BeNull();
        context.Request.CallId.Should().BeNull();
        context.Caller.ScopeId.Should().BeNull();
        context.Credentials.NyxIdAccessToken.Should().BeNull();
        context.Credentials.NyxIdOrgToken.Should().BeNull();
        context.Routing.ModelOverride.Should().BeNull();
        context.Routing.NyxIdRoutePreference.Should().BeNull();
        context.Routing.MaxToolRoundsOverride.Should().BeNull();
        context.ExternalMetadata.Should().ContainSingle();
        context.ExternalMetadata["external-trace"].Should().Be("trace-1");
    }

    [Fact]
    public void FromRequest_WhenToolContextIsProvided_ShouldReturnTypedContextAndIgnoreMetadataFallback()
    {
        var typedContext = AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("typed-request", "typed-call"),
            Credentials = new AgentToolCredentials("typed-token", null, null),
            ExternalMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["typed-note"] = "kept",
            },
        };
        var request = new LLMRequest
        {
            Messages = [],
            ToolContext = typedContext,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LLMRequestMetadataKeys.RequestId] = "metadata-request",
                [LLMRequestMetadataKeys.CallId] = "metadata-call",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "metadata-token",
                ["external-trace"] = "trace-1",
            },
        };

        var context = AgentToolExecutionContextMapper.FromRequest(request);

        context.Should().NotBeSameAs(typedContext);
        context.Request.RequestId.Should().Be("typed-request");
        context.Request.CallId.Should().Be("typed-call");
        context.Credentials.NyxIdAccessToken.Should().Be("typed-token");
        context.ExternalMetadata["typed-note"].Should().Be("kept");
        context.ExternalMetadata["external-trace"].Should().Be("trace-1");
        context.ExternalMetadata.Should().NotContainKey(LLMRequestMetadataKeys.RequestId);
        context.ExternalMetadata.Should().NotContainKey(LLMRequestMetadataKeys.CallId);
        context.ExternalMetadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
    }

    [Fact]
    public void ToPayloadAndFromPayload_ShouldPreserveRestrictedToolVisibility()
    {
        var context = AgentToolExecutionContext.Empty with
        {
            ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames(["search"]),
        };

        var payload = AgentToolExecutionContextMapper.ToPayload(context);
        var mapped = AgentToolExecutionContextMapper.FromPayload(payload);

        payload.ToolVisibility.Should().NotBeNull();
        payload.ToolVisibility.AllowedToolNames.Should().Equal("search");
        mapped.ToolVisibility.IsRestricted.Should().BeTrue();
        mapped.ToolVisibility.Allows("search").Should().BeTrue();
        mapped.ToolVisibility.Allows("calendar").Should().BeFalse();
    }

    [Fact]
    public void ToPayloadAndFromPayload_ShouldPreserveTypedNyxIdAuthority()
    {
        var context = AgentToolExecutionContext.Empty with
        {
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "nyxid",
                "tenant-alpha",
                "user-alpha"),
        };

        var payload = AgentToolExecutionContextMapper.ToPayload(context);
        var restored = AgentToolExecutionContextMapper.FromPayload(payload);

        payload.NyxIdAuthority.Platform.Should().Be("nyxid");
        payload.NyxIdAuthority.ExternalUserId.Should().Be("user-alpha");
        restored.NyxIdAuthority.Should().BeEquivalentTo(context.NyxIdAuthority);
    }

    [Fact]
    public void ToPayloadAndFromPayload_ShouldPreserveRequestIssuedTime()
    {
        const long issuedAtUnixMs = 1_785_484_800_000;
        var context = AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(
                "request-1",
                "call-1",
                "idempotency-1",
                issuedAtUnixMs),
        };

        var payload = context.ToPayload();
        var restored = AgentToolExecutionContextMapper.FromPayload(
            AgentToolExecutionContextPayload.Parser.ParseFrom(payload.ToByteArray()));

        payload.Request.IssuedAtUnixMs.Should().Be(issuedAtUnixMs);
        restored.Request.IssuedAtUnixMs.Should().Be(issuedAtUnixMs);
    }

    [Fact]
    public void ToPayload_WhenToolVisibilityUnrestricted_ShouldOmitVisibilityPayload()
    {
        var payload = AgentToolExecutionContextMapper.ToPayload(AgentToolExecutionContext.Empty);

        payload.ToolVisibility.Should().BeNull();
        AgentToolExecutionContextMapper.FromPayload(payload).ToolVisibility.IsRestricted.Should().BeFalse();
    }

    [Fact]
    public void FromMetadata_ShouldIgnoreOwnedControlKeysAndKeepExternalMetadata()
    {
        var context = AgentToolExecutionContextMapper.FromMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.RequestId] = "legacy-request",
            [LLMRequestMetadataKeys.CallId] = "legacy-call",
            [LLMRequestMetadataKeys.ScopeId] = "legacy-scope",
            ["scope_id"] = "legacy-scope-alias",
            [LLMRequestMetadataKeys.OwnerSubject] = "legacy-owner",
            [LLMRequestMetadataKeys.ResponseId] = "legacy-response",
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "legacy-access",
            [LLMRequestMetadataKeys.NyxIdOrgToken] = "legacy-org",
            [LLMRequestMetadataKeys.SenderNyxIdAccessToken] = "legacy-sender-access",
                [LLMRequestMetadataKeys.SenderBindingId] = "legacy-binding",
                [LLMRequestMetadataKeys.ModelOverride] = "legacy-model",
                [LLMRequestMetadataKeys.NyxIdRoutePreference] = "legacy-route",
                [LLMRequestMetadataKeys.MaxToolRoundsOverride] = "7",
                [LLMRequestMetadataKeys.UserMemoryPrompt] = "legacy-memory",
                [LLMRequestMetadataKeys.ConnectedServicesContext] = """{"services":[]}""",
                ["channel.platform"] = "canonical-lark",
                ["platform"] = "lark",
                ["channel.sender_id"] = "ou-canonical",
                ["sender_id"] = "ou-legacy",
                ["registration_scope_id"] = "scope-legacy",
                ["delivery_target_id"] = "agent-forged",
                ["channel.delivery_target_id"] = "agent-forged-canonical",
                ["channel.durable_reply_credential_ref"] = "secrets://nyx/forged",
                ["channel.message_id"] = "msg-canonical",
                ["message_id"] = "msg-legacy",
                ["channel.platform_message_id"] = "platform-msg-canonical",
                ["platform_message_id"] = "platform-msg-legacy",
            ["lark.open_id"] = "ou-lark",
            ["lark.message_id"] = "msg-lark",
            ["telegram.chat_id"] = "10001",
            ["workflow.parent_actor_id"] = "forged-parent",
            ["workflow.parent_run_id"] = "forged-run",
            ["workflow.parent_step_id"] = "forged-step",
            ["workflow.root_run_id"] = "forged-root",
            ["workflow.depth"] = "99",
            ["workflow_call.parent_actor_id"] = "forged-parent-2",
            ["aevatar.workflow.root_run_id"] = "forged-root-2",
            ["trace-id"] = "trace-1",
        });

        context.Request.RequestId.Should().BeNull();
        context.Request.CallId.Should().BeNull();
        context.Caller.ScopeId.Should().BeNull();
        context.Caller.OwnerSubject.Should().BeNull();
        context.Caller.ResponseId.Should().BeNull();
        context.Credentials.NyxIdAccessToken.Should().BeNull();
        context.Credentials.NyxIdOrgToken.Should().BeNull();
        context.Credentials.SenderNyxIdAccessToken.Should().BeNull();
        context.Channel.Platform.Should().BeNull();
        context.Channel.SenderId.Should().BeNull();
        context.Channel.RegistrationScopeId.Should().BeNull();
        context.Channel.DeliveryTargetId.Should().BeNull();
        context.Channel.MessageId.Should().BeNull();
        context.Channel.PlatformMessageId.Should().BeNull();
        context.SenderBinding.BindingId.Should().BeNull();
        context.Routing.ModelOverride.Should().BeNull();
        context.Routing.NyxIdRoutePreference.Should().BeNull();
        context.Routing.MaxToolRoundsOverride.Should().BeNull();
        context.Routing.UserMemoryPrompt.Should().BeNull();
        context.ConnectedServices.ContextJson.Should().BeNull();
        context.WorkflowRuntime.Should().Be(AgentWorkflowRuntimeContext.Empty);
        context.ExternalMetadata.Should().ContainSingle();
        context.ExternalMetadata["trace-id"].Should().Be("trace-1");
    }

    [Fact]
    public void FromMetadata_WhenOnlyExternalMetadata_ShouldPreserveExternalAnnotations()
    {
        var context = AgentToolExecutionContextMapper.FromMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["trace-id"] = "trace-1",
            ["x-client-note"] = "external-note",
        });

        context.Should().BeEquivalentTo(AgentToolExecutionContext.Empty with
        {
            ExternalMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["trace-id"] = "trace-1",
                ["x-client-note"] = "external-note",
            },
        });
    }

    [Fact]
    public void PayloadRoundTrip_ShouldPreserveTypedContextAndStripOwnedControlKeys()
    {
        var context = new AgentToolExecutionContext(
            new AgentToolRequestIdentity(" request-1 ", " call-1 "),
            new AgentToolCredentials(
                " access-1 ",
                " org-1 ",
                " sender-access-1 ",
                AgentToolNyxIdCredentialKind.ProxyDelegation),
            new AgentToolCallerContext(" scope-1 ", " owner-1 ", " response-1 "),
            new AgentToolChannelContext(
                " telegram ",
                " sender-1 ",
                " registration-1 ",
                " message-1 ",
                " platform-message-1 ",
                " delivery-target-1 ",
                ChannelWorkflowResultDeliveryCredentialTestData.Create("roundtrip"),
                " bot-reg-1 ",
                [
                    new AgentToolChannelIdentityHint(" sender ", " global ", " on_sender_1 "),
                    new AgentToolChannelIdentityHint(" conversation ", " platform ", " oc_provider_1 "),
                ]),
            new AgentToolSenderBindingContext(" binding-1 ", " nyx-user-1 ", " ou_tenant_1 "),
            new LLMRequestRoutingContext(" model-1 ", " route-1 ", 7, " memory-1 "),
            new AgentToolConnectedServicesContext("""{"service":"telegram"}"""),
            new AgentWorkflowRuntimeContext(" parent-actor ", " parent-run ", " parent-step ", " root-run ", 3),
            new AgentToolScheduleContext(" schedule-1 "),
            AgentToolCredentialSource.ChannelRegistration,
            new AgentSkillRecoveryContext(
                RequireInitialOrnnSearch: true,
                RequireOrnnSearchOnBlocker: true,
                CommandName: " goal ",
                OriginalCommand: " /goal ship ",
                PrimarySkillName: " goal-skill ",
                MaxOrnnSearchAttempts: 2,
                CommandArguments: " ship ",
                DiscoveryRequested: true),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["external-trace"] = "trace-1",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "legacy-token",
                ["telegram.chat_id"] = "10001",
            }) with
        {
            ExecutionOwner = AgentToolExecutionOwners.HostService("svc-context-roundtrip"),
        };

        var payload = context.ToPayload();
        var copy = AgentToolExecutionContextMapper.FromPayload(
            AgentToolExecutionContextPayload.Parser.ParseFrom(payload.ToByteArray()));

        copy.Request.RequestId.Should().Be("request-1");
        copy.Request.CallId.Should().Be("call-1");
        copy.Credentials.NyxIdAccessToken.Should().Be("access-1");
        copy.Credentials.NyxIdOrgToken.Should().Be("org-1");
        copy.Credentials.SenderNyxIdAccessToken.Should().Be("sender-access-1");
        copy.Credentials.NyxIdCredentialKind.Should().Be(AgentToolNyxIdCredentialKind.ProxyDelegation);
        copy.Caller.ScopeId.Should().Be("scope-1");
        copy.Caller.OwnerSubject.Should().Be("owner-1");
        copy.Caller.ResponseId.Should().Be("response-1");
        copy.Channel.Platform.Should().Be("telegram");
        copy.Channel.SenderId.Should().Be("sender-1");
        copy.Channel.RegistrationScopeId.Should().Be("registration-1");
        copy.Channel.MessageId.Should().Be("message-1");
        copy.Channel.PlatformMessageId.Should().Be("platform-message-1");
        copy.Channel.DeliveryTargetId.Should().Be("delivery-target-1");
        copy.Channel.WorkflowResultDeliveryCredential.Should().Be(ChannelWorkflowResultDeliveryCredentialTestData.Create("roundtrip"));
        copy.Channel.BotRegistrationId.Should().Be("bot-reg-1");
        copy.Channel.IdentityHints.Should().BeEquivalentTo(
            new[]
            {
                new AgentToolChannelIdentityHint("sender", "global", "on_sender_1"),
                new AgentToolChannelIdentityHint("conversation", "platform", "oc_provider_1"),
            },
            options => options.WithStrictOrdering());
        copy.SenderBinding.BindingId.Should().Be("binding-1");
        copy.SenderBinding.NyxUserId.Should().Be("nyx-user-1");
        copy.SenderBinding.SenderTenant.Should().Be("ou_tenant_1");
        copy.Routing.ModelOverride.Should().Be("model-1");
        copy.Routing.NyxIdRoutePreference.Should().Be("route-1");
        copy.Routing.MaxToolRoundsOverride.Should().Be(7);
        copy.Routing.UserMemoryPrompt.Should().Be("memory-1");
        copy.ConnectedServices.ContextJson.Should().Be("""{"service":"telegram"}""");
        copy.WorkflowRuntime.ParentActorId.Should().Be("parent-actor");
        copy.WorkflowRuntime.ParentRunId.Should().Be("parent-run");
        copy.WorkflowRuntime.ParentStepId.Should().Be("parent-step");
        copy.WorkflowRuntime.RootRunId.Should().Be("root-run");
        copy.WorkflowRuntime.Depth.Should().Be(3);
        copy.WorkflowRuntime.HasManagedParent.Should().BeTrue();
        copy.Schedule.ScheduleId.Should().Be("schedule-1");
        copy.CredentialSource.Should().Be(AgentToolCredentialSource.ChannelRegistration);
        copy.SkillRecovery.RequireInitialOrnnSearch.Should().BeTrue();
        copy.SkillRecovery.RequireOrnnSearchOnBlocker.Should().BeTrue();
        copy.SkillRecovery.CommandName.Should().Be("goal");
        copy.SkillRecovery.OriginalCommand.Should().Be("/goal ship");
        copy.SkillRecovery.PrimarySkillName.Should().Be("goal-skill");
        copy.SkillRecovery.MaxOrnnSearchAttempts.Should().Be(2);
        copy.SkillRecovery.CommandArguments.Should().Be("ship");
        copy.SkillRecovery.DiscoveryRequested.Should().BeTrue();
        copy.ExecutionOwner.Kind.Should().Be(AgentToolExecutionOwnerKind.HostService);
        copy.ExecutionOwner.OwnerId.Should().Be("svc-context-roundtrip");
        copy.ExternalMetadata.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("external-trace", "trace-1"));
    }

    [Fact]
    public void FromPayload_WhenSkillRecoveryNewFieldsAreMissing_ShouldUseDefaults()
    {
        var payload = new AgentToolExecutionContextPayload
        {
            SkillRecovery = new AgentSkillRecoveryContextPayload
            {
                RequireInitialOrnnSearch = true,
                CommandName = "goal",
            },
        };

        var context = AgentToolExecutionContextMapper.FromPayload(
            AgentToolExecutionContextPayload.Parser.ParseFrom(payload.ToByteArray()));

        context.SkillRecovery.CommandName.Should().Be("goal");
        context.SkillRecovery.CommandArguments.Should().BeNull();
        context.SkillRecovery.DiscoveryRequested.Should().BeFalse();
    }

    [Fact]
    public void PayloadRoundTrip_ShouldPreserveTypedChatInvocationContext()
    {
        var context = AgentToolExecutionContext.Empty with
        {
            Chat = new AgentChatInvocationContext(
                AgentChatInvocationSurface.NyxIdAssistant,
                "conversation-alpha",
                "turn-alpha",
                "task-alpha",
                "step-alpha",
                "action-alpha"),
        };

        var payload = AgentToolExecutionContextMapper.ToPayload(context);
        var mapped = AgentToolExecutionContextMapper.FromPayload(
            AgentToolExecutionContextPayload.Parser.ParseFrom(payload.ToByteArray()));

        mapped.Chat.Should().Be(context.Chat);
        payload.ExternalMetadata.Keys.Should().NotContain(
            ["conversation_id", "turn_id", "task_id", "step_id", "action_request_id"]);
    }

    [Fact]
    public void PayloadRoundTrip_ShouldPreserveTypedChannelContextAcrossMultipleToolRounds()
    {
        var firstRound = AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-channel", "call-1"),
            Channel = new AgentToolChannelContext(
                "lark",
                "sender-1",
                "registration-1",
                "message-1",
                "platform-message-1",
                "delivery-target-1",
                ChannelWorkflowResultDeliveryCredentialTestData.Create("channel"),
                "bot-reg-1"),
            SenderBinding = new AgentToolSenderBindingContext("binding-1", "nyx-user-1", "tenant-1"),
            CredentialSource = AgentToolCredentialSource.ChannelRegistration,
        };

        var secondRound = AgentToolExecutionContextMapper
            .FromPayload(AgentToolExecutionContextPayload.Parser.ParseFrom(firstRound.ToPayload().ToByteArray()))
            .WithCallId("call-2");
        var thirdRound = AgentToolExecutionContextMapper.FromPayload(
            AgentToolExecutionContextPayload.Parser.ParseFrom(secondRound.ToPayload().ToByteArray()));

        thirdRound.Request.RequestId.Should().Be("request-channel");
        thirdRound.Request.CallId.Should().Be("call-2");
        thirdRound.Channel.Platform.Should().Be("lark");
        thirdRound.Channel.SenderId.Should().Be("sender-1");
        thirdRound.Channel.RegistrationScopeId.Should().Be("registration-1");
        thirdRound.Channel.DeliveryTargetId.Should().Be("delivery-target-1");
        thirdRound.Channel.WorkflowResultDeliveryCredential.Should().Be(ChannelWorkflowResultDeliveryCredentialTestData.Create("channel"));
        thirdRound.Channel.BotRegistrationId.Should().Be("bot-reg-1");
        thirdRound.SenderBinding.BindingId.Should().Be("binding-1");
        thirdRound.SenderBinding.NyxUserId.Should().Be("nyx-user-1");
        thirdRound.SenderBinding.SenderTenant.Should().Be("tenant-1");
        thirdRound.CredentialSource.Should().Be(AgentToolCredentialSource.ChannelRegistration);
        thirdRound.ExternalMetadata.Should().BeEmpty();
    }

    [Fact]
    public void FromPayload_WhenPayloadIsNull_ShouldReturnEmptyContext()
    {
        AgentToolExecutionContextMapper.FromPayload(null).Should().Be(AgentToolExecutionContext.Empty);
    }

    [Fact]
    public void ScopeDispose_WhenNestedScopesAreUsed_ShouldRestoreOuterContext()
    {
        var outer = AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("outer-request", "outer-call"),
        };
        var inner = AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("inner-request", "inner-call"),
        };

        using (AgentToolContextScope.Push(outer))
        {
            AgentToolRequestContext.Current.Should().BeSameAs(outer);

            using (AgentToolContextScope.Push(inner))
            {
                AgentToolRequestContext.Current.Should().BeSameAs(inner);
            }

            AgentToolRequestContext.Current.Should().BeSameAs(outer);
        }

        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public void ProductionSources_ShouldNotUseLegacyToolMetadataControlShims()
    {
        var repositoryRoot = FindRepositoryRoot();
        var files = Directory
            .EnumerateFiles(Path.Combine(repositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(repositoryRoot, "agents"), "*.cs", SearchOption.AllDirectories))
            .Where(static path => !IsGeneratedFile(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        files.Should().NotBeEmpty();

        var source = string.Join(
            Environment.NewLine,
            files.Select(path => StripComments(File.ReadAllText(path))));

        source.Should().NotContain("AgentToolRequestContext.CurrentMetadata");
        source.Should().NotContain("AgentToolRequestContext.TryGet(");
        source.Should().NotContain(".ToLegacyMetadata(");
        source.Should().NotContain("HttpAuthorizationMetadataKey");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }

    private static bool IsGeneratedFile(string path) =>
        path.EndsWith(".g.cs", StringComparison.Ordinal) ||
        path.EndsWith(".Designer.cs", StringComparison.Ordinal);

    private static string StripComments(string source)
    {
        var builder = new StringBuilder(source.Length);
        var inLineComment = false;
        var inBlockComment = false;
        var inString = false;
        var inVerbatimString = false;
        var inChar = false;

        for (var i = 0; i < source.Length; i++)
        {
            var current = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLineComment)
            {
                if (current == '\n')
                {
                    inLineComment = false;
                    builder.Append(current);
                }

                continue;
            }

            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                    builder.Append(' ');
                }
                else if (current == '\n')
                {
                    builder.Append(current);
                }

                continue;
            }

            if (!inString && !inChar && current == '/' && next == '/')
            {
                inLineComment = true;
                i++;
                builder.Append(' ');
                continue;
            }

            if (!inString && !inChar && current == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                builder.Append(' ');
                continue;
            }

            builder.Append(current);

            if (inString)
            {
                if (inVerbatimString)
                {
                    if (current == '"' && next == '"')
                    {
                        builder.Append(next);
                        i++;
                        continue;
                    }

                    if (current == '"')
                        inString = inVerbatimString = false;
                    continue;
                }

                if (current == '\\' && next != '\0')
                {
                    builder.Append(next);
                    i++;
                    continue;
                }

                if (current == '"')
                    inString = false;
                continue;
            }

            if (inChar)
            {
                if (current == '\\' && next != '\0')
                {
                    builder.Append(next);
                    i++;
                    continue;
                }

                if (current == '\'')
                    inChar = false;
                continue;
            }

            if (current == '@' && next == '"')
            {
                builder.Append(next);
                i++;
                inString = inVerbatimString = true;
                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current == '\'')
                inChar = true;
        }

        return builder.ToString();
    }
}
