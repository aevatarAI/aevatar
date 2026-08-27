using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using ProtoValue = Google.Protobuf.WellKnownTypes.Value;

namespace Aevatar.ChatRouting.Core.Tests;

public sealed class ChatRouteResolverTests
{
    [Fact]
    public void Resolve_NullSnapshot_UsesFallbackDecision()
    {
        var fallback = Substitute.For<IChatRouteFallbackProvider>();
        fallback.GetFallbackDecision().Returns(new ChatRouteDecision
        {
            Action = ForwardToModelAction("fallback-model"),
            UsedFallback = true,
        });
        var resolver = new ChatRouteResolver(fallback);

        var decision = resolver.Resolve(null, new ChatRouteInput());

        decision.UsedFallback.Should().BeTrue();
        decision.MatchedRuleId.Should().BeEmpty();
        decision.Action.ForwardToModel.ModelName.Should().Be("fallback-model");
    }

    [Fact]
    public void Resolve_NullSnapshot_WhenDefaultToolSetConfigured_ShouldInjectDefaultToolSetIntoFallbackDecision()
    {
        var fallback = Substitute.For<IChatRouteFallbackProvider>();
        fallback.GetFallbackDecision().Returns(new ChatRouteDecision
        {
            Action = ForwardToModelAction("fallback-model"),
            UsedFallback = true,
        });
        var resolver = new ChatRouteResolver(
            fallback,
            Options.Create(new ChatRoutingOptions
            {
                Defaults = new ChatRoutingDefaultsOptions
                {
                    DefaultForwardToModelToolSetName = "workspace.default",
                },
            }));

        var decision = resolver.Resolve(null, new ChatRouteInput());

        decision.UsedFallback.Should().BeTrue();
        decision.Action.ForwardToModel.ToolSetRef.Name.Should().Be("workspace.default");
    }

    [Fact]
    public void Resolve_NullSnapshot_WhenImplicitToolSetOverrideSelected_ShouldReplaceFallbackToolSet()
    {
        var fallback = Substitute.For<IChatRouteFallbackProvider>();
        fallback.GetFallbackDecision().Returns(new ChatRouteDecision
        {
            Action = ForwardToModelAction(
                "fallback-model",
                includeToolSetRef: true,
                includeToolChoiceHint: true),
            UsedFallback = true,
        });
        var resolver = new ChatRouteResolver(fallback);

        var decision = resolver.Resolve(
            null,
            new ChatRouteInput(),
            implicitToolSetNameOverride: "profile.route");

        decision.UsedFallback.Should().BeTrue();
        decision.Action.ForwardToModel.ToolSetRef.Name.Should().Be("profile.route");
        decision.Action.ForwardToModel.ToolChoiceHint.ToolName.Should().Be("notify_self");
    }

    [Fact]
    public void Resolve_NullSnapshot_ShouldNotLeakImplicitToolSetOverrideIntoSharedFallbackDecision()
    {
        var sharedFallback = new ChatRouteDecision
        {
            Action = ForwardToModelAction(
                "fallback-model",
                includeToolSetRef: true,
                includeToolChoiceHint: false),
            UsedFallback = true,
        };
        var fallback = Substitute.For<IChatRouteFallbackProvider>();
        fallback.GetFallbackDecision().Returns(sharedFallback);
        var resolver = new ChatRouteResolver(fallback);

        var selected = resolver.Resolve(
            null,
            new ChatRouteInput(),
            implicitToolSetNameOverride: "profile.route");
        var unselected = resolver.Resolve(null, new ChatRouteInput());

        selected.Action.ForwardToModel.ToolSetRef.Name.Should().Be("profile.route");
        unselected.Action.ForwardToModel.ToolSetRef.Name.Should().Be("lark.self_notify");
        sharedFallback.Action.ForwardToModel.ToolSetRef.Name.Should().Be("lark.self_notify");
    }

    [Fact]
    public void Resolve_RulesEmpty_ReturnsDefaultTarget()
    {
        var resolver = NewResolver();
        var snapshot = new ChatRoutePolicySnapshot(ForwardToModelAction("default-model"), []);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput());

