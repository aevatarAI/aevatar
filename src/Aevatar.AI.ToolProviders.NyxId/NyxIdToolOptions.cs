namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>NyxID tool provider configuration.</summary>
public sealed class NyxIdToolOptions
{
    /// <summary>NyxID API base URL (e.g. https://nyx-api.chrono-ai.fun).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// When <c>true</c>, expose the <c>ssh_exec</c> tool to the LLM. Off by default
    /// because <c>ssh_exec</c> can run arbitrary commands on a remote host: hosts
    /// without an approval middleware in their tool execution pipeline would let
    /// the model run shell commands directly. Hosts that have wired the approval
    /// middleware (or that explicitly accept the risk for an internal-only deploy
    /// like the share-ops Lark bot) opt in by setting this to <c>true</c>.
    /// </summary>
    public bool EnableSshExecTool { get; set; }

    /// <summary>
    /// When <c>true</c>, <c>ssh_exec</c> returns <c>RequiresApproval=false</c> so the
    /// local tool approval middleware executes it immediately. Defaults to false; enable
    /// only in a host-owned, internal-only deployment where the surrounding channel and
    /// identity policy already define the trust boundary.
    /// </summary>
    public bool BypassSshExecApproval { get; set; }
}
