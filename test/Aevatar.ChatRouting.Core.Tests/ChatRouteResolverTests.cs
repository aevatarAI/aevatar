using Aevatar.ChatRouting.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
                    RuleId = "daily",
                    Priority = 10,
                    Match = new ChatRouteMatch { CommandName = "/daily" },
                    Action = ForwardToModelAction("daily-model"),
                },
            ]);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput { CommandName = "/daily" });

        decision.MatchedRuleId.Should().Be("daily");
        decision.Action.ForwardToModel.ModelName.Should().Be("daily-model");
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
    public void Resolve_ForwardToModelRule_PassesThroughUnchanged(
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
        decision.Action.Should().Be(action);
        decision.Deprecations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Resolve_DefaultTargetForwardToModel_PassesThroughUnchanged(
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
        decision.Action.Should().Be(defaultAction);
    }

    [Fact]
    public void Resolve_ForwardToGAgentRule_TranslatesToToolDrivenModelAction()
    {
        var resolver = NewResolver();
        var snapshot = new ChatRoutePolicySnapshot(
            ForwardToModelAction("default-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "voice-openai",
                    Priority = 10,
                    Match = new ChatRouteMatch { SourceKind = ChatSourceKind.Voice },
                    Action = new ChatRouteAction
                    {
                        ForwardToGagent = new ForwardToGAgent
                        {
                            ActorId = "agent-voice",
                            VoiceModuleName = "voice_presence_openai",
                        },
                    },
                },
            ]);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput
        {
            SourceKind = ChatSourceKind.Voice,
            Voice = new VoiceInput { VoiceModuleName = "voice_presence_openai" },
        });

        decision.MatchedRuleId.Should().Be("voice-openai");
        decision.Deprecations.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Code = "legacy_forward_to_gagent",
            ActionKind = "ForwardToGAgent",
            MatchedRuleId = "voice-openai",
            TranslatedTarget = "aevatar_invoke_gagent(actor_id=agent-voice)",
        });
        AssertForwardToModelTool(
            decision.Action,
            expectedToolName: "aevatar_invoke_gagent",
            expectedArguments: new Dictionary<string, string>
            {
                ["actor_id"] = "agent-voice",
            });
        decision.Action.ForwardToModel.ToolChoiceHint.PrefilledArguments.Fields
            .Should()
            .NotContainKey(
                "voice_module_name",
                "the current aevatar_invoke_gagent tool schema has no voice_module_name argument");
    }

    [Fact]
    public void Resolve_ForwardToTeamRule_TranslatesToToolDrivenModelAction()
    {
        var resolver = NewResolver();
        var snapshot = new ChatRoutePolicySnapshot(
            ForwardToModelAction("default-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "team-route",
                    Priority = 10,
                    Match = new ChatRouteMatch { CommandName = "/triage" },
                    Action = new ChatRouteAction
                    {
                        ForwardToTeam = new ForwardToTeam
                        {
                            TeamId = "team-1",
                            EndpointId = "chat",
                            ScopeId = "scope-1",
                        },
                    },
                },
            ]);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput { CommandName = "/triage" });

        decision.MatchedRuleId.Should().Be("team-route");
        decision.Deprecations.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Code = "legacy_forward_to_team",
            ActionKind = "ForwardToTeam",
            MatchedRuleId = "team-route",
            TranslatedTarget = "aevatar_invoke_team(team_id=team-1, endpoint_id=chat, scope_id=scope-1)",
        });
        AssertForwardToModelTool(
            decision.Action,
            expectedToolName: "aevatar_invoke_team",
            expectedArguments: new Dictionary<string, string>
            {
                ["team_id"] = "team-1",
                ["endpoint_id"] = "chat",
                ["scope_id"] = "scope-1",
            });
    }

    [Fact]
    public void Resolve_ForwardToWorkflowRule_TranslatesToToolDrivenModelAction()
    {
        var resolver = NewResolver();
        var snapshot = new ChatRoutePolicySnapshot(
            ForwardToModelAction("default-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "workflow-route",
                    Priority = 10,
                    Match = new ChatRouteMatch { CommandName = "/run" },
                    Action = new ChatRouteAction
                    {
                        ForwardToWorkflow = new ForwardToWorkflow { WorkflowId = "workflow-1" },
                    },
                },
            ]);

        var decision = resolver.Resolve(snapshot, new ChatRouteInput { CommandName = "/run" });

        decision.MatchedRuleId.Should().Be("workflow-route");
        decision.Deprecations.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Code = "legacy_forward_to_workflow",
            ActionKind = "ForwardToWorkflow",
            MatchedRuleId = "workflow-route",
            TranslatedTarget = "aevatar_start_workflow(workflow_id=workflow-1)",
        });
        AssertForwardToModelTool(
            decision.Action,
            expectedToolName: "aevatar_start_workflow",
            expectedArguments: new Dictionary<string, string>
            {
                ["workflow_id"] = "workflow-1",
            });
    }

    [Fact]
    public void Resolve_LegacyRule_LogsStructuredWarning()
    {
        var logger = new RecordingLogger<ChatRouteResolver>();
        var resolver = NewResolver(logger);
        var snapshot = new ChatRoutePolicySnapshot(
            ForwardToModelAction("default-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "legacy-agent",
                    Priority = 10,
                    Match = new ChatRouteMatch { CommandName = "/agent" },
                    Action = new ChatRouteAction
                    {
                        ForwardToGagent = new ForwardToGAgent { ActorId = "agent-1" },
                    },
                },
            ]);

        _ = resolver.Resolve(snapshot, new ChatRouteInput { CommandName = "/agent" });

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("chat_route_legacy_action_used");
        entry.State.Should().ContainKey("MatchedRuleId").WhoseValue.Should().Be("legacy-agent");
        entry.State.Should().ContainKey("ActionKind").WhoseValue.Should().Be("ForwardToGAgent");
        entry.State.Should().ContainKey("TranslatedTarget").WhoseValue.Should()
            .Be("aevatar_invoke_gagent(actor_id=agent-1)");
        entry.State.Should().ContainKey("Suggestion").WhoseValue.Should().BeOfType<string>()
            .Which.Should().Contain("ChatRoutePolicyMigrator");
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
                    RuleId = "legacy-first",
                    Priority = 1,
                    Match = new ChatRouteMatch { Channel = "lark" },
                    Action = new ChatRouteAction
                    {
                        ForwardToGagent = new ForwardToGAgent { ActorId = "first-agent" },
                    },
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
            "legacy-first",
            "the resolver consumes the already-projected rule order even when legacy and new action shapes are mixed");
        AssertForwardToModelTool(
            decision.Action,
            expectedToolName: "aevatar_invoke_gagent",
            expectedArguments: new Dictionary<string, string>
            {
                ["actor_id"] = "first-agent",
            });
    }

    [Fact]
    public void Resolve_DefaultTargetIsForwardToTeam_TranslatesToToolDrivenModelAction()
    {
        var resolver = NewResolver();
        var defaultTeam = new ChatRouteAction
        {
            ForwardToTeam = new ForwardToTeam
            {
                TeamId = "default-team",
                EndpointId = "chat",
                ScopeId = "scope-x",
            },
        };
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
                ["scope_id"] = "scope-x",
            });
    }

    [Fact]
    public void Resolve_DefaultTargetIsForwardToGAgent_TranslatesToToolDrivenModelAction()
    {
        var resolver = NewResolver();
        var snapshot = new ChatRoutePolicySnapshot(
            new ChatRouteAction
            {
                ForwardToGagent = new ForwardToGAgent { ActorId = "default-agent" },
            },
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
                Defaults = new ChatRoutingDefaultsOptions { FallbackModel = "option-model" },
            }));

            var decision = provider.GetFallbackDecision();

            decision.UsedFallback.Should().BeTrue();
            decision.MatchedRuleId.Should().BeEmpty();
            decision.Action.ForwardToModel.ModelName.Should().Be("env-model");
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
    }

    private static ChatRouteResolver NewResolver(ILogger<ChatRouteResolver>? logger = null)
    {
        var fallback = Substitute.For<IChatRouteFallbackProvider>();
        fallback.GetFallbackDecision().Returns(new ChatRouteDecision
        {
            Action = ForwardToModelAction("fallback-model"),
            UsedFallback = true,
        });
        return new ChatRouteResolver(fallback, logger);
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
        IReadOnlyDictionary<string, string> expectedArguments)
    {
        action.ActionCase.Should().Be(ChatRouteAction.ActionOneofCase.ForwardToModel);
        action.ForwardToModel.ModelName.Should().BeEmpty();
        action.ForwardToModel.ToolSetRef.Name.Should().Be("workspace.default");
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

    internal static OwnerScope CallerScope() => OwnerScope.ForChannel(
        "user-1",
        "lark",
        "bot-1",
        "sender-1");

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state as IReadOnlyList<KeyValuePair<string, object?>>;
            Entries.Add(new LogEntry(
                logLevel,
                formatter(state, exception),
                values?
                    .Where(static item => item.Key != "{OriginalFormat}")
                    .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, object?>(StringComparer.Ordinal)));
        }

        public sealed record LogEntry(
            LogLevel Level,
            string Message,
            IReadOnlyDictionary<string, object?> State);
    }
}
