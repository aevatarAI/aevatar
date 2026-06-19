using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Application.Responses;

public sealed class LlmRunExecutionScheduler(
    ILlmRunExecutionTargetProvisioner executionTargetProvisioner,
    IActorDispatchPort dispatchPort) : ILlmRunExecutionScheduler
{
    private const string PublisherId = "gagent-service.llm-run-executor";

    public async ValueTask ScheduleAsync(
        LlmRunExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Command);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResponseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);

        var executionActorId = await executionTargetProvisioner
            .EnsureExecutionTargetAsync(request, ct)
            .ConfigureAwait(false);
        var responseId = request.ResponseId.Trim();
        var runId = request.RunId.Trim();
        var envelope = new EventEnvelope
        {
            Id = $"execute-{responseId}-{runId}",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(new ExecuteLlmRunRequested
            {
                SessionActorId = request.SessionActorId.Trim(),
                ResponseId = responseId,
                RunId = runId,
                Command = request.Command.Clone(),
                OriginPlatform = request.OriginPlatform ?? string.Empty,
            }),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherId, executionActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = responseId,
            },
        };
        await dispatchPort.DispatchAsync(executionActorId, envelope, ct).ConfigureAwait(false);
    }
}
