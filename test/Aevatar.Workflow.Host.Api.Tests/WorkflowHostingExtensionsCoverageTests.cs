using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Hosting;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Infrastructure.Runs;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowHostingExtensionsCoverageTests
{
    [Fact]
    public void AddWorkflowCapabilityWithAIDefaults_ShouldValidateBuilder()
    {
        Action act = () => WorkflowCapabilityHostBuilderExtensions.AddWorkflowCapabilityWithAIDefaults(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task AddWorkflowCapabilityWithAIDefaults_ShouldRegisterWorkflowAndAIServices()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.AddWorkflowCapabilityWithAIDefaults(options =>
        {
            options.EnableMCPTools = false;
            options.EnableSkills = false;
            options.ApiKey = "demo-key";
            options.DefaultProvider = "openai";
        }, includeScriptCapability: true);

        builder.Services.Any(x => x.ServiceType == typeof(IWorkflowRunInteractionService)).Should().BeTrue();
        builder.Services.Any(x => x.ServiceType == typeof(ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>)).Should().BeTrue();
        builder.Services.Any(x => x.ServiceType == typeof(IWorkflowRunActorPort)).Should().BeTrue();
        builder.Services.Any(x => x.ServiceType == typeof(IProjectionDocumentStore<WorkflowExecutionReport, string>)).Should().BeTrue();
        builder.Services
            .Where(x => x.ServiceType == typeof(AevatarCapabilityRegistration))
            .Select(x => x.ImplementationInstance)
            .OfType<AevatarCapabilityRegistration>()
            .Should()
            .Contain(x => x.Name == "script");

        await using var provider = builder.Services.BuildServiceProvider();
        provider.GetService<ILLMProviderFactory>().Should().NotBeNull();
        provider.GetService<IProjectionDocumentStore<WorkflowExecutionReport, string>>().Should().NotBeNull();

        var toolSources = provider.GetServices<IAgentToolSource>().ToList();
        toolSources.Should().NotContain(x => x is MCPAgentToolSource);
        toolSources.Should().NotContain(x => x is SkillsAgentToolSource);
    }

    [Fact]
    public void AddWorkflowCapabilityWithAIDefaults_WhenScriptCapabilityDisabled_ShouldNotRegisterScriptCapability()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.AddWorkflowCapabilityWithAIDefaults(includeScriptCapability: false);

        builder.Services
            .Where(x => x.ServiceType == typeof(AevatarCapabilityRegistration))
            .Select(x => x.ImplementationInstance)
            .OfType<AevatarCapabilityRegistration>()
            .Should()
            .NotContain(x => x.Name == "script");
    }
}
