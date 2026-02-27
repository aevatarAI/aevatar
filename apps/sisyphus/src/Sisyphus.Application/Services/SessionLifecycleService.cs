using System.Collections.Concurrent;
using Sisyphus.Application.Models;

namespace Sisyphus.Application.Services;

public sealed class SessionLifecycleService(GraphIdProvider graphIdProvider)
{
    private readonly ConcurrentDictionary<Guid, ResearchSession> _sessions = new();

    public async Task<ResearchSession> CreateSessionAsync(
        string topic, int maxRounds = 20, CancellationToken ct = default)
    {
        var readGraphId = await graphIdProvider.WaitReadAsync(ct);
        var writeGraphId = await graphIdProvider.WaitWriteAsync(ct);

        var session = new ResearchSession
        {
            Topic = topic,
            MaxRounds = maxRounds,
            ReadGraphId = readGraphId,
            WriteGraphId = writeGraphId,
        };

        _sessions[session.Id] = session;
        return session;
    }

    public IReadOnlyList<ResearchSession> ListSessions() =>
        _sessions.Values.ToList();

    public ResearchSession? GetSession(Guid id) =>
        _sessions.GetValueOrDefault(id);

    public bool TryStartSession(Guid id)
    {
        var session = GetSession(id);
        if (session is null) return false;
        lock (session)
        {
            if (session.Status == SessionStatus.Running)
                return false;
            session.Status = SessionStatus.Running;
            session.StartedAt = DateTime.UtcNow;
            return true;
        }
    }

    public Task<bool> DeleteSessionAsync(Guid id, CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult(_sessions.TryRemove(id, out _));
    }
}
