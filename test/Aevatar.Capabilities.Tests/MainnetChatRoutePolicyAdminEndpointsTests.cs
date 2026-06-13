using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Text;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatRouting;
using Aevatar.Capabilities;
using Aevatar.Mainnet.Host.Api.ChatRouting;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Capabilities.Tests;

/// <summary>
/// REST admin surface for ChatRoutePolicyGAgent. Without these tests it is
/// easy to regress the JSON parsing, scope-stamp behavior, or the command-port
/// admission shape (accepted scope / stamped owner scope / rule command) — the actor itself
/// (validated by ChatRoutePolicyGAgentTests) is fire-and-forget on the
/// stream, so an endpoint bug surfaces only in operator pain.
/// </summary>
// Refactor (iter34/cluster-005-mainnet-host-direct-actor-runtime):
//   Old pattern: Mainnet Host endpoints inject IActorRuntime/IActorDispatchPort and build EventEnvelope + dispatch directly in Host code.
//   New principle: Host calls Application command ports that normalize, resolve target, build envelope, dispatch, return honest accepted receipt.
//   Host endpoint stays minimal (auth + body parsing). NO direct dependency on IActorRuntime/IActorDispatchPort in Host.
public sealed class MainnetChatRoutePolicyAdminEndpointsTests
{
    private const string Scope = "5d0d7b72-acff-49af-bb1b-9f30bbb7c102";

    [Fact]
    public async Task PutPolicy_StampsOwnerScopeAndDispatchesUpsertCommandToScopeActor()
    {
        var commandPort = new RecordingChatRoutePolicyCommandPort();
        await using var app = await CreateAppAsync(commandPort);
        var client = app.GetTestClient();

        var body = """
        {
          "default_target": { "forward_to_model": { "model_name": "deepseek/deepseek-chat" } },
          "rules": [{
            "rule_id": "claude-for-responses",
            "priority": 100,
            "match": { "source_kind": "CHAT_SOURCE_KIND_NYX_RESPONSES" },
            "action": { "forward_to_model": { "model_name": "anthropic/claude-sonnet-4-6" } },
            "description": "use claude for /v1/responses"
          }]
        }
        """;
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/scopes/{Scope}/chat-route-policy")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        commandPort.Upserts.Should().ContainSingle();
        var (acceptedScope, command) = commandPort.Upserts[0];
        acceptedScope.Should().Be(Scope);
        command.OwnerScope.NyxUserId.Should().Be(Scope,
            "server must stamp owner_scope from the URL, ignoring whatever the client sent");
        command.OwnerScope.Platform.Should().Be(OwnerScope.NyxIdPlatform);
        command.DefaultTarget.ForwardToModel.ModelName.Should().Be("deepseek/deepseek-chat");
        command.Rules.Should().ContainSingle()
            .Which.Action.ForwardToModel.ModelName.Should().Be("anthropic/claude-sonnet-4-6");
    }

    [Fact]
    public async Task PutPolicy_RejectsBodyMissingDefaultTarget()
    {
        // ChatRoutePolicyGAgent.HandleUpsertAsync would throw on an empty
        // default_target; catch the error synchronously at the REST boundary
        // so operators see a 400 + reason instead of a silent fire-and-forget
        // dispatch that drops on the actor side.
        var commandPort = new RecordingChatRoutePolicyCommandPort();
        await using var app = await CreateAppAsync(commandPort);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/scopes/{Scope}/chat-route-policy")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("default_target_required");
        commandPort.Upserts.Should().BeEmpty(
            "REST validation must short-circuit before fire-and-forget dispatch when the body is invalid");
    }

    [Fact]
    public async Task PutRule_StampsOwnerScopeAndDispatchesRuleCommandToScopeActor()
    {
        var commandPort = new RecordingChatRoutePolicyCommandPort();
        await using var app = await CreateAppAsync(commandPort);
        var client = app.GetTestClient();

        var body = """
        {
          "owner_scope": { "nyx_user_id": "attacker" },
          "default_target_if_uninitialized": {
            "forward_to_model": { "model_name": "deepseek/deepseek-chat" }
          },
          "rule": {
            "rule_id": "body-rule",
            "priority": 100,
            "match": { "source_kind": "CHAT_SOURCE_KIND_VOICE" },
            "action": {
              "forward_to_model": {
                "tool_choice_hint": {
                  "voice_attach_target": {
                    "actor_id": "voice-agent",
                    "voice_module_name": "voice_presence_openai"
                  }
                }
              }
            },
            "description": "voice route"
          }
        }
        """;
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/scopes/{Scope}/chat-route-policy/rules/voice-demo")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        commandPort.Upserts.Should().BeEmpty();
        commandPort.RuleUpserts.Should().ContainSingle();
        var (acceptedScope, command) = commandPort.RuleUpserts[0];
        acceptedScope.Should().Be(Scope);
        command.OwnerScope.NyxUserId.Should().Be(Scope);
        command.OwnerScope.Platform.Should().Be(OwnerScope.NyxIdPlatform);
        command.DefaultTargetIfUninitialized.ForwardToModel.ModelName.Should().Be("deepseek/deepseek-chat");
        command.Rule.RuleId.Should().Be("voice-demo",
            "the path rule id is the command identity; body rule_id must not target a different rule");
        command.Rule.Match.SourceKind.Should().Be(ChatSourceKind.Voice);
        command.Rule.Action.ForwardToModel.ToolChoiceHint.VoiceAttachTarget.ActorId.Should().Be("voice-agent");
    }

