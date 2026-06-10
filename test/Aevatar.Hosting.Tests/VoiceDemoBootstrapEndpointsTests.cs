using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Aevatar.Authentication.Abstractions;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.Scheduled;
using Aevatar.Hosting;
using Aevatar.Mainnet.Host.Api.Voice;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RoutingOwnerScope = Aevatar.Foundation.Abstractions.OwnerScope;

namespace Aevatar.Hosting.Tests;

// Refactor (iter34/cluster-004-voice-bootstrap-application-port):
//   Old pattern: Endpoint tests expected synchronous readiness after request-path polling.
//   New principle: Tests assert accepted command receipt semantics and dispatch-only behavior, with readiness left to readmodels or events.
public sealed class VoiceDemoBootstrapEndpointsTests
{
    private const string Scope = "voice-scope-1";

    [Fact]
    public async Task Bootstrap_AcceptsTypedCommandsWithoutReadinessPolling()
    {
        var voiceDemoCommandPort = new RecordingVoiceDemoAgentCommandPort();
        var catalogCommandPort = new RecordingCatalogCommandPort();
        var routePolicyCommandPort = new RecordingChatRoutePolicyCommandPort();
        var fallbackProvider = new StaticChatRouteFallbackProvider("fallback-model");
        await using var app = await CreateAppAsync(
            voiceDemoCommandPort,
            catalogCommandPort,
            routePolicyCommandPort,
            fallbackProvider);
        var client = app.GetTestClient();

        var response = await client.PostAsync("/api/demo/voice/bootstrap", content: null);
        var responseText = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<Dictionary<string, object>>(responseText);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, responseText);
        body.Should().ContainKey("status").WhoseValue.ToString().Should().Be("accepted");
        body.Should().ContainKey("actor_id");
        body.Should().ContainKey("route_policy_actor_id");
        body.Should().ContainKey("agent_command_id");
        body.Should().ContainKey("route_policy_command_id");
        body.Should().ContainKey("readiness");
        body.Should().NotContainKey("nyxid_proxy");
        responseText.Should().NotContain("https://nyx.chrono-ai.fun/api/v1/proxy/s/llm-openai");
        var demoActorId = body!["actor_id"].ToString()!;
        demoActorId.Should().Be(RecordingVoiceDemoAgentCommandPort.DemoActorId);
        body["route_policy_actor_id"].ToString().Should().Be($"chat-route-policy:{Scope}");
        body["agent_command_id"].ToString().Should().Be("voice-demo-command");
        body["route_policy_command_id"].ToString().Should().Be("route-policy-rule-command");

        voiceDemoCommandPort.Commands.Should().ContainSingle()
            .Which.Should().Be((Scope, "voice_presence_openai"));
        catalogCommandPort.Commands.Should().ContainSingle()
            .Which.AgentId.Should().Be(demoActorId);

