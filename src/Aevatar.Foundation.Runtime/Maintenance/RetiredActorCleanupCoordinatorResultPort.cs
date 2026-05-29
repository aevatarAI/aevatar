using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Maintenance;

// Refactor (issue1056/impl): Old pattern: hosted-service EventStore marker replay/write. New principle: actor-owned cleanup lease via IActorDispatchPort + EventEnvelope + narrow command-result contract (Phase 9 r6 consensus).
public sealed class RetiredActorCleanupCoordinatorResultPort : IRetiredActorCleanupCoordinatorResultPort
{
    private readonly IStreamProvider _streamProvider;

    public RetiredActorCleanupCoordinatorResultPort(IStreamProvider streamProvider)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
    }

    public string CreateResultStreamId(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        return $"retired-actor-cleanup.result:{commandId.Trim()}";
    }

    public async Task<T> AwaitResultAsync<T>(string commandId, string resultStreamId, CancellationToken ct)
        where T : class, IMessage<T>, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultStreamId);
        ct.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await _streamProvider
            .GetStream(resultStreamId.Trim())
            .SubscribeAsync<RetiredActorCleanupCoordinatorCommandResult>(result =>
            {
                var typed = ExtractResult<T>(result);
                if (typed != null && string.Equals(ResolveCommandId(typed), commandId, StringComparison.Ordinal))
                    completion.TrySetResult(typed);

                return Task.CompletedTask;
            }, ct)
            .ConfigureAwait(false);

        await using var registration = ct.UnsafeRegister(
            static state => ((TaskCompletionSource<T>)state!).TrySetCanceled(),
            completion);

        return await completion.Task.ConfigureAwait(false);
    }

    public Task PublishAsync(
        string resultStreamId,
        RetiredActorCleanupCoordinatorCommandResult result,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultStreamId);
        ArgumentNullException.ThrowIfNull(result);
        ct.ThrowIfCancellationRequested();

        return _streamProvider.GetStream(resultStreamId.Trim()).ProduceAsync(result, ct);
    }

    private static T? ExtractResult<T>(RetiredActorCleanupCoordinatorCommandResult result)
        where T : class, IMessage<T>, new()
    {
        IMessage? payload = result.ResultCase switch
        {
            RetiredActorCleanupCoordinatorCommandResult.ResultOneofCase.AcquireLease => result.AcquireLease,
            RetiredActorCleanupCoordinatorCommandResult.ResultOneofCase.CheckLease => result.CheckLease,
            RetiredActorCleanupCoordinatorCommandResult.ResultOneofCase.ReleaseLease => result.ReleaseLease,
            RetiredActorCleanupCoordinatorCommandResult.ResultOneofCase.RecordFailure => result.RecordFailure,
            _ => null,
        };

        return payload as T;
    }

    private static string ResolveCommandId(IMessage message) =>
        message switch
        {
            RetiredActorCleanupAcquireLeaseResult result => result.CommandId,
            RetiredActorCleanupCheckLeaseResult result => result.CommandId,
            RetiredActorCleanupReleaseLeaseResult result => result.CommandId,
            RetiredActorCleanupRecordFailureResult result => result.CommandId,
            _ => string.Empty,
        };
}