    [Theory]
    [InlineData("%20%20", "{\"rule\": {\"action\": {\"forward_to_model\": {\"model_name\": \"model\"}}}}", "rule_id_required")]
    [InlineData("voice-demo", "", "empty_body")]
    [InlineData("voice-demo", "{", "invalid_body")]
    [InlineData("voice-demo", "{}", "rule_required")]
    public async Task PutRule_RejectsInvalidRequestWithoutDispatch(
        string routeRuleId,
        string bodyJson,
        string expectedError)
    {
        var commandPort = new RecordingChatRoutePolicyCommandPort();
        await using var app = await CreateAppAsync(commandPort);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/scopes/{Scope}/chat-route-policy/rules/{routeRuleId}")
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain(expectedError);
        commandPort.RuleUpserts.Should().BeEmpty(
            "REST validation must reject malformed rule upserts before admitting an actor command");
        commandPort.Upserts.Should().BeEmpty();
        commandPort.Removals.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteRule_DispatchesRemoveCommandWithTrimmedRuleId()
    {
        var commandPort = new RecordingChatRoutePolicyCommandPort();
        await using var app = await CreateAppAsync(commandPort);
        var client = app.GetTestClient();

        var response = await client.DeleteAsync($"/api/scopes/{Scope}/chat-route-policy/rules/  claude-for-responses  ");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        commandPort.Removals.Should().ContainSingle();
        var (_, command) = commandPort.Removals[0];
        command.RuleId.Should().Be("claude-for-responses");
    }

    [Fact]
    public async Task GetPolicy_ReturnsNotFoundWhenSnapshotMissing()
    {
        var commandPort = new RecordingChatRoutePolicyCommandPort();
        var queryPort = new StaticPolicyQueryPort(snapshot: null);
        await using var app = await CreateAppAsync(commandPort, queryPort);
        var client = app.GetTestClient();

        var response = await client.GetAsync($"/api/scopes/{Scope}/chat-route-policy");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().Contain("policy_not_found");
    }

    [Fact]
    public async Task GetPolicy_ReturnsProtobufJsonWithDefaultTargetAndRules()
    {
        var snapshot = new ChatRoutePolicySnapshot(
            new ChatRouteAction { ForwardToModel = new ForwardToModel { ModelName = "deepseek/deepseek-chat" } },
            [
                new ChatRouteRule
                {
                    RuleId = "rule-1", Priority = 50,
                    Match = new ChatRouteMatch { Channel = "lark" },
                    Action = GAgentToolHint("agent-x"),
                    Description = "test rule",
                },
            ]);
        var commandPort = new RecordingChatRoutePolicyCommandPort();
        var queryPort = new StaticPolicyQueryPort(snapshot);
        await using var app = await CreateAppAsync(commandPort, queryPort);
        var client = app.GetTestClient();

        var response = await client.GetAsync($"/api/scopes/{Scope}/chat-route-policy");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("\"defaultTarget\"");
        body.Should().Contain("deepseek/deepseek-chat");
        body.Should().Contain("\"ruleId\": \"rule-1\"");
        body.Should().Contain("aevatar_invoke_gagent");
        body.Should().Contain("agent-x");
    }

    [Fact]
    public void RequestPathSources_ShouldNotContainProjectionPrimingOutsideRefactorComments()
    {
        // Refactor (iter32/cluster-034-chat-route-policy-request-path-projection-activation):
        //   Old pattern: tests only proved endpoint business responses, not absence of projection priming calls.
        //   New principle: source-regression assertion locks request paths to dispatch-only behavior.
        var adminSource = StripLineComments(File.ReadAllText(GetSourcePath(
            "src",
            "Aevatar.Mainnet.Host.Api",
            "ChatRouting",
            "ChatRoutePolicyAdminEndpoints.cs")));

        adminSource.Should().NotContain("ChatRoutePolicyProjectionPort");
        adminSource.Should().NotContain("EnsureProjectionForActorAsync");
    }

    [Fact]
    public void MainnetHostEndpoints_ShouldNotInjectActorRuntimeOrDispatchPortOutsideRefactorComments()
    {
        var adminSource = StripLineComments(File.ReadAllText(GetSourcePath(
            "src",
            "Aevatar.Mainnet.Host.Api",
            "ChatRouting",
            "ChatRoutePolicyAdminEndpoints.cs")));

        adminSource.Should().NotContain("IActorRuntime");
        adminSource.Should().NotContain("IActorDispatchPort");
        adminSource.Should().NotContain("EventEnvelope");
        adminSource.Should().NotContain("CreateDirect");
    }

    [Fact]
    public void MainnetHost_ShouldNotKeepHardcodedVoiceDemoBootstrapSurface()
    {
        GetOptionalSourcePath("src", "Aevatar.Mainnet.Host.Api", "Voice", "VoiceDemoBootstrapEndpoints.cs")
            .Should()
            .BeNull();
        GetOptionalSourcePath("src", "Aevatar.Mainnet.Host.Api", "wwwroot", "demo", "voice", "index.html")
            .Should()
            .BeNull();

        var hostSource = File.ReadAllText(GetSourcePath(
            "src",
            "Aevatar.Mainnet.Host.Api",
            "Hosting",
            "MainnetHostBuilderExtensions.cs"));
        hostSource.Should().NotContain("MapVoiceDemoBootstrapEndpoints");
        hostSource.Should().NotContain("/api/demo/voice/bootstrap");
        hostSource.Should().NotContain("/demo/voice");
    }

    // ----- Test fixtures -------------------------------------------------------

    private static async Task<WebApplication> CreateAppAsync(
        RecordingChatRoutePolicyCommandPort commandPort,
        IChatRoutePolicyQueryPort? queryPort = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        // Scope guard reads Aevatar:Authentication:Enabled = false in Dev to
        // skip scope-claim cross-check.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aevatar:Authentication:Enabled"] = "false",
        });
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, AlwaysSucceedAuthHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IChatRoutePolicyCommandPort>(commandPort);
        builder.Services.AddSingleton(queryPort ?? new StaticPolicyQueryPort(snapshot: null));

