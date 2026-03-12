using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.DependencyInjection;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Application.Workflows;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowApplicationRegistrationAndExecutionTests
{
    [Fact]
    public void AddWorkflowApplication_Default_ShouldRegisterBuiltInDirectWorkflow()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkflowDefinitionRegistry>();
        var yaml = registry.GetYaml("direct");
        yaml.Should().NotBeNullOrWhiteSpace();
        registry.GetDefinition("direct")!.DefinitionActorId.Should().Be(WorkflowDefinitionActorId.Format("direct"));
    }

    [Fact]
    public void AddWorkflowApplication_WhenConfigured_ShouldAllowDisablingBuiltInDirectWorkflow()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication(options => options.RegisterBuiltInDirectWorkflow = false);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkflowDefinitionRegistry>();
        registry.GetYaml("direct").Should().BeNull();
    }

    [Fact]
    public void AddWorkflowApplication_Default_ShouldRegisterBuiltInAutoWorkflow()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkflowDefinitionRegistry>();
        var yaml = registry.GetYaml("auto");
        yaml.Should().NotBeNullOrWhiteSpace();
        registry.GetDefinition("auto")!.DefinitionActorId.Should().Be(WorkflowDefinitionActorId.Format("auto"));
        yaml.Should().Contain("name: auto");
        yaml.Should().Contain("dynamic_workflow");
        yaml!.IndexOf("- id: done", StringComparison.Ordinal)
            .Should().BeGreaterThan(yaml.IndexOf("- id: extract_and_execute", StringComparison.Ordinal));
    }

    [Fact]
    public void AddWorkflowApplication_WhenConfigured_ShouldAllowDisablingBuiltInAutoWorkflow()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication(options => options.RegisterBuiltInAutoWorkflow = false);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkflowDefinitionRegistry>();
        registry.GetYaml("auto").Should().BeNull();
    }

    [Fact]
    public void AddWorkflowApplication_Default_ShouldRegisterBuiltInAutoReviewWorkflow()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkflowDefinitionRegistry>();
        var yaml = registry.GetYaml("auto_review");
        yaml.Should().NotBeNullOrWhiteSpace();
        registry.GetDefinition("auto_review")!.DefinitionActorId.Should().Be(WorkflowDefinitionActorId.Format("auto_review"));
        yaml.Should().Contain("name: auto_review");
        yaml.Should().Contain("\"true\": done");
        yaml.Should().Contain("Approve to finalize YAML for manual run");
        yaml!.IndexOf("- id: done", StringComparison.Ordinal)
            .Should().BeGreaterThan(yaml.IndexOf("- id: show_for_approval", StringComparison.Ordinal));
    }

    [Fact]
    public void AddWorkflowApplication_WhenConfigured_ShouldAllowDisablingBuiltInAutoReviewWorkflow()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication(options => options.RegisterBuiltInAutoReviewWorkflow = false);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkflowDefinitionRegistry>();
        registry.GetYaml("auto_review").Should().BeNull();
    }

    [Fact]
    public void AddWorkflowApplication_Default_ShouldRegisterRunBehaviorOptions()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<WorkflowRunBehaviorOptions>();

        options.DefaultWorkflowName.Should().Be("direct");
        options.UseAutoAsDefaultWhenWorkflowUnspecified.Should().BeFalse();
        options.EnableDirectFallback.Should().BeTrue();
        options.DirectFallbackWorkflowWhitelist.Should().Contain("auto");
        options.DirectFallbackWorkflowWhitelist.Should().Contain("auto_review");
        options.DirectFallbackExceptionWhitelist.Should().Contain(typeof(WorkflowDirectFallbackTriggerException));
    }

    [Fact]
    public void AddWorkflowApplication_WhenConfigured_ShouldApplyRunBehaviorOptionsToFallbackPolicy()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication(
            configureRunBehavior: options =>
            {
                options.DefaultWorkflowName = "direct";
                options.UseAutoAsDefaultWhenWorkflowUnspecified = true;
                options.EnableDirectFallback = true;
                options.DirectFallbackWorkflowWhitelist.Clear();
                options.DirectFallbackWorkflowWhitelist.Add("analysis");
                options.DirectFallbackExceptionWhitelist.Clear();
                options.DirectFallbackExceptionWhitelist.Add(typeof(TimeoutException));
            });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<WorkflowRunBehaviorOptions>();
        var policy = provider.GetRequiredService<WorkflowDirectFallbackPolicy>();

        options.UseAutoAsDefaultWhenWorkflowUnspecified.Should().BeTrue();
        options.DirectFallbackWorkflowWhitelist.Should().ContainSingle().Which.Should().Be("analysis");
        options.DirectFallbackExceptionWhitelist.Should().ContainSingle().Which.Should().Be(typeof(TimeoutException));

        policy.ShouldFallback(new WorkflowChatRunRequest("hello", "analysis", null), new TimeoutException("timeout"))
            .Should().BeTrue();
        policy.ShouldFallback(
                new WorkflowChatRunRequest("hello", "analysis", null),
                new WorkflowDirectFallbackTriggerException("boom"))
            .Should().BeFalse();
        policy.ShouldFallback(new WorkflowChatRunRequest("hello", "analysis", null), new InvalidOperationException("boom"))
            .Should().BeFalse();
    }

    [Fact]
    public void AddWorkflowApplication_ShouldWireWorkflowRunOutputStreamerAcrossAbstractions()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication();
        using var provider = services.BuildServiceProvider();

        var concrete = provider.GetRequiredService<WorkflowRunOutputStreamer>();
        var outputStreamer = provider.GetRequiredService<IWorkflowRunOutputStreamer>();
        var eventOutputStream = provider.GetRequiredService<IEventOutputStream<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>>();
        var frameMapper = provider.GetRequiredService<IEventFrameMapper<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>>();

        outputStreamer.Should().BeSameAs(concrete);
        eventOutputStream.Should().BeSameAs(concrete);
        frameMapper.Should().BeSameAs(concrete);
    }

    [Fact]
    public void AddWorkflowApplication_ShouldShareRegistryBackedCatalogAcrossQueryPorts()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication();
        using var provider = services.BuildServiceProvider();

        var catalogPort = provider.GetRequiredService<IWorkflowCatalogPort>();
        var capabilitiesPort = provider.GetRequiredService<IWorkflowCapabilitiesPort>();

        catalogPort.GetType().Name.Should().Be("RegistryBackedWorkflowCatalogPort");
        capabilitiesPort.GetType().Name.Should().Be("RegistryBackedWorkflowCatalogPort");
        catalogPort.Should().BeSameAs(capabilitiesPort);
    }

    [Fact]
    public void EnvelopeFactory_ShouldMergeHeadersAndCommandMetadata()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICommandEnvelopeFactory<WorkflowChatRunRequest>>();
        var context = new CommandContext(
            TargetId: "actor-1",
            CommandId: "cmd-1",
            CorrelationId: "corr-1",
            Headers: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkflowRunCommandMetadataKeys.ChannelId] = "slack#ops",
                ["source"] = "headers",
            });
        var command = new WorkflowChatRunRequest(
            "hello",
            "direct",
            "actor-1",
            SessionId: "session-42",
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkflowRunCommandMetadataKeys.ChannelId] = "slack#request",
                [WorkflowRunCommandMetadataKeys.UserId] = "u-1001",
            });

        var envelope = factory.CreateEnvelope(command, context);
        var request = envelope.Payload.Unpack<ChatRequestEvent>();

        envelope.Route!.TargetActorId.Should().Be("actor-1");
        envelope.Propagation!.CorrelationId.Should().Be("corr-1");
        envelope.Route.Direction.Should().Be(EventDirection.Self);
        envelope.Route.PublisherActorId.Should().Be("api");
        request.Prompt.Should().Be("hello");
        request.SessionId.Should().Be("session-42");
        request.Metadata[WorkflowRunCommandMetadataKeys.ChannelId].Should().Be("slack#request");
        request.Metadata[WorkflowRunCommandMetadataKeys.UserId].Should().Be("u-1001");
        request.Metadata["source"].Should().Be("headers");
    }

    [Fact]
    public void EnvelopeFactory_WhenSessionIdMissingOrWhitespace_ShouldFallbackToCorrelationId()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICommandEnvelopeFactory<WorkflowChatRunRequest>>();
        var command = new WorkflowChatRunRequest("hello", null, null);

        var noMetadata = factory.CreateEnvelope(command, new CommandContext(
            "actor-2",
            "cmd-2",
            "corr-2",
            new Dictionary<string, string>()));
        noMetadata.Payload.Unpack<ChatRequestEvent>().SessionId.Should().Be("corr-2");

        var whiteSpaceSession = factory.CreateEnvelope(new WorkflowChatRunRequest("hello", null, null, SessionId: "   "), new CommandContext(
            "actor-3",
            "cmd-3",
            "corr-3",
            new Dictionary<string, string>()));
        whiteSpaceSession.Payload.Unpack<ChatRequestEvent>().SessionId.Should().Be("corr-3");
    }
}
