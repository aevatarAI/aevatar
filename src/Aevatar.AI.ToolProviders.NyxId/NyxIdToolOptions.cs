namespace Aevatar.AI.ToolProviders.NyxId;

public enum NyxIdManagedWorkflowAdmissionMode
{
    Shadow = 0,
    Enforce = 1,
}

/// <summary>NyxID tool provider configuration.</summary>
public sealed class NyxIdToolOptions
{
    public const long DefaultProxyFileArtifactMaxBytes = 25L * 1024 * 1024;
    public const long HardProxyFileArtifactMaxBytes = 100L * 1024 * 1024;
    public const string DefaultSandboxServiceSlug = "chrono-sandbox";

    /// <summary>
    /// Transport ceiling for a single NyxID HTTP call. Must stay above the longest per-call
    /// deadline any caller imposes, otherwise the transport aborts first and the caller's own
    /// timeout never gets to report the honest failure. The longest managed request deadline
    /// today is 300 seconds.
    /// </summary>
    public const int DefaultMaxRequestDurationSeconds = 330;

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
    /// When <c>true</c>, expose the <c>ssh_exec</c> tool to the LLM. This option is
    /// off by default. Explicit opt-in only exposes the tool; every invocation must
    /// still pass through the unified admitted execution port with an exact durable
    /// approval grant owned by the calling actor. There is no middleware or accepted-risk
    /// bypass for SSH execution.
    /// </summary>
    public bool EnableSshExecTool { get; set; }

    /// <summary>
    /// When <c>true</c>, expose the managed-sandbox target of <c>codex_exec</c>.
    /// A matching <c>ICodexExecutionPort</c> must be registered by the host; endpoint,
    /// image, credential, and admission policy remain operator-owned configuration.
    /// </summary>
    public bool EnableManagedCodexExecTool { get; set; }

    public NyxIdManagedWorkflowAdmissionMode ManagedWorkflowAdmissionMode { get; set; } =
        NyxIdManagedWorkflowAdmissionMode.Shadow;

    /// <summary>
    /// Maximum bytes accepted by nyxid_proxy response_mode=file_artifact.
    /// </summary>
    public long ProxyFileArtifactMaxBytes { get; set; } = DefaultProxyFileArtifactMaxBytes;

    public long EffectiveProxyFileArtifactMaxBytes =>
        ProxyFileArtifactMaxBytes <= 0
            ? DefaultProxyFileArtifactMaxBytes
            : Math.Min(ProxyFileArtifactMaxBytes, HardProxyFileArtifactMaxBytes);

    /// <summary>
    /// Transport ceiling for a single NyxID HTTP call, in seconds. Defaults to
    /// <see cref="DefaultMaxRequestDurationSeconds"/>. This is a backstop, not a per-call
    /// deadline: callers that need to fail sooner impose their own linked
    /// <see cref="CancellationTokenSource"/>.
    /// </summary>
    public int MaxRequestDurationSeconds { get; set; } = DefaultMaxRequestDurationSeconds;

    public TimeSpan EffectiveMaxRequestDuration =>
        TimeSpan.FromSeconds(
            MaxRequestDurationSeconds <= 0
                ? DefaultMaxRequestDurationSeconds
                : MaxRequestDurationSeconds);
}
