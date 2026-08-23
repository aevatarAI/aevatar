using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Core.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.RunForks;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.DependencyInjection;
using Aevatar.Workflow.Application.RunForks;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Application.Workflows;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowApplicationRegistrationAndExecutionTests
{
    [Fact]
    public void AddWorkflowApplication_Default_ShouldRegisterBuiltInDirectWorkflow()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkflowDefinitionCatalog>();
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
        var registry = provider.GetRequiredService<IWorkflowDefinitionCatalog>();
        registry.GetYaml("direct").Should().BeNull();
    }

    [Fact]
    public void AddWorkflowApplication_Default_ShouldRegisterBuiltInAutoWorkflow()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkflowDefinitionCatalog>();
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
        var registry = provider.GetRequiredService<IWorkflowDefinitionCatalog>();
        registry.GetYaml("auto").Should().BeNull();
    }

    [Fact]
    public void AddWorkflowApplication_Default_ShouldRegisterBuiltInAutoReviewWorkflow()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkflowDefinitionCatalog>();
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
        var registry = provider.GetRequiredService<IWorkflowDefinitionCatalog>();
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
        options.AcceptedObservationTimeout.Should().Be(TimeSpan.FromSeconds(30));
        options.ChatHistoryReservationObservationTimeout.Should().Be(TimeSpan.FromSeconds(3));
        options.ChatHistoryReservationObservationInterval.Should().Be(TimeSpan.FromMilliseconds(50));
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

        policy.ShouldFallback(new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("analysis"), ExternalCapabilityExecutionMode.Interactive), new TimeoutException("timeout"))
            .Should().BeTrue();
        policy.ShouldFallback(
                new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("analysis"), ExternalCapabilityExecutionMode.Interactive),
                new WorkflowDirectFallbackTriggerException("boom"))
            .Should().BeFalse();
        policy.ShouldFallback(new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("analysis"), ExternalCapabilityExecutionMode.Interactive), new InvalidOperationException("boom"))
            .Should().BeFalse();
    }

    [Fact]
    public void AddWorkflowApplication_ShouldWireGenericEventStreamingAndInteractionServices()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication();

        services.Should().Contain(x =>
            x.ServiceType == typeof(IEventOutputStream<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>) &&
            x.ImplementationType == typeof(DefaultEventOutputStream<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IEventFrameMapper<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>) &&
            x.ImplementationType == typeof(IdentityEventFrameMapper<WorkflowRunEventEnvelope>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandFinalizeEmitter<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus, WorkflowRunEventEnvelope>) &&
            x.ImplementationType == typeof(WorkflowRunFinalizeEmitter));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandObservationLifecycle<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>) &&
            x.ImplementationType == typeof(WorkflowRunObservationLifecycle));
        services.Should().Contain(x =>
            x.ServiceType == typeof(DefaultCommandInteractionService<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>) &&
            x.ImplementationFactory != null);
        services.Should().NotContain(x =>
            x.ServiceType == typeof(ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowChatRunInteractionPort) &&
            x.ImplementationFactory != null);
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandTargetResolver<WorkflowForkRunCommand, WorkflowForkRunCommandTarget, WorkflowForkRunStartError>) &&
            x.ImplementationType == typeof(WorkflowForkRunCommandTargetResolver));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>) &&
            x.ImplementationType == typeof(WorkflowForkRunCommandDispatchService));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandTargetEnvelopeFactory<WorkflowForkRunCommand, WorkflowForkRunCommandTarget>) &&
            x.ImplementationType == typeof(WorkflowForkRunCommandEnvelopeFactory));
        services.Should().Contain(x =>
            x.ServiceType == typeof(DefaultCommandDispatchService<WorkflowChatRunRequest, WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>) &&
            x.ImplementationType == typeof(DefaultCommandDispatchService<WorkflowChatRunRequest, WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>) &&
            x.ImplementationFactory != null);
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandFallbackPolicy<WorkflowChatRunRequest>) &&
            x.ImplementationFactory != null);
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>) &&
            x.ImplementationType == typeof(DefaultCommandDispatchService<WorkflowResumeCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>) &&
            x.ImplementationType == typeof(DefaultCommandDispatchService<WorkflowSignalCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandDispatchService<WorkflowRetryCompensationCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>) &&
            x.ImplementationType == typeof(DefaultCommandDispatchService<WorkflowRetryCompensationCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>) &&
            x.ImplementationType == typeof(DefaultCommandDispatchService<WorkflowStopCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>));
        services.Should().NotContain(x =>
            x.ServiceType == typeof(IWorkflowRunForkSeedQueryPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandDispatchPipeline<WorkflowForkRunCommand, WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>) &&
            x.ImplementationType == typeof(DefaultCommandDispatchPipeline<WorkflowForkRunCommand, WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>));
    }

    [Fact]
    public void AddWorkflowApplication_ShouldResolvePublicationHooksWithoutForkReadModelDependencies()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = true,
        });

        provider.GetServices<ICommittedStatePublicationHook>()
            .Should()
            .ContainSingle(hook => hook.GetType().Name == "WorkflowRunForkCoordinator");
    }

    [Fact]
    public void AddWorkflowApplication_ShouldNotExposeGenericLiveInteractionServiceBypass()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = true,
        });

        provider.GetService<ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>>()
            .Should()
            .BeNull();
    }

    [Fact]
    public void AddWorkflowApplication_ShouldRegisterAcceptedOnlyDispatchWithAcceptedTarget()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication();

        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandTargetResolver<WorkflowChatRunRequest, WorkflowRunAcceptedCommandTarget, WorkflowChatRunStartError>) &&
            x.ImplementationType == typeof(WorkflowRunAcceptedCommandTargetResolver));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>) &&
            x.ImplementationType == typeof(DefaultCommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandTargetDispatcher<WorkflowRunAcceptedCommandTarget>) &&
            x.ImplementationType == typeof(ActorCommandTargetDispatcher<WorkflowRunAcceptedCommandTarget>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandReceiptFactory<WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt>) &&
            x.ImplementationType == typeof(WorkflowRunAcceptedReceiptFactory));
    }

    [Fact]
    public void AddWorkflowApplication_ShouldKeepInteractionServiceOnCommandTargetAndObservationLifecycle()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication();

        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandTargetResolver<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunStartError>) &&
            x.ImplementationType == typeof(WorkflowRunCommandTargetResolver));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandObservationLifecycle<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>) &&
            x.ImplementationType == typeof(WorkflowRunObservationLifecycle));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>) &&
            x.ImplementationType == typeof(DefaultCommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandTargetDispatcher<WorkflowRunCommandTarget>) &&
            x.ImplementationType == typeof(ActorCommandTargetDispatcher<WorkflowRunCommandTarget>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandReceiptFactory<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>) &&
            x.ImplementationType == typeof(WorkflowRunAcceptedReceiptFactory));
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
    public async Task AddWorkflowApplication_ShouldWireDraftRunExternalCapabilityAdmissionIntoResolver()
    {
        const string workflowYaml =
            """
            name: inline_external
            roles: []
            steps:
              - id: call_external
                type: tool_call
                capability:
                  nyxid_request:
                    user_service_id: user-service-alpha
                    method: POST
                    path_template: /open-apis/im/v1/messages
                    query_parameters: [receive_id_type]
                    body_required: true
                    body_mode: json
                    response_mode: text
                parameters:
                  tool: nyxid_proxy
                  arguments: "{}"
            """ + "\n";
        var selector = new NyxIdRequestSelector
        {
            UserServiceId = "user-service-alpha",
            Method = NyxIdRequestMethod.Post,
            PathTemplate = "/open-apis/im/v1/messages",
            BodyRequired = true,
            BodyMode = NyxIdRequestBodyMode.Json,
            ResponseMode = NyxIdRequestResponseMode.Text,
        };
        selector.QueryParameters.Add("receive_id_type");
        var dependencies = new WorkflowAuthorizationDependencies();
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = "inline_external/call_external",
            ToolName = "nyxid_proxy",
            Selector = new ExternalWorkflowCapabilitySelector
            {
                NyxIdRequest = selector.Clone(),
            },
        });
        var actorPort = new RecordingWorkflowRunActorPort();
        actorPort.ParseResults[workflowYaml] = WorkflowYamlParseResult.Success("inline_external", dependencies);

        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowActorBindingReader>(new StaticWorkflowActorBindingReader(null));
        services.AddSingleton(actorPort);
        services.AddSingleton<IWorkflowRunProvisioningPort>(sp => sp.GetRequiredService<RecordingWorkflowRunActorPort>());
        services.AddSingleton<IWorkflowDefinitionParser>(sp => sp.GetRequiredService<RecordingWorkflowRunActorPort>());
        services.AddSingleton<IExternalWorkflowCapabilitySource>(new ReadyNyxIdRequestCapabilitySource(selector));
        services.AddWorkflowApplication();
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IWorkflowRunActorResolver>().ResolveOrCreateAsync(
            new WorkflowChatRunRequest(
                "run the draft",
                WorkflowChatSource.InlineYamlBundle([workflowYaml]),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                ScopeId: "scope-alpha",
                CallerCredential: new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential(
                    "bearer-alpha",
                    new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerNyxIdAuthority(
                        "nyxid",
                        string.Empty,
                        "owner-alpha",
                        "proxy"),
                    NyxIdCallerCredentialKind.SourceReadableUserBearer),
                CommandIdSeed: "command-alpha"),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        actorPort.CreateRunBindings.Should().ContainSingle();
        var binding = actorPort.CreateRunBindings[0];
        binding.SourceKind.Should().Be("workflow_draft_run");
        binding.CapabilityAdmissionPlan.Should().NotBeNull();
        binding.CapabilityAdmissionPlan!.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        binding.CapabilityAdmissionPlan.InvocationAdmissions.Should().ContainSingle()
            .Which.CallSiteId.Should().Be("inline_external/call_external");
        binding.WorkflowId.Should().StartWith("workflow-draft-run-");
        binding.RevisionId.Should().StartWith("rev-draft-run-");
    }

    [Fact]
    public void EnvelopeFactory_ShouldKeepCommandMetadataOutOfHeaders()
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
            WorkflowChatSource.DefinitionActor("actor-1", "direct"),
            ExternalCapabilityExecutionMode.Interactive,
            SessionId: "session-42",
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkflowRunCommandMetadataKeys.ChannelId] = "slack#request",
                ["connector.http.authorization"] = "Bearer metadata-secret",
            },
            ScopeId: "u-1001",
            CallerCredential: new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential(" typed-secret "));

        var envelope = factory.CreateEnvelope(command, context);
        var request = envelope.Payload.Unpack<WorkflowChatRequestEvent>();

        envelope.Id.Should().Be("cmd-1");
        envelope.Route.GetTargetActorId().Should().Be("actor-1");
        envelope.Propagation!.CorrelationId.Should().Be("corr-1");
        envelope.Route.IsDirect().Should().BeTrue();
        envelope.Route.PublisherActorId.Should().Be("api");
        request.Prompt.Should().Be("hello");
        request.SessionId.Should().Be("session-42");
        request.ScopeId.Should().Be("u-1001");
        request.CallerCredential.BearerToken.Should().Be("typed-secret");
        request.Headers[WorkflowRunCommandMetadataKeys.ChannelId].Should().Be("slack#ops");
        request.Headers["source"].Should().Be("headers");
        request.Metadata[WorkflowRunCommandMetadataKeys.ChannelId].Should().Be("slack#request");
        request.Metadata.Should().NotContainKey("connector.http.authorization");
        request.Headers.Should().NotContainKey("workflow.command_id");
        request.Headers.Should().NotContainKey(WorkflowRunCommandMetadataKeys.ScopeId);
        request.Headers.Should().NotContainKey("scope_id");
    }

    [Fact]
    public void WorkflowCallerCredential_DurableHandle_ShouldBeTrustedInternalStateOnly()
    {
        var descriptor = new SecretReference
        {
            Ref = "sec-webhook-binding",
            Purpose = CredentialSecretPurposes.WorkflowWebhookBindingAgentKey,
            OwnerScopeKey = "scope-1",
            Fingerprint = "sha256:test",
            Version = 1,
            CreatedAtUnixMs = 1,
        };
        var durable = new DurableCallerCredentialRef
        {
            Ref = descriptor.Ref,
            Purpose = descriptor.Purpose,
            OwnerScopeKey = descriptor.OwnerScopeKey,
            SubjectId = "owner-alpha",
            SourceKind = DurableCallerCredentialSourceKind.WebhookBinding,
            SecretReference = descriptor,
            ProviderCredentialId = "provider-key-1",
        };
        var credential = new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential(
            NyxIdAuthority: new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerNyxIdAuthority(
                "nyxid",
                string.Empty,
                "owner-alpha",
                "proxy",
                "binding-1"),
            Kind: NyxIdCallerCredentialKind.ProxyDelegation,
            DurableCallerCredential: durable);

        var json = JsonSerializer.Serialize(credential);
        json.Should().NotContain("DurableCallerCredential");
        var forged = JsonSerializer.Deserialize<Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential>(
            """
            {
              "kind": 2,
              "durableCallerCredential": {
                "ref": "sec-forged",
                "purpose": "workflow.webhook.binding.agent-key"
              }
            }
            """,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        forged.Should().NotBeNull();
        forged!.DurableCallerCredential.Should().BeNull();

        var services = new ServiceCollection();
        services.AddWorkflowApplication();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICommandEnvelopeFactory<WorkflowChatRunRequest>>();
        var command = new WorkflowChatRunRequest(
            "hello",
            WorkflowChatSource.DefinitionActor("actor-1", "direct"),
            ExternalCapabilityExecutionMode.Durable,
            CallerCredential: credential);

        var envelope = factory.CreateEnvelope(command, new CommandContext(
            "actor-1",
            "cmd-1",
            "corr-1",
            new Dictionary<string, string>()));
        var request = envelope.Payload.Unpack<WorkflowChatRequestEvent>();

        request.CallerCredential.DurableCallerCredential.Should().NotBeNull();
        request.CallerCredential.DurableCallerCredential.Ref.Should().Be(durable.Ref);
        request.CallerCredential.DurableCallerCredential.SecretReference.Fingerprint
            .Should().Be(descriptor.Fingerprint);
    }

    [Fact]
    public void EnvelopeFactory_ShouldCarryWorkflowLlmControl()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICommandEnvelopeFactory<WorkflowChatRunRequest>>();
        var command = new WorkflowChatRunRequest(
            "hello",
            WorkflowChatSource.DefinitionActor("actor-1", "direct"),
            ExternalCapabilityExecutionMode.Interactive,
            LlmControl: new WorkflowLlmControl(
                ModelOverride: " model-a ",
                MaxToolRoundsOverride: 3,
                UserMemoryPrompt: " memory ",
                RoutePreference: " route-a "));

        var envelope = factory.CreateEnvelope(command, new CommandContext(
            "actor-1",
            "cmd-1",
            "corr-1",
            new Dictionary<string, string>()));
        var request = envelope.Payload.Unpack<WorkflowChatRequestEvent>();

        request.LlmControl.ModelOverride.Should().Be(" model-a ");
        request.LlmControl.MaxToolRoundsOverride.Should().Be(3);
        request.LlmControl.UserMemoryPrompt.Should().Be(" memory ");
        request.LlmControl.RoutePreference.Should().Be(" route-a ");
    }

    [Fact]
    public void EnvelopeFactory_ShouldCarryForkSeed()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICommandEnvelopeFactory<WorkflowChatRunRequest>>();
        var command = new WorkflowChatRunRequest(
            "resume-input",
            WorkflowChatSource.DefinitionActor("actor-1", "direct"),
            ExternalCapabilityExecutionMode.Interactive,
            ForkSeed: new WorkflowChatRunForkSeed(
                "source-run",
                "step-b",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["input"] = "seed-input",
                    ["step-a"] = "alpha",
                }));

        var envelope = factory.CreateEnvelope(command, new CommandContext(
            "actor-1",
            "cmd-1",
            "corr-1",
            new Dictionary<string, string>()));
        var request = envelope.Payload.Unpack<WorkflowChatRequestEvent>();

        request.Prompt.Should().Be("resume-input");
        request.ForkSeed.SourceRunId.Should().Be("source-run");
        request.ForkSeed.StartAtStepId.Should().Be("step-b");
        request.ForkSeed.Variables.Should().Contain("input", "seed-input");
        request.ForkSeed.Variables.Should().Contain("step-a", "alpha");
    }

    [Fact]
    public void EnvelopeFactory_ShouldCarryForkSeedOnRequestLevel()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICommandEnvelopeFactory<WorkflowChatRunRequest>>();
        var command = new WorkflowChatRunRequest(
            "resume-input",
            WorkflowChatSource.DefinitionActor("actor-1", "direct"),
            ExternalCapabilityExecutionMode.Interactive,
            ForkSeed: new WorkflowChatRunForkSeed(
                "source-run",
                "step-b",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["input"] = "seed-input",
                    ["step-a"] = "alpha",
                },
                Attempt: 2,
                StartStepIdempotency: new WorkflowStepIdempotencyView(
                    "source-run",
                    "step-b",
                    3,
                    "source-run:step-b:3")));

        var envelope = factory.CreateEnvelope(command, new CommandContext(
            "actor-1",
            "cmd-1",
            "corr-1",
            new Dictionary<string, string>()));
        var request = envelope.Payload.Unpack<WorkflowChatRequestEvent>();

        request.ForkSeed.SourceRunId.Should().Be("source-run");
        request.ForkSeed.StartAtStepId.Should().Be("step-b");
        request.ForkSeed.Attempt.Should().Be(2);
        request.ForkSeed.StartStepIdempotency.Should().NotBeNull();
        request.ForkSeed.StartStepIdempotency.LogicalRunId.Should().Be("source-run");
        request.ForkSeed.StartStepIdempotency.StepId.Should().Be("step-b");
        request.ForkSeed.StartStepIdempotency.LogicalAttempt.Should().Be(3);
        request.ForkSeed.StartStepIdempotency.IdempotencyKey.Should().Be("source-run:step-b:3");
        request.ForkSeed.Variables.Should().Contain("input", "seed-input");
        request.ForkSeed.Variables.Should().Contain("step-a", "alpha");
    }

    [Fact]
    public void EnvelopeFactory_ShouldMaterializeTrustedControlAsTypedProtoFields_NotMetadata()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICommandEnvelopeFactory<WorkflowChatRunRequest>>();
        var command = new WorkflowChatRunRequest(
            "hello",
            WorkflowChatSource.DefinitionActor("actor-1", "direct"),
            ExternalCapabilityExecutionMode.Interactive,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkflowRunCommandMetadataKeys.ScopeId] = "evil-scope",
                ["scope_id"] = "evil-legacy-scope",
                ["client-note"] = "open-extension",
            },
            ScopeId: "scope-typed",
            LlmControl: new WorkflowLlmControl(
                ModelOverride: "model-a",
                MaxToolRoundsOverride: 5,
                UserMemoryPrompt: "memory",
                RoutePreference: "route-a"),
            CallerCredential: new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential(
                "trusted-token",
                new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerNyxIdAuthority(
                    "nyxid",
                    string.Empty,
                    "nyx-user-42",
                    "proxy"),
                Aevatar.Workflow.Abstractions.NyxIdCallerCredentialKind.ProxyDelegation));

        var envelope = factory.CreateEnvelope(command, new CommandContext(
            "actor-1",
            "cmd-1",
            "corr-1",
            new Dictionary<string, string>()));
        var request = envelope.Payload.Unpack<WorkflowChatRequestEvent>();

        request.ScopeId.Should().Be("scope-typed");
        request.LlmControl.ModelOverride.Should().Be("model-a");
        request.LlmControl.MaxToolRoundsOverride.Should().Be(5);
        request.LlmControl.UserMemoryPrompt.Should().Be("memory");
        request.LlmControl.RoutePreference.Should().Be("route-a");
        request.CallerCredential.BearerToken.Should().Be("trusted-token");
        request.CallerCredential.Kind.Should().Be(
            Aevatar.Workflow.Abstractions.NyxIdCallerCredentialKind.ProxyDelegation);
        request.CallerCredential.NyxIdAuthority.Should().BeEquivalentTo(
            new Aevatar.Workflow.Abstractions.WorkflowCallerNyxIdAuthority
            {
                Platform = "nyxid",
                ExternalUserId = "nyx-user-42",
                Scope = "proxy",
            });
        request.Metadata.Should().Contain("client-note", "open-extension");
        request.Metadata.Should().NotContainKey(WorkflowRunCommandMetadataKeys.ScopeId);
        request.Metadata.Should().NotContainKey("scope_id");
    }

    [Fact]
    public void EnvelopeFactory_ShouldRejectMalformedDirectCallerCredential()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICommandEnvelopeFactory<WorkflowChatRunRequest>>();
        var command = new WorkflowChatRunRequest(
            "hello",
            WorkflowChatSource.DefinitionActor("actor-1", "direct"),
            ExternalCapabilityExecutionMode.Interactive,
            CallerCredential: new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential("Bearer token-123"));

        FluentActions.Invoking(() => factory.CreateEnvelope(command, new CommandContext(
                "actor-1",
                "cmd-1",
                "corr-1",
                new Dictionary<string, string>())))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*caller credential*invalid*");
    }

    [Fact]
    public void EnvelopeFactory_ShouldCarryInputParts_AlongsideTypedScopeId()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICommandEnvelopeFactory<WorkflowChatRunRequest>>();
        var command = new WorkflowChatRunRequest(
            "describe this",
            WorkflowChatSource.DefinitionActor("actor-1"),
            ExternalCapabilityExecutionMode.Interactive,
            InputParts:
            [
                new WorkflowChatInputPart
                {
                    Kind = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPartKind.Text,
                    Text = "describe this",
                },
                new WorkflowChatInputPart
                {
                    Kind = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPartKind.File,
                    Uri = "artifact-1",
                    MediaType = "application/pdf",
                    Name = "invoice.pdf",
                    FileRef = new Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef
                    {
                        FileId = "file-1",
                        ArtifactId = "artifact-1",
                        SourceKind = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactSourceKind.ConnectedServiceResource,
                        SourceMessageId = "om_1",
                        SourceResourceKey = "file_key_1",
                        FileName = "invoice.pdf",
                        MediaType = "application/pdf",
                        Sha256 = "abc",
                        CreatedAtUnixMs = 1710000000000,
                        ExpiresAtUnixMs = 1710003600000,
                    },
                },
            ],
            ScopeId: "scope-7");

        var envelope = factory.CreateEnvelope(command, new CommandContext(
            "actor-1",
            "cmd-1",
            "corr-1",
            new Dictionary<string, string>()));
        var request = envelope.Payload.Unpack<WorkflowChatRequestEvent>();

        request.ScopeId.Should().Be("scope-7");
        request.InputParts.Should().HaveCount(2);
        request.InputParts[0].Kind.Should().Be(Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Text);
        request.InputParts[0].Text.Should().Be("describe this");
        request.InputParts[1].Kind.Should().Be(Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.File);
        request.InputParts[1].Uri.Should().Be("artifact-1");
        request.InputParts[1].MediaType.Should().Be("application/pdf");
        request.InputParts[1].Name.Should().Be("invoice.pdf");
        request.InputParts[1].FileRef.FileId.Should().Be("file-1");
        request.InputParts[1].FileRef.ArtifactId.Should().Be("artifact-1");
        request.InputParts[1].FileRef.SourceKind.Should().Be(Aevatar.Workflow.Abstractions.WorkflowFileSourceKind.ConnectedServiceResource);
        request.InputParts[1].FileRef.SourceMessageId.Should().Be("om_1");
        request.InputParts[1].FileRef.SourceResourceKey.Should().Be("file_key_1");
        request.InputParts[1].FileRef.FileName.Should().Be("invoice.pdf");
        request.InputParts[1].FileRef.MediaType.Should().Be("application/pdf");
        request.InputParts[1].FileRef.Sha256.Should().Be("abc");
        request.InputParts[1].FileRef.CreatedAtUnixMs.Should().Be(1710000000000);
        request.InputParts[1].FileRef.ExpiresAtUnixMs.Should().Be(1710003600000);
    }

    [Fact]
    public void EnvelopeFactory_WhenSessionIdMissingOrWhitespace_ShouldFallbackToCorrelationId()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICommandEnvelopeFactory<WorkflowChatRunRequest>>();
        var command = new WorkflowChatRunRequest("hello", WorkflowChatSource.Direct(), ExternalCapabilityExecutionMode.Interactive);

        var noMetadata = factory.CreateEnvelope(command, new CommandContext(
            "actor-2",
            "cmd-2",
            "corr-2",
            new Dictionary<string, string>()));
        noMetadata.Payload.Unpack<WorkflowChatRequestEvent>().SessionId.Should().Be("corr-2");

        var whiteSpaceSession = factory.CreateEnvelope(new WorkflowChatRunRequest("hello", WorkflowChatSource.Direct(), ExternalCapabilityExecutionMode.Interactive, SessionId: "   "), new CommandContext(
            "actor-3",
            "cmd-3",
            "corr-3",
            new Dictionary<string, string>()));
        whiteSpaceSession.Payload.Unpack<WorkflowChatRequestEvent>().SessionId.Should().Be("corr-3");
    }

    private sealed class StaticWorkflowActorBindingReader(WorkflowActorBinding? binding) : IWorkflowActorBindingReader
    {
        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(binding);
        }
    }

    private sealed class RecordingWorkflowRunActorPort : IWorkflowRunProvisioningPort, IWorkflowDefinitionParser
    {
        public Dictionary<string, WorkflowYamlParseResult> ParseResults { get; } = new(StringComparer.Ordinal);

        public List<WorkflowDefinitionBinding> CreateRunBindings { get; } = [];

        public Task<WorkflowRunCreationReceipt> CreateRunAsync(
            WorkflowDefinitionBinding definition,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CreateRunBindings.Add(definition);
            return Task.FromResult(new WorkflowRunCreationReceipt(
                "run-1",
                "definition-isolated-1",
                ["definition-isolated-1", "run-1"]));
        }

        public Task DestroyAsync(string actorId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ParseResults.TryGetValue(workflowYaml, out var result)
                ? result
                : WorkflowYamlParseResult.Invalid($"Unexpected workflow YAML: {workflowYaml}"));
        }

        public async Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default)
        {
            if (inlineWorkflowDocuments.Count == 0)
                return WorkflowInlineYamlBundleParseResult.Invalid("workflowYamls is required.");

            var workflowYamlsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string entryWorkflowName = string.Empty;
            string entryWorkflowYaml = string.Empty;
            for (var i = 0; i < inlineWorkflowDocuments.Count; i++)
            {
                var document = inlineWorkflowDocuments[i];
                var parseResult = await ParseWorkflowYamlAsync(document.Yaml, ct);
                if (!parseResult.Succeeded)
                    return WorkflowInlineYamlBundleParseResult.Invalid(
                        parseResult.Error,
                        parseResult.ExternalCapabilityReadiness);

                if (!workflowYamlsByName.TryAdd(parseResult.WorkflowName, document.Yaml))
                    return WorkflowInlineYamlBundleParseResult.Invalid(
                        $"Duplicate workflow name '{parseResult.WorkflowName}' in workflowYamls.");

                if (i == 0)
                {
                    entryWorkflowName = parseResult.WorkflowName;
                    entryWorkflowYaml = document.Yaml;
                }
            }

            return WorkflowInlineYamlBundleParseResult.Success(
                entryWorkflowName,
                entryWorkflowYaml,
                workflowYamlsByName);
        }
    }

    private sealed class ReadyNyxIdRequestCapabilitySource(NyxIdRequestSelector requestContract) :
        IExternalWorkflowCapabilitySource
    {
        public ExternalWorkflowCapabilitySelector.SelectorOneofCase SelectorKind =>
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest;

        public Task<ExternalWorkflowCapabilityDiscoveryResult> ListAsync(
            ExternalWorkflowCapabilityAccessContext access,
            CancellationToken cancellationToken = default)
        {
            _ = access;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ExternalWorkflowCapabilityDiscoveryResult());
        }

        public Task<ExternalCapabilityReadiness> InspectAsync(
            ExternalWorkflowCapabilityAccessContext access,
            ExternalWorkflowCapabilitySelector selector,
            ExternalCapabilityExecutionMode executionMode,
            CancellationToken cancellationToken = default)
        {
            _ = access;
            cancellationToken.ThrowIfCancellationRequested();
            var policy = new NyxIdOperationExecutionPolicy
            {
                Risk = NyxIdOperationRisk.Write,
                Approval = NyxIdOperationApproval.None,
                EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
            };
            policy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
            var requestDigest = WorkflowCapabilityAdmissionPlanIntegrity
                .ComputeNyxIdRequestContractDigest(requestContract);
            var result = new ExternalCapabilityReadiness
            {
                ExecutionMode = executionMode,
                Status = ExternalCapabilityReadinessStatus.Ready,
                SelectedSelector = selector.Clone(),
                SelectedCapability = new ExternalWorkflowCapabilityRef
                {
                    NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
                    {
                        Request = requestContract.Clone(),
                        ServiceSlugSnapshot = "api-lark-bot",
                        ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                            .ComputeNyxIdExplicitRequestProofDigest(requestDigest, "api-lark-bot"),
                        ExecutionPolicy = policy,
                    },
                },
            };
            var observedAt = DateTimeOffset.UtcNow;
            result.Sources.Add(new ExternalCapabilitySourceStamp
            {
                SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
                SourceId = "nyxid-user-services:caller:owner-alpha",
                ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
                FreshUntil = Timestamp.FromDateTimeOffset(observedAt.AddMinutes(5)),
                ContentDigest = "user-services-alpha",
            });
            return Task.FromResult(result);
        }
    }

}
