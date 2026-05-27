using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Integration.AI;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Integration.AI.Tests;

public sealed class WorkflowAiDependencyInjectionTests
{
    [Fact]
    public void AddWorkflowAiIntegration_ShouldRegisterPortResolverAndModulePack()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILLMProviderFactory>(new EmptyProviderFactory());
        services.AddSingleton(NullLogger<WorkflowAiLlmInvocationPort>.Instance);

        services.AddWorkflowAiIntegration();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IWorkflowLlmInvocationPort>()
            .Should().BeOfType<WorkflowAiLlmInvocationPort>();
        provider.GetRequiredService<IWorkflowRoleActorTypeResolver>()
            .Should().BeOfType<WorkflowAiRoleActorTypeResolver>();
        var modulePack = provider.GetServices<IWorkflowModulePack>()
            .Should().ContainSingle(x => x is WorkflowAiModulePack)
            .Subject;
        modulePack.DependencyExpanders.Should().ContainSingle();
    }

    [Fact]
    public void AddWorkflowAiIntegration_ShouldNotReplaceExistingPortOrResolver()
    {
        var services = new ServiceCollection();
        var customPort = new CustomInvocationPort();
        var customResolver = new CustomRoleActorTypeResolver();
        services.AddSingleton<IWorkflowLlmInvocationPort>(customPort);
        services.AddSingleton<IWorkflowRoleActorTypeResolver>(customResolver);

        services.AddWorkflowAiIntegration();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IWorkflowLlmInvocationPort>().Should().BeSameAs(customPort);
        provider.GetRequiredService<IWorkflowRoleActorTypeResolver>().Should().BeSameAs(customResolver);
    }

    [Fact]
    public void WorkflowAiModulePack_ShouldCreateMessageAdapterModuleThroughWorkflowModuleFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowLlmInvocationPort, CustomInvocationPort>();
        services.AddWorkflowAiIntegration();
        services.AddSingleton<IEventModuleFactory<IWorkflowExecutionContext>, WorkflowModuleFactory>();

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IEventModuleFactory<IWorkflowExecutionContext>>();

        factory.TryCreate("workflow_ai_message_adapter", out var module).Should().BeTrue();
        module.Should().BeOfType<WorkflowAiMessageAdapterModule>();
    }

    [Fact]
    public void WorkflowAiModulePack_ShouldInstallAdapterWheneverLlmCallIsNeeded()
    {
        var pack = new WorkflowAiModulePack();
        var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "llm_call",
        };

        pack.DependencyExpanders.Single().Expand(workflow: null, modules);

        modules.Should().Contain("workflow_ai_message_adapter");
    }

    private sealed class EmptyProviderFactory : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) => throw new InvalidOperationException(name);

        public ILLMProvider GetDefault() => throw new InvalidOperationException("default");

        public IReadOnlyList<string> GetAvailableProviders() => [];
    }

    private sealed class CustomInvocationPort : IWorkflowLlmInvocationPort
    {
        public async IAsyncEnumerable<WorkflowLlmInvocationEvent> InvokeAsync(
            WorkflowLlmExecutionIntent intent,
            [EnumeratorCancellation]
            CancellationToken ct = default)
        {
            _ = intent;
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield break;
        }
    }

    private sealed class CustomRoleActorTypeResolver : IWorkflowRoleActorTypeResolver
    {
        public Type ResolveRoleActorType() => typeof(Aevatar.AI.Core.RoleGAgent);
    }
}
