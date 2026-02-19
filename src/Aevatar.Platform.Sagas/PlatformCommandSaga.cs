using Aevatar.Platform.Sagas.States;

namespace Aevatar.Platform.Sagas;

public sealed class PlatformCommandSaga : SagaBase<PlatformCommandSagaState>
{
    public override string Name => PlatformCommandSagaNames.CommandLifecycle;

    public override ValueTask<bool> CanHandleAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        _ = ct;
        return ValueTask.FromResult(PlatformCommandSagaPayload.TryParse(envelope, out _));
    }

    public override ValueTask<bool> CanStartAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        _ = ct;
        if (!PlatformCommandSagaPayload.TryParse(envelope, out var signal))
            return ValueTask.FromResult(false);

        var canStart = signal != null &&
                       !string.IsNullOrWhiteSpace(signal.CommandId) &&
                       !string.IsNullOrWhiteSpace(envelope.CorrelationId) &&
                       string.Equals(signal.State, "Accepted", StringComparison.OrdinalIgnoreCase);
        return ValueTask.FromResult(canStart);
    }

    protected override PlatformCommandSagaState CreateState(string correlationId, EventEnvelope envelope)
    {
        var state = base.CreateState(correlationId, envelope);
        Apply(state, envelope);
        return state;
    }

    protected override ValueTask HandleAsync(
        PlatformCommandSagaState state,
        EventEnvelope envelope,
        ISagaActionSink actions,
        CancellationToken ct = default)
    {
        _ = ct;
        Apply(state, envelope);

        if (string.Equals(state.State, "Completed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state.State, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            actions.MarkCompleted();
        }

        return ValueTask.CompletedTask;
    }

    private static void Apply(PlatformCommandSagaState state, EventEnvelope envelope)
    {
        if (!PlatformCommandSagaPayload.TryParse(envelope, out var signal))
            return;
        if (signal == null)
            return;

        state.CommandId = signal.CommandId;
        state.Subsystem = signal.Subsystem;
        state.Command = signal.Command;
        state.Method = signal.Method;
        state.TargetEndpoint = signal.TargetEndpoint;
        state.State = signal.State;
        state.Succeeded = signal.Succeeded;
        state.ResponseStatusCode = signal.ResponseStatusCode;
        state.ResponseContentType = signal.ResponseContentType;
        state.ResponseBody = signal.ResponseBody;
        state.Error = signal.Error;
        state.AcceptedAt = signal.AcceptedAt;
        state.UpdatedAt = signal.UpdatedAt == default ? DateTimeOffset.UtcNow : signal.UpdatedAt;
    }
}
