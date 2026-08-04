using System.Collections.Concurrent;
using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Persistence;

/// <summary>File-backed Protobuf persistence for committed-state publication progress.</summary>
public sealed class FileCommittedStatePublicationStateStore
    : ICommittedStatePublicationStateStore
{
    private readonly string _rootDirectory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _agentLocks =
        new(StringComparer.Ordinal);

    public FileCommittedStatePublicationStateStore(FileEventStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.RootDirectory))
            throw new InvalidOperationException("File publication state store requires a non-empty root directory.");

        _rootDirectory = Path.GetFullPath(options.RootDirectory);
        Directory.CreateDirectory(_rootDirectory);
    }

    public Task<CommittedStatePublicationState?> LoadAsync(
        string actorId,
        CancellationToken ct = default) =>
        WithLockAsync(actorId, ct, () => Task.FromResult(Read(actorId)));

    public Task<CommittedStatePublicationState> InitializeAsync(
        string actorId,
        long baselinePublishedVersion,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baselinePublishedVersion);
        return WithLockAsync(actorId, ct, () =>
        {
            var state = Read(actorId);
            if (state != null)
                return Task.FromResult(state);

            state = InMemoryCommittedStatePublicationStateStore.NewState(
                actorId,
                baselinePublishedVersion);
            Write(actorId, state);
            return Task.FromResult(state.Clone());
        });
    }

    public Task<CommittedStatePublicationState> AdvanceAsync(
        string actorId,
        long expectedPublishedVersion,
        StateEvent publishedEvent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(publishedEvent);
        return WithLockAsync(actorId, ct, () =>
        {
            var state = GetInitialized(actorId);
            InMemoryCommittedStatePublicationStateStore.EnsureExpectedVersion(
                actorId,
                expectedPublishedVersion,
                state.PublishedVersion);
            InMemoryCommittedStatePublicationStateStore.EnsureNextEvent(
                actorId,
                expectedPublishedVersion,
                publishedEvent);
            state.PublishedVersion = publishedEvent.Version;
            state.PublishedEventId = publishedEvent.EventId;
            state.Revision++;
            state.UpdatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow);
            state.Failure = null;
            Write(actorId, state);
            return Task.FromResult(state.Clone());
        });
    }

    public Task<CommittedStatePublicationState> RecordFailureAsync(
        string actorId,
        long expectedPublishedVersion,
        StateEvent failedEvent,
        CommittedStatePublicationFailureStage stage,
        Exception error,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(failedEvent);
        ArgumentNullException.ThrowIfNull(error);
        return WithLockAsync(actorId, ct, () =>
        {
            var state = GetInitialized(actorId);
            InMemoryCommittedStatePublicationStateStore.EnsureExpectedVersion(
                actorId,
                expectedPublishedVersion,
                state.PublishedVersion);
            var previousAttempts = state.Failure?.Version == failedEvent.Version
                && string.Equals(state.Failure.EventId, failedEvent.EventId, StringComparison.Ordinal)
                    ? state.Failure.Attempts
                    : 0;
            state.Failure = InMemoryCommittedStatePublicationStateStore.BuildFailure(
                failedEvent,
                stage,
                error,
                previousAttempts + 1);
            state.Revision++;
            state.UpdatedAt = state.Failure.LastFailedAt;
            Write(actorId, state);
            return Task.FromResult(state.Clone());
        });
    }

    private async Task<T> WithLockAsync<T>(
        string actorId,
        CancellationToken ct,
        Func<Task<T>> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
        var gate = _agentLocks.GetOrAdd(actorId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }

    private CommittedStatePublicationState GetInitialized(string actorId)
    {
        var state = Read(actorId);
        if (state?.Initialized == true)
            return state;

        throw new InvalidOperationException(
            $"Committed-state publication checkpoint for actor '{actorId}' is not initialized.");
    }

    private CommittedStatePublicationState? Read(string actorId)
    {
        var path = GetPath(actorId);
        return File.Exists(path)
            ? CommittedStatePublicationState.Parser.ParseFrom(File.ReadAllBytes(path))
            : null;
    }

    private void Write(string actorId, CommittedStatePublicationState state)
    {
        var path = GetPath(actorId);
        var temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, state.ToByteArray());
        File.Move(temporaryPath, path, overwrite: true);
    }

    private string GetPath(string actorId)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(actorId))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return Path.Combine(_rootDirectory, encoded + ".publication-state.pb");
    }
}
