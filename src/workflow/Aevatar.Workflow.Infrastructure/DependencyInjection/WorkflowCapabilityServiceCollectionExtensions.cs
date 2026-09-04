using System.Text;
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
                    || options.Bindings.All(IsValidStaticWebhookBinding),
                $"Every enabled {WorkflowWebhookIngressOptions.SectionName} binding must configure valid secrets, signed delivery-id mapping, prompt mapping, and time zone.")
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
        services.TryAddSingleton<
            IWorkflowWebhookAgentKeyMaterializer,
            WorkflowWebhookAgentKeyMaterializer>();
        services.TryAddSingleton<Aevatar.Workflow.Application.Abstractions.Runs.IWorkflowWebhookReplayAdmissionPort, WorkflowWebhookReplayAdmissionPort>();
        // Zero-config default: hosts that already run on Garnet reuse the
        // actor-runtime connection for webhook replay/binding storage, and the
        // mounted secret-store keyring supplies (via domain-separated
        // derivation) the binding-secret encryption key. Explicit
        // WorkflowWebhookIngress values override both.
        var webhookReplayRedisConnectionString = configuration[$"{WorkflowWebhookIngressOptions.SectionName}:RedisConnectionString"];
        if (string.IsNullOrWhiteSpace(webhookReplayRedisConnectionString))
        {
            webhookReplayRedisConnectionString = configuration["ActorRuntime:OrleansGarnetConnectionString"];
            if (!string.IsNullOrWhiteSpace(webhookReplayRedisConnectionString))
            {
                var fallbackConnectionString = webhookReplayRedisConnectionString;
                services.PostConfigure<WorkflowWebhookIngressOptions>(options =>
                {
                    if (string.IsNullOrWhiteSpace(options.RedisConnectionString))
                        options.RedisConnectionString = fallbackConnectionString;
                });
            }
        }

        var bindingSecretEncryptionKey = configuration[$"{WorkflowWebhookIngressOptions.SectionName}:BindingSecretEncryptionKey"];
        IWorkflowWebhookBindingSecretCipher? bindingSecretCipher =
            string.IsNullOrWhiteSpace(bindingSecretEncryptionKey)
                ? AesGcmWorkflowWebhookBindingSecretCipher.TryCreateFromKeyring(
                    configuration["ActorRuntime:SecretStoreKeyringPath"])
                : new AesGcmWorkflowWebhookBindingSecretCipher(bindingSecretEncryptionKey);

        if (!string.IsNullOrWhiteSpace(webhookReplayRedisConnectionString))
        {
            services.TryAddSingleton<WorkflowWebhookReplayRedisConnection>();
            services.TryAddSingleton<Aevatar.Workflow.Application.Abstractions.Runs.IWorkflowWebhookReplayStore, RedisWorkflowWebhookReplayStore>();
            // Fail closed: without a host encryption key the Redis-backed
            // binding store stays unregistered (management API answers 503)
            // rather than persisting scope-submitted secrets in plaintext.
            if (bindingSecretCipher != null)
            {
                services.TryAddSingleton<IWorkflowWebhookBindingSecretCipher>(
                    bindingSecretCipher);
                services.TryAddSingleton<IWorkflowWebhookBindingStore, RedisWorkflowWebhookBindingStore>();
            }
        }
        else if (configuration.GetValue<bool>($"{WorkflowWebhookIngressOptions.SectionName}:UseInMemoryReplayStore"))
        {
            services.TryAddSingleton<Aevatar.Workflow.Application.Abstractions.Runs.IWorkflowWebhookReplayStore, InMemoryWorkflowWebhookReplayStore>();
            services.TryAddSingleton<IWorkflowWebhookBindingStore, InMemoryWorkflowWebhookBindingStore>();
        }
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

    private static bool IsValidStaticWebhookBinding(WorkflowWebhookIngressBindingOptions binding)
    {
        if (!HasSufficientHmacSecret(binding.HmacSecret) ||
            (!string.IsNullOrWhiteSpace(binding.PreviousHmacSecret) &&
             !HasSufficientHmacSecret(binding.PreviousHmacSecret)) ||
            !WorkflowWebhookJsonPath.IsValid(binding.DeliveryIdJsonPath))
        {
            return false;
        }

        var promptTemplate = binding.PromptTemplate?.Trim();
        var promptPath = binding.PromptJsonPath?.Trim();
        if (promptTemplate == null && promptPath == null)
            return false;
        if (promptPath != null && !WorkflowWebhookJsonPath.IsValid(promptPath))
            return false;
        if (promptTemplate != null && !WorkflowWebhookPromptTemplate.Validate(promptTemplate).Succeeded)
            return false;

        var timeZoneId = string.IsNullOrWhiteSpace(binding.TimeZoneId)
            ? TimeZoneInfo.Utc.Id
            : binding.TimeZoneId.Trim();
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    public sealed class WorkflowCapabilityRegistrationsMarker;
}
