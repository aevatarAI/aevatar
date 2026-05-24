using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.ExternalLinks;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Extensions.Bridge;

// Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
//   Old pattern: TelegramBridgeGAgent.ExecuteConnectorWithWatchdogAsync 用 Task.Delay 兜底超时 + ContinueWith race + actor turn 内同步 await /getUpdates 长轮询
//   New principle: TelegramWaitReplyGAgent owns /getUpdates polling through the existing ExternalLink stream; it sends getUpdates requests via IExternalLinkPort and handles ExternalLinkMessageReceivedEvent continuations, so long polling no longer blocks an actor turn and no new actor type is introduced.
public sealed class TelegramGetUpdatesExternalLinkTransport(
    IConnectorRegistry connectorRegistry,
    ILogger<TelegramGetUpdatesExternalLinkTransport> logger) : IExternalLinkTransport
{
    public const string TransportTypeName = "telegram-get-updates";

    public string TransportType => TransportTypeName;

    // Refactor (iter56/cluster-912-external-link-signal-contract):
    // old=transport direct callback, new=typed signal sink.
    // This transport emits typed ExternalLink signals instead of arbitrary delegates.
    // The owning actor/module turn decides how to process each signal.
    public IExternalLinkSignalSink? SignalSink { private get; set; }

    public async Task ConnectAsync(ExternalLinkDescriptor descriptor, CancellationToken ct)
    {
        // Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
        //   Old pattern: Telegram wait reply had no actor-owned ExternalLink session for /getUpdates.
        //   New principle: transport participates in the existing ExternalLink lifecycle and reports readiness.
        _ = descriptor;
        await NotifyStateChangedAsync(ExternalLinkStateChange.Connected, null, ct);
    }

    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        // Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
        //   Old pattern: /getUpdates connector execution could block the Telegram wait actor turn.
        //   New principle: SendAsync starts the connector and publishes completion back through callbacks.
        var request = TelegramGetUpdatesRequest.Parser.ParseFrom(payload.Span);
        var connectorTask = StartConnectorExecution(request);
        if (connectorTask.IsCompleted)
            return PublishCompletedConnectorTaskAsync(request, connectorTask, CancellationToken.None);

        // Long polling must finish outside the actor turn; the result re-enters through ExternalLink callbacks.
        _ = PublishCompletedConnectorTaskAsync(request, connectorTask, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        await NotifyStateChangedAsync(ExternalLinkStateChange.Closed, "closed", ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<ConnectorResponse> StartConnectorExecution(TelegramGetUpdatesRequest request)
    {
        // Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
        //   Old pattern: TelegramBridgeGAgent owned connector lookup and watchdog execution inline.
        //   New principle: transport adapts the existing connector as replaceable ExternalLink I/O.
        if (!connectorRegistry.TryGet(request.ConnectorName, out var connector) || connector == null)
            return Task.FromResult(new ConnectorResponse
            {
                Success = false,
                Error = $"telegram connector '{request.ConnectorName}' not found",
            });

        var connectorRequest = BuildConnectorRequest(request);
        try
        {
            return connector.ExecuteAsync(connectorRequest, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ConnectorResponse
            {
                Success = false,
                Error = $"telegram getUpdates execution failed: {ex.Message}",
            });
        }
    }

    private async Task PublishCompletedConnectorTaskAsync(
        TelegramGetUpdatesRequest request,
        Task<ConnectorResponse> connectorTask,
        CancellationToken ct)
    {
        // Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
        //   Old pattern: connector result raced in-turn watchdog code.
        //   New principle: every result is serialized as a typed TelegramGetUpdatesResult callback.
        TelegramGetUpdatesResult result;
        try
        {
            result = BuildResult(request, await connectorTask);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telegram getUpdates external-link execution failed for request {RequestId}", request.RequestId);
            result = new TelegramGetUpdatesResult
            {
                CommandId = request.CommandId,
                Generation = request.Generation,
                RequestId = request.RequestId,
                Bootstrap = request.Bootstrap,
                Success = false,
                Error = $"telegram getUpdates execution failed: {ex.Message}",
            };
            if (request.HasRequestedOffset)
                result.RequestedOffset = request.RequestedOffset;
        }

        // Refactor (iter56/cluster-912-external-link-signal-contract):
        // old=transport direct callback, new=typed signal sink.
        // Connector completion is serialized as an internal message signal.
        // ExternalLinkManager decides the observable event in the actor turn.
        if (SignalSink != null)
        {
            await SignalSink.PublishMessageReceivedAsync(
                new ExternalLinkMessageReceivedSignal
                {
                    RawPayload = Google.Protobuf.ByteString.CopyFrom(result.ToByteArray()),
                    ReceivedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
                },
                ct);
        }
    }

    private static ConnectorRequest BuildConnectorRequest(TelegramGetUpdatesRequest request)
    {
        // Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
        //   Old pattern: wait actor assembled Telegram connector calls inside the actor turn.
        //   New principle: transport maps typed getUpdates requests to the connector boundary.
        var parameters = new Dictionary<string, string>(request.Parameters, StringComparer.OrdinalIgnoreCase)
        {
            ["method"] = string.IsNullOrWhiteSpace(request.HttpMethod) ? "POST" : request.HttpMethod,
            ["content_type"] = string.IsNullOrWhiteSpace(request.ContentType) ? "application/json" : request.ContentType,
            ["timeout_ms"] = Math.Max(1, request.PerCallTimeoutMs).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        return new ConnectorRequest
        {
            RunId = request.RunId,
            StepId = request.StepId,
            Connector = request.ConnectorName,
            Operation = "/getUpdates",
            Payload = BuildGetUpdatesPayload(request),
            Parameters = parameters,
        };
    }

    private static string BuildGetUpdatesPayload(TelegramGetUpdatesRequest request)
    {
        // Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
        //   Old pattern: /getUpdates payload construction was coupled to actor polling control flow.
        //   New principle: transport owns Telegram HTTP payload adaptation from the typed request.
        var payload = new Dictionary<string, object?>
        {
            ["timeout"] = Math.Max(0, request.PollTimeoutSeconds),
            ["allowed_updates"] = request.AllowedUpdates.Count > 0
                ? request.AllowedUpdates.ToArray()
                : ["message", "channel_post"],
        };
        if (request.HasRequestedOffset && request.RequestedOffset >= 0)
            payload["offset"] = request.RequestedOffset;

        return JsonSerializer.Serialize(payload);
    }

    private static TelegramGetUpdatesResult BuildResult(TelegramGetUpdatesRequest request, ConnectorResponse response)
    {
        // Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
        //   Old pattern: connector response was consumed directly by the blocked actor call stack.
        //   New principle: response becomes a typed callback that carries command/generation/request identity.
        var result = new TelegramGetUpdatesResult
        {
            CommandId = request.CommandId,
            Generation = request.Generation,
            RequestId = request.RequestId,
            Bootstrap = request.Bootstrap,
            Success = response.Success,
            Output = response.Output ?? string.Empty,
            Error = response.Error ?? string.Empty,
        };
        if (request.HasRequestedOffset)
            result.RequestedOffset = request.RequestedOffset;
        return result;
    }

    // Refactor (iter56/cluster-912-external-link-signal-contract):
    // old=transport direct callback, new=typed signal sink.
    // Lifecycle changes leave this transport as typed state signals.
    // No caller callback is invoked from transport execution.
    private Task NotifyStateChangedAsync(ExternalLinkStateChange state, string? reason, CancellationToken ct) =>
        SignalSink?.PublishStateChangedAsync(
            new ExternalLinkTransportStateChangedSignal
            {
                State = ToSignalKind(state),
                Reason = reason ?? string.Empty,
            },
            ct) ?? Task.CompletedTask;

    private static ExternalLinkTransportStateSignalKind ToSignalKind(ExternalLinkStateChange state) =>
        state switch
        {
            ExternalLinkStateChange.Connected => ExternalLinkTransportStateSignalKind.Connected,
            ExternalLinkStateChange.Disconnected => ExternalLinkTransportStateSignalKind.Disconnected,
            ExternalLinkStateChange.Error => ExternalLinkTransportStateSignalKind.Error,
            ExternalLinkStateChange.Closed => ExternalLinkTransportStateSignalKind.Closed,
            _ => ExternalLinkTransportStateSignalKind.Unspecified,
        };
}

// Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
//   Old pattern: TelegramBridgeGAgent.ExecuteConnectorWithWatchdogAsync 用 Task.Delay 兜底超时 + ContinueWith race + actor turn 内同步 await /getUpdates 长轮询
//   New principle: TelegramWaitReplyGAgent owns /getUpdates polling through the existing ExternalLink stream; it sends getUpdates requests via IExternalLinkPort and handles ExternalLinkMessageReceivedEvent continuations, so long polling no longer blocks an actor turn and no new actor type is introduced.
public sealed class TelegramGetUpdatesExternalLinkTransportFactory(
    IConnectorRegistry connectorRegistry,
    ILogger<TelegramGetUpdatesExternalLinkTransport> logger) : IExternalLinkTransportFactory
{
    public bool CanCreate(string transportType) =>
        string.Equals(transportType, TelegramGetUpdatesExternalLinkTransport.TransportTypeName, StringComparison.OrdinalIgnoreCase);

    public IExternalLinkTransport Create()
    {
        // Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
        //   Old pattern: wait-reply /getUpdates used no ExternalLink transport factory.
        //   New principle: DI creates the Telegram getUpdates transport through the standard factory contract.
        return new TelegramGetUpdatesExternalLinkTransport(connectorRegistry, logger);
    }
}
