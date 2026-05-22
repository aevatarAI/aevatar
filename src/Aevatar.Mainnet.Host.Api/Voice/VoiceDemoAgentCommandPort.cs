using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatRouting;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.Scheduled;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using RoutingOwnerScope = Aevatar.ChatRouting.Core.OwnerScope;
using ScheduledOwnerScope = Aevatar.GAgents.Scheduled.OwnerScope;

namespace Aevatar.Mainnet.Host.Api.Voice;

// Refactor (iter34/cluster-004-voice-bootstrap-application-port):
//   Old pattern: Voice bootstrap endpoint(VoiceDemoBootstrapEndpoints)同步等待 actor readiness/observation loop;POST 返回前阻塞读取 actor 状态;route-policy mutation 在 Host 内做。
//   New principle: Medium-B framing(reflector force-pick): 删除 POST readiness polling;移 voice bootstrap + voice-demo route mutation 到 Application/actor-owned typed command port;无新 bootstrap actor / 新 envelope / 新 projection phase / mandatory status endpoint / shared route-policy command port(留给可能后续 cluster)。POST 返回 honest accepted receipt + stable id;readiness 由 client 显式 readmodel query 获取(或事件 notification,无需 Host 内同步等)。
internal sealed class VoiceDemoAgentCommandPort
{
    private const string VoiceModuleName = "voice_presence_openai";
    private const string RouteRuleId = "voice-demo";
    private const string ChatRoutePolicyActorIdPrefix = "chat-route-policy:";
    private const string PublisherActorId = "voice-demo-bootstrap";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly IUserAgentCatalogCommandPort _catalogCommandPort;
    private readonly IChatRoutePolicyQueryPort _routePolicyQueryPort;

    public VoiceDemoAgentCommandPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        IUserAgentCatalogCommandPort catalogCommandPort,
        IChatRoutePolicyQueryPort routePolicyQueryPort)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
        _routePolicyQueryPort = routePolicyQueryPort ?? throw new ArgumentNullException(nameof(routePolicyQueryPort));
    }

    public async Task<VoiceDemoBootstrapReceipt> AcceptBootstrapAsync(
        VoiceDemoBootstrapCommand command,
        CancellationToken ct)
    {
        // Refactor (iter34/cluster-004-voice-bootstrap-application-port):
        //   Old pattern: Voice bootstrap endpoint(VoiceDemoBootstrapEndpoints)同步等待 actor readiness/observation loop;POST 返回前阻塞读取 actor 状态;route-policy mutation 在 Host 内做。
        //   New principle: Medium-B framing(reflector force-pick): 删除 POST readiness polling;移 voice bootstrap + voice-demo route mutation 到 Application/actor-owned typed command port;无新 bootstrap actor / 新 envelope / 新 projection phase / mandatory status endpoint / shared route-policy command port(留给可能后续 cluster)。POST 返回 honest accepted receipt + stable id;readiness 由 client 显式 readmodel query 获取(或事件 notification,无需 Host 内同步等)。
        ArgumentNullException.ThrowIfNull(command);
        var scopeId = command.ScopeId.Trim();
        var routingScope = RoutingOwnerScope.ForNyxIdNative(scopeId);
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
            routingScope,
            correlationId,
            ct);

        return new VoiceDemoBootstrapReceipt(
            actorId,
            routePolicyActorId,
            VoiceModuleName,
            RouteRuleId,
            correlationId,
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
        RoutingOwnerScope routingScope,
        string correlationId,
        CancellationToken ct)
    {
        var existing = await _routePolicyQueryPort.LookupForCallerAsync(routingScope, ct);
        var command = new UpsertChatRoutePolicyRequested
        {
            OwnerScope = new ChatRouteCallerScope
            {
                NyxUserId = scopeId,
                Platform = RoutingOwnerScope.NyxIdPlatform,
            },
            DefaultTarget = existing?.DefaultTarget.Clone() ?? ForwardToDemoActor(actorId),
        };

        if (existing is not null)
        {
            command.Rules.AddRange(existing.Rules
                .Where(static rule => !string.Equals(rule.RuleId, RouteRuleId, StringComparison.Ordinal))
                .Select(static rule => rule.Clone()));
        }

        command.Rules.Add(new ChatRouteRule
        {
            RuleId = RouteRuleId,
            Priority = 1000,
            Match = new ChatRouteMatch
            {
                SourceKind = ChatSourceKind.Voice,
            },
            Action = ForwardToDemoActor(actorId),
            Description = "route browser voice demo to the current user's mainnet agent",
        });

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
