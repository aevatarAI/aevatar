using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.ExternalLinks;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Extensions.Bridge;

// Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
//   Old pattern: TelegramBridgeGAgent.ExecuteConnectorWithWatchdogAsync 用 Task.Delay 兜底超时 + ContinueWith race + actor turn 内同步 await /getUpdates 长轮询
//   New principle: 复用现有 ExternalLink actor-owned stream pattern(reflector force-pick):TelegramWaitReplyGAgent 实现 IExternalLinkAware + 加 TelegramGetUpdatesExternalLinkTransport;/getUpdates 走 IExternalLinkPort.SendAsync,result 经 ExternalLinkMessageReceivedEvent 回 actor;删 ExecuteConnectorWithWatchdogAsync/Task.Delay/ContinueWith race。**不新增 actor 类型**
public sealed class TelegramGetUpdatesExternalLinkTransport(
    IConnectorRegistry connectorRegistry,
    ILogger<TelegramGetUpdatesExternalLinkTransport> logger) : IExternalLinkTransport
{
    public const string TransportTypeName = "telegram-get-updates";

    public string TransportType => TransportTypeName;

    public Func<ReadOnlyMemory<byte>, CancellationToken, Task>? OnMessageReceived { private get; set; }
    public Func<ExternalLinkStateChange, string?, CancellationToken, Task>? OnStateChanged { private get; set; }

    public async Task ConnectAsync(ExternalLinkDescriptor descriptor, CancellationToken ct)
    {
        _ = descriptor;
        await NotifyStateChangedAsync(ExternalLinkStateChange.Connected, null, ct);
    }

    // refactor helper, no behavior change: bridges connector completion back to the existing ExternalLink callback path.
    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var request = TelegramGetUpdatesRequest.Parser.ParseFrom(payload.Span);
        var connectorTask = StartConnectorExecution(request);
        if (connectorTask.IsCompleted)
            return PublishCompletedConnectorTaskAsync(request, connectorTask, CancellationToken.None);

        _ = AwaitAndPublishAsync(request, connectorTask, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        await NotifyStateChangedAsync(ExternalLinkStateChange.Closed, "closed", ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<ConnectorResponse> StartConnectorExecution(TelegramGetUpdatesRequest request)
    {
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

    private async Task AwaitAndPublishAsync(
        TelegramGetUpdatesRequest request,
        Task<ConnectorResponse> connectorTask,
        CancellationToken ct)
    {
        await PublishCompletedConnectorTaskAsync(request, connectorTask, ct);
    }

    private async Task PublishCompletedConnectorTaskAsync(
        TelegramGetUpdatesRequest request,
        Task<ConnectorResponse> connectorTask,
        CancellationToken ct)
    {
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

        if (OnMessageReceived != null)
            await OnMessageReceived(result.ToByteArray(), ct);
    }

    private static ConnectorRequest BuildConnectorRequest(TelegramGetUpdatesRequest request)
    {
        return new ConnectorRequest
        {
            RunId = request.RunId,
            StepId = request.StepId,
            Connector = request.ConnectorName,
            Operation = "/getUpdates",
            Payload = request.Payload,
            Parameters = new Dictionary<string, string>(request.Parameters, StringComparer.OrdinalIgnoreCase),
        };
    }

    private static TelegramGetUpdatesResult BuildResult(TelegramGetUpdatesRequest request, ConnectorResponse response)
    {
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

    private Task NotifyStateChangedAsync(ExternalLinkStateChange state, string? reason, CancellationToken ct) =>
        OnStateChanged?.Invoke(state, reason, ct) ?? Task.CompletedTask;
}

// refactor helper, no behavior change
public sealed class TelegramGetUpdatesExternalLinkTransportFactory(
    IConnectorRegistry connectorRegistry,
    ILogger<TelegramGetUpdatesExternalLinkTransport> logger) : IExternalLinkTransportFactory
{
    public bool CanCreate(string transportType) =>
        string.Equals(transportType, TelegramGetUpdatesExternalLinkTransport.TransportTypeName, StringComparison.OrdinalIgnoreCase);

    public IExternalLinkTransport Create() =>
        new TelegramGetUpdatesExternalLinkTransport(connectorRegistry, logger);
}
