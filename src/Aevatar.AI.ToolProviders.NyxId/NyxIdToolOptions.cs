using Aevatar.AI.Abstractions.ToolProviders;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.ToolProviders.NyxId;

public enum NyxIdManagedWorkflowAdmissionMode
{
    Shadow = 0,
    Enforce = 1,
}

public sealed class NyxIdAssistantReadinessCapabilityBinding
{
    public string CatalogServiceSlug { get; set; } = string.Empty;

    public string ReadinessCapabilityId { get; set; } = string.Empty;
}

public enum NyxIdAssistantOperationArgumentLocation
{
    Unspecified = 0,
    Path = 1,
    Query = 2,
    Header = 3,
    Body = 4,
}

public sealed class NyxIdAssistantReadBackArgumentBinding
{
    public NyxIdAssistantOperationArgumentLocation EffectLocation { get; set; }

    public string EffectArgumentName { get; set; } = string.Empty;

    public NyxIdAssistantOperationArgumentLocation ReadLocation { get; set; }

    public string ReadArgumentName { get; set; } = string.Empty;
}

public sealed class NyxIdAssistantReadBackLiteralArgument
{
    public NyxIdAssistantOperationArgumentLocation ReadLocation { get; set; }

    public string ReadArgumentName { get; set; } = string.Empty;

    public Value Value { get; set; } = new();
}

public sealed class NyxIdAssistantEffectArgumentConstraint
{
    public NyxIdAssistantOperationArgumentLocation EffectLocation { get; set; }

    public string EffectArgumentName { get; set; } = string.Empty;

    public Value ExpectedValue { get; set; } = new();
}

/// <summary>
/// Server-owned exact effect-to-read contract. Endpoint identities and argument mappings are
/// configuration facts; the model supplies values only through the admitted effect schema.
/// </summary>
public sealed class NyxIdAssistantOperationReadBackBinding
{
    public string CatalogServiceSlug { get; set; } = string.Empty;

    public string EffectEndpointId { get; set; } = string.Empty;

    public string EffectHttpMethod { get; set; } = string.Empty;

    public string EffectPathTemplate { get; set; } = string.Empty;

    public string ReadEndpointId { get; set; } = string.Empty;

    public string ReadHttpMethod { get; set; } = string.Empty;

    public string ReadPathTemplate { get; set; } = string.Empty;

    public List<NyxIdAssistantReadBackArgumentBinding> ArgumentBindings { get; set; } = [];

    public List<NyxIdAssistantReadBackLiteralArgument> LiteralReadArguments { get; set; } = [];

    public List<NyxIdAssistantEffectArgumentConstraint> EffectArgumentConstraints { get; set; } = [];

    public AgentToolReadBackMatch Match { get; set; }

    public string JsonPointer { get; set; } = string.Empty;

    public string ElementJsonPointer { get; set; } = string.Empty;

    public NyxIdAssistantOperationArgumentLocation ExpectedValueLocation { get; set; }

    public string ExpectedValueArgumentName { get; set; } = string.Empty;

    public string CheckName { get; set; } = string.Empty;
}

/// <summary>NyxID tool provider configuration.</summary>
public sealed class NyxIdToolOptions
{
    public const long DefaultProxyFileArtifactMaxBytes = 25L * 1024 * 1024;
    public const long HardProxyFileArtifactMaxBytes = 100L * 1024 * 1024;

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
    /// When <c>true</c>, expose the <c>ssh_exec</c> tool to the LLM. This option is
    /// off by default. Explicit opt-in only exposes the tool; every invocation must
    /// still pass through the unified admitted execution port with an exact durable
    /// approval grant owned by the calling actor. There is no middleware or accepted-risk
    /// bypass for SSH execution.
    /// </summary>
    public bool EnableSshExecTool { get; set; }

    /// <summary>
    /// When <c>true</c>, expose the managed-sandbox target of <c>codex_exec</c>
    /// instead of <c>code_execute</c>. A matching <c>ICodexExecutionPort</c> must be
    /// registered by the host; endpoint, image, credential, and admission policy
    /// remain operator-owned configuration.
    /// </summary>
    public bool EnableManagedCodexExecTool { get; set; }

    /// <summary>
    /// Enables request-local connected-service effect tools only after the host has durable,
    /// actor-owned selector, dispatch, idempotency, and receipt persistence. Safe reads do not
    /// depend on this rollout gate.
    /// </summary>
    public bool EnableAssistantConnectedServiceEffects { get; set; }

    /// <summary>
    /// Server-owned bindings from NyxID catalog service identity to the closed assistant
    /// readiness registry. A missing or ambiguous binding omits recovery provenance.
    /// </summary>
    public List<NyxIdAssistantReadinessCapabilityBinding> AssistantReadinessCapabilityBindings { get; set; } =
    [
        new()
        {
            CatalogServiceSlug = "api-github",
            ReadinessCapabilityId = "api-github",
        },
    ];

    /// <summary>
    /// Closed effect-to-read bindings. A missing, ambiguous, or schema-incompatible entry leaves
    /// the effect honestly unverifiable and never falls back to endpoint-name heuristics.
    /// </summary>
    public List<NyxIdAssistantOperationReadBackBinding> AssistantOperationReadBackBindings { get; set; } = [];

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
