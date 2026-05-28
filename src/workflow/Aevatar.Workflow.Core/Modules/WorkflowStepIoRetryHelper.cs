using Aevatar.Foundation.Abstractions.Connectors;

namespace Aevatar.Workflow.Core.Modules;

internal static class WorkflowStepIoRetryHelper
{
    // Refactor (iter110/cluster-1): Old pattern: connector_call retry and timeout loops ran inside module/actor turns.  New principle: bounded executors run connector IO loops and return typed connector continuation results.
    public static async Task<ConnectorCallExecutionOutcome> ExecuteConnectorAsync(
        IConnector connector,
        ConnectorRequest request,
        int retryCount,
        int timeoutMs,
        Action<Exception?, int, int, string?>? onRetryFailure,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(request);

        var attempts = Math.Max(1, retryCount + 1);
        ConnectorResponse? response = null;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

            try
            {
                response = await connector.ExecuteAsync(request, timeoutCts.Token);
                if (response.Success)
                    break;

                lastError = new InvalidOperationException(response.Error);
                if (attempt < attempts)
                    onRetryFailure?.Invoke(null, attempt, attempts, response.Error);
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < attempts)
                    onRetryFailure?.Invoke(ex, attempt, attempts, null);
            }
        }

        return new ConnectorCallExecutionOutcome(attempts, response, lastError);
    }
}

internal sealed record ConnectorCallExecutionOutcome(
    int Attempts,
    ConnectorResponse? Response,
    Exception? LastError);