        decision.UsedFallback.Should().BeFalse();
        decision.MatchedRuleId.Should().BeEmpty();
        decision.Action.ForwardToModel.ModelName.Should().Be("default-model");
    }

    [Fact]
    public void Resolve_SingleMatchingRule_ReturnsRuleActionAndMatchedRuleId()
    {
        var resolver = NewResolver();
        var snapshot = new ChatRoutePolicySnapshot(
            ForwardToModelAction("default-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "summary",
                    Priority = 10,
                    Match = new ChatRouteMatch { CommandName = "/summary" },
                    Action = ForwardToModelAction("summary-model"),
                },
            ]);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput { CommandName = "/summary" });

        decision.MatchedRuleId.Should().Be("summary");
        decision.Action.ForwardToModel.ModelName.Should().Be("summary-model");
    }

    [Fact]
    public void Resolve_ContentHintRule_WhenInputContainsHint_ReturnsRuleAction()
    {
        var resolver = NewResolver();
        var snapshot = new ChatRoutePolicySnapshot(
            ForwardToModelAction("default-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "reservation-route",
                    Priority = 10,
                    Match = new ChatRouteMatch { ContentHint = "订餐" },
                    Action = ToolHintAction(
                        "aevatar_start_workflow",
                        new Dictionary<string, string>
                        {
                            ["workflow_id"] = "phone-restaurant-reservation",
                        }),
                },
            ]);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput { ContentHint = "今晚 7 点帮我订餐" });

        decision.MatchedRuleId.Should().Be("reservation-route");
        AssertForwardToModelTool(
            decision.Action,
            expectedToolName: "aevatar_start_workflow",
            expectedArguments: new Dictionary<string, string>
            {
                ["workflow_id"] = "phone-restaurant-reservation",
            });
    }

    [Fact]
    public void Resolve_ContentHintRule_WhenInputDoesNotContainHint_ReturnsDefaultTarget()
    {
        var resolver = NewResolver();
        var snapshot = new ChatRoutePolicySnapshot(
            ForwardToModelAction("default-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "reservation-route",
                    Priority = 10,
                    Match = new ChatRouteMatch { ContentHint = "订餐" },
                    Action = ToolHintAction(
                        "aevatar_start_workflow",
                        new Dictionary<string, string>
                        {
                            ["workflow_id"] = "phone-restaurant-reservation",
                        }),
                },
            ]);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput { ContentHint = "随便聊聊今天的天气" });

        decision.MatchedRuleId.Should().BeEmpty();
        decision.Action.ForwardToModel.ModelName.Should().Be("default-model");
        decision.Action.ForwardToModel.ToolChoiceHint.Should().BeNull();
    }

    [Fact]
    public void Resolve_ForwardToModelWithoutToolSetRef_WhenDefaultToolSetConfigured_ShouldInjectDefaultToolSet()
    {
        var resolver = NewResolver(defaultToolSetName: "workspace.default");
        var snapshot = new ChatRoutePolicySnapshot(ForwardToModelAction("default-model"), []);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput());

        decision.Action.ForwardToModel.ToolSetRef.Name.Should().Be("workspace.default");
    }

    [Fact]
    public void Resolve_ProjectedForwardToModelWithoutToolSetRef_ShouldUseImplicitToolSetOverride()
    {
        var resolver = NewResolver(defaultToolSetName: "workspace.default");
        var snapshot = new ChatRoutePolicySnapshot(ForwardToModelAction("default-model"), []);

        var decision = resolver.Resolve(
            snapshot,
            new ChatRouteInput(),
            implicitToolSetNameOverride: "profile.route");

        decision.Action.ForwardToModel.ToolSetRef.Name.Should().Be("profile.route");
    }

    [Fact]
    public void Resolve_ForwardToModelWithToolSetRef_WhenDefaultToolSetConfigured_ShouldPreserveExplicitToolSet()
    {
        var resolver = NewResolver(defaultToolSetName: "workspace.default");
        var action = ForwardToModelAction(
            "default-model",
            includeToolSetRef: true,
            includeToolChoiceHint: true);
        var snapshot = new ChatRoutePolicySnapshot(action, []);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput());

        decision.Action.ForwardToModel.ToolSetRef.Name.Should().Be("lark.self_notify");
        decision.Action.ForwardToModel.ToolChoiceHint.ToolName.Should().Be("notify_self");
    }

    [Fact]
    public void Resolve_ProjectedForwardToModelWithToolSetRef_ShouldPreserveExplicitRouteOverImplicitOverride()
    {
        var resolver = NewResolver(defaultToolSetName: "workspace.default");
        var action = ForwardToModelAction(
            "default-model",
            includeToolSetRef: true,
            includeToolChoiceHint: true);
        var snapshot = new ChatRoutePolicySnapshot(action, []);

        var decision = resolver.Resolve(
            snapshot,
            new ChatRouteInput(),
            implicitToolSetNameOverride: "profile.route");

        decision.Action.ForwardToModel.ToolSetRef.Name.Should().Be("lark.self_notify");
        decision.Action.ForwardToModel.ToolChoiceHint.ToolName.Should().Be("notify_self");
    }

    [Fact]
    public void Resolve_MultipleMatchingRules_UsesProjectedRuleOrder()
    {
        var resolver = NewResolver();
        var snapshot = new ChatRoutePolicySnapshot(
            ForwardToModelAction("default-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "low",
                    Priority = 1,
                    Match = new ChatRouteMatch { Channel = "lark" },
                    Action = ForwardToModelAction("low-model"),
                },
                new ChatRouteRule
                {
                    RuleId = "high",
                    Priority = 20,
                    Match = new ChatRouteMatch { Channel = "lark" },
                    Action = ForwardToModelAction("high-model"),
                },
            ]);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput { Channel = "lark" });

        decision.MatchedRuleId.Should().Be("low");
        decision.Action.ForwardToModel.ModelName.Should().Be(
            "low-model",
            "the policy actor stores rules in priority order, so the resolver must not re-sort on the hot path");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Resolve_ForwardToModelRule_AddsWorkspaceProfileKindWithoutChangingOtherFields(
        bool includeToolSetRef,
        bool includeToolChoiceHint)
    {
        var resolver = NewResolver();
        var action = ForwardToModelAction(
            "routed-model",
            includeToolSetRef,
            includeToolChoiceHint);
        var snapshot = new ChatRoutePolicySnapshot(
            ForwardToModelAction("default-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "model-route",
                    Priority = 10,
                    Match = new ChatRouteMatch { CommandName = "/model" },
                    Action = action,
                },
            ]);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput { CommandName = "/model" });

        decision.MatchedRuleId.Should().Be("model-route");
        decision.Action.Should().Be(WithProfileKind(action, ChatRouteAgentProfileKind.WorkspaceChat));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Resolve_DefaultTargetForwardToModel_AddsWorkspaceProfileKindWithoutChangingOtherFields(
        bool includeToolSetRef,
        bool includeToolChoiceHint)
    {
        var resolver = NewResolver();
        var defaultAction = ForwardToModelAction(
            "default-model",
            includeToolSetRef,
            includeToolChoiceHint);
        var snapshot = new ChatRoutePolicySnapshot(defaultAction, []);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput());

        decision.MatchedRuleId.Should().BeEmpty();
        decision.Action.Should().Be(WithProfileKind(defaultAction, ChatRouteAgentProfileKind.WorkspaceChat));
    }

    [Fact]
    public void Resolve_GAgentToolHintRule_AddsVoiceWorkspaceProfileKind()
    {
        var resolver = NewResolver();
        var action = ToolHintAction(
            "aevatar_invoke_gagent",
            new Dictionary<string, string>
            {
                ["actor_id"] = "agent-voice",
                ["voice_module_name"] = "voice_presence_openai",
            },
            toolSetName: "workspace.default");
        var snapshot = new ChatRoutePolicySnapshot(
            ForwardToModelAction("default-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "voice-openai",
                    Priority = 10,
                    Match = new ChatRouteMatch { SourceKind = ChatSourceKind.Voice },
                    Action = action,
                },
            ]);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput
        {
            SourceKind = ChatSourceKind.Voice,
            Voice = new VoiceInput { VoiceModuleName = "voice_presence_openai" },
        });

        decision.MatchedRuleId.Should().Be("voice-openai");
        decision.Action.Should().Be(WithProfileKind(action, ChatRouteAgentProfileKind.WorkspaceChat));
        AssertForwardToModelTool(
            decision.Action,
            expectedToolName: "aevatar_invoke_gagent",
            expectedArguments: new Dictionary<string, string>
            {
                ["actor_id"] = "agent-voice",
                ["voice_module_name"] = "voice_presence_openai",
            },
            expectedToolSetName: "workspace.default");
    }

    [Fact]
    public void Resolve_TeamToolHintRule_AddsWorkspaceProfileKind()
    {
        var resolver = NewResolver();
        var action = ToolHintAction(
            "aevatar_invoke_team",
            new Dictionary<string, string>
            {
                ["team_id"] = "team-1",
                ["endpoint_id"] = "chat",
            });
        var snapshot = new ChatRoutePolicySnapshot(
            ForwardToModelAction("default-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "team-route",
                    Priority = 10,
                    Match = new ChatRouteMatch { CommandName = "/triage" },
                    Action = action,
                },
            ]);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput { CommandName = "/triage" });

        decision.MatchedRuleId.Should().Be("team-route");
        decision.Action.Should().Be(WithProfileKind(action, ChatRouteAgentProfileKind.WorkspaceChat));
        AssertForwardToModelTool(
            decision.Action,
            expectedToolName: "aevatar_invoke_team",
            expectedArguments: new Dictionary<string, string>
            {
                ["team_id"] = "team-1",
                ["endpoint_id"] = "chat",
            });
    }

    [Fact]
    public void Resolve_RejectRule_PassesThroughUnchanged()
    {
        var resolver = NewResolver();
        var reject = new ChatRouteAction { Reject = new Reject { Reason = "blocked" } };
        var snapshot = new ChatRoutePolicySnapshot(
            ForwardToModelAction("default-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "reject-route",
                    Priority = 10,
                    Match = new ChatRouteMatch { CommandName = "/deny" },
                    Action = reject,
                },
            ]);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput { CommandName = "/deny" });

        decision.MatchedRuleId.Should().Be("reject-route");
        decision.Action.Should().Be(reject);
    }

    [Fact]
    public void Resolve_MixedLegacyAndNewRules_PreservesProjectedRuleOrder()
    {
        var resolver = NewResolver();
        var snapshot = new ChatRoutePolicySnapshot(
            ForwardToModelAction("default-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "tool-first",
                    Priority = 1,
                    Match = new ChatRouteMatch { Channel = "lark" },
                    Action = ToolHintAction(
                        "aevatar_invoke_gagent",
                        new Dictionary<string, string> { ["actor_id"] = "first-agent" }),
                },
                new ChatRouteRule
                {
                    RuleId = "new-second",
                    Priority = 20,
                    Match = new ChatRouteMatch { Channel = "lark" },
                    Action = ForwardToModelAction("second-model", includeToolSetRef: true, includeToolChoiceHint: true),
                },
            ]);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput { Channel = "lark" });

        decision.MatchedRuleId.Should().Be(
            "tool-first",
            "the resolver consumes the already-projected rule order even when tool-hint and model actions are mixed");
        AssertForwardToModelTool(
            decision.Action,
            expectedToolName: "aevatar_invoke_gagent",
            expectedArguments: new Dictionary<string, string>
            {
                ["actor_id"] = "first-agent",
            });
    }

    [Fact]
    public void Resolve_DefaultTargetIsTeamToolHint_PassesThroughUnchanged()
    {
        var resolver = NewResolver();
        var defaultTeam = ToolHintAction(
            "aevatar_invoke_team",
            new Dictionary<string, string>
            {
                ["team_id"] = "default-team",
                ["endpoint_id"] = "chat",
            });
        var snapshot = new ChatRoutePolicySnapshot(defaultTeam, []);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput());

        decision.UsedFallback.Should().BeFalse();
        decision.MatchedRuleId.Should().BeEmpty();
        AssertForwardToModelTool(
            decision.Action,
            expectedToolName: "aevatar_invoke_team",
            expectedArguments: new Dictionary<string, string>
            {
                ["team_id"] = "default-team",
                ["endpoint_id"] = "chat",
            });
    }

    [Fact]
    public void Resolve_DefaultTargetIsGAgentToolHint_PassesThroughUnchanged()
    {
        var resolver = NewResolver();
        var snapshot = new ChatRoutePolicySnapshot(
            ToolHintAction(
                "aevatar_invoke_gagent",
                new Dictionary<string, string> { ["actor_id"] = "default-agent" }),
            []);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput());

        decision.UsedFallback.Should().BeFalse();
        decision.MatchedRuleId.Should().BeEmpty();
        AssertForwardToModelTool(
            decision.Action,
            expectedToolName: "aevatar_invoke_gagent",
            expectedArguments: new Dictionary<string, string>
            {
                ["actor_id"] = "default-agent",
            });
    }

    [Fact]
    public void Resolve_ModelAndToolModeRule_ReturnsRuleAction()
    {
        var resolver = NewResolver();
        var snapshot = new ChatRoutePolicySnapshot(
            ForwardToModelAction("default-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "responses-tools",
                    Priority = 10,
                    Match = new ChatRouteMatch
                    {
                        SourceKind = ChatSourceKind.NyxResponses,
                        Model = "original-model",
                        ToolMode = ToolMode.Declared,
                    },
                    Action = ForwardToModelAction("tool-aware-model"),
                },
            ]);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput
        {
            SourceKind = ChatSourceKind.NyxResponses,
            Model = "original-model",
            ToolMode = ToolMode.Declared,
        });

        decision.MatchedRuleId.Should().Be("responses-tools");
        decision.Action.ForwardToModel.ModelName.Should().Be("tool-aware-model");
    }

    [Fact]
    public void Resolve_MatchingRuleWithEmptyAction_FallsThroughToDefaultTarget()
    {
        var resolver = NewResolver();
        var snapshot = new ChatRoutePolicySnapshot(
            ForwardToModelAction("default-model"),
            [
                // A projected rule whose action was never set — proto3 leaves
                // the message field null, and the write side only validates
                // default_target on upsert, so this is reachable on the read
                // side. Resolver must not NRE on action.Clone().
                new ChatRouteRule
                {
                    RuleId = "broken",
                    Priority = 10,
                    Match = new ChatRouteMatch { Channel = "lark" },
                    Action = null,
                },
            ]);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput { Channel = "lark" });

        decision.MatchedRuleId.Should().BeEmpty(
            "an actionless matching rule must be skipped, not return a half-built decision");
        decision.Action.ForwardToModel.ModelName.Should().Be("default-model");
        decision.UsedFallback.Should().BeFalse();
    }

    [Fact]
    public void Resolve_HigherPriorityRuleEmpty_LowerPriorityRuleWithActionStillWins()
    {
        var resolver = NewResolver();
        var snapshot = new ChatRoutePolicySnapshot(
            ForwardToModelAction("default-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "high-empty",
                    Priority = 20,
                    Match = new ChatRouteMatch { Channel = "lark" },
                    Action = null,
                },
                new ChatRouteRule
                {
                    RuleId = "low-actioned",
                    Priority = 5,
                    Match = new ChatRouteMatch { Channel = "lark" },
                    Action = ForwardToModelAction("low-model"),
                },
            ]);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput { Channel = "lark" });

        decision.MatchedRuleId.Should().Be("low-actioned",
            "a higher-priority but actionless rule must not block a lower-priority actionable one");
        decision.Action.ForwardToModel.ModelName.Should().Be("low-model");
    }

    [Fact]
    public void ResolverAssembly_HasNoOrleansRuntimeOrHttpClientInjectionSurface()
    {
        var disallowedReferences = typeof(ChatRouteResolver).Assembly.GetReferencedAssemblies()
            .Select(static name => name.Name)
            .Where(static name =>
                name is not null &&
                name.Contains("Orleans", StringComparison.OrdinalIgnoreCase));
        disallowedReferences.Should().BeEmpty();

        var disallowedConstructorTypes = typeof(ChatRouteResolver).GetConstructors()
            .SelectMany(static ctor => ctor.GetParameters())
            .Select(static parameter => parameter.ParameterType.FullName)
            .Where(static name =>
                name is not null &&
                (name.Contains("HttpClient", StringComparison.Ordinal) ||
                 name.Contains("IActorRuntime", StringComparison.Ordinal)));
        disallowedConstructorTypes.Should().BeEmpty();
    }

    [Fact]
    public void EnvFallback_EnvModelWinsOverOptions()
    {
        var previous = Environment.GetEnvironmentVariable(EnvChatRouteFallbackProvider.DefaultModelEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(EnvChatRouteFallbackProvider.DefaultModelEnvironmentVariable, "env-model");
            var provider = new EnvChatRouteFallbackProvider(Options.Create(new ChatRoutingOptions
            {
                Defaults = new ChatRoutingDefaultsOptions
                {
                    FallbackModel = "option-model",
                    DefaultForwardToModelToolSetName = "workspace.default",
                },
            }));

            var decision = provider.GetFallbackDecision();

            decision.UsedFallback.Should().BeTrue();
            decision.MatchedRuleId.Should().BeEmpty();
            decision.Action.ForwardToModel.ModelName.Should().Be("env-model");
            decision.Action.ForwardToModel.ToolSetRef.Name.Should().Be("workspace.default");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvChatRouteFallbackProvider.DefaultModelEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void AddChatRoutingCore_RegistersResolverFallbackAndQueryPort()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<
            Aevatar.CQRS.Projection.Stores.Abstractions.IProjectionDocumentReader<
                ChatRoutePolicyCurrentStateDocument,
                string>>());

        using var provider = services.AddChatRoutingCore().BuildServiceProvider();

        provider.GetRequiredService<ChatRouteResolver>().Should().NotBeNull();
        provider.GetRequiredService<IChatRouteFallbackProvider>().Should().BeOfType<EnvChatRouteFallbackProvider>();
        provider.GetRequiredService<IChatRoutePolicyQueryPort>().Should().BeOfType<ChatRoutePolicyQueryPort>();
    }

    [Fact]
    public void AddChatRoutingCore_BindsChatRoutingOptionsFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChatRouting:Defaults:FallbackModel"] = "configured-model",
                ["ChatRouting:Defaults:DefaultForwardToModelToolSetName"] = "workspace.default",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(Substitute.For<
            Aevatar.CQRS.Projection.Stores.Abstractions.IProjectionDocumentReader<
                ChatRoutePolicyCurrentStateDocument,
                string>>());

        using var provider = services.AddChatRoutingCore().BuildServiceProvider();

        provider.GetRequiredService<IOptions<ChatRoutingOptions>>()
            .Value.Defaults.FallbackModel.Should().Be("configured-model");
        provider.GetRequiredService<IOptions<ChatRoutingOptions>>()
            .Value.Defaults.DefaultForwardToModelToolSetName.Should().Be("workspace.default");
    }

    [Theory]
    [InlineData(ChatSourceKind.Direct, ChatRouteAgentProfileKind.NyxidChat)]
    [InlineData(ChatSourceKind.NyxRelay, ChatRouteAgentProfileKind.ChannelReply)]
    [InlineData(ChatSourceKind.NyxResponses, ChatRouteAgentProfileKind.WorkspaceChat)]
    [InlineData(ChatSourceKind.Voice, ChatRouteAgentProfileKind.WorkspaceChat)]
    public void Resolve_ShouldMapIngressToServerOwnedProfileKind(
        ChatSourceKind sourceKind,
        ChatRouteAgentProfileKind expectedProfileKind)
    {
        var resolver = NewResolver();
        var snapshot = new ChatRoutePolicySnapshot(ForwardToModelAction("model"), []);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput { SourceKind = sourceKind });

        decision.Action.ForwardToModel.ProfileKind.Should().Be(expectedProfileKind);
    }

    [Fact]
    public void Resolve_ShouldPreserveExplicitProfileKindAndReference()
    {
        var resolver = NewResolver();
        var action = ForwardToModelAction("model");
        action.ForwardToModel.ProfileKind = ChatRouteAgentProfileKind.ChannelReply;
        action.ForwardToModel.ProfileRef = new ChatRouteAgentProfileRef
        {
            OwnerKind = ChatRouteAgentProfileReferenceOwnerKind.Caller,
            ProfileSlug = "my-channel-agent",
        };
        var snapshot = new ChatRoutePolicySnapshot(action, []);

        var decision = resolver.Resolve(
            snapshot,
            new ChatRouteInput { SourceKind = ChatSourceKind.NyxResponses });

        decision.Action.Should().Be(action);
    }

    private static ChatRouteResolver NewResolver(string defaultToolSetName = "")
    {
        var fallback = Substitute.For<IChatRouteFallbackProvider>();
        fallback.GetFallbackDecision().Returns(new ChatRouteDecision
        {
            Action = ForwardToModelAction("fallback-model"),
            UsedFallback = true,
        });
        return new ChatRouteResolver(
            fallback,
            Options.Create(new ChatRoutingOptions
            {
                Defaults = new ChatRoutingDefaultsOptions
                {
                    DefaultForwardToModelToolSetName = defaultToolSetName,
                },
            }));
    }

    private static ChatRouteAction WithProfileKind(
        ChatRouteAction action,
        ChatRouteAgentProfileKind profileKind)
    {
        var clone = action.Clone();
        clone.ForwardToModel.ProfileKind = profileKind;
        return clone;
    }

    internal static ChatRouteAction ForwardToModelAction(string modelName) =>
        new() { ForwardToModel = new ForwardToModel { ModelName = modelName } };

    private static ChatRouteAction ForwardToModelAction(
        string modelName,
        bool includeToolSetRef,
        bool includeToolChoiceHint)
    {
        var forward = new ForwardToModel { ModelName = modelName };
        if (includeToolSetRef)
        {
            forward.ToolSetRef = new ChatRouteToolSetRef { Name = "lark.self_notify" };
        }

        if (includeToolChoiceHint)
        {
            forward.ToolChoiceHint = new ChatRouteToolChoiceHint
            {
                ToolName = "notify_self",
                PrefilledArguments = new Struct
                {
                    Fields =
                    {
                        ["recipient"] = ProtoValue.ForString("me"),
                    },
                },
            };
        }

        return new ChatRouteAction { ForwardToModel = forward };
    }

    private static void AssertForwardToModelTool(
        ChatRouteAction action,
        string expectedToolName,
        IReadOnlyDictionary<string, string> expectedArguments,
        string expectedToolSetName = "workspace.default")
    {
        action.ActionCase.Should().Be(ChatRouteAction.ActionOneofCase.ForwardToModel);
        action.ForwardToModel.ModelName.Should().BeEmpty();
        action.ForwardToModel.ToolSetRef.Name.Should().Be(expectedToolSetName);
        action.ForwardToModel.ToolChoiceHint.ToolName.Should().Be(expectedToolName);
        action.ForwardToModel.ToolChoiceHint.PrefilledArguments.Fields
            .Should()
            .HaveCount(expectedArguments.Count);

        foreach (var (key, value) in expectedArguments)
        {
            action.ForwardToModel.ToolChoiceHint.PrefilledArguments.Fields[key].StringValue
                .Should()
                .Be(value);
        }
    }

    private static ChatRouteAction ToolHintAction(
        string toolName,
        IReadOnlyDictionary<string, string> arguments,
        string toolSetName = "workspace.default")
    {
        var fields = new Struct();
        foreach (var (key, value) in arguments)
            fields.Fields[key] = ProtoValue.ForString(value);

        return new ChatRouteAction
        {
            ForwardToModel = new ForwardToModel
            {
                ToolSetRef = new ChatRouteToolSetRef { Name = toolSetName },
                ToolChoiceHint = new ChatRouteToolChoiceHint
                {
                    ToolName = toolName,
                    PrefilledArguments = fields,
                },
            },
        };
    }

    internal static OwnerScope CallerScope() => OwnerScope.ForChannel(
        "user-1",
        "lark",
        "bot-1",
        "sender-1");
}
