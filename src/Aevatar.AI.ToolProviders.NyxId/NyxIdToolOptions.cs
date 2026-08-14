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

public sealed class NyxIdAssistantReadBackProviderResourceArgument
{
    public NyxIdAssistantOperationArgumentLocation ReadLocation { get; set; }

    public string ReadArgumentName { get; set; } = string.Empty;
}

public sealed class NyxIdAssistantEffectArgumentConstraint
{
    public NyxIdAssistantOperationArgumentLocation EffectLocation { get; set; }

    public string EffectArgumentName { get; set; } = string.Empty;

    public Value ExpectedValue { get; set; } = new();
}

public sealed class NyxIdAssistantReadBackNotAppliedEvidence
{
    public string JsonPointer { get; set; } = string.Empty;

    public Value ExpectedValue { get; set; } = new();
}

public sealed class NyxIdAssistantReadBackPagination
{
    public string HasMoreJsonPointer { get; set; } = string.Empty;

    public string PageTokenJsonPointer { get; set; } = string.Empty;

    public NyxIdAssistantOperationArgumentLocation PageTokenLocation { get; set; }

    public string PageTokenArgumentName { get; set; } = string.Empty;

    public int MaxPages { get; set; }
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

    /// <summary>
    /// Optional read argument populated from the provider resource identity extracted from the
    /// effect response. The binding is frozen with the read-back admission; the value is supplied
    /// only after the effect succeeds.
    /// </summary>
    public NyxIdAssistantReadBackProviderResourceArgument? ProviderResourceArgument { get; set; }

    public List<NyxIdAssistantEffectArgumentConstraint> EffectArgumentConstraints { get; set; } = [];

    public AgentToolReadBackMatch Match { get; set; }

    public string JsonPointer { get; set; } = string.Empty;

    public string ElementJsonPointer { get; set; } = string.Empty;

    public NyxIdAssistantOperationArgumentLocation ExpectedValueLocation { get; set; }

    public string ExpectedValueArgumentName { get; set; } = string.Empty;

    /// <summary>RFC 6901 pointer to the provider resource ID in the effect response.</summary>
    public string EffectResultIdentityJsonPointer { get; set; } = string.Empty;

    public NyxIdAssistantReadBackNotAppliedEvidence? NotAppliedEvidence { get; set; }

    public NyxIdAssistantReadBackPagination? Pagination { get; set; }

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
    /// Default NyxID REST API transport base URL. Deployments may configure an internal transport
    /// independently from their public browser/OIDC authority; the production default remains usable
    /// when no override is supplied.
    /// </summary>
    public const string DefaultBaseUrl = "https://nyx-api.chrono-ai.fun/";

    /// <summary>
    /// Legacy single-endpoint URL. Configuration-aware registration also assigns the effective
    /// transport here for compatibility with existing consumers; new code should use
    /// <see cref="EffectiveTransportBaseUrl"/>.
    /// </summary>
    public string? BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>
    /// Dedicated server-to-server NyxID REST transport URL. A deployment may point this at the
    /// NyxID backend's cluster-local service. It must never be exposed as an OAuth issuer,
    /// browser locator, or third-party webhook URL.
    /// </summary>
    public string? InternalApiBaseUrl { get; set; }

    /// <summary>Public, browser-reachable NyxID REST API URL.</summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>Public NyxID OAuth/OIDC issuer and discovery authority.</summary>
    public string? Authority { get; set; }

    /// <summary>
    /// Optional public REST fallback derived from <c>Aevatar:NyxId:ApiBaseUrl</c> when the
    /// primary transport is explicitly internal. It is separate from <see cref="ApiBaseUrl"/>
    /// so manually constructed options never fall back to an unrelated default endpoint.
    /// </summary>
    public string? PublicTransportFallbackBaseUrl { get; set; }

    public string? EffectiveTransportBaseUrl => FirstNonEmpty(InternalApiBaseUrl, ApiBaseUrl, BaseUrl);

    public string? EffectiveApiBaseUrl => FirstNonEmpty(
        ApiBaseUrl,
        InternalApiBaseUrl is null ? BaseUrl : null);

    public string? EffectiveAuthority => FirstNonEmpty(
        Authority,
        InternalApiBaseUrl is null ? ApiBaseUrl : null,
        InternalApiBaseUrl is null ? BaseUrl : null);

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

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
