using Aevatar.ChatRouting.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

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
    public void Resolve_MultipleMatchingRules_HighestPriorityWins()
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

        decision.MatchedRuleId.Should().Be("high");
        decision.Action.ForwardToModel.ModelName.Should().Be("high-model");
    }

    [Fact]
    public void Resolve_VoiceSourceRule_ReturnsVoiceTargetModule()
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
        decision.Action.ForwardToGagent.ActorId.Should().Be("agent-voice");
        decision.Action.ForwardToGagent.VoiceModuleName.Should().Be("voice_presence_openai");
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
                Aevatar.GAgents.ChatRouting.ChatRoutePolicyCurrentStateDocument,
                string>>());

        using var provider = services.AddChatRoutingCore().BuildServiceProvider();

        provider.GetRequiredService<ChatRouteResolver>().Should().NotBeNull();
        provider.GetRequiredService<IChatRouteFallbackProvider>().Should().BeOfType<EnvChatRouteFallbackProvider>();
        provider.GetRequiredService<IChatRoutePolicyQueryPort>().Should().BeOfType<ChatRoutePolicyQueryPort>();
    }

    private static ChatRouteResolver NewResolver()
    {
        var fallback = Substitute.For<IChatRouteFallbackProvider>();
        fallback.GetFallbackDecision().Returns(new ChatRouteDecision
        {
            Action = ForwardToModelAction("fallback-model"),
            UsedFallback = true,
        });
        return new ChatRouteResolver(fallback);
    }

    internal static ChatRouteAction ForwardToModelAction(string modelName) =>
        new() { ForwardToModel = new ForwardToModel { ModelName = modelName } };

    internal static OwnerScope CallerScope() => OwnerScope.ForChannel(
        "user-1",
        "lark",
        "bot-1",
        "sender-1");
}
