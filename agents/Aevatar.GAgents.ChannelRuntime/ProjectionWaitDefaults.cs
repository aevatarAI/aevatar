namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Shared default polling budget for tools that wait on the read model after
/// dispatching a write to the user agent catalog actor. 30 attempts × 500 ms
/// = 15 s — covers the production projection lag the prior 5 s budget lost to.
/// </summary>
internal static class ProjectionWaitDefaults
{
    public const int Attempts = 30;
    public const int DelayMilliseconds = 500;
}
