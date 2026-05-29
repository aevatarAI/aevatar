using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Aevatar.Authentication.Abstractions;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.Scheduled;
using Aevatar.Hosting;
using Aevatar.Mainnet.Host.Api.Voice;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
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
        var existing = new ChatRoutePolicySnapshot(
            new ChatRouteAction { ForwardToModel = new ForwardToModel { ModelName = "existing-default" } },
            [
                new ChatRouteRule
                {
                    RuleId = "keep-chat",
                    Priority = 10,
                    Match = new ChatRouteMatch { SourceKind = ChatSourceKind.NyxResponses },
                    Action = new ChatRouteAction { ForwardToModel = new ForwardToModel { ModelName = "kept-model" } },
                    Description = "preserve non-voice-demo rule",
                },
                new ChatRouteRule
                {
                    RuleId = "voice-demo",
                    Priority = 900,
                    Match = new ChatRouteMatch { SourceKind = ChatSourceKind.Voice },
                    Action = GAgentToolHint("old-agent", "voice_presence_openai"),
                    Description = "replace stale voice demo rule",
                },
            ]);
        var routePolicyQueryPort = new StaticRoutePolicyQueryPort(existing);
        var routePolicyCommandPort = new RecordingChatRoutePolicyCommandPort();
        await using var app = await CreateAppAsync(
            voiceDemoCommandPort,
            catalogCommandPort,
            routePolicyCommandPort,
            routePolicyQueryPort);
        var client = app.GetTestClient();

        var response = await client.PostAsync("/api/demo/voice/bootstrap", content: null);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        body.Should().ContainKey("status").WhoseValue.ToString().Should().Be("accepted");
        body.Should().ContainKey("actor_id");
        body.Should().ContainKey("route_policy_actor_id");
        body.Should().ContainKey("agent_command_id");
        body.Should().ContainKey("route_policy_command_id");
        body.Should().ContainKey("readiness");
        var demoActorId = body!["actor_id"].ToString()!;
        demoActorId.Should().Be(RecordingVoiceDemoAgentCommandPort.DemoActorId);
        body["route_policy_actor_id"].ToString().Should().Be($"chat-route-policy:{Scope}");
        body["agent_command_id"].ToString().Should().Be("voice-demo-command");
        body["route_policy_command_id"].ToString().Should().Be("route-policy-command");

        voiceDemoCommandPort.Commands.Should().ContainSingle()
            .Which.Should().Be((Scope, "voice_presence_openai"));
        catalogCommandPort.Commands.Should().ContainSingle()
            .Which.AgentId.Should().Be(demoActorId);

        routePolicyCommandPort.Upserts.Should().ContainSingle();
        var (policyScope, command) = routePolicyCommandPort.Upserts[0];
        policyScope.Should().Be(Scope);
        command.OwnerScope.NyxUserId.Should().Be(Scope);
        command.OwnerScope.Platform.Should().Be(RoutingOwnerScope.NyxIdPlatform);
        command.DefaultTarget.ForwardToModel.ModelName.Should().Be("existing-default");
        command.Rules.Should().ContainSingle(rule => rule.RuleId == "keep-chat")
            .Which.Action.ForwardToModel.ModelName.Should().Be("kept-model");
        command.Rules.Should().NotContain(rule => rule.RuleId == "voice-demo",
            "Refactor (issue1321-first): bootstrap removes the stale route that used tool_choice_hint as actor addressing");
    }

    private static ChatRouteAction GAgentToolHint(string actorId, string voiceModuleName) => new()
    {
        ForwardToModel = new ForwardToModel
        {
            ToolSetRef = new ChatRouteToolSetRef { Name = "voice.realtime" },
            ToolChoiceHint = new ChatRouteToolChoiceHint
            {
                ToolName = "aevatar_invoke_gagent",
                PrefilledArguments = new Struct
                {
                    Fields =
                    {
                        ["actor_id"] = Google.Protobuf.WellKnownTypes.Value.ForString(actorId),
                        ["voice_module_name"] = Google.Protobuf.WellKnownTypes.Value.ForString(voiceModuleName),
                    },
                },
            },
        },
    };

    private static async Task<WebApplication> CreateAppAsync(
        RecordingVoiceDemoAgentCommandPort voiceDemoCommandPort,
        RecordingCatalogCommandPort catalogCommandPort,
        RecordingChatRoutePolicyCommandPort routePolicyCommandPort,
        StaticRoutePolicyQueryPort routePolicyQueryPort)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IVoiceDemoAgentCommandPort>(voiceDemoCommandPort);
        builder.Services.AddSingleton<IUserAgentCatalogCommandPort>(catalogCommandPort);
        builder.Services.AddSingleton<IChatRoutePolicyCommandPort>(routePolicyCommandPort);
        builder.Services.AddSingleton<IChatRoutePolicyQueryPort>(routePolicyQueryPort);

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

    private sealed class StaticRoutePolicyQueryPort(ChatRoutePolicySnapshot? snapshot) : IChatRoutePolicyQueryPort
    {
        public Task<ChatRoutePolicySnapshot?> LookupForCallerAsync(
            RoutingOwnerScope callerScope,
            CancellationToken ct = default) =>
            Task.FromResult(snapshot);
    }
}
