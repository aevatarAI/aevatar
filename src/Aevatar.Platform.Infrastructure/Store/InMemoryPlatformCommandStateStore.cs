using Aevatar.Platform.Application.Abstractions.Commands;
using Aevatar.Platform.Application.Abstractions.Ports;
using System.Collections.Concurrent;

namespace Aevatar.Platform.Infrastructure.Store;

public sealed class InMemoryPlatformCommandStateStore : IPlatformCommandStateStore
{
    private readonly ConcurrentDictionary<string, PlatformCommandStatus> _states = new(StringComparer.Ordinal);

    public Task UpsertAsync(PlatformCommandStatus status, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _states[status.CommandId] = Clone(status);
        return Task.CompletedTask;
    }

    public Task<PlatformCommandStatus?> GetAsync(string commandId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_states.TryGetValue(commandId, out var status) ? Clone(status) : null);
    }

    public Task<IReadOnlyList<PlatformCommandStatus>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var boundedTake = Math.Clamp(take, 1, 500);
        var items = _states.Values
            .OrderByDescending(x => x.UpdatedAt)
            .Take(boundedTake)
            .Select(Clone)
            .ToList();
        return Task.FromResult<IReadOnlyList<PlatformCommandStatus>>(items);
    }

    private static PlatformCommandStatus Clone(PlatformCommandStatus source) => new()
    {
        CommandId = source.CommandId,
        Subsystem = source.Subsystem,
        Command = source.Command,
        Method = source.Method,
        TargetEndpoint = source.TargetEndpoint,
        State = source.State,
        Succeeded = source.Succeeded,
        ResponseStatusCode = source.ResponseStatusCode,
        ResponseContentType = source.ResponseContentType,
        ResponseBody = source.ResponseBody,
        Error = source.Error,
        AcceptedAt = source.AcceptedAt,
        UpdatedAt = source.UpdatedAt,
    };
}
