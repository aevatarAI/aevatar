using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.ExternalCapabilities;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunActorResolverTests
{
    [Fact]
    public async Task ResolveOrCreateAsync_WithUnspecifiedMode_ShouldRejectBeforeProvisioning()
    {
        var actorPort = new RecordingWorkflowRunActorPort();
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(null),
            actorPort,
            actorPort,
            new InMemoryWorkflowDefinitionCatalog());

        var act = () => resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest(
                "hello",
                WorkflowChatSource.Direct(),
                ExternalCapabilityExecutionMode.Unspecified),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*execution mode*");
        actorPort.CreateRunBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldCreateRunFromRequestedRegistryWorkflow()
    {
        var bindingReader = new StaticWorkflowActorBindingReader(null);
        var actorPort = new RecordingWorkflowRunActorPort();
        var registry = new InMemoryWorkflowDefinitionCatalog();
        RegisterPublishedWorkflow(
            registry,
            actorPort,
            "direct",
            "name: direct\nroles: []\nsteps: []\n",
            ExternalCapabilityExecutionMode.Interactive);
        var resolver = new WorkflowRunActorResolver(bindingReader, actorPort, actorPort, registry);

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow(" direct "), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        result.WorkflowNameForRun.Should().Be("direct");
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].DefinitionActorId.Should().Be("definition-direct");
        actorPort.CreateRunBindings[0].WorkflowName.Should().Be("direct");
        actorPort.CreateRunBindings[0].WorkflowYaml.Should().Be("name: direct\nroles: []\nsteps: []\n");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldForwardScopeIdFromTypedRequest()
    {
        var bindingReader = new StaticWorkflowActorBindingReader(null);
        var actorPort = new RecordingWorkflowRunActorPort();
        var registry = new InMemoryWorkflowDefinitionCatalog();
        RegisterPublishedWorkflow(
            registry,
            actorPort,
            "direct",
            "name: direct\nroles: []\nsteps: []\n",
            ExternalCapabilityExecutionMode.Interactive);
        var resolver = new WorkflowRunActorResolver(bindingReader, actorPort, actorPort, registry);

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest(
                "hello",
                WorkflowChatSource.CatalogWorkflow("direct"),
                ExternalCapabilityExecutionMode.Interactive,
                ScopeId: "scope-user-1"),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].ScopeId.Should().Be("scope-user-1");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldUseAutoWorkflow_WhenConfiguredAsDefault()
    {
        var bindingReader = new StaticWorkflowActorBindingReader(null);
        var actorPort = new RecordingWorkflowRunActorPort();
        var registry = new InMemoryWorkflowDefinitionCatalog();
        RegisterPublishedWorkflow(
            registry,
            actorPort,
            "auto",
            "name: auto\nroles: []\nsteps: []\n",
            ExternalCapabilityExecutionMode.Interactive);
        var resolver = new WorkflowRunActorResolver(bindingReader, actorPort, actorPort, registry, new WorkflowRunBehaviorOptions
            {
                UseAutoAsDefaultWhenWorkflowUnspecified = true,
            });

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.Direct(), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        result.WorkflowNameForRun.Should().Be("auto");
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].WorkflowName.Should().Be("auto");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldUseConfiguredDefaultWorkflowName_WhenWorkflowUnspecified()
    {
        var actorPort = new RecordingWorkflowRunActorPort();
        var registry = new InMemoryWorkflowDefinitionCatalog();
        RegisterPublishedWorkflow(
            registry,
            actorPort,
            "review",
            "name: review\nroles: []\nsteps: []\n",
            ExternalCapabilityExecutionMode.Interactive);
        var resolver = new WorkflowRunActorResolver(new StaticWorkflowActorBindingReader(null), actorPort, actorPort, registry, new WorkflowRunBehaviorOptions
            {
                DefaultWorkflowName = "review",
            });

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.Direct(), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        result.WorkflowNameForRun.Should().Be("review");
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].WorkflowName.Should().Be("review");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldNotReadSourceActorBinding_WhenSourceIsDirect()
    {
        var bindingReader = new ThrowingWorkflowActorBindingReader();
        var actorPort = new RecordingWorkflowRunActorPort();
        var registry = new InMemoryWorkflowDefinitionCatalog();
        RegisterPublishedWorkflow(
            registry,
            actorPort,
            "direct",
            "name: direct\nroles: []\nsteps: []\n",
            ExternalCapabilityExecutionMode.Interactive);
        var resolver = new WorkflowRunActorResolver(bindingReader, actorPort, actorPort, registry);

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.Direct(), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        result.WorkflowNameForRun.Should().Be("direct");
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].WorkflowName.Should().Be("direct");
        bindingReader.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldReturnWorkflowNotFound_WhenRegistryDoesNotContainWorkflow()
    {
        var resolver = new WorkflowRunActorResolver(new StaticWorkflowActorBindingReader(null), new RecordingWorkflowRunActorPort(), new RecordingWorkflowRunActorPort(), new InMemoryWorkflowDefinitionCatalog());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("missing"), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.WorkflowNotFound);
        result.Target.Should().BeNull();
        result.WorkflowNameForRun.Should().Be("missing");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldCreateIsolatedInlineRun_WhenAgentIdAndWorkflowYamlsAreProvided()
    {
        const string entryWorkflowYaml =
            """
            name: inline_entry
            roles: []
            steps: []
            """;
        const string helperWorkflowYaml =
            """
            name: helper
            roles: []
            steps: []
            """;
        var bindingReader = new StaticWorkflowActorBindingReader(
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "agent-1",
                "shared-definition-1",
                "source-run-1",
                "inline_entry",
                "name: inline_entry\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["inline_entry"] = "name: inline_entry\nroles: []\nsteps: []\n",
                },
                ExternalCapabilityExecutionMode.Interactive));
        var actorPort = new RecordingWorkflowRunActorPort();
        actorPort.ParseResults[entryWorkflowYaml] = WorkflowYamlParseResult.Success("inline_entry");
        actorPort.ParseResults[helperWorkflowYaml] = WorkflowYamlParseResult.Success("helper");
        var resolver = new WorkflowRunActorResolver(bindingReader, actorPort, actorPort, new InMemoryWorkflowDefinitionCatalog());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest(
                "hello",
                WorkflowChatSource.InlineYamlBundle([entryWorkflowYaml, helperWorkflowYaml], actorId: "agent-1"),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        result.WorkflowNameForRun.Should().Be("inline_entry");
        result.Target.Should().NotBeNull();
        result.Target!.ActorId.Should().Be("run-1");
        result.Target!.CreatedActorIds.Should().Equal("definition-isolated-1", "run-1");
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].DefinitionActorId.Should().BeEmpty();
        actorPort.CreateRunBindings[0].WorkflowName.Should().Be("inline_entry");
        actorPort.CreateRunBindings[0].WorkflowYaml.Should().Be(entryWorkflowYaml);
        actorPort.CreateRunBindings[0].RunOrigin.Should().Be(Aevatar.Workflow.Abstractions.WorkflowRunOrigins.Draft);
        actorPort.CreateRunBindings[0].InlineWorkflowYamls.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>("helper", helperWorkflowYaml));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ResolveOrCreateAsync_ShouldUseSupplementalSourceCredentialForDraftAdmission(
        bool canManageNyxIdUserServices,
        bool selectionMatchesCredential)
    {
        var workflowYaml =
            """
            name: inline_external
            roles: []
            steps:
              - id: call_external
                type: tool_call
                capability:
                  nyxid_request:
                    user_service_id: user-service-alpha
                    method: GET
                    path_template: /2/users/{id}/timelines/reverse_chronological
                    query_parameters: [max_results]
                    body_mode: none
                    response_mode: text
                parameters:
                  tool: nyxid_proxy
                  arguments: "{}"
            """ + "\n";
        var selector = new NyxIdRequestSelector
        {
            UserServiceId = "user-service-alpha",
            Method = NyxIdRequestMethod.Get,
            PathTemplate = "/2/users/{id}/timelines/reverse_chronological",
            BodyMode = NyxIdRequestBodyMode.None,
            ResponseMode = NyxIdRequestResponseMode.Text,
        };
        selector.QueryParameters.Add("max_results");
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
        var readinessPort = new ReadyExplicitRequestReadinessPort(selector);
        var draftAdmissionService = new WorkflowDraftRunCapabilityAdmissionService(
            new WorkflowExplicitRequestPreviewService(actorPort, readinessPort),
            new WorkflowExternalCapabilityAdmissionService(actorPort, readinessPort));
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(null),
            actorPort,
            actorPort,
            new InMemoryWorkflowDefinitionCatalog(),
            draftAdmissionService);

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest(
                "run the draft",
                WorkflowChatSource.InlineYamlBundle([workflowYaml]),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                ScopeId: "scope-alpha",
                CallerCredential: new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential(
                    "proxy-delegation-alpha",
                    new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerNyxIdAuthority(
                        "nyxid",
                        string.Empty,
                        "owner-alpha",
                        "proxy"),
                    NyxIdCallerCredentialKind.ProxyDelegation,
                    SourceReadableUserBearerToken: "source-readable-alpha"),
                CommandIdSeed: "command-alpha",
                CallerNyxIdCredentialSelection: canManageNyxIdUserServices
                    ? NyxIdCallerCredentialSelection.DirectUserBearer(
                        selectionMatchesCredential
                            ? "source-readable-alpha"
                            : "different-source-token")
                    : NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                        "source-readable-alpha")),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        readinessPort.LastAccess!.NyxIdCallerCredential!.SourceReadableUserBearerToken.Should()
            .Be("source-readable-alpha");
        readinessPort.LastAccess.NyxIdCallerCredential.CanManageUserServices.Should()
            .Be(canManageNyxIdUserServices && selectionMatchesCredential);
        readinessPort.LastAccess.NyxIdCallerCredential.ProxyDelegationToken.Should().BeNull();
        actorPort.CreateRunBindings.Should().ContainSingle();
        var binding = actorPort.CreateRunBindings[0];
        binding.SourceKind.Should().Be("workflow_draft_run");
        binding.CapabilityAdmissionPlan.Should().NotBeNull();
        binding.CapabilityAdmissionPlan!.SchemaVersion.Should()
            .Be(WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion);
        binding.CapabilityAdmissionPlan.ExecutionMode.Should()
            .Be(ExternalCapabilityExecutionMode.Interactive);
        var invocationAdmission = binding.CapabilityAdmissionPlan.InvocationAdmissions
            .Should().ContainSingle().Which;
        invocationAdmission.CallSiteId.Should().Be("inline_external/call_external");
        invocationAdmission.Capability.CapabilityCase.Should()
            .Be(ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest);
        invocationAdmission.NyxIdExplicitRequestGrant.Should().NotBeNull();
        binding.WorkflowId.Should().NotBeNullOrWhiteSpace();
        binding.RevisionId.Should().NotBeNullOrWhiteSpace();
        binding.WorkflowId.Should().NotBe(binding.RevisionId);
        invocationAdmission.NyxIdExplicitRequestGrant!.WorkflowId.Should().Be(binding.WorkflowId);
        invocationAdmission.NyxIdExplicitRequestGrant.RevisionId.Should().Be(binding.RevisionId);
        invocationAdmission.NyxIdExplicitRequestGrant.AllowedExecutionModes.Should()
            .Equal(ExternalCapabilityExecutionMode.Interactive);
        binding.InlineWorkflowYamls.Should().BeEmpty();
        WorkflowCapabilityAdmissionPlanIntegrity.ValidateOrThrow(
            binding.CapabilityAdmissionPlan,
            binding.WorkflowYaml,
            binding.InlineWorkflowYamls,
            ExternalCapabilityExecutionMode.Interactive,
            dependencies.ExternalInvocations,
            binding.WorkflowId,
            binding.RevisionId);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldRejectDraftRunBeforeActorCreation_WhenCapabilityAdmissionIsBlocked()
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
                    method: GET
                    path_template: /2/users/me
                    body_mode: none
                    response_mode: text
                parameters:
                  tool: nyxid_proxy
                  arguments: "{}"
            """;
        var selector = new ExternalWorkflowCapabilitySelector
        {
            NyxIdRequest = new NyxIdRequestSelector
            {
                UserServiceId = "user-service-alpha",
                Method = NyxIdRequestMethod.Get,
                PathTemplate = "/2/users/me",
                BodyMode = NyxIdRequestBodyMode.None,
                ResponseMode = NyxIdRequestResponseMode.Text,
            },
        };
        var dependencies = new WorkflowAuthorizationDependencies();
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = "inline_external/call_external",
            ToolName = "nyxid_proxy",
            Selector = selector.Clone(),
        });
        var blockedReadiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            Status = ExternalCapabilityReadinessStatus.ServiceRegistrationRequired,
            SelectedSelector = selector.Clone(),
        };
        blockedReadiness.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = blockedReadiness.Status,
            Code = "USER_SERVICE_NOT_VISIBLE",
            SafeMessage = "The selected NyxID UserService is not visible to the current caller.",
        });
        var actorPort = new RecordingWorkflowRunActorPort();
        actorPort.ParseResults[workflowYaml] = WorkflowYamlParseResult.Success("inline_external", dependencies);
        var readinessPort = new StaticExternalCapabilityReadinessPort(blockedReadiness);
        var draftAdmissionService = new WorkflowDraftRunCapabilityAdmissionService(
            new WorkflowExplicitRequestPreviewService(actorPort, readinessPort),
            new WorkflowExternalCapabilityAdmissionService(actorPort, readinessPort));
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(null),
            actorPort,
            actorPort,
            new InMemoryWorkflowDefinitionCatalog(),
            draftAdmissionService);

        var result = await resolver.ResolveOrCreateAsync(
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

        result.Error.Should().Be(WorkflowChatRunStartError.ExternalCapabilityNotReady);
        result.Target.Should().BeNull();
        result.FailureDetail.Should().NotBeNull();
        result.FailureDetail!.ExternalCapabilityReadiness.Should().NotBeSameAs(blockedReadiness);
        result.FailureDetail.ExternalCapabilityReadiness!.Status.Should()
            .Be(ExternalCapabilityReadinessStatus.ServiceRegistrationRequired);
        result.FailureDetail.ExternalCapabilityReadiness.Blockers.Should().ContainSingle()
            .Which.Code.Should().Be("USER_SERVICE_NOT_VISIBLE");
        actorPort.CreateRunBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldResolveTypedInlineYamlSourceActorId()
    {
        const string entryWorkflowYaml =
            """
            name: inline_entry
            roles: []
            steps: []
            """;
        const string sourceActorId = "typed-source-actor-1";
        var bindingReader = new RecordingWorkflowActorBindingReader();
        bindingReader.Register(
            sourceActorId,
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                sourceActorId,
                "shared-definition-1",
                "source-run-1",
                "inline_entry",
                "name: inline_entry\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                ScopeId: "source-scope-1"));
        var actorPort = new RecordingWorkflowRunActorPort();
        actorPort.ParseResults[entryWorkflowYaml] = WorkflowYamlParseResult.Success("inline_entry");
        var resolver = new WorkflowRunActorResolver(bindingReader, actorPort, actorPort, new InMemoryWorkflowDefinitionCatalog());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest(
                "hello",
                ScopeId: "request-scope-1",
                Source: WorkflowChatSource.InlineYamlBundle([entryWorkflowYaml], actorId: sourceActorId),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        bindingReader.LastActorId.Should().Be(sourceActorId);
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].WorkflowName.Should().Be("inline_entry");
        actorPort.CreateRunBindings[0].ScopeId.Should().Be("source-scope-1");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldResolveLegacyWorkflowAgentIdSourceActor()
    {
        const string sourceActorId = "legacy-source-actor-1";
        const string legacyYaml =
            """
            name: direct
            description: source actor definition
            roles: []
            steps: []
            """;
        var bindingReader = new RecordingWorkflowActorBindingReader();
        bindingReader.Register(
            sourceActorId,
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                sourceActorId,
                "definition-direct-bound",
                "source-run-1",
                "direct",
                legacyYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                ScopeId: "source-scope-1"));
        var actorPort = new RecordingWorkflowRunActorPort();
        var registry = new InMemoryWorkflowDefinitionCatalog();
        registry.Register(
            "direct",
            "name: direct\nroles: []\nsteps: []\n",
            ExternalCapabilityExecutionMode.Interactive);
        var resolver = new WorkflowRunActorResolver(bindingReader, actorPort, actorPort, registry);

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest(
                "hello",
                WorkflowChatSource.DefinitionActor(sourceActorId, "direct"),
                ExternalCapabilityExecutionMode.Interactive,
                ScopeId: "request-scope-1",
                LlmControl: null),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        result.WorkflowNameForRun.Should().Be("direct");
        bindingReader.LastActorId.Should().Be(sourceActorId);
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].DefinitionActorId.Should().Be("definition-direct-bound");
        actorPort.CreateRunBindings[0].WorkflowName.Should().Be("direct");
        actorPort.CreateRunBindings[0].WorkflowYaml.Should().Be(legacyYaml);
        actorPort.CreateRunBindings[0].ScopeId.Should().Be("source-scope-1");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldCarrySourceBindingAdmissionPlanAndRevisionIdentity()
    {
        const string sourceActorId = "definition-service-active";
        const string workflowYaml =
            """
            name: admitted
            roles: []
            steps: []
            """;
        var admissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            workflowYaml,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);
        var bindingReader = new RecordingWorkflowActorBindingReader();
        bindingReader.Register(
            sourceActorId,
            new WorkflowActorBinding(
                WorkflowActorKind.Definition,
                sourceActorId,
                sourceActorId,
                string.Empty,
                "admitted",
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                ScopeId: "scope-authoritative",
                SourceVersion: 12,
                SourceKind: "service_revision",
                CapabilityAdmissionPlan: admissionPlan,
                WorkflowId: "wf-admitted",
                RevisionId: "rev-admitted"));
        var actorPort = new RecordingWorkflowRunActorPort();
        var resolver = new WorkflowRunActorResolver(bindingReader, actorPort, actorPort, new InMemoryWorkflowDefinitionCatalog());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest(
                "hello",
                WorkflowChatSource.DefinitionActor(sourceActorId, "admitted"),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                ScopeId: "scope-request"),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        actorPort.CreateRunBindings.Should().ContainSingle();
        var runBinding = actorPort.CreateRunBindings[0];
        runBinding.DefinitionActorId.Should().Be(sourceActorId);
        runBinding.ScopeId.Should().Be("scope-authoritative");
        runBinding.SourceKind.Should().Be("service_revision");
        runBinding.CapabilityAdmissionPlan.Should().NotBeSameAs(admissionPlan);
        runBinding.CapabilityAdmissionPlan!.AdmissionDigest.Should().Be(admissionPlan.AdmissionDigest);
        runBinding.WorkflowId.Should().Be("wf-admitted");
        runBinding.RevisionId.Should().Be("rev-admitted");
        runBinding.DefinitionVersion.Should().Be(12);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldNotTreatRunBindingSourceVersionAsDefinitionVersion()
    {
        const string sourceActorId = "source-run-active";
        const string workflowYaml =
            """
            name: active_run
            roles: []
            steps: []
            """;
        var bindingReader = new RecordingWorkflowActorBindingReader();
        bindingReader.Register(
            sourceActorId,
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                sourceActorId,
                "definition-active-run",
                "run-active",
                "active_run",
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                ScopeId: "scope-authoritative",
                SourceVersion: 99,
                RevisionId: "rev-active-run"));
        var actorPort = new RecordingWorkflowRunActorPort();
        var resolver = new WorkflowRunActorResolver(bindingReader, actorPort, actorPort, new InMemoryWorkflowDefinitionCatalog());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest(
                "hello",
                WorkflowChatSource.DefinitionActor(sourceActorId, "active_run"),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        actorPort.CreateRunBindings.Should().ContainSingle();
        var runBinding = actorPort.CreateRunBindings[0];
        runBinding.RevisionId.Should().Be("rev-active-run");
        runBinding.DefinitionVersion.Should().Be(0);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldRejectInlineRun_WhenAgentWorkflowBindingConflicts()
    {
        const string entryWorkflowYaml =
            """
            name: inline_entry
            roles: []
            steps: []
            """;
        var bindingReader = new StaticWorkflowActorBindingReader(
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "agent-1",
                "shared-definition-1",
                "source-run-1",
                "bound_workflow",
                "name: bound_workflow\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), ExternalCapabilityExecutionMode.Interactive));
        var actorPort = new RecordingWorkflowRunActorPort();
        actorPort.ParseResults[entryWorkflowYaml] = WorkflowYamlParseResult.Success("inline_entry");
        var resolver = new WorkflowRunActorResolver(bindingReader, actorPort, actorPort, new InMemoryWorkflowDefinitionCatalog());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest(
                "hello",
                WorkflowChatSource.InlineYamlBundle([entryWorkflowYaml], actorId: "agent-1"),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.WorkflowBindingMismatch);
        result.WorkflowNameForRun.Should().Be("bound_workflow");
        actorPort.CreateRunBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldRejectInlineRun_WhenRequestedWorkflowNameDiffersFromEntryWorkflow()
    {
        const string entryWorkflowYaml =
            """
            name: inline_entry
            roles: []
            steps: []
            """;
        const string helperWorkflowYaml =
            """
            name: helper
            roles: []
            steps: []
            """;
        var actorPort = new RecordingWorkflowRunActorPort();
        actorPort.ParseResults[entryWorkflowYaml] = WorkflowYamlParseResult.Success("inline_entry");
        actorPort.ParseResults[helperWorkflowYaml] = WorkflowYamlParseResult.Success("helper");
        var resolver = new WorkflowRunActorResolver(new StaticWorkflowActorBindingReader(null), actorPort, actorPort, new InMemoryWorkflowDefinitionCatalog());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest(
                "hello",
                WorkflowChatSource.InlineYamlBundle([entryWorkflowYaml, helperWorkflowYaml], "auto"),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.WorkflowNameMismatch);
        result.WorkflowNameForRun.Should().Be("inline_entry");
        result.Target.Should().BeNull();
        actorPort.CreateRunBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldReturnInvalidWorkflowYaml_WhenInlineYamlBundleIsInvalid()
    {
        var actorPort = new RecordingWorkflowRunActorPort();
        var readiness = new ExternalCapabilityReadiness
        {
            Status = ExternalCapabilityReadinessStatus.AdmissionRebindRequired,
        };
        actorPort.ParseResults["bad"] = WorkflowYamlParseResult.Invalid("bad yaml", readiness);
        var resolver = new WorkflowRunActorResolver(new StaticWorkflowActorBindingReader(null), actorPort, actorPort, new InMemoryWorkflowDefinitionCatalog());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.InlineYamlBundle(["bad"]), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.InvalidWorkflowYaml);
        result.FailureDetail.Should().NotBeNull();
        result.FailureDetail!.Message.Should().Be("bad yaml");
        result.FailureDetail.ExternalCapabilityReadiness.Should().NotBeSameAs(readiness);
        result.FailureDetail.ExternalCapabilityReadiness!.Status.Should().Be(ExternalCapabilityReadinessStatus.AdmissionRebindRequired);
        actorPort.CreateRunBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldReturnInvalidWorkflowYaml_WhenInlineWorkflowNamesDuplicate()
    {
        const string firstYaml =
            """
            name: duplicate
            roles: []
            steps: []
            """;
        const string secondYaml =
            """
            name: duplicate
            roles: []
            steps: []
            """;
        var actorPort = new RecordingWorkflowRunActorPort();
        actorPort.ParseResults[firstYaml] = WorkflowYamlParseResult.Success("duplicate");
        actorPort.ParseResults[secondYaml] = WorkflowYamlParseResult.Success("duplicate");
        var resolver = new WorkflowRunActorResolver(new StaticWorkflowActorBindingReader(null), actorPort, actorPort, new InMemoryWorkflowDefinitionCatalog());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.InlineYamlBundle([firstYaml, secondYaml]), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.InvalidWorkflowYaml);
        actorPort.CreateRunBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldReturnInvalidWorkflowYaml_WhenNamedInlineDocumentNameMismatchesYaml()
    {
        const string helperWorkflowYaml =
            """
            name: helper
            roles: []
            steps: []
            """;
        var actorPort = new RecordingWorkflowRunActorPort();
        actorPort.ParseResults[helperWorkflowYaml] = WorkflowYamlParseResult.Success("helper");
        var resolver = new WorkflowRunActorResolver(new StaticWorkflowActorBindingReader(null), actorPort, actorPort, new InMemoryWorkflowDefinitionCatalog());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest(
                "hello",
                WorkflowChatSource.InlineYamlBundle(
                    "foo",
                    [new WorkflowChatInlineYamlDocument("foo", helperWorkflowYaml)]),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.InvalidWorkflowYaml);
        result.Target.Should().BeNull();
        actorPort.CreateRunBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldCreateRun_WhenNamedInlineDocumentNameMatchesYaml()
    {
        const string entryWorkflowYaml =
            """
            name: inline_entry
            roles: []
            steps: []
            """;
        var actorPort = new RecordingWorkflowRunActorPort();
        actorPort.ParseResults[entryWorkflowYaml] = WorkflowYamlParseResult.Success("inline_entry");
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(null),
            actorPort,
            actorPort,
            new InMemoryWorkflowDefinitionCatalog());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest(
                "hello",
                WorkflowChatSource.InlineYamlBundle(
                    "inline_entry",
                    [new WorkflowChatInlineYamlDocument("inline_entry", entryWorkflowYaml)]),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        result.WorkflowNameForRun.Should().Be("inline_entry");
        result.Target.Should().NotBeNull();
        result.Target!.ActorId.Should().Be("run-1");
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].WorkflowName.Should().Be("inline_entry");
        actorPort.CreateRunBindings[0].WorkflowYaml.Should().Be(entryWorkflowYaml);
        actorPort.CreateRunBindings[0].InlineWorkflowYamls.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldReturnAgentNotFound_WhenSourceActorBindingMissing()
    {
        var resolver = new WorkflowRunActorResolver(new StaticWorkflowActorBindingReader(null), new RecordingWorkflowRunActorPort(), new RecordingWorkflowRunActorPort(), new InMemoryWorkflowDefinitionCatalog());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.DefinitionActor("agent-404"), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.AgentNotFound);
        result.Target.Should().BeNull();
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldReturnAgentTypeNotSupported_WhenSourceActorIsUnsupported()
    {
        var resolver = new WorkflowRunActorResolver(new StaticWorkflowActorBindingReader(WorkflowActorBinding.Unsupported("agent-1")), new RecordingWorkflowRunActorPort(), new RecordingWorkflowRunActorPort(), new InMemoryWorkflowDefinitionCatalog());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.DefinitionActor("agent-1"), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.AgentTypeNotSupported);
        result.Target.Should().BeNull();
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldReturnAgentWorkflowNotConfigured_WhenBoundWorkflowNameMissing()
    {
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Run,
                    "agent-1",
                    string.Empty,
                    "run-1",
                    string.Empty,
                    string.Empty,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), ExternalCapabilityExecutionMode.Interactive)),
            new RecordingWorkflowRunActorPort(),
            new RecordingWorkflowRunActorPort(),
            new InMemoryWorkflowDefinitionCatalog());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.DefinitionActor("agent-1"), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.AgentWorkflowNotConfigured);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldReturnWorkflowBindingMismatch_WhenRequestedWorkflowDiffersFromBoundWorkflow()
    {
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Run,
                    "agent-1",
                    "definition-1",
                    "run-1",
                    "bound",
                    string.Empty,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), ExternalCapabilityExecutionMode.Interactive)),
            new RecordingWorkflowRunActorPort(),
            new RecordingWorkflowRunActorPort(),
            new InMemoryWorkflowDefinitionCatalog());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.DefinitionActor("agent-1", "requested"), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.WorkflowBindingMismatch);
        result.WorkflowNameForRun.Should().Be("bound");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldUseRegistryYaml_WhenSourceBindingHasWorkflowNameOnly()
    {
        var registry = new InMemoryWorkflowDefinitionCatalog();
        registry.Register(
            "direct",
            "name: direct\nroles: []\nsteps: []\n",
            ExternalCapabilityExecutionMode.Interactive);
        var actorPort = new RecordingWorkflowRunActorPort();
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Run,
                    "agent-1",
                    string.Empty,
                    "run-1",
                    "direct",
                    string.Empty,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), ExternalCapabilityExecutionMode.Interactive)),
            actorPort,
            actorPort,
            registry);

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.DefinitionActor("agent-1"), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].DefinitionActorId.Should().Be("definition-direct");
        actorPort.CreateRunBindings[0].WorkflowYaml.Should().Be("name: direct\nroles: []\nsteps: []\n");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldPreferSourceBindingYamlAndDefinitionActorId_WhenPresent()
    {
        var registry = new InMemoryWorkflowDefinitionCatalog();
        registry.Register(
            "direct",
            "name: direct\nroles: []\nsteps: []\n",
            ExternalCapabilityExecutionMode.Interactive);
        var actorPort = new RecordingWorkflowRunActorPort();
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Run,
                    "agent-1",
                    "definition-bound",
                    "run-1",
                    "direct",
                    "name: source\nroles: []\nsteps: []\n",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), ExternalCapabilityExecutionMode.Interactive)),
            actorPort,
            actorPort,
            registry);

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.DefinitionActor("agent-1"), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].DefinitionActorId.Should().Be("definition-bound");
        actorPort.CreateRunBindings[0].WorkflowYaml.Should().Be("name: source\nroles: []\nsteps: []\n");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldPreserveSourceBindingAdmissionPlanAndRevisionIdentity()
    {
        const string workflowYaml = "name: direct\nroles: []\nsteps: []\n";
        var admissionPlan = new WorkflowCapabilityAdmissionPlan
        {
            SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion,
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            DefinitionDigest = "definition-digest-alpha",
            AdmissionDigest = "admission-digest-alpha",
        };
        admissionPlan.InvocationAdmissions.Add(new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = "direct/request-alpha",
            NyxIdExplicitRequestGrant = new NyxIdExplicitRequestGrant
            {
                WorkflowId = "wf-alpha",
                RevisionId = "rev-alpha",
            },
        });
        var actorPort = new RecordingWorkflowRunActorPort();
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Definition,
                    "definition-alpha",
                    string.Empty,
                    string.Empty,
                    "direct",
                    workflowYaml,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                    CapabilityAdmissionPlan: admissionPlan,
                    WorkflowId: "wf-alpha",
                    RevisionId: "rev-alpha")),
            actorPort,
            actorPort,
            new InMemoryWorkflowDefinitionCatalog());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest(
                "hello",
                WorkflowChatSource.DefinitionActor("definition-alpha", "direct"),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        var runBinding = actorPort.CreateRunBindings.Should().ContainSingle().Which;
        runBinding.CapabilityAdmissionPlan.Should().Be(admissionPlan);
        runBinding.CapabilityAdmissionPlan.Should().NotBeSameAs(admissionPlan);
        runBinding.WorkflowId.Should().Be("wf-alpha");
        runBinding.RevisionId.Should().Be("rev-alpha");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldKeepExistingBindingForOpaqueActorId_WhileNewRunUsesLatestRegistryDefinition()
    {
        const string opaqueActorId = "script-runtime:legacy-worker-42";
        const string legacyYaml =
            """
            name: direct
            description: legacy implementation
            roles: []
            steps: []
            """;
        const string latestYaml =
            """
            name: direct
            description: latest implementation
            roles: []
            steps: []
            """;
        var bindingReader = new RecordingWorkflowActorBindingReader();
        bindingReader.Register(
            opaqueActorId,
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                opaqueActorId,
                "definition-direct-legacy",
                "run-legacy",
                "direct",
                legacyYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), ExternalCapabilityExecutionMode.Interactive));
        var actorPort = new RecordingWorkflowRunActorPort();
        var registry = new InMemoryWorkflowDefinitionCatalog();
        RegisterPublishedWorkflow(
            registry,
            actorPort,
            "direct",
            latestYaml,
            ExternalCapabilityExecutionMode.Interactive);
        var resolver = new WorkflowRunActorResolver(bindingReader, actorPort, actorPort, registry);

        var boundResult = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.DefinitionActor(opaqueActorId), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        boundResult.Error.Should().Be(WorkflowChatRunStartError.None);
        bindingReader.LastActorId.Should().Be(opaqueActorId);
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].DefinitionActorId.Should().Be("definition-direct-legacy");
        actorPort.CreateRunBindings[0].WorkflowName.Should().Be("direct");
        actorPort.CreateRunBindings[0].WorkflowYaml.Should().Be(legacyYaml);

        actorPort.CreateRunBindings.Clear();

        var freshResult = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct"), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        freshResult.Error.Should().Be(WorkflowChatRunStartError.None);
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].DefinitionActorId.Should().Be("definition-direct");
        actorPort.CreateRunBindings[0].WorkflowName.Should().Be("direct");
        actorPort.CreateRunBindings[0].WorkflowYaml.Should().Be(latestYaml);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldReturnAgentWorkflowNotConfigured_WhenBoundWorkflowYamlMissingEverywhere()
    {
        var actorPort = new RecordingWorkflowRunActorPort();
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Run,
                    "agent-1",
                    string.Empty,
                    "run-1",
                    "direct",
                    string.Empty,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), ExternalCapabilityExecutionMode.Interactive)),
            actorPort,
            actorPort,
            new InMemoryWorkflowDefinitionCatalog());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.DefinitionActor("agent-1"), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.AgentWorkflowNotConfigured);
        actorPort.CreateRunBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldWrapFallbackEligibleCreateFailures()
    {
        var actorPort = new RecordingWorkflowRunActorPort
        {
            CreateRunException = new InvalidOperationException("boom"),
        };
        var registry = new InMemoryWorkflowDefinitionCatalog();
        RegisterPublishedWorkflow(
            registry,
            actorPort,
            "direct",
            "name: direct\nroles: []\nsteps: []\n",
            ExternalCapabilityExecutionMode.Interactive);
        var resolver = new WorkflowRunActorResolver(new StaticWorkflowActorBindingReader(null), actorPort, actorPort, registry);

        var act = async () => await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct"), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<WorkflowDirectFallbackTriggerException>();
        ex.Which.InnerException.Should().Be(actorPort.CreateRunException);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldNotWrapCreateFailure_ForInlineWorkflowRun()
    {
        const string entryWorkflowYaml =
            """
            name: inline_entry
            roles: []
            steps: []
            """;
        var actorPort = new RecordingWorkflowRunActorPort
        {
            CreateRunException = new InvalidOperationException("inline failed"),
        };
        actorPort.ParseResults[entryWorkflowYaml] = WorkflowYamlParseResult.Success("inline_entry");
        var resolver = new WorkflowRunActorResolver(new StaticWorkflowActorBindingReader(null), actorPort, actorPort, new InMemoryWorkflowDefinitionCatalog());

        var act = async () => await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.InlineYamlBundle([entryWorkflowYaml]), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Be("inline failed");
    }

    private sealed class StaticWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        private readonly WorkflowActorBinding? _binding;

        public StaticWorkflowActorBindingReader(WorkflowActorBinding? binding)
        {
            _binding = binding;
        }

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_binding);
        }
    }

    private sealed class ThrowingWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        public int Calls { get; private set; }

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException($"Direct source must not read actor binding '{actorId}'.");
        }
    }

    private sealed class RecordingWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        private readonly Dictionary<string, WorkflowActorBinding> _bindings = new(StringComparer.Ordinal);

        public string? LastActorId { get; private set; }

        public void Register(string actorId, WorkflowActorBinding binding) =>
            _bindings[actorId] = binding;

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastActorId = actorId;
            return Task.FromResult(_bindings.GetValueOrDefault(actorId));
        }
    }

    private sealed class RecordingWorkflowRunActorPort : IWorkflowRunProvisioningPort, IWorkflowDefinitionParser
    {
        public Dictionary<string, WorkflowYamlParseResult> ParseResults { get; } = new(StringComparer.Ordinal);
        public List<WorkflowDefinitionBinding> CreateRunBindings { get; } = [];
        public Exception? CreateRunException { get; set; }
        public Task<WorkflowRunCreationReceipt> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (CreateRunException != null)
                throw CreateRunException;
            CreateRunBindings.Add(definition);
            return Task.FromResult(
                new WorkflowRunCreationReceipt("run-1",
                    "definition-isolated-1",
                    ["definition-isolated-1", "run-1"]));
        }

        public Task DestroyAsync(string actorId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task BindWorkflowDefinitionAsync(
            IActor actor,
            string workflowYaml,
            string workflowName,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
            string? scopeId = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task MarkStoppedAsync(
            string actorId,
            string runId,
            string reason,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                ParseResults.TryGetValue(workflowYaml, out var result)
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
                    return WorkflowInlineYamlBundleParseResult.Invalid(parseResult.Error, parseResult.ExternalCapabilityReadiness);

                var documentName = document.Name.Trim();
                if (!string.IsNullOrWhiteSpace(documentName) &&
                    !string.Equals(documentName, parseResult.WorkflowName, StringComparison.OrdinalIgnoreCase))
                {
                    return WorkflowInlineYamlBundleParseResult.Invalid(
                        $"workflowYamls[{i}] document name '{documentName}' does not match workflow name '{parseResult.WorkflowName}'.");
                }

                if (!workflowYamlsByName.TryAdd(parseResult.WorkflowName, document.Yaml))
                    return WorkflowInlineYamlBundleParseResult.Invalid($"Duplicate workflow name '{parseResult.WorkflowName}' in workflowYamls.");

                if (i == 0)
                {
                    entryWorkflowName = parseResult.WorkflowName;
                    entryWorkflowYaml = document.Yaml;
                }
            }

            return WorkflowInlineYamlBundleParseResult.Success(entryWorkflowName, entryWorkflowYaml, workflowYamlsByName);
        }
    }

    private sealed class ReadyExplicitRequestReadinessPort(NyxIdRequestSelector requestContract) :
        IExternalWorkflowCapabilityReadinessPort
    {
        public ExternalWorkflowCapabilityAccessContext? LastAccess { get; private set; }

        public Task<ExternalCapabilityReadiness> InspectAsync(
            InspectExternalWorkflowCapabilityReadinessRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastAccess = request.Access;
            var policy = new NyxIdOperationExecutionPolicy
            {
                Risk = NyxIdOperationRisk.ReadOnly,
                Approval = NyxIdOperationApproval.None,
                EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
            };
            policy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
            var requestDigest = WorkflowCapabilityAdmissionPlanIntegrity
                .ComputeNyxIdRequestContractDigest(requestContract);
            var readiness = new ExternalCapabilityReadiness
            {
                ExecutionMode = request.ExecutionMode,
                Status = ExternalCapabilityReadinessStatus.Ready,
                SelectedSelector = request.Selector.Clone(),
                SelectedCapability = new ExternalWorkflowCapabilityRef
                {
                    NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
                    {
                        Request = requestContract.Clone(),
                        ServiceSlugSnapshot = "x",
                        ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                            .ComputeNyxIdExplicitRequestProofDigest(requestDigest, "x"),
                        ExecutionPolicy = policy,
                    },
                },
            };
            var observedAt = DateTimeOffset.UtcNow;
            readiness.Sources.Add(new ExternalCapabilitySourceStamp
            {
                SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
                SourceId = "nyxid-user-services:caller:owner-alpha",
                ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
                FreshUntil = Timestamp.FromDateTimeOffset(observedAt.AddMinutes(5)),
                ContentDigest = "user-services-alpha",
            });
            return Task.FromResult(readiness);
        }
    }

    private sealed class StaticExternalCapabilityReadinessPort(ExternalCapabilityReadiness readiness) :
        IExternalWorkflowCapabilityReadinessPort
    {
        public Task<ExternalCapabilityReadiness> InspectAsync(
            InspectExternalWorkflowCapabilityReadinessRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = readiness.Clone();
            result.ExecutionMode = request.ExecutionMode;
            return Task.FromResult(result);
        }
    }

    private sealed class InMemoryWorkflowDefinitionCatalog : IWorkflowDefinitionCatalog
    {
        private readonly Dictionary<string, WorkflowDefinitionRegistration> _definitions = new(StringComparer.OrdinalIgnoreCase);

        public void Register(
            string name,
            string yaml,
            ExternalCapabilityExecutionMode expectedExecutionMode)
        {
            var normalizedName = name.Trim();
            _definitions[normalizedName] = new WorkflowDefinitionRegistration(
                normalizedName,
                yaml,
                $"definition-{normalizedName}",
                expectedExecutionMode,
                "test");
        }

        public WorkflowDefinitionRegistration? GetDefinition(string name) =>
            _definitions.TryGetValue(name, out var registration)
                ? registration
                : null;

        public string? GetYaml(string name) =>
            _definitions.TryGetValue(name, out var registration)
                ? registration.WorkflowYaml
                : null;

        public IReadOnlyList<string> GetNames() => _definitions.Keys.OrderBy(static x => x, StringComparer.Ordinal).ToArray();
    }

    private static void RegisterPublishedWorkflow(
        InMemoryWorkflowDefinitionCatalog registry,
        RecordingWorkflowRunActorPort parser,
        string name,
        string yaml,
        ExternalCapabilityExecutionMode expectedExecutionMode)
    {
        registry.Register(name, yaml, expectedExecutionMode);
        parser.ParseResults[yaml] = WorkflowYamlParseResult.Success(name);
    }

    private sealed class FakeActor : IActor
    {
        public FakeActor(string id)
        {
            Id = id;
            Agent = new FakeAgent(id + "-agent");
        }

        public string Id { get; }
        public IAgent Agent { get; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeAgent : IAgent
    {
        public FakeAgent(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("fake");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
