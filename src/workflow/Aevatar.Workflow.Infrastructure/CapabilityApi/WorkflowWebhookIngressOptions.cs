namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public sealed class WorkflowWebhookIngressOptions
{
    public const string SectionName = "WorkflowWebhookIngress";

    public bool Enabled { get; set; }

    public bool UseInMemoryReplayStore { get; set; }

    public string? RedisConnectionString { get; set; }

    public string RedisKeyPrefix { get; set; } = "aevatar:workflow:webhook-replay";

    public int RedisDatabase { get; set; } = -1;

    public int ReplayRetentionDays { get; set; } = 30;

    /// <summary>
    /// Host-level key protecting dynamic binding HMAC secrets at rest. The
    /// Redis-backed binding store is only registered when this is configured,
    /// so scope-registered secrets are never persisted in plaintext.
    /// </summary>
    public string? BindingSecretEncryptionKey { get; set; }

    public List<WorkflowWebhookIngressBindingOptions> Bindings { get; } = [];
}

public sealed class WorkflowWebhookIngressBindingOptions
{
    public string? RouteKey { get; set; }
    public string? SourceId { get; set; }
    public string? WorkflowName { get; set; }

    /// <summary>
    /// Scope-published target identity. When set, the run starts against this
    /// definition actor instead of a catalog name lookup.
    /// </summary>
    public string? DefinitionActorId { get; set; }

    /// <summary>
    /// Revision pinned when the binding is created. Definition-actor webhook
    /// deliveries fail closed when the committed actor moves to another
    /// revision instead of silently executing changed workflow code.
    /// </summary>
    public string? TargetRevisionId { get; set; }

    public string? ScopeId { get; set; }
    public string? PromptTemplate { get; set; }
    public string? PromptJsonPath { get; set; }
    public string? TimeZoneId { get; set; }
    public string? DeliveryIdHeader { get; set; }
    public string? DeliveryIdJsonPath { get; set; }
    public string? HmacSecret { get; set; }

    /// <summary>
    /// Optional retired secret honored during rotation: senders still signing
    /// with the previous secret keep delivering until it is cleared.
    /// </summary>
    public string? PreviousHmacSecret { get; set; }

    public string? HmacSignatureHeader { get; set; }
    public string? HmacTimestampHeader { get; set; }
    public int MaxTimestampSkewSeconds { get; set; } = 300;
}

internal static class WorkflowWebhookIngressLimits
{
    public const int MaxRouteKeyBytes = 256;
    public const int MaxBodyBytes = 256 * 1024;
    public const int MaxPromptTemplateBytes = 64 * 1024;
    public const int MaxPromptBytes = 256 * 1024;
    public const int MaxDeliveryIdBytes = 256;
    public const int MaxJsonPathBytes = 256;
    public const int MaxJsonPathSegments = 32;
    public const int MaxPromptPlaceholders = 128;
    public const int MaxJsonDepth = 32;
}
