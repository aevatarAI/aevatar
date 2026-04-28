namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Per-skill-run counter for nyxid_proxy tool calls. Maintained by
/// <see cref="NyxIdProxyToolFailureCountingMiddleware"/> and read by
/// <see cref="SkillRunnerGAgent.EnsureToolStatusAllowsCompletion"/> as the runner-layer
/// safety net for issue #439: when every nyxid_proxy call in a run failed, the LLM's
/// plain-text output is structurally indistinguishable from a real "no activity" report,
/// so the runner refuses to record it as a clean success.
/// </summary>
internal sealed class SkillRunnerToolFailureCounter
{
    private int _failureCount;
    private int _successCount;

    public int FailureCount => Volatile.Read(ref _failureCount);

    public int SuccessCount => Volatile.Read(ref _successCount);

    public void Reset()
    {
        Volatile.Write(ref _failureCount, 0);
        Volatile.Write(ref _successCount, 0);
    }

    public void RecordFailure() => Interlocked.Increment(ref _failureCount);

    public void RecordSuccess() => Interlocked.Increment(ref _successCount);
}
