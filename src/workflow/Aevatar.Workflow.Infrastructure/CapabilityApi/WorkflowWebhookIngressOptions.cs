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

    public List<WorkflowWebhookIngressBindingOptions> Bindings { get; } = [];
}

public sealed class WorkflowWebhookIngressBindingOptions
{
    public string? RouteKey { get; set; }
    public string? SourceId { get; set; }
    public string? WorkflowName { get; set; }
    public string? ScopeId { get; set; }
    public string? PromptTemplate { get; set; }
    public string? PromptJsonPath { get; set; }
    public string? DeliveryIdHeader { get; set; }
    public string? DeliveryIdJsonPath { get; set; }
    public string? HmacSecret { get; set; }
    public string? HmacSignatureHeader { get; set; }
    public string? HmacTimestampHeader { get; set; }
    public int MaxTimestampSkewSeconds { get; set; } = 300;
}
