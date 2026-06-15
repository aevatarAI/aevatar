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

    /// <summary>
    /// Two non-atomic field writes. Safe in practice because the runner only calls
    /// <c>Reset()</c> from <c>ExecuteSkillAsync</c> at the top of a run, when no
    /// concurrent middleware invocation can be in flight (the prior <c>ChatStreamAsync</c>
    /// has fully drained or this is the first run). A reader observing a transient
    /// <c>(0, N)</c> state mid-Reset would only happen if Reset and middleware ran
    /// concurrently, which the actor's single-threaded turn discipline prevents.
    /// </summary>
    public void Reset()
    {
        Volatile.Write(ref _failureCount, 0);
        Volatile.Write(ref _successCount, 0);
    }

    public void RecordFailure() => Interlocked.Increment(ref _failureCount);

    public void RecordSuccess() => Interlocked.Increment(ref _successCount);
}
