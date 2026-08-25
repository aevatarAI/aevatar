using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.AI.Infrastructure.ToolExecution;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Capabilities;
using Aevatar.Configuration;
using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Scripting.Hosting.CapabilityApi;
using Aevatar.Workflow.Extensions.Maker;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Infrastructure.Workflows;
using Aevatar.Workflow.Projection.ReadModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Aevatar.Workflow.Extensions.Hosting;

public sealed class AevatarPlatformCompositionOptions
{
    public bool EnableAIFeatures { get; set; } = true;

    public bool EnableWorkflowCapability { get; set; } = true;

    public bool MapWorkflowChatPost { get; set; } = true;

    // Security lockdown (2026-07): scripting executes tenant-supplied C# in-process via Roslyn.
    // The capability is disabled by default; a host must opt in explicitly to compose it.
    public bool EnableScriptingCapability { get; set; }

    public bool EnableMakerExtensions { get; set; }

    public Action<AevatarAIFeatureOptions>? ConfigureAIFeatures { get; set; }
}

public static class AevatarPlatformHostBuilderExtensions
{
    internal const string AgentToolAdmissionRedisConnectionStringKey =
        "AgentToolAdmission:RedisConnectionString";
    internal const string AgentToolAdmissionKeyPrefixKey =
        "AgentToolAdmission:KeyPrefix";
    internal const string DefaultAgentToolAdmissionKeyPrefix =
        "aevatar:workflow:agent-tool-admission:v1:";

    public static WebApplicationBuilder AddAevatarPlatform(
        this WebApplicationBuilder builder,
        Action<AevatarPlatformCompositionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new AevatarPlatformCompositionOptions();
        configure?.Invoke(options);

        ValidateOptions(options);

        if (options.EnableAIFeatures)
        {
            builder.Services.AddAevatarAIFeatures(builder.Configuration, aiOptions =>
            {
                aiOptions.EnableMEAIProviders = true;
                aiOptions.EnableMCPTools = true;
                aiOptions.EnableSkills = true;
                aiOptions.EnableOrnnSkills = true;
                aiOptions.OrnnNyxIdSlug = builder.Configuration["Aevatar:Ornn:NyxIdSlug"];
                aiOptions.EnableSystemSkillOverlay = ReadBoolean(builder.Configuration["Aevatar:SystemSkills:Enabled"]);
                aiOptions.SystemSkillOverlaySetName = builder.Configuration["Aevatar:SystemSkills:SetName"];
                if (TimeSpan.TryParse(builder.Configuration["Aevatar:SystemSkills:RefreshTtl"], out var refreshTtl))
                    aiOptions.SystemSkillOverlayRefreshTtl = refreshTtl;
                if (int.TryParse(builder.Configuration["Aevatar:SystemSkills:MaxSkills"], out var maxSkills))
                    aiOptions.SystemSkillOverlayMaxSkills = maxSkills;
                if (int.TryParse(builder.Configuration["Aevatar:SystemSkills:MaxBytes"], out var maxBytes))
                    aiOptions.SystemSkillOverlayMaxBytes = maxBytes;
                aiOptions.EnableWebTools = true;
                aiOptions.WebSearchNyxIdBaseUrl =
                    FirstConfiguredValue(
                        builder.Configuration,
                        "Aevatar:Web:NyxIdBaseUrl")
                    ?? NyxIdEndpointResolver.ResolvePublicApiBaseUrl(builder.Configuration);
                aiOptions.WebSearchNyxIdSlug =
                    FirstConfiguredValue(
                        builder.Configuration,
                        "Aevatar:Web:NyxIdSearchSlug",
                        "Aevatar:Web:SearchSlug",
                        "Aevatar:WebSearch:NyxIdSlug");
                aiOptions.WebSearchApiBaseUrl =
                    FirstConfiguredValue(
                        builder.Configuration,
                        "Aevatar:Web:SearchApiBaseUrl",
                        "Aevatar:WebSearch:ApiBaseUrl");
                if (options.EnableWorkflowCapability)
                    aiOptions.EnableWorkflowTools = true;
                if (options.EnableScriptingCapability)
                    aiOptions.EnableScriptingTools = true;
                options.ConfigureAIFeatures?.Invoke(aiOptions);
            });
        }

        if (options.EnableWorkflowCapability)
        {
            builder.Services.AddWorkflowProjectionReadModelProviders(builder.Configuration);
            builder.Services.AddAevatarHealthContributor(new AevatarHealthContributorRegistration
            {
                Name = "workflow-document-readmodel",
                Category = "dependency",
                ProbeAsync = static async (serviceProvider, cancellationToken) =>
                {
                    var indexProbe = serviceProvider.GetService<IProjectionIndexConsistencyProbe<WorkflowExecutionCurrentStateDocument>>();
                    if (indexProbe != null)
                    {
                        var consistency = await indexProbe.CheckIndexConsistencyAsync(cancellationToken);
                        return ProjectionIndexDiagnostics.ToContributorResult(consistency);
                    }

                    var documentReader = serviceProvider.GetRequiredService<IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string>>();
                    try
                    {
                        _ = await documentReader.QueryAsync(new ProjectionDocumentQuery
                        {
                            Take = 1,
                        }, cancellationToken);
                        return AevatarHealthContributorResult.Healthy("Workflow document read model is reachable.");
                    }
                    catch (ProjectionIndexSchemaDriftException exception)
                    {
                        return ProjectionIndexDiagnostics.ToUnhealthyContributorResult(exception);
                    }
                },
            });
            builder.Services.AddAevatarHealthContributor(new AevatarHealthContributorRegistration
            {
                Name = "workflow-graph-readmodel",
                Category = "dependency",
                ProbeAsync = static async (serviceProvider, cancellationToken) =>
                {
                    var providerStatus = serviceProvider.GetService<ProjectionGraphProviderStatus>();
                    if (providerStatus is { Enabled: false })
                    {
                        return AevatarHealthContributorResult.Healthy(
                            "Workflow graph read model is disabled by configuration.",
                            new Dictionary<string, string>
                            {
                                ["provider"] = providerStatus.ProviderName,
                                ["enabled"] = bool.FalseString,
                            });
                    }

                    var graphStore = serviceProvider.GetRequiredService<IProjectionGraphStore>();
                    _ = await graphStore.ListNodesByOwnerAsync(
                        scope: WorkflowExecutionGraphConstants.Scope,
                        ownerId: "health-probe",
                        take: 1,
                        ct: cancellationToken);
                    return AevatarHealthContributorResult.Healthy("Workflow graph read model is reachable.");
                },
            });
            builder.AddWorkflowCapabilityBundle(options.MapWorkflowChatPost);
            builder.AddAevatarCapability(
                "scheduled-dispatch",
                static (services, configuration) => services.AddScheduledDispatchCapability(configuration),
                static app => app.MapScheduledDispatchEndpoints());
        }

        if (options.EnableScriptingCapability)
            builder.AddScriptingCapabilityBundle();

        if (options.EnableMakerExtensions)
            builder.Services.AddWorkflowMakerExtensions();

        return builder;
    }

