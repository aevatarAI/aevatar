namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>NyxID tool provider configuration.</summary>
public sealed class NyxIdToolOptions
{
    public const long DefaultProxyFileArtifactMaxBytes = 25L * 1024 * 1024;
    public const long HardProxyFileArtifactMaxBytes = 100L * 1024 * 1024;
    public const string DefaultSandboxServiceSlug = "chrono-sandbox";

    /// <summary>
    /// Default NyxID REST API base URL. Deployments may configure a dedicated API/resource-server
    /// base independently from their browser/OIDC authority; the production default remains usable
    /// when no override is supplied.
    /// </summary>
    public const string DefaultBaseUrl = "https://nyx.chrono-ai.fun/";

    /// <summary>NyxID REST API base URL. Defaults to <see cref="DefaultBaseUrl"/>.</summary>
    public string? BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>
    /// NyxID service slug used by <c>code_execute</c>. Hosts that expose the
    /// tool through a per-user OAuth binding must request this same service as
    /// an RFC 8707 resource.
    /// </summary>
    public string SandboxServiceSlug { get; set; } = DefaultSandboxServiceSlug;

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
    /// When <c>true</c>, expose the managed-sandbox target of <c>codex_exec</c>.
    /// A matching <c>ICodexExecutionPort</c> must be registered by the host; endpoint,
    /// image, credential, and admission policy remain operator-owned configuration.
    /// </summary>
    public bool EnableManagedCodexExecTool { get; set; }

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
