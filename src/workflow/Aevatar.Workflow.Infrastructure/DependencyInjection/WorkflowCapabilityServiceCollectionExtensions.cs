using System.Text;
using Aevatar.Configuration;
using Aevatar.Workflow.Application.DependencyInjection;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Infrastructure.Workflows;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Extensions.Schedules;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using Aevatar.Workflow.Presentation.AGUIAdapter.DependencyInjection;
using Aevatar.Workflow.Projection.DependencyInjection;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Workflow.Infrastructure.DependencyInjection;

public static class WorkflowCapabilityServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowCapability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAevatarWorkflow();
        services.AddWorkflowExecutionProjectionCQRS(options =>
            configuration.GetSection("WorkflowExecutionProjection").Bind(options));
        services.AddWorkflowExecutionAGUIAdapter();
        services.TryAddSingleton<IHumanInteractionPort, NullHumanInteractionPort>();
        services.TryAddSingleton<IChannelInteractionNotificationPort, NullChannelInteractionNotificationPort>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<WorkflowExecutionProjectionContext>,
            WorkflowExecutionRunEventProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<WorkflowExecutionProjectionContext>,
            WorkflowHumanInteractionProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<WorkflowExecutionProjectionContext>,
            WorkflowHumanApprovalResolutionProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<WorkflowExecutionProjectionContext>,
            WorkflowInteractionNotificationProjector>());
        services.AddWorkflowApplication();
        services.AddWorkflowScheduleExtensions();
        services.AddOptions<WorkflowWebhookIngressOptions>()
            .Bind(configuration.GetSection(WorkflowWebhookIngressOptions.SectionName))
            .Validate(
                static options => !options.Enabled
                    || options.Bindings.All(static binding => HasSufficientHmacSecret(binding.HmacSecret)),
                $"Every enabled {WorkflowWebhookIngressOptions.SectionName} binding must configure an HMAC secret of at least {MinHmacSecretByteLength} UTF-8 bytes.")
            .ValidateOnStart();
        services.AddOptions<WorkflowMultipartFileIngressOptions>()
            .Bind(configuration.GetSection(WorkflowMultipartFileIngressOptions.SectionName));
        services.AddOptions<WorkflowFormFileIngressOptions>()
            .Bind(configuration.GetSection(WorkflowFormFileIngressOptions.SectionName));
        services.TryAddSingleton<WorkflowMultipartFileInputParser>();
        services.TryAddSingleton<WorkflowMultipartChatRequestParser>();
        services.AddOptions<WorkflowExternalApprovalCallbackOptions>()
            .Bind(configuration.GetSection(WorkflowExternalApprovalCallbackOptions.SectionName))
            .Validate(
                static options => !options.Enabled
                    || options.Bindings.All(static binding => HasSufficientHmacSecret(binding.HmacSecret)),
                $"Every enabled {WorkflowExternalApprovalCallbackOptions.SectionName} binding must configure an HMAC secret of at least {MinHmacSecretByteLength} UTF-8 bytes.")
            .ValidateOnStart();
        services.TryAddSingleton<WorkflowWebhookIngressRequestBuilder>();
        services.TryAddSingleton<Aevatar.Workflow.Application.Abstractions.Runs.IWorkflowWebhookReplayAdmissionPort, WorkflowWebhookReplayAdmissionPort>();
        var webhookReplayRedisConnectionString = configuration[$"{WorkflowWebhookIngressOptions.SectionName}:RedisConnectionString"];
        if (!string.IsNullOrWhiteSpace(webhookReplayRedisConnectionString))
        {
            services.TryAddSingleton<WorkflowWebhookReplayRedisConnection>();
            services.TryAddSingleton<Aevatar.Workflow.Application.Abstractions.Runs.IWorkflowWebhookReplayStore, RedisWorkflowWebhookReplayStore>();
        }
        else if (configuration.GetValue<bool>($"{WorkflowWebhookIngressOptions.SectionName}:UseInMemoryReplayStore"))
        {
            services.TryAddSingleton<Aevatar.Workflow.Application.Abstractions.Runs.IWorkflowWebhookReplayStore, InMemoryWorkflowWebhookReplayStore>();
        }
        services.AddWorkflowDefinitionFileSource(options =>
        {
            options.WorkflowDirectories.Add(Path.Combine(AppContext.BaseDirectory, "workflows"));
            options.WorkflowDirectories.Add(AevatarPaths.RepoRootWorkflows);
            options.WorkflowDirectories.Add(Path.Combine(Directory.GetCurrentDirectory(), "workflows"));
            options.WorkflowDirectories.Add(AevatarPaths.Workflows);
            options.DuplicatePolicy = WorkflowDefinitionDuplicatePolicy.Override;
        });
        services.AddWorkflowInfrastructure(
            options => configuration.GetSection("WorkflowRunReportExport").Bind(options),
            configuration);
        services.TryAddSingleton<WorkflowCapabilityRegistrationsMarker>();
        return services;
    }

    internal const int MinHmacSecretByteLength = 32;

    private static bool HasSufficientHmacSecret(string? hmacSecret)
    {
        if (string.IsNullOrWhiteSpace(hmacSecret))
        {
            return false;
        }

        return Encoding.UTF8.GetByteCount(hmacSecret.Trim()) >= MinHmacSecretByteLength;
    }

    public sealed class WorkflowCapabilityRegistrationsMarker;
}