        routePolicyCommandPort.Upserts.Should().BeEmpty();
        routePolicyCommandPort.RuleUpserts.Should().ContainSingle();
        var (policyScope, command) = routePolicyCommandPort.RuleUpserts[0];
        policyScope.Should().Be(Scope);
        command.OwnerScope.NyxUserId.Should().Be(Scope);
        command.OwnerScope.Platform.Should().Be(RoutingOwnerScope.NyxIdPlatform);
        command.DefaultTargetIfUninitialized.ForwardToModel.ModelName.Should().Be("fallback-model");
        var voiceRule = command.Rule;
        voiceRule.RuleId.Should().Be("voice-demo");
        voiceRule.Priority.Should().Be(900);
        voiceRule.Match.SourceKind.Should().Be(ChatSourceKind.Voice);
        voiceRule.Action.ForwardToModel.ToolChoiceHint.VoiceAttachTarget.ActorId.Should().Be(demoActorId);
        voiceRule.Action.ForwardToModel.ToolChoiceHint.VoiceAttachTarget.VoiceModuleName.Should().Be("voice_presence_openai");
        voiceRule.Action.ForwardToModel.ToolChoiceHint.PrefilledArguments.Should().BeNull();
    }

    [Fact]
    public async Task Bootstrap_UpsertsVoiceRuleWithoutReadingRoutePolicySnapshot()
    {
        var voiceDemoCommandPort = new RecordingVoiceDemoAgentCommandPort();
        var catalogCommandPort = new RecordingCatalogCommandPort();
        var routePolicyCommandPort = new RecordingChatRoutePolicyCommandPort();
        var fallbackProvider = new StaticChatRouteFallbackProvider("cold-start-model");
        await using var app = await CreateAppAsync(
            voiceDemoCommandPort,
            catalogCommandPort,
            routePolicyCommandPort,
            fallbackProvider);
        var client = app.GetTestClient();

        var response = await client.PostAsync("/api/demo/voice/bootstrap", content: null);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object?>>();

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        voiceDemoCommandPort.Commands.Should().ContainSingle()
            .Which.Should().Be((Scope, "voice_presence_openai"));
        catalogCommandPort.Commands.Should().ContainSingle()
            .Which.AgentId.Should().Be(RecordingVoiceDemoAgentCommandPort.DemoActorId);
        routePolicyCommandPort.Upserts.Should().BeEmpty();
        routePolicyCommandPort.RuleUpserts.Should().ContainSingle();
        routePolicyCommandPort.RuleUpserts[0].Command
            .DefaultTargetIfUninitialized.ForwardToModel.ModelName.Should().Be("cold-start-model");
        body.Should().ContainKey("route_policy_actor_id")
            .WhoseValue!.ToString().Should().Be($"chat-route-policy:{Scope}");
        body.Should().ContainKey("route_policy_command_id")
            .WhoseValue!.ToString().Should().Be("route-policy-rule-command");
    }

    private static async Task<WebApplication> CreateAppAsync(
        RecordingVoiceDemoAgentCommandPort voiceDemoCommandPort,
        RecordingCatalogCommandPort catalogCommandPort,
        RecordingChatRoutePolicyCommandPort routePolicyCommandPort,
        IChatRouteFallbackProvider fallbackProvider)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IVoiceDemoAgentCommandPort>(voiceDemoCommandPort);
        builder.Services.AddSingleton<IUserAgentCatalogCommandPort>(catalogCommandPort);
        builder.Services.AddSingleton<IChatRoutePolicyCommandPort>(routePolicyCommandPort);
        builder.Services.AddSingleton(fallbackProvider);

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AevatarStandardClaimTypes.ScopeId, Scope)],
                authenticationType: "test"));
            await next();
        });
        app.MapVoiceDemoBootstrapEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class RecordingVoiceDemoAgentCommandPort : IVoiceDemoAgentCommandPort
    {
        public const string DemoActorId = "nyxid-chat-voice-demo-test-scope";

        public List<(string ScopeId, string VoiceModuleName)> Commands { get; } = [];

        public Task<VoiceDemoAgentCommandAcceptedReceipt> EnsureAsync(
            string scopeId,
            string voiceModuleName,
            CancellationToken ct = default)
        {
            Commands.Add((scopeId, voiceModuleName));
            return Task.FromResult(new VoiceDemoAgentCommandAcceptedReceipt(
                DemoActorId,
                "voice-demo-command",
                "voice-demo-command"));
        }
    }

    private sealed class RecordingChatRoutePolicyCommandPort : IChatRoutePolicyCommandPort
    {
        public List<(string ScopeId, UpsertChatRoutePolicyRequested Command)> Upserts { get; } = [];
        public List<(string ScopeId, UpsertChatRouteRuleRequested Command)> RuleUpserts { get; } = [];

        public Task<ChatRoutePolicyCommandAcceptedReceipt> UpsertAsync(
            string scopeId,
            UpsertChatRoutePolicyRequested command,
            CancellationToken ct = default)
        {
            Upserts.Add((scopeId, command.Clone()));
            return Task.FromResult(new ChatRoutePolicyCommandAcceptedReceipt(
                $"chat-route-policy:{scopeId}",
                "route-policy-command",
                "route-policy-command"));
        }

        public Task<ChatRoutePolicyCommandAcceptedReceipt> UpsertRuleAsync(
            string scopeId,
            UpsertChatRouteRuleRequested command,
            CancellationToken ct = default)
        {
            RuleUpserts.Add((scopeId, command.Clone()));
            return Task.FromResult(new ChatRoutePolicyCommandAcceptedReceipt(
                $"chat-route-policy:{scopeId}",
                "route-policy-rule-command",
                "route-policy-rule-command"));
        }

        public Task<ChatRoutePolicyCommandAcceptedReceipt> RemoveRuleAsync(
            string scopeId,
            RemoveChatRouteRuleRequested command,
            CancellationToken ct = default) =>
            Task.FromResult(new ChatRoutePolicyCommandAcceptedReceipt(
                $"chat-route-policy:{scopeId}",
                "route-policy-remove",
                "route-policy-remove"));
    }

    private sealed class RecordingCatalogCommandPort : IUserAgentCatalogCommandPort
    {
        public List<UserAgentCatalogUpsertCommand> Commands { get; } = [];

        public Task UpsertAsync(UserAgentCatalogUpsertCommand command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.CompletedTask;
        }

        public Task TombstoneAsync(string agentId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StaticChatRouteFallbackProvider(string modelName) : IChatRouteFallbackProvider
    {
        public ChatRouteDecision GetFallbackDecision() => new()
        {
            Action = new ChatRouteAction
            {
                ForwardToModel = new ForwardToModel { ModelName = modelName },
            },
            UsedFallback = true,
        };
    }
}
