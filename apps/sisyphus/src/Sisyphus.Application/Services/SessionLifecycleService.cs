using System.Collections.Concurrent;
using Sisyphus.Application.Models;

namespace Sisyphus.Application.Services;

public sealed class SessionLifecycleService(ChronoGraphClient chronoGraph)
{
    private readonly ConcurrentDictionary<Guid, ResearchSession> _sessions = new();

    public async Task<ResearchSession> CreateSessionAsync(
        string topic, int maxRounds = 20, CancellationToken ct = default)
    {
        var session = new ResearchSession
        {
            Topic = topic,
            MaxRounds = maxRounds,
        };

        var graphId = await chronoGraph.CreateGraphAsync($"session-{session.Id}", ct);
        session.GraphId = graphId;

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
            session.CompletedAt = null;
            session.FailureReason = null;
            return true;
        }
    }

    public bool MarkSessionCompleted(Guid id)
    {
        var session = GetSession(id);
        if (session is null) return false;

        lock (session)
        {
            session.Status = SessionStatus.Completed;
            session.CompletedAt = DateTime.UtcNow;
            session.FailureReason = null;
            return true;
        }
    }

    public bool MarkSessionFailed(Guid id, string? reason = null)
    {
        var session = GetSession(id);
        if (session is null) return false;

        lock (session)
        {
            session.Status = SessionStatus.Failed;
            session.CompletedAt = DateTime.UtcNow;
            session.FailureReason = reason;
            return true;
        }
    }

    public async Task<bool> DeleteSessionAsync(Guid id, CancellationToken ct = default)
    {
        if (!_sessions.TryRemove(id, out var session))
            return false;

        if (!string.IsNullOrWhiteSpace(session.GraphId))
            await chronoGraph.DeleteGraphAsync(session.GraphId, ct);

        return true;
    }
}
