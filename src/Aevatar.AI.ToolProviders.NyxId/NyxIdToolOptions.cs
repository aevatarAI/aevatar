namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>NyxID tool provider configuration.</summary>
public sealed class NyxIdToolOptions
{
    public const long DefaultProxyFileArtifactMaxBytes = 25L * 1024 * 1024;
    public const long HardProxyFileArtifactMaxBytes = 100L * 1024 * 1024;

    /// <summary>
    /// The single default NyxID base URL (the identity/OIDC authority AND the proxy host — the
    /// nyx-api.chrono-ai.fun alias is the same endpoint). This is the one place the default lives;
    /// hosts override it from config (e.g. Aevatar:NyxId:Authority) only when a value is provided, so
    /// when config is absent the relay OIDC discovery and nyxid_proxy calls still work out of the box.
    /// </summary>
    public const string DefaultBaseUrl = "https://nyx.chrono-ai.fun/";

    /// <summary>NyxID base URL. Defaults to <see cref="DefaultBaseUrl"/>; set via config to override.</summary>
    public string? BaseUrl { get; set; } = DefaultBaseUrl;

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

    /// <summary>
    /// Maximum bytes accepted by nyxid_proxy response_mode=file_artifact.
    /// </summary>
    public long ProxyFileArtifactMaxBytes { get; set; } = DefaultProxyFileArtifactMaxBytes;

    public long EffectiveProxyFileArtifactMaxBytes =>
        ProxyFileArtifactMaxBytes <= 0
            ? DefaultProxyFileArtifactMaxBytes
            : Math.Min(ProxyFileArtifactMaxBytes, HardProxyFileArtifactMaxBytes);
}