    public static WebApplicationBuilder AddWorkflowAgentToolAdmission(
        this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var policy = ResolveAgentToolAdmissionPolicy(builder.Configuration);
        if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
        {
            builder.Services.AddInMemoryAgentToolAdmissionLedger(policy);
            return builder;
        }

        var connectionString = builder.Configuration[AgentToolAdmissionRedisConnectionStringKey]?.Trim();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Workflow Host requires '{AgentToolAdmissionRedisConnectionStringKey}' " +
                "when server-owned AI tools are enabled outside Development or Testing.");
        }

        builder.Services.TryAddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(connectionString));
        builder.Services.AddGarnetAgentToolAdmissionLedger(
            ResolveAgentToolAdmissionLedgerOptions(builder.Configuration),
            policy);
        return builder;
    }

    private static void ValidateOptions(AevatarPlatformCompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.EnableMakerExtensions && !options.EnableWorkflowCapability)
        {
            throw new InvalidOperationException(
                "Maker extensions require workflow capability to be enabled.");
        }
    }

    private static bool ReadBoolean(string? value) =>
        bool.TryParse(value, out var result) && result;

    private static string? FirstConfiguredValue(
        IConfiguration configuration,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static AgentToolAdmissionPolicy ResolveAgentToolAdmissionPolicy(
        IConfiguration configuration)
    {
        var defaults = AgentToolAdmissionPolicy.Default;
        return new AgentToolAdmissionPolicy(
            configuration.GetValue<TimeSpan?>("AgentToolAdmission:MaximumRequestLifetime") ??
            AgentToolAdmissionPolicy.DefaultMaximumRequestLifetime,
            configuration.GetValue<TimeSpan?>("AgentToolAdmission:MaximumFutureClockSkew") ??
            defaults.MaximumFutureClockSkew);
    }

    private static AgentToolAdmissionLedgerOptions ResolveAgentToolAdmissionLedgerOptions(
        IConfiguration configuration) =>
        new(configuration[AgentToolAdmissionKeyPrefixKey]?.Trim() ?? DefaultAgentToolAdmissionKeyPrefix);
}
