using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatRouting;
using Aevatar.GAgents.Scheduled;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using RoutingOwnerScope = Aevatar.ChatRouting.Core.OwnerScope;
using ScheduledOwnerScope = Aevatar.GAgents.Scheduled.OwnerScope;

namespace Aevatar.GAgents.NyxidChat.Voice;

// Refactor (iter34/cluster-004-voice-bootstrap-application-port):
//   Old pattern: Voice demo bootstrap lived in the Host endpoint, polled read-side readiness before returning, and mutated route policy from API code.
//   New principle: The NyxID chat module owns the typed bootstrap command port; Host/API only adapts HTTP to an accepted command receipt, while readiness remains an explicit readmodel/event concern.
public sealed class VoiceDemoAgentCommandPort
{
    private const string VoiceModuleName = "voice_presence_openai";
    private const string RouteRuleId = "voice-demo";
    private const string ChatRoutePolicyActorIdPrefix = "chat-route-policy:";
    private const string PublisherActorId = "voice-demo-bootstrap";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly IUserAgentCatalogCommandPort _catalogCommandPort;

    public VoiceDemoAgentCommandPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        IUserAgentCatalogCommandPort catalogCommandPort)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
    }

    // Refactor (iter34/cluster-004-voice-bootstrap-application-port):
    //   Old pattern: POST /api/demo/voice/bootstrap synchronously waited for catalog, route, and voice-session readiness.
    //   New principle: AcceptBootstrapAsync dispatches the actor-owned commands and returns stable command ids; callers observe completion through readmodels or events.
    public async Task<VoiceDemoBootstrapReceipt> AcceptBootstrapAsync(
        VoiceDemoBootstrapCommand command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var scopeId = command.ScopeId.Trim();
        var scheduledScope = ScheduledOwnerScope.ForNyxIdNative(scopeId);
        var actorId = BuildDemoActorId(scopeId);
        var routePolicyActorId = $"{ChatRoutePolicyActorIdPrefix}{scopeId}";
        var correlationId = Guid.NewGuid().ToString("N");

        var agentCommandId = await EnsureDemoAgentAsync(actorId, correlationId, ct);
        await _catalogCommandPort.UpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = actorId,
            AgentType = NyxIdChatServiceDefaults.GAgentTypeName,
            TemplateName = "voice-demo",
            OwnerScope = scheduledScope.Clone(),
        }, ct);

        var routePolicyCommandId = await EnsureVoiceRoutePolicyAsync(
            routePolicyActorId,
            scopeId,
            actorId,
            correlationId,
            ct);

        return new VoiceDemoBootstrapReceipt(
            actorId,
            routePolicyActorId,
            VoiceModuleName,
            RouteRuleId,
            correlationId,
            agentCommandId,
            routePolicyCommandId);
    }

    private async Task<string> EnsureDemoAgentAsync(
        string actorId,
        string correlationId,
        CancellationToken ct)
    {
        var actor = await _actorRuntime.CreateAsync<NyxIdChatGAgent>(actorId, ct);
        var initialize = new InitializeRoleAgentEvent
        {
            RoleId = "voice-demo",
            RoleName = "Voice Demo Agent",
            ProviderName = NyxIdChatServiceDefaults.ProviderName,
            SystemPrompt = "You are the Aevatar voice demo agent. Reply conversationally and keep spoken answers concise.",
            MaxHistoryMessages = 16,
            EventModules = VoiceModuleName,
        };

        return await DispatchAsync(actor.Id, initialize, correlationId, ct);
    }

    private async Task<string> EnsureVoiceRoutePolicyAsync(
        string routePolicyActorId,
        string scopeId,
        string actorId,
        string correlationId,
        CancellationToken ct)
    {
        var command = new UpsertChatRouteRuleRequested
        {
            OwnerScope = new ChatRouteCallerScope
            {
                NyxUserId = scopeId,
                Platform = RoutingOwnerScope.NyxIdPlatform,
            },
            DefaultTargetIfUninitialized = ForwardToDemoActor(actorId),
            Rule = new ChatRouteRule
            {
                RuleId = RouteRuleId,
                Priority = 1000,
                Match = new ChatRouteMatch
                {
                    SourceKind = ChatSourceKind.Voice,
                },
                Action = ForwardToDemoActor(actorId),
                Description = "route browser voice demo to the current user's mainnet agent",
            },
        };

        var actor = await _actorRuntime.CreateAsync<ChatRoutePolicyGAgent>(routePolicyActorId, ct);
        return await DispatchAsync(actor.Id, command, correlationId, ct);
    }

    private static ChatRouteAction ForwardToDemoActor(string actorId) =>
        new()
        {
            ForwardToGagent = new ForwardToGAgent
            {
                ActorId = actorId,
                VoiceModuleName = VoiceModuleName,
            },
        };

    private async Task<string> DispatchAsync(
        string actorId,
        IMessage command,
        string correlationId,
        CancellationToken ct)
    {
        var commandId = Guid.NewGuid().ToString("N");
        var envelope = new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, actorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId,
            },
            Runtime = new EnvelopeRuntime
            {
                Deduplication = new DeliveryDeduplication
                {
                    OperationId = commandId,
                },
            },
        };

        await _actorDispatchPort.DispatchAsync(actorId, envelope, ct);
        return commandId;
    }

    private static string BuildDemoActorId(string scopeId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(scopeId.Trim()));
        var hash = Convert.ToHexString(bytes)[..16].ToLowerInvariant();
        return $"{NyxIdChatServiceDefaults.ActorIdPrefix}-voice-demo-{hash}";
    }
}

// Refactor (iter34/cluster-004-voice-bootstrap-application-port):
//   Old pattern: The Host endpoint accepted raw HTTP state and built actor commands inline.
//   New principle: A typed command captures the stable voice bootstrap input owned by the NyxID chat module.
public sealed record VoiceDemoBootstrapCommand(string ScopeId);

// Refactor (iter34/cluster-004-voice-bootstrap-application-port):
//   Old pattern: The bootstrap response implied synchronous readiness after polling readmodels.
//   New principle: The receipt only reports accepted dispatch ids and correlation data; completion is observed asynchronously.
public sealed record VoiceDemoBootstrapReceipt(
    string ActorId,
    string RoutePolicyActorId,
    string VoiceModuleName,
    string PolicyRuleId,
    string CorrelationId,
    string AgentCommandId,
    string RoutePolicyCommandId);
