using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

/// <summary>
/// The Redis-backed webhook store graph must survive the container's
/// ValidateOnBuild pass — the same check the production host runs at startup.
/// Unit tests construct these types directly, so a non-public constructor
/// (which MS.DI silently ignores) only ever explodes at boot; this test keeps
/// that failure in CI.
/// </summary>
public sealed class WorkflowWebhookStoreDiCompositionTests
{
    [Fact]
    public void RedisWebhookStoreGraph_ShouldValidateOnBuild()
    {
        var services = new ServiceCollection();
        services.AddOptions<WorkflowWebhookIngressOptions>()
            .Configure(options => options.RedisConnectionString = "localhost:6379");
        services.AddSingleton<WorkflowWebhookReplayRedisConnection>();
        services.AddSingleton<IWorkflowWebhookReplayStore, RedisWorkflowWebhookReplayStore>();
        services.AddSingleton<IWorkflowWebhookReplayAdmissionPort, WorkflowWebhookReplayAdmissionPort>();
        services.AddSingleton<IWorkflowWebhookBindingSecretCipher>(
            new AesGcmWorkflowWebhookBindingSecretCipher("di-composition-test-key"));
        services.AddSingleton<IWorkflowWebhookBindingStore, RedisWorkflowWebhookBindingStore>();

        // ValidateOnBuild resolves every call site without instantiating
        // singletons, so no Redis connection is attempted here.
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