        var app = builder.Build();
        app.MapChatRoutePolicyAdminEndpoints();
        await app.StartAsync();
        return app;
    }

    private static ChatRouteAction GAgentToolHint(string actorId) => new()
    {
        ForwardToModel = new ForwardToModel
        {
            ToolSetRef = new ChatRouteToolSetRef { Name = "workspace.default" },
            ToolChoiceHint = new ChatRouteToolChoiceHint
            {
                ToolName = "aevatar_invoke_gagent",
                PrefilledArguments = new Struct
                {
                    Fields =
                    {
                        ["actor_id"] = Google.Protobuf.WellKnownTypes.Value.ForString(actorId),
                    },
                },
            },
        },
    };

    private static string StripLineComments(string source) =>
        Regex.Replace(source, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);

    private static string GetSourcePath(params string[] relativePath)
    {
        var candidate = GetOptionalSourcePath(relativePath);
        if (candidate is not null)
            return candidate;

        throw new FileNotFoundException($"Could not locate {Path.Combine(relativePath)} from test output directory.");
    }

    private static string? GetOptionalSourcePath(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
            {
                directory = directory.Parent;
                continue;
            }

            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            return File.Exists(candidate) ? candidate : null;
        }

        return null;
    }

    private sealed class RecordingChatRoutePolicyCommandPort : IChatRoutePolicyCommandPort
    {
        public List<(string ScopeId, UpsertChatRoutePolicyRequested Command)> Upserts { get; } = [];
        public List<(string ScopeId, UpsertChatRouteRuleRequested Command)> RuleUpserts { get; } = [];
        public List<(string ScopeId, RemoveChatRouteRuleRequested Command)> Removals { get; } = [];

        public Task<ChatRoutePolicyCommandAcceptedReceipt> UpsertAsync(
            string scopeId,
            UpsertChatRoutePolicyRequested command,
            CancellationToken ct = default)
        {
            Upserts.Add((scopeId, command.Clone()));
            return Task.FromResult(new ChatRoutePolicyCommandAcceptedReceipt(
                $"chat-route-policy:{scopeId}",
                "accepted-upsert",
                "accepted-upsert"));
        }

        public Task<ChatRoutePolicyCommandAcceptedReceipt> UpsertRuleAsync(
            string scopeId,
            UpsertChatRouteRuleRequested command,
            CancellationToken ct = default)
        {
            RuleUpserts.Add((scopeId, command.Clone()));
            return Task.FromResult(new ChatRoutePolicyCommandAcceptedReceipt(
                $"chat-route-policy:{scopeId}",
                "accepted-rule-upsert",
                "accepted-rule-upsert"));
        }

        public Task<ChatRoutePolicyCommandAcceptedReceipt> RemoveRuleAsync(
            string scopeId,
            RemoveChatRouteRuleRequested command,
            CancellationToken ct = default)
        {
            Removals.Add((scopeId, command.Clone()));
            return Task.FromResult(new ChatRoutePolicyCommandAcceptedReceipt(
                $"chat-route-policy:{scopeId}",
                "accepted-remove",
                "accepted-remove"));
        }
    }

    private sealed class StaticPolicyQueryPort(ChatRoutePolicySnapshot? snapshot) : IChatRoutePolicyQueryPort
    {
        public Task<ChatRoutePolicySnapshot?> LookupForCallerAsync(
            OwnerScope callerScope, CancellationToken ct = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class AlwaysSucceedAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}
