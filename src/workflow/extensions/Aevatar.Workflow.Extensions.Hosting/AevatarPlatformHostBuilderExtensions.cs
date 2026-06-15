using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Capabilities;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Scripting.Hosting.CapabilityApi;
using Aevatar.Workflow.Extensions.Maker;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Infrastructure.Workflows;
using Aevatar.Workflow.Projection.ReadModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Extensions.Hosting;

public sealed class AevatarPlatformCompositionOptions
{
    public bool EnableAIFeatures { get; set; } = true;

    public bool EnableWorkflowCapability { get; set; } = true;

    public bool EnableScriptingCapability { get; set; } = true;

    public bool EnableMakerExtensions { get; set; }

    public Action<AevatarAIFeatureOptions>? ConfigureAIFeatures { get; set; }
}

public static class AevatarPlatformHostBuilderExtensions
{
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
                aiOptions.EnableWebTools = true;
                aiOptions.WebSearchNyxIdSlug = builder.Configuration["Aevatar:WebSearch:NyxIdSlug"];
                aiOptions.WebSearchApiBaseUrl = builder.Configuration["Aevatar:WebSearch:ApiBaseUrl"];
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
                    var graphStore = serviceProvider.GetRequiredService<IProjectionGraphStore>();
                    _ = await graphStore.ListNodesByOwnerAsync(
                        scope: WorkflowExecutionGraphConstants.Scope,
                        ownerId: "health-probe",
                        take: 1,
                        ct: cancellationToken);
                    return AevatarHealthContributorResult.Healthy("Workflow graph read model is reachable.");
                },
            });
            builder.AddWorkflowCapabilityBundle();
            builder.AddAevatarCapability(
                "scheduled-dispatch",
                static (_, _) => { },
                static app => app.MapScheduledDispatchEndpoints());
        }

        if (options.EnableScriptingCapability)
            builder.AddScriptingCapabilityBundle();

        if (options.EnableMakerExtensions)
            builder.Services.AddWorkflowMakerExtensions();

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
}
