using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions.Tools;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.Tests;

public sealed class AgentTurnToolCatalogMaterializerTests
{
    private const string SkillGuid = "11111111-1111-1111-1111-111111111111";
    private const string SkillVersion = "1.2";
    private const string SkillName = "skill-alpha";
    private const string PublisherId = "publisher-alpha";
    private const string SkillMarkdown = "---\nname: skill-alpha\n---\nSelected instructions.";
    private static readonly ByteString SkillSha256 =
        ByteString.CopyFrom(Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());

    [Fact]
    public async Task TurnIntentClassifier_ExactServiceConnectPrompt_ShouldReturnTypedIntent()
    {
        var inner = new RecordingClassifier(
            AgentProfileTurnClassificationResult.Matched(
                NyxIdChatTurnIntentClassifier.ServiceConnectIntentId));
        var classifier = new NyxIdChatTurnIntentClassifier(inner);
        var llmControl = new LLMControlContext(
            "caller-token",
            null,
            null,
            null,
            null,
            null,
            null);

        var intent = await classifier.ClassifyAsync(
            "turn-connect",
            "Connect GitHub and verify the connection",
            llmControl,
            CancellationToken.None);

        intent.Should().Be(NyxIdChatTurnIntent.ServiceConnect);
        inner.LastRequest.Should().NotBeNull();
        var request = inner.LastRequest!;
        request.RequestId.Should().Be("turn-connect");
        request.LlmControl.Should().BeSameAs(llmControl);
        request.Timeout.Should().Be(TimeSpan.FromSeconds(15));
        request.UserMessage.Should().Be("Connect GitHub and verify the connection");
        request.Candidates.Select(static candidate => candidate.IntentId).Should().Equal(
            NyxIdChatTurnIntentClassifier.ServiceConnectIntentId,
            NyxIdChatTurnIntentClassifier.KeyCreateIntentId,
            NyxIdChatTurnIntentClassifier.KeyRotateIntentId,
            NyxIdChatTurnIntentClassifier.WorkflowAuthoringIntentId);
        request.Candidates.Single(candidate =>
                candidate.IntentId == NyxIdChatTurnIntentClassifier.ServiceConnectIntentId)
            .RoutingDescription.Should()
            .Contain("missing hosted external service account connection")
            .And.Contain("already-connected exact UserService")
            .And.Contain("ordinary task route");
    }

    [Fact]
    public async Task TurnIntentClassifier_KeyCreationPrompt_ShouldReturnTypedIntent()
    {
        var inner = new RecordingClassifier(
            AgentProfileTurnClassificationResult.Matched(
                NyxIdChatTurnIntentClassifier.KeyCreateIntentId));
        var classifier = new NyxIdChatTurnIntentClassifier(inner);

        var intent = await classifier.ClassifyAsync(
            "turn-key-create",
            "Create a NyxID key limited to GitHub",
            llmControl: null,
            CancellationToken.None);

        intent.Should().Be(NyxIdChatTurnIntent.KeyCreate);
        inner.LastRequest!.Candidates.Should().Contain(candidate =>
            candidate.IntentId == NyxIdChatTurnIntentClassifier.KeyCreateIntentId &&
            candidate.SideEffectClass == AgentProfileSideEffectClass.ExternalHandoff);
    }

    [Fact]
    public async Task TurnIntentClassifier_KeyRotationPrompt_ShouldReturnTypedIntent()
    {
        var inner = new RecordingClassifier(
            AgentProfileTurnClassificationResult.Matched(
                NyxIdChatTurnIntentClassifier.KeyRotateIntentId));
        var classifier = new NyxIdChatTurnIntentClassifier(inner);

        var intent = await classifier.ClassifyAsync(
            "turn-key-rotate",
            "Rotate the exact NyxID key",
            llmControl: null,
            CancellationToken.None);

        intent.Should().Be(NyxIdChatTurnIntent.KeyRotate);
        inner.LastRequest!.Candidates.Should().Contain(candidate =>
            candidate.IntentId == NyxIdChatTurnIntentClassifier.KeyRotateIntentId &&
            candidate.SideEffectClass == AgentProfileSideEffectClass.ExternalHandoff);
    }

    [Fact]
    public async Task TurnIntentClassifier_WorkflowAuthoringPrompt_ShouldReturnReadOnlyTypedIntent()
    {
        var inner = new RecordingClassifier(
            AgentProfileTurnClassificationResult.Matched(
                NyxIdChatTurnIntentClassifier.WorkflowAuthoringIntentId));
        var classifier = new NyxIdChatTurnIntentClassifier(inner);

        var intent = await classifier.ClassifyAsync(
            "turn-workflow-authoring",
            "Create a workflow draft that calls my GitHub service",
            llmControl: null,
            CancellationToken.None);

        intent.Should().Be(NyxIdChatTurnIntent.WorkflowAuthoring);
        inner.LastRequest!.Candidates.Should().Contain(candidate =>
            candidate.IntentId == NyxIdChatTurnIntentClassifier.WorkflowAuthoringIntentId &&
            candidate.SideEffectClass == AgentProfileSideEffectClass.ReadOnly &&
            candidate.RoutingDescription.Contains("Workflow YAML"));
    }

    [Fact]
    public async Task MaterializeBuiltInIntentAsync_ServiceConnect_ShouldUseRequestLocalDiscoveryAndExposeOnlyAdmissionTools()
    {
        var catalogTool = new TestTool("nyxid_catalog");
        var requireServiceTool = new TestTool("nyxid_require_service");
        var unrelatedTool = new TestTool("nyxid_services");
        var source = new TokenBoundToolSource(
            "request-token",
            [catalogTool, requireServiceTool, unrelatedTool]);
        var registry = new RecordingToolSetRegistry();
        registry.Add(ToolSetNames.NyxIdAssistantAdmission, source);
        var materializer = NewMaterializer(
            registry,
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
            fetcher: null);

        var catalog = await materializer.MaterializeBuiltInIntentAsync(
            NyxIdChatTurnIntent.ServiceConnect,
            ToolContext("request-token"),
            CancellationToken.None);

        registry.ResolveCalls.Should().Equal(ToolSetNames.NyxIdAssistantAdmission);
        source.ObservedTokens.Should().Equal("request-token");
        catalog.FinalAllowedToolNames.Should().BeEquivalentTo(
            "nyxid_catalog",
            "nyxid_require_service");
        catalog.ExactTools.Keys.Should().BeEquivalentTo(
            "nyxid_catalog",
            "nyxid_require_service");
        catalog.ExactTools["nyxid_catalog"].Should().BeSameAs(catalogTool);
        catalog.ExactTools["nyxid_require_service"].Should().BeSameAs(requireServiceTool);
        catalog.SelectedIntentId.Should().Be(NyxIdChatTurnIntentClassifier.ServiceConnectIntentId);
        catalog.CandidateIntentId.Should().Be(NyxIdChatTurnIntentClassifier.ServiceConnectIntentId);
    }

    [Fact]
    public async Task MaterializeBuiltInIntentAsync_KeyCreate_ShouldExposeInventoryAndTypedProducerOnly()
    {
        var servicesTool = new TestTool("nyxid_services");
        var keyCreateTool = new TestTool("nyxid_request_key_create");
        var unrelatedTool = new TestTool("nyxid_catalog");
        var source = new TokenBoundToolSource(
            "request-token",
            [servicesTool, keyCreateTool, unrelatedTool]);
        var registry = new RecordingToolSetRegistry();
        registry.Add(ToolSetNames.NyxIdAssistantAdmission, source);
        var materializer = NewMaterializer(
            registry,
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
            fetcher: null);

        var catalog = await materializer.MaterializeBuiltInIntentAsync(
            NyxIdChatTurnIntent.KeyCreate,
            ToolContext("request-token"),
            CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo(
            "nyxid_services",
            "nyxid_request_key_create");
        catalog.ExactTools["nyxid_services"].Should().BeSameAs(servicesTool);
        catalog.ExactTools["nyxid_request_key_create"].Should().BeSameAs(keyCreateTool);
        catalog.SelectedIntentId.Should().Be(NyxIdChatTurnIntentClassifier.KeyCreateIntentId);
    }

    [Fact]
    public async Task MaterializeBuiltInIntentAsync_KeyCreateWithVerifiedHumanDelegation_ShouldUseRealAssistantSource()
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var client = new NyxIdApiClient(options, new HttpClient());
        var registry = new RecordingToolSetRegistry();
        registry.Add(
            ToolSetNames.NyxIdAssistantAdmission,
            new NyxIdAssistantToolSource(options, client));
        var materializer = NewMaterializer(
            registry,
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
            fetcher: null);
        var toolContext = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "proxy-delegation",
                null,
                null,
                AgentToolNyxIdCredentialKind.ProxyDelegation),
            Caller = new AgentToolCallerContext(
                "scope-alpha",
                "owner-alpha",
                "turn-alpha",
                "scope-alpha"),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "nyxid",
                null,
                "owner-alpha",
                "proxy"),
            InvocationSurface = AgentToolInvocationSurface.HumanSession,
            Chat = new AgentChatInvocationContext(
                AgentChatInvocationSurface.NyxIdAssistant,
                "conversation-alpha",
                "turn-alpha",
                "task-alpha",
                null,
                null),
        };

        var catalog = await materializer.MaterializeBuiltInIntentAsync(
            NyxIdChatTurnIntent.KeyCreate,
            toolContext,
            CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo(
            "nyxid_services",
            "nyxid_request_key_create");
        catalog.ExactTools.Keys.Should().BeEquivalentTo(
            "nyxid_services",
            "nyxid_request_key_create");
        catalog.Diagnostics.Should().NotContain(static diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolCapabilityRejected);
    }

    [Fact]
    public async Task MaterializeBuiltInIntentAsync_KeyRotate_ShouldExposeInventoryAndTypedProducerOnly()
    {
        var keysTool = new TestTool("nyxid_api_keys");
        var keyRotateTool = new TestTool("nyxid_request_key_rotate");
        var unrelatedTool = new TestTool("nyxid_catalog");
        var source = new TokenBoundToolSource(
            "request-token",
            [keysTool, keyRotateTool, unrelatedTool]);
        var registry = new RecordingToolSetRegistry();
        registry.Add(ToolSetNames.NyxIdAssistantAdmission, source);
        var materializer = NewMaterializer(
            registry,
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
            fetcher: null);

        var catalog = await materializer.MaterializeBuiltInIntentAsync(
            NyxIdChatTurnIntent.KeyRotate,
            ToolContext("request-token"),
            CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo(
            "nyxid_api_keys",
            "nyxid_request_key_rotate");
        catalog.ExactTools["nyxid_api_keys"].Should().BeSameAs(keysTool);
        catalog.ExactTools["nyxid_request_key_rotate"].Should().BeSameAs(keyRotateTool);
        catalog.SelectedIntentId.Should().Be(NyxIdChatTurnIntentClassifier.KeyRotateIntentId);
    }

    [Fact]
    public async Task MaterializeBuiltInIntentAsync_WorkflowAuthoring_ShouldExposeOnlyDedicatedReadTools()
    {
        IAgentTool[] tools =
        [
            new ReadOnlyTestTool("list_external_workflow_capabilities"),
            new ReadOnlyTestTool("inspect_external_workflow_capability_readiness"),
            new ReadOnlyTestTool("preview_workflow_explicit_requests"),
        ];
        var registry = new RecordingToolSetRegistry();
        registry.Add(
            ToolSetNames.WorkflowExternalCapabilityAuthoring,
            new StaticToolSource(tools));
        var materializer = NewMaterializer(
            registry,
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
            fetcher: null);

        var catalog = await materializer.MaterializeBuiltInIntentAsync(
            NyxIdChatTurnIntent.WorkflowAuthoring,
            ToolContext("request-token"),
            CancellationToken.None);

        registry.ResolveCalls.Should().Equal(ToolSetNames.WorkflowExternalCapabilityAuthoring);
        catalog.FinalAllowedToolNames.Should().BeEquivalentTo(
            "list_external_workflow_capabilities",
            "inspect_external_workflow_capability_readiness",
            "preview_workflow_explicit_requests");
        catalog.ExactTools.Values.Should().OnlyContain(static tool => tool.IsReadOnly);
        catalog.SelectedIntentId.Should().Be(
            NyxIdChatTurnIntentClassifier.WorkflowAuthoringIntentId);
    }

    [Fact]
    public async Task MaterializeUnprofiledBaselineAsync_ShouldExposeReviewedClassRReadSurface()
    {
        var reviewedNames = new[]
        {
            "nyxid_services",
            "nyxid_api_keys",
            "nyxid_nodes",
            "nyxid_account",
            "nyxid_status",
            "nyxid_catalog",
            "nyxid_require_service",
            "ask_user",
            "use_skill",
            "ornn_search_skills",
        };
        var tools = reviewedNames
            .Select(static name => (IAgentTool)new TestTool(name))
            .Append(new TestTool("nyxid_proxy"))
            .ToArray();
        var source = new TokenBoundToolSource("request-token", tools);
        var registry = new RecordingToolSetRegistry();
        registry.Add(ToolSetNames.NyxIdChatBaseline, source);
        var materializer = NewMaterializer(
            registry,
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
            fetcher: null);

        var catalog = await materializer.MaterializeUnprofiledBaselineAsync(
            ToolContext("request-token"),
            CancellationToken.None);

        registry.ResolveCalls.Should().Equal(ToolSetNames.NyxIdChatBaseline);
        source.ObservedTokens.Should().Equal("request-token");
        catalog.FinalAllowedToolNames.Should().BeEquivalentTo(reviewedNames);
        catalog.ExactTools.Keys.Should().BeEquivalentTo(reviewedNames);
        catalog.ExactTools.Keys.Should().NotContain("nyxid_proxy");
        catalog.SelectedIntentId.Should().Be(
            AgentTurnToolCatalogMaterializer.UnprofiledBaselineIntentId);
        catalog.CandidateIntentId.Should().Be(
            AgentTurnToolCatalogMaterializer.UnprofiledBaselineIntentId);
    }

    [Fact]
    public async Task MaterializeUnprofiledBaselineAsync_ShouldSelectRelevantReadOnlyConnectedOperation()
    {
        var baselineSource = new StaticToolSource(
        [
            new TestTool("nyxid_services"),
            new TestTool("use_skill"),
        ]);
        var routeSource = new StaticToolSource(
        [
            new TestTool("nyxid_services"),
            new TestTool("use_skill"),
            new AdmittedTestTool(
                "nyxop_user_context_read",
                CreateReadAdmission(
                    "us-context-alpha",
                    "user-context-alpha",
                    "readDiningProfileContext",
                    "user-context")),
            new AdmittedTestTool(
                "nyxop_user_context_write",
                CreateWriteAdmission(
                    "us-context-alpha",
                    "user-context-alpha",
                    "updateDiningProfileContext",
                    "user-context")),
        ]);
        var registry = new RecordingToolSetRegistry();
        registry.Add(ToolSetNames.NyxIdChatBaseline, baselineSource);
        registry.Add(AgentProfilePolicies.NyxIdChatRouteToolSet, routeSource);
        var connectedSelector = new RecordingConnectedOperationSelector(request =>
            AgentProfileConnectedOperationSelectionResult.Selected(
            [request.Candidates.Single(candidate =>
                candidate.DisplayName == "nyxop_user_context_read").CandidateId]));
        var materializer = NewMaterializer(
            registry,
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
            fetcher: null,
            connectedOperationSelector: connectedSelector);

        var catalog = await materializer.MaterializeUnprofiledBaselineAsync(
            ToolContext(),
            "Use my connected dining profile context for dinner booking.",
            llmControl: null,
            CancellationToken.None);

        registry.ResolveCalls.Should().Equal(
            ToolSetNames.NyxIdChatBaseline,
            AgentProfilePolicies.NyxIdChatRouteToolSet);
        catalog.FinalAllowedToolNames.Should().BeEquivalentTo(
            "nyxid_services",
            "use_skill",
            "nyxop_user_context_read");
        catalog.FinalAllowedToolNames.Should().NotContain("nyxop_user_context_write");
        catalog.ExactTools.Keys.Should().Contain("nyxop_user_context_read");
        connectedSelector.CallCount.Should().Be(1);
        connectedSelector.LastRequest!.MaximumReadSelections.Should().Be(3);
        connectedSelector.LastRequest.MaximumWriteSelections.Should().Be(0);
        connectedSelector.LastRequest.Candidates.Should().ContainSingle(candidate =>
            candidate.DisplayName == "nyxop_user_context_read" &&
            candidate.Risk == AgentToolOperationRisk.ReadOnly);
    }

    [Fact]
    public async Task MaterializeUnprofiledBaselineAsync_ShouldKeepDiningReadCandidateForDinnerRequest()
    {
        var baselineSource = new StaticToolSource(
        [
            new TestTool("nyxid_services"),
            new TestTool("use_skill"),
        ]);
        var broadContextReads = Enumerable.Range(0, 40)
            .Select(index => (IAgentTool)new AdmittedTestTool(
                $"nyxop_profile_context_{index:D2}",
                CreateReadAdmission(
                    $"us-context-{index:D2}",
                    $"api-context-{index:D2}",
                    $"readProfileContext{index:D2}",
                    "context-service")));
        var relevantRead = new AdmittedTestTool(
            "nyxop_dining_profile_context_read",
            CreateReadAdmission(
                "us-dining-alpha",
                "user-context-alpha",
                "readDiningProfileContext",
                "user-context"));
        var routeSource = new StaticToolSource(
        [
            new TestTool("nyxid_services"),
            new TestTool("use_skill"),
            .. broadContextReads,
            relevantRead,
        ]);
        var registry = new RecordingToolSetRegistry();
        registry.Add(ToolSetNames.NyxIdChatBaseline, baselineSource);
        registry.Add(AgentProfilePolicies.NyxIdChatRouteToolSet, routeSource);
        var connectedSelector = new RecordingConnectedOperationSelector(request =>
            AgentProfileConnectedOperationSelectionResult.Selected(
            [request.Candidates.Single(candidate =>
                candidate.DisplayName == "nyxop_dining_profile_context_read").CandidateId]));
        var materializer = NewMaterializer(
            registry,
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
            fetcher: null,
            connectedOperationSelector: connectedSelector);

        var catalog = await materializer.MaterializeUnprofiledBaselineAsync(
            ToolContext(),
            "Plan a dinner date this week.",
            llmControl: null,
            CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().Contain("nyxop_dining_profile_context_read");
        connectedSelector.CallCount.Should().Be(1);
        connectedSelector.LastRequest!.Candidates.Should().HaveCount(32);
        connectedSelector.LastRequest.Candidates.Should().Contain(candidate =>
            candidate.DisplayName == "nyxop_dining_profile_context_read");
    }

    [Fact]
    public async Task MaterializeRouteToolChoiceHintAsync_ShouldSelectOnlyHintedTool()
    {
        var source = new StaticToolSource(
        [
            new TestTool("aevatar_start_workflow"),
            new TestTool("ask_user"),
        ]);
        var registry = new RecordingToolSetRegistry();
        registry.Add(AgentProfilePolicies.NyxIdChatRouteToolSet, source);
        var materializer = NewMaterializer(
            registry,
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
            fetcher: null);

        var catalog = await materializer.MaterializeRouteToolChoiceHintAsync(
            AgentProfilePolicies.NyxIdChatRouteToolSet,
            "aevatar_start_workflow",
            ToolContext(),
            CancellationToken.None);

        registry.ResolveCalls.Should().Equal(AgentProfilePolicies.NyxIdChatRouteToolSet);
        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("aevatar_start_workflow");
        catalog.ExactTools.Keys.Should().BeEquivalentTo("aevatar_start_workflow");
        catalog.ExactTools.Keys.Should().NotContain("ask_user");
        catalog.SelectedIntentId.Should().Be(
            AgentTurnToolCatalogMaterializer.RouteToolChoiceHintIntentId);
        catalog.CandidateIntentId.Should().Be(
            AgentTurnToolCatalogMaterializer.RouteToolChoiceHintIntentId);
    }

    [Fact]
    public async Task MaterializeUnprofiledBaselineAsync_WithPartialComposition_ShouldDegradePerTool()
    {
        var source = new StaticToolSource(
            [new TestTool("nyxid_services"), new TestTool("ask_user")]);
        var registry = new RecordingToolSetRegistry();
        registry.Add(ToolSetNames.NyxIdChatBaseline, source);
        var materializer = NewMaterializer(
            registry,
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
            fetcher: null);

        var catalog = await materializer.MaterializeUnprofiledBaselineAsync(
            ToolContext(),
            CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo(
            "nyxid_services",
            "ask_user");
    }

    [Fact]
    public async Task MaterializeUnprofiledBaselineAsync_ShouldDegradeIneligibleToolsIndividually()
    {
        // A human-session-only read on a turn without a resolvable human
        // bearer drops on its own; the remaining reviewed tools still ship.
        var source = new StaticToolSource(
        [
            new CapabilityTool("nyxid_services", [AgentToolCapabilities.RequiresHumanSession]),
            new TestTool("nyxid_catalog"),
            new TestTool("nyxid_require_service"),
            new TestTool("ask_user"),
        ]);
        var registry = new RecordingToolSetRegistry();
        registry.Add(ToolSetNames.NyxIdChatBaseline, source);
        var materializer = NewMaterializer(
            registry,
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
            fetcher: null);

        var catalog = await materializer.MaterializeUnprofiledBaselineAsync(
            ToolContext(accessToken: null),
            CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo(
            "nyxid_catalog",
            "nyxid_require_service",
            "ask_user");
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolCapabilityRejected &&
            diagnostic.Detail == "nyxid_services");
    }

    [Fact]
    public async Task MaterializeUnprofiledBaselineAsync_WhenToolSetIsUnavailable_ShouldRestrictEmpty()
    {
        var materializer = NewMaterializer(
            new RecordingToolSetRegistry(),
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
            fetcher: null);

        var catalog = await materializer.MaterializeUnprofiledBaselineAsync(
            ToolContext(),
            CancellationToken.None);

        catalog.ExactTools.Should().BeEmpty();
        catalog.FinalAllowedToolNames.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializeBuiltInIntentAsync_WhenAdmissionToolIsMissing_ShouldFailClosed()
    {
        var registry = new RecordingToolSetRegistry();
        registry.Add(
            ToolSetNames.NyxIdAssistantAdmission,
            new StaticToolSource([new TestTool("nyxid_catalog"), new TestTool("nyxid_services")]));
        var materializer = NewMaterializer(
            registry,
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
            fetcher: null);

        var catalog = await materializer.MaterializeBuiltInIntentAsync(
            NyxIdChatTurnIntent.ServiceConnect,
            ToolContext(),
            CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.ExactTools.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareNyxIdChatAsync_ServiceConnectNoMatch_ShouldContinueWithProfileClassification()
    {
        var tools = NewTools("recovery", "task", "extra");
        var classifier = new SequencedClassifier(
            AgentProfileTurnClassificationResult.NoMatch(),
            AgentProfileTurnClassificationResult.Matched("intent-alpha"));

        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                classifier,
                fetcher: null)
            .PrepareNyxIdChatAsync(
                SealProfile(BuildProfile()),
                "session-a",
                "Run the alpha report",
                tools,
                ToolContext(),
                llmControl: null,
                CancellationToken.None);

        preparation.Authority.CandidateRoute!.IntentId.Should().Be("intent-alpha");
        classifier.Requests.Should().HaveCount(2);
        classifier.Requests[0].Candidates.Select(static candidate => candidate.IntentId).Should().Equal(
            NyxIdChatTurnIntentClassifier.ServiceConnectIntentId,
            NyxIdChatTurnIntentClassifier.KeyCreateIntentId,
            NyxIdChatTurnIntentClassifier.KeyRotateIntentId,
            NyxIdChatTurnIntentClassifier.WorkflowAuthoringIntentId,
            AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId);
        classifier.Requests[1].Candidates.Should().ContainSingle().Which.IntentId.Should()
            .Be("intent-alpha");
    }

    [Fact]
    public async Task PrepareNyxIdChatAsync_BuiltInClassifierFailure_ShouldFailClosedWithoutProfileClassification()
    {
        var tools = NewTools("recovery", "task", "extra");
        var classifier = new SequencedClassifier(
            AgentProfileTurnClassificationResult.Failed("provider_failure"),
            AgentProfileTurnClassificationResult.Matched("intent-alpha"));

        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                classifier,
                fetcher: null)
            .PrepareNyxIdChatAsync(
                SealProfile(BuildProfile()),
                "session-built-in-classifier-failure",
                "Run the alpha report",
                tools,
                ToolContext(),
                llmControl: null,
                CancellationToken.None);

        preparation.Authority.CandidateRoute.Should().BeNull();
        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        preparation.Authority.AuthorityCeilingToolNames.Should().Equal("recovery");
        preparation.Authority.DegradationReasons.Should().Equal(
            AgentProfileTurnDegradationReason.ClassifierFailed);
        preparation.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierFailed &&
            diagnostic.Detail == "provider_failure");
        classifier.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task PrepareNyxIdChatAsync_ProfileMemberNoMatch_ShouldKeepOrdinaryBaselineWithinProfileSurface()
    {
        // Mirrors the production #3532 shape: an enforced profile whose members
        // are ops skills, an empty recovery policy, and an ordinary read
        // question that matches the ordinary task route but no exact member.
        var tools = NewTools("nyxid_services", "use_skill", "task", "extra");
        var profile = BuildProfile();
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add(["nyxid_services", "use_skill", "task"]);
        profile.RecoveryToolPolicy.ToolNames.Clear();
        var classifier = new SequencedClassifier(
            AgentProfileTurnClassificationResult.Matched(
                AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId),
            AgentProfileTurnClassificationResult.NoMatch());

        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                classifier,
                fetcher: null)
            .PrepareNyxIdChatAsync(
                SealProfile(profile),
                "session-member-no-match",
                "what NyxID services are connected to my account?",
                tools,
                ToolContext(),
                llmControl: null,
                CancellationToken.None);

        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        preparation.Authority.AuthorityCeilingToolNames.Should().BeEquivalentTo(
            "nyxid_services",
            "use_skill");
        preparation.Authority.CandidateRoute.Should().BeNull();
        preparation.Diagnostics.Should().Contain(static diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierNoMatch);
    }

    [Fact]
    public async Task PrepareNyxIdChatAsync_EmptyMembersDinnerRequest_ShouldKeepManagedWorkflowExecutionTools()
    {
        var unrelatedReads = Enumerable.Range(1, StreamingAgentProfileConnectedOperationSelector.MaximumCandidates + 16)
            .Select(index => (IAgentTool)new AdmittedTestTool(
                $"nyxop_inventory_read_{index:D2}",
                CreateReadAdmission(
                    $"us-inventory-{index:D2}",
                    $"inventory-service-{index:D2}",
                    $"readInventory{index:D2}")))
            .ToArray();
        IAgentTool[] tools =
        [
            new TestTool("ask_user"),
            new TestTool("aevatar_start_workflow"),
            new TestTool("aevatar_observe_run"),
            new TestTool("aevatar_read_workflow_run_artifact"),
            new TestTool("nyxid_services"),
            .. unrelatedReads,
            new AdmittedTestTool(
                "nyxop_current_user_dining_context_read",
                CreateReadAdmission(
                    "us-context-current",
                    "current-user-context",
                    "readDiningProfileContext")),
            new AdmittedTestTool(
                "nyxop_current_user_dining_context_write",
                CreateWriteAdmission(
                    "us-context-current",
                    "current-user-context",
                    "updateDiningProfileContext",
                    "user-context")),
        ];
        var profile = BuildProfile();
        profile.Instructions = "For dinner reservation requests, start workflow_id dinner_date.";
        profile.Members.Clear();
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add([
            "ask_user",
            "aevatar_start_workflow",
            "aevatar_observe_run",
            "aevatar_read_workflow_run_artifact",
            "nyxid_services",
        ]);
        profile.MaximumToolPolicy.ConnectedServiceSelectors.Add(
            ConnectedServiceSelector(string.Empty, AgentToolOperationRiskPayload.ReadOnly));
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.RecoveryToolPolicy.ToolNames.Add([
            "ask_user",
            "aevatar_observe_run",
            "aevatar_read_workflow_run_artifact",
        ]);
        var sealedProfile = SealProfile(profile);
        var connectedSelector = new RecordingConnectedOperationSelector(request =>
            AgentProfileConnectedOperationSelectionResult.Selected(
            [request.Candidates.Single(candidate =>
                candidate.DisplayName == "nyxop_current_user_dining_context_read").CandidateId]));
        var materializer = NewMaterializer(
            RegistryWithRoute(tools),
            new SequencedClassifier(
                AgentProfileTurnClassificationResult.Matched(
                    AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId),
                AgentProfileTurnClassificationResult.Failed("classifier_not_configured")),
            fetcher: null,
            connectedOperationSelector: connectedSelector);

        var preparation = await materializer.PrepareNyxIdChatAsync(
            sealedProfile,
            "session-dinner-empty-members",
            "帮我订周五 19:30 Keong Saik 附近两个人的晚餐",
            tools,
            ToolContext(),
            llmControl: null,
            CancellationToken.None);
        var materialization = await materializer.MaterializeCommittedAsync(
            sealedProfile,
            preparation.Authority,
            accessToken: null,
            tools,
            ToolContext(),
            CancellationToken.None);

        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Selected);
        preparation.Authority.CandidateRoute!.IntentId.Should()
            .Be(AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId);
        preparation.Authority.AuthorityCeilingToolNames.Should().BeEquivalentTo(
            "ask_user",
            "aevatar_start_workflow",
            "aevatar_observe_run",
            "aevatar_read_workflow_run_artifact",
            "nyxid_services",
            "nyxop_current_user_dining_context_read");
        preparation.Authority.AuthorityCeilingToolNames.Should().NotContain(
            "nyxop_current_user_dining_context_write");
        materialization.Catalog.FinalAllowedToolNames.Should().BeEquivalentTo(
            preparation.Authority.AuthorityCeilingToolNames);
        materialization.Catalog.ExactTools.Keys.Should().BeEquivalentTo(
            preparation.Authority.AuthorityCeilingToolNames);
        connectedSelector.CallCount.Should().Be(1);
        connectedSelector.LastRequest!.Candidates.Should().HaveCountLessThanOrEqualTo(
            StreamingAgentProfileConnectedOperationSelector.MaximumCandidates);
        connectedSelector.LastRequest.Candidates.Should().ContainSingle(candidate =>
            candidate.DisplayName == "nyxop_current_user_dining_context_read");
        materialization.Catalog.ProfilePromptLayer.Should().NotBeNull();
        materialization.Catalog.ProfilePromptLayer!.Content.Should()
            .Contain("Instructions:")
            .And.Contain("For dinner reservation requests, start workflow_id dinner_date.");
    }

    [Fact]
    public async Task PrepareNyxIdChatAsync_ProfileMemberNoMatch_ShouldSelectRelevantReadOnlyConnectedOperation()
    {
        IAgentTool[] tools =
        [
            new TestTool("nyxid_services"),
            new TestTool("use_skill"),
            new AdmittedTestTool(
                "nyxop_user_context_read",
                CreateReadAdmission(
                    "us-context-alpha",
                    "user-context-alpha",
                    "readDiningProfileContext",
                    "user-context")),
            new AdmittedTestTool(
                "nyxop_user_context_write",
                CreateWriteAdmission(
                    "us-context-alpha",
                    "user-context-alpha",
                    "updateDiningProfileContext",
                    "user-context")),
        ];
        var profile = BuildProfile();
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add([
            "nyxid_services",
            "use_skill",
            "nyxop_user_context_read",
            "nyxop_user_context_write",
        ]);
        profile.RecoveryToolPolicy.ToolNames.Clear();
        var classifier = new SequencedClassifier(
            AgentProfileTurnClassificationResult.Matched(
                AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId),
            AgentProfileTurnClassificationResult.NoMatch());
        var connectedSelector = new RecordingConnectedOperationSelector(request =>
            AgentProfileConnectedOperationSelectionResult.Selected(
            [request.Candidates.Single(candidate =>
                candidate.DisplayName == "nyxop_user_context_read").CandidateId]));
        var sealedProfile = SealProfile(profile);
        var materializer = NewMaterializer(
            RegistryWithRoute(tools),
            classifier,
            fetcher: null,
            connectedOperationSelector: connectedSelector);

        var preparation = await materializer.PrepareNyxIdChatAsync(
            sealedProfile,
            "session-member-no-match-connected-read",
            "I want to book dinner on Tuesday; use my connected profile context if relevant.",
            tools,
            ToolContext(),
            llmControl: null,
            CancellationToken.None);
        var materialization = await materializer.MaterializeCommittedAsync(
            sealedProfile,
            preparation.Authority,
            accessToken: null,
            tools,
            ToolContext(),
            CancellationToken.None);

        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        preparation.Authority.AuthorityCeilingToolNames.Should().BeEquivalentTo(
            "nyxid_services",
            "use_skill",
            "nyxop_user_context_read");
        preparation.Authority.AuthorityCeilingToolNames.Should().NotContain("nyxop_user_context_write");
        materialization.Catalog.FinalAllowedToolNames.Should().BeEquivalentTo(
            "nyxid_services",
            "use_skill",
            "nyxop_user_context_read");
        materialization.Catalog.ExactTools.Keys.Should().Contain("nyxop_user_context_read");
        connectedSelector.CallCount.Should().Be(1);
        connectedSelector.LastRequest!.MaximumReadSelections.Should().Be(3);
        connectedSelector.LastRequest.MaximumWriteSelections.Should().Be(0);
        connectedSelector.LastRequest.Candidates.Should().ContainSingle(candidate =>
            candidate.DisplayName == "nyxop_user_context_read" &&
            candidate.Risk == AgentToolOperationRisk.ReadOnly);
    }

    [Fact]
    public async Task PrepareNyxIdChatAsync_ProfileClassifierFailureWithEmptyRecovery_ShouldKeepOrdinaryBaseline()
    {
        var tools = NewTools("nyxid_services", "use_skill", "task", "extra");
        var profile = BuildProfile();
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add(["nyxid_services", "use_skill", "task"]);
        profile.RecoveryToolPolicy.ToolNames.Clear();
        var classifier = new SequencedClassifier(
            AgentProfileTurnClassificationResult.Matched(
                AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId),
            AgentProfileTurnClassificationResult.Failed("provider_failure"));

        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                classifier,
                fetcher: null)
            .PrepareNyxIdChatAsync(
                SealProfile(profile),
                "session-member-classifier-failure",
                "check the issues assigned to me on my github",
                tools,
                ToolContext(),
                llmControl: null,
                CancellationToken.None);

        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        preparation.Authority.AuthorityCeilingToolNames.Should().BeEquivalentTo(
            "nyxid_services",
            "use_skill");
        preparation.Diagnostics.Should().Contain(static diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierFailed);
    }

    [Fact]
    public async Task PrepareNyxIdChatAsync_SelectedMemberWithEmptyPolicies_ShouldKeepOrdinaryBaseline()
    {
        // Mirrors the production #3532 catch-all shape: classification selects a
        // member whose task policy and the profile recovery policy are both
        // empty, so the sealed selection alone would admit zero tools.
        var tools = NewTools("nyxid_services", "use_skill", "task", "extra");
        var profile = BuildProfile();
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add(["nyxid_services", "use_skill", "task"]);
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        var classifier = new SequencedClassifier(
            AgentProfileTurnClassificationResult.Matched(
                AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId),
            AgentProfileTurnClassificationResult.Matched("intent-alpha"));

        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                classifier,
                fetcher: null)
            .PrepareNyxIdChatAsync(
                SealProfile(profile),
                "session-selected-empty-policy",
                "what NyxID services are connected to my account?",
                tools,
                ToolContext(),
                llmControl: null,
                CancellationToken.None);

        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Selected);
        preparation.Authority.CandidateRoute!.IntentId.Should().Be("intent-alpha");
        preparation.Authority.AuthorityCeilingToolNames.Should().BeEquivalentTo(
            "nyxid_services",
            "use_skill");
        preparation.Diagnostics.Should().Contain(static diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.SelectedPolicyEmpty &&
            diagnostic.Detail == "intent-alpha");
        preparation.Authority.DegradationReasons.Should().ContainSingle().Which.Should().Be(
            AgentProfileTurnDegradationReason.SelectedPolicyEmpty);
    }

    [Fact]
    public async Task MaterializeCommittedAsync_SelectedEmptyPolicyWithInvalidSkillBody_ShouldKeepOrdinaryBaseline()
    {
        var tools = NewTools("nyxid_services", "use_skill", "task", "extra");
        var profile = BuildProfile();
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add(["nyxid_services", "use_skill", "task"]);
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        var sealedProfile = SealProfile(profile);
        var materializer = NewMaterializer(
            RegistryWithRoute(tools),
            new SequencedClassifier(
                AgentProfileTurnClassificationResult.Matched(
                    AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId),
                AgentProfileTurnClassificationResult.Matched("intent-alpha")),
            new RecordingFetcher(SuccessfulFetch(new string('x', profile.MaxSelectedSkillBytes + 1))));

        var preparation = await materializer.PrepareNyxIdChatAsync(
            sealedProfile,
            "session-selected-empty-invalid-body",
            "read my NyxID services",
            tools,
            ToolContext(),
            llmControl: null,
            CancellationToken.None);
        var materialization = await materializer.MaterializeCommittedAsync(
            sealedProfile,
            preparation.Authority,
            "token",
            tools,
            ToolContext(),
            CancellationToken.None);

        materialization.Catalog.FinalAllowedToolNames.Should().BeEquivalentTo(
            "nyxid_services",
            "use_skill");
        materialization.Catalog.ExactTools.Keys.Should().BeEquivalentTo(
            "nyxid_services",
            "use_skill");
        materialization.Catalog.SelectedSkillPromptLayer.Should().BeNull();
        materialization.Catalog.Diagnostics.Should().Contain(static diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.SelectedPolicyEmpty);
        materialization.Catalog.Diagnostics.Should().Contain(static diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.SelectedSkillBodyInvalid &&
            diagnostic.Detail == "body_out_of_bounds");
        materialization.ReconcileProposal.AuthorityKind.Should().Be(
            AgentProfileTurnAuthorityKind.Recovery);
        materialization.ReconcileProposal.DegradationReasons.Should().Contain([
            AgentProfileTurnDegradationReason.SelectedPolicyEmpty,
            AgentProfileTurnDegradationReason.SelectedSkillBodyInvalid,
        ]);
    }

    [Fact]
    public async Task PrepareAsync_SelectedMemberWithEmptyPolicies_ShouldKeepSealedSelectionOutsideNyxIdChat()
    {
        var tools = NewTools("nyxid_services", "use_skill", "task");
        var profile = BuildProfile();
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add(["nyxid_services", "use_skill", "task"]);
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();

        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha")),
                fetcher: null)
            .PrepareAsync(
                SealProfile(profile),
                "session-non-chat-selected-empty",
                "what NyxID services are connected to my account?",
                tools,
                ToolContext(),
                CancellationToken.None);

        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Selected);
        preparation.Authority.AuthorityCeilingToolNames.Should().BeEmpty();
        preparation.Diagnostics.Should().NotContain(static diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.SelectedPolicyEmpty);
    }

    [Fact]
    public async Task PrepareAsync_ClassifierNoMatch_ShouldStayRestrictedEmptyOutsideNyxIdChat()
    {
        var tools = NewTools("nyxid_services", "use_skill", "task");
        var profile = BuildProfile();
        profile.MaximumToolPolicy.ToolNames.Add(["nyxid_services", "use_skill"]);

        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher: null)
            .PrepareAsync(
                SealProfile(profile),
                "session-non-chat-no-match",
                "what NyxID services are connected to my account?",
                tools,
                ToolContext(),
                CancellationToken.None);

        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.RestrictedEmpty);
        preparation.Authority.AuthorityCeilingToolNames.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareNyxIdChatAsync_ExplicitAlias_ShouldIntentionallyBypassBuiltInClassification()
    {
        var tools = NewTools("recovery", "task", "extra");
        var classifier = new RecordingClassifier(
            new InvalidOperationException("an explicit alias must not classify"));

        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                classifier,
                fetcher: null)
            .PrepareNyxIdChatAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "session-explicit-alias",
                "/alpha run",
                tools,
                ToolContext(),
                llmControl: null,
                CancellationToken.None);

        preparation.Authority.CandidateRoute!.IntentId.Should().Be("intent-alpha");
        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Selected);
        preparation.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.AliasMatched &&
            diagnostic.Detail == "intent-alpha");
        classifier.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task PrepareNyxIdChatAsync_ExplicitAliasInsideNaturalLanguage_ShouldBypassClassification()
    {
        var tools = NewTools("recovery", "task", "extra");
        var classifier = new RecordingClassifier(
            new InvalidOperationException("an explicit alias must not classify"));
        var profile = BuildProfile();
        profile.Members[0].ExplicitTriggerAliases.Add("github");

        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                classifier,
                fetcher: null)
            .PrepareNyxIdChatAsync(
                SealProfile(profile),
                "session-natural-language-alias",
                "hi can you check what are the issues assigned to me on my github ?",
                tools,
                ToolContext(),
                llmControl: null,
                CancellationToken.None);

        preparation.Authority.CandidateRoute!.IntentId.Should().Be("intent-alpha");
        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Selected);
        preparation.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.AliasMatched &&
            diagnostic.Detail == "intent-alpha");
        classifier.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task PrepareNyxIdChatAsync_ExactConnectedServiceOperation_ShouldNotBecomeServiceConnect()
    {
        var tools = NewTools("recovery", "lark-message-create", "extra");
        var profile = BuildProfile();
        profile.Members[0].IntentId = "connected-service-write";
        profile.Members[0].RoutingDescription =
            "Write through an already-connected exact UserService operation.";
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Add("lark-message-create");
        profile.MaximumToolPolicy.ToolNames.Add("lark-message-create");
        var classifier = new SequencedClassifier(
            AgentProfileTurnClassificationResult.Matched(
                AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId),
            AgentProfileTurnClassificationResult.Matched("connected-service-write"));

        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                classifier,
                fetcher: null)
            .PrepareNyxIdChatAsync(
                SealProfile(profile),
                "session-connected-service-write",
                "Use exact UserService us-lark-alpha endpoint im-message-create through " +
                "tool lark-message-create.",
                tools,
                ToolContext(),
                llmControl: null,
                CancellationToken.None);

        preparation.Authority.CandidateRoute!.IntentId.Should().Be("connected-service-write");
        preparation.Authority.AuthorityCeilingToolNames.Should().BeEquivalentTo(
            "recovery",
            "lark-message-create");
        classifier.Requests.Should().HaveCount(2);
        classifier.Requests[0].Candidates.Single(candidate =>
                candidate.IntentId == NyxIdChatTurnIntentClassifier.ServiceConnectIntentId)
            .RoutingDescription.Should().Contain("already-connected exact UserService");
        classifier.Requests[0].Candidates.Should().ContainSingle(candidate =>
            candidate.IntentId == AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId &&
            candidate.RoutingDescription.Contains("already-connected exact UserService"));
        classifier.Requests[1].Candidates.Should().ContainSingle(candidate =>
            candidate.IntentId == "connected-service-write" &&
            candidate.RoutingDescription ==
            "Write through an already-connected exact UserService operation.");
    }

    [Fact]
    public async Task PrepareNyxIdChatAsync_ProfileTaskRoute_ShouldPreserveAllProfileCandidates()
    {
        var tools = NewTools("recovery", "task", "extra");
        var profile = BuildProfile();
        profile.Members.Clear();
        for (var index = 0; index < StreamingAgentProfileTurnClassifier.MaximumCandidates; index++)
        {
            profile.Members.Add(new AgentProfileSkillMember
            {
                IntentId = $"intent-{index:D2}",
                RoutingDescription = $"Route profile task {index:D2}.",
                TaskToolPolicy = new AgentProfileToolPolicy { ToolNames = { "task" } },
                SideEffectClass = AgentProfileSideEffectClass.ReadOnly,
            });
        }
        var classifier = new SequencedClassifier(
            AgentProfileTurnClassificationResult.Matched(
                AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId),
            AgentProfileTurnClassificationResult.Matched("intent-31"));

        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                classifier,
                fetcher: null)
            .PrepareNyxIdChatAsync(
                SealProfile(profile),
                "session-profile-member-31",
                "Run profile task 31.",
                tools,
                ToolContext(),
                llmControl: null,
                CancellationToken.None);

        preparation.Authority.CandidateRoute!.IntentId.Should().Be("intent-31");
        classifier.Requests.Should().HaveCount(2);
        classifier.Requests[0].Candidates.Should().HaveCount(5);
        classifier.Requests[1].Candidates.Should()
            .HaveCount(StreamingAgentProfileTurnClassifier.MaximumCandidates);
        classifier.Requests[1].Candidates.Select(static candidate => candidate.IntentId)
            .Should().Contain("intent-00").And.Contain("intent-31");
    }

    [Fact]
    public async Task PrepareAsync_ShouldFreezeCandidateRefAndCanonicalCeilingWithoutExactFetch()
    {
        var tools = NewTools("recovery", "task", "extra");
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Add([" task ", "RECOVERY"]);
        profile.Members[0].TaskToolPolicy.ToolNames.Add(" TASK ");
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var preparation = await NewMaterializer(RegistryWithRoute(tools), classifier, fetcher)
            .PrepareAsync(
                SealProfile(profile),
                "session-a",
                "/alpha run",
                tools,
                ToolContext(),
                CancellationToken.None);

        preparation.Authority.ReconciliationKey.Should().BeEquivalentTo(
            new AgentProfileTurnReconciliationKey { SessionId = "session-a", Attempt = 1 });
        preparation.Authority.CandidateRoute.Should().BeEquivalentTo(
            new AgentProfileTurnCandidateRouteIdentity
            {
                ProfileId = "profile-alpha",
                ProfileVersion = "profile-v1",
                PolicyRevision = "policy-v1",
                IntentId = "intent-alpha",
            });
        preparation.Authority.SelectedExactSkillRef.Should().BeEquivalentTo(
            new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = SkillVersion });
        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Selected);
        preparation.Authority.AuthorityCeilingToolNames.Should().Equal("recovery", "task");
        classifier.CallCount.Should().Be(0);
        fetcher.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task PrepareAsync_WhenSessionIdIsBlank_ShouldRejectBeforeDiscovery(string sessionId)
    {
        var tools = NewTools("recovery", "task");
        var registry = RegistryWithRoute(tools);
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var act = async () => await NewMaterializer(registry, classifier, fetcher)
            .PrepareAsync(
                SealProfile(BuildProfile()),
                sessionId,
                "route me",
                tools,
                ToolContext(),
                CancellationToken.None);

        await act.Should()
            .ThrowAsync<ArgumentException>()
            .WithParameterName("sessionId");
        registry.ResolveCalls.Should().BeEmpty();
        classifier.CallCount.Should().Be(0);
        fetcher.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("classifier-no-match")]
    [InlineData("shadow")]
    [InlineData("task-policy-failure")]
    public async Task PrepareAsync_WhenRecoveryFallbackHasNoEffectiveTools_ShouldRestrictEmpty(
        string fallbackPath)
    {
        var tools = NewTools("task");
        var profile = BuildProfile(withAlias: fallbackPath != "classifier-no-match");
        if (fallbackPath == "shadow")
            profile.ActivationMode = AgentProfileActivationMode.Shadow;
        if (fallbackPath == "task-policy-failure")
            profile.Members[0].TaskToolPolicy.ToolSetRefs.Add("missing.task");
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var preparation = await NewMaterializer(RegistryWithRoute(tools), classifier, fetcher)
            .PrepareAsync(
                SealProfile(profile),
                "session-empty-recovery",
                fallbackPath == "classifier-no-match" ? "classify me" : "/alpha run",
                tools,
                ToolContext(),
                CancellationToken.None);

        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.RestrictedEmpty);
        preparation.Authority.AuthorityCeilingToolNames.Should().BeEmpty();
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeCommittedAsync_ShouldUseFrozenAuthorityWithoutClassifierAndEnforceCeiling()
    {
        var tools = NewTools("recovery", "task", "extra");
        var profile = SealProfile(BuildProfile(withAlias: true));
        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .PrepareAsync(
                profile,
                "session-a",
                "/alpha run",
                tools,
                ToolContext(),
                CancellationToken.None);
        var classifier = new RecordingClassifier(new InvalidOperationException("must not classify"));
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var materialization = await NewMaterializer(RegistryWithRoute(tools), classifier, fetcher)
            .MaterializeCommittedAsync(
                profile,
                preparation.Authority,
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        materialization.Catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery", "task");
        materialization.Catalog.SelectedSkillPromptLayer!.Content.Should().Be("Selected instructions.");
        materialization.ReconcileProposal.ReconciliationKey.Should().BeEquivalentTo(
            preparation.Authority.ReconciliationKey);
        classifier.CallCount.Should().Be(0);
        fetcher.CallCount.Should().Be(1);

        var invalidCatalog = new AgentTurnToolCatalog(
            ["hidden"],
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            selectedIntentId: null,
            candidateIntentId: null);
        var create = () => AgentTurnToolCatalogMaterialization.Create(
            invalidCatalog,
            preparation.Authority);
        create.Should().Throw<InvalidOperationException>().WithMessage("*ceiling*");
    }

    [Fact]
    public async Task PrepareAndMaterializeCommittedAsync_ShouldScopeRequestContextDuringToolDiscovery()
    {
        var tools = NewTools("recovery", "task");
        var source = new TokenBoundToolSource("turn-token", tools);
        var registry = new RecordingToolSetRegistry();
        registry.Add("profile.route", source);
        var profile = SealProfile(BuildProfile(withAlias: true));
        var materializer = NewMaterializer(
            registry,
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
            new RecordingFetcher(SuccessfulFetch()));
        var toolContext = ToolContext("turn-token");

        var preparation = await materializer.PrepareAsync(
            profile,
            "session-context",
            "/alpha run",
            registeredTools: [],
            toolContext,
            CancellationToken.None);
        var materialization = await materializer.MaterializeCommittedAsync(
            profile,
            preparation.Authority,
            "turn-token",
            registeredTools: [],
            toolContext,
            CancellationToken.None);

        source.ObservedTokens.Should().Equal("turn-token", "turn-token");
        preparation.Authority.AuthorityCeilingToolNames.Should().Equal("recovery", "task");
        materialization.Catalog.ExactTools["recovery"].Should().BeSameAs(tools[0]);
        materialization.Catalog.ExactTools["task"].Should().BeSameAs(tools[1]);
    }

    [Fact]
    public async Task MaterializeCommittedAsync_WhenFrozenExactRefDoesNotMatchProfile_ShouldRestrictEmptyWithoutFetch()
    {
        var tools = NewTools("recovery", "task", "extra");
        var profile = SealProfile(BuildProfile(withAlias: true));
        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .PrepareAsync(
                profile,
                "session-a",
                "/alpha run",
                tools,
                ToolContext(),
                CancellationToken.None);
        var committedAuthority = preparation.Authority;
        committedAuthority.SelectedExactSkillRef = new ExactRemoteSkillRef
        {
            Guid = "22222222-2222-2222-2222-222222222222",
            LiteralVersion = SkillVersion,
        };
        var classifier = new RecordingClassifier(new InvalidOperationException("must not classify"));
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var materialization = await NewMaterializer(RegistryWithRoute(tools), classifier, fetcher)
            .MaterializeCommittedAsync(
                profile,
                committedAuthority,
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        materialization.Catalog.FinalAllowedToolNames.Should().BeEmpty();
        materialization.Catalog.SelectedSkillPromptLayer.Should().BeNull();
        materialization.ReconcileProposal.ReconciliationKey.Should().BeEquivalentTo(
            committedAuthority.ReconciliationKey);
        materialization.ReconcileProposal.AuthorityKind.Should().Be(
            AgentProfileTurnAuthorityKind.RestrictedEmpty);
        materialization.ReconcileProposal.AuthorityCeilingToolNames.Should().BeEmpty();
        materialization.ReconcileProposal.DegradationReasons.Should().Contain(
            AgentProfileTurnDegradationReason.ExactSkillIdentityMismatch);
        materialization.Catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillIdentityMismatch);
        classifier.CallCount.Should().Be(0);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeCommittedAsync_WhenHistoricalProfileHasDuplicateExactCandidate_ShouldRestrictEmpty()
    {
        var tools = NewTools("recovery", "task", "extra");
        var validProfile = SealProfile(BuildProfile(withAlias: true));
        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .PrepareAsync(
                validProfile,
                "session-duplicate-committed-candidate",
                "/alpha run",
                tools,
                ToolContext(),
                CancellationToken.None);
        var historicalProfile = validProfile.Clone();
        historicalProfile.Members.Add(historicalProfile.Members[0].Clone());
        historicalProfile.DeterministicPolicySha256 = ByteString.Empty;
        historicalProfile = SealProfile(historicalProfile);
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var materialization = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(new InvalidOperationException("must not classify")),
                fetcher)
            .MaterializeCommittedAsync(
                historicalProfile,
                preparation.Authority,
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        materialization.Catalog.FinalAllowedToolNames.Should().BeEmpty();
        materialization.ReconcileProposal.AuthorityKind.Should().Be(
            AgentProfileTurnAuthorityKind.RestrictedEmpty);
        materialization.Catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation &&
            diagnostic.Detail == "committed_intent_id_collision");
        fetcher.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(nameof(AgentProfileTurnCandidateRouteIdentity.ProfileId), "profile-other")]
    [InlineData(nameof(AgentProfileTurnCandidateRouteIdentity.ProfileVersion), "profile-v2")]
    [InlineData(nameof(AgentProfileTurnCandidateRouteIdentity.PolicyRevision), "policy-v2")]
    public async Task MaterializeCommittedAsync_WhenCommittedProfileIdentityDiffers_ShouldRestrictEmptyWithoutIo(
        string identityField,
        string mismatchedValue)
    {
        var tools = NewTools("recovery", "task");
        var profile = SealProfile(BuildProfile(withAlias: true));
        var committedAuthority = new AgentProfileTurnAuthorityState
        {
            ReconciliationKey = new AgentProfileTurnReconciliationKey
            {
                SessionId = "session-profile-mismatch",
                Attempt = 3,
            },
            CandidateRoute = new AgentProfileTurnCandidateRouteIdentity
            {
                ProfileId = profile.ProfileId,
                ProfileVersion = profile.ProfileVersion,
                PolicyRevision = profile.PolicyRevision,
                IntentId = "intent-alpha",
            },
            SelectedExactSkillRef = new ExactRemoteSkillRef
            {
                Guid = SkillGuid,
                LiteralVersion = SkillVersion,
            },
            AuthorityKind = AgentProfileTurnAuthorityKind.Selected,
            AuthorityCeilingToolNames = { "recovery", "task" },
        };
        switch (identityField)
        {
            case nameof(AgentProfileTurnCandidateRouteIdentity.ProfileId):
                committedAuthority.CandidateRoute.ProfileId = mismatchedValue;
                break;
            case nameof(AgentProfileTurnCandidateRouteIdentity.ProfileVersion):
                committedAuthority.CandidateRoute.ProfileVersion = mismatchedValue;
                break;
            case nameof(AgentProfileTurnCandidateRouteIdentity.PolicyRevision):
                committedAuthority.CandidateRoute.PolicyRevision = mismatchedValue;
                break;
            default:
                throw new InvalidOperationException($"Unknown identity field '{identityField}'.");
        }
        var registry = RegistryWithRoute(tools);
        var classifier = new RecordingClassifier(new InvalidOperationException("must not classify"));
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var materialization = await NewMaterializer(registry, classifier, fetcher)
            .MaterializeCommittedAsync(
                profile,
                committedAuthority,
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        materialization.Catalog.FinalAllowedToolNames.Should().BeEmpty();
        materialization.ReconcileProposal.ReconciliationKey.Should().BeEquivalentTo(
            committedAuthority.ReconciliationKey);
        materialization.ReconcileProposal.AuthorityKind.Should().Be(
            AgentProfileTurnAuthorityKind.RestrictedEmpty);
        materialization.ReconcileProposal.DegradationReasons.Should().Contain(
            AgentProfileTurnDegradationReason.ProfileInvalid);
        materialization.Catalog.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ProfileInvalid &&
            diagnostic.Detail == "committed_profile_mismatch");
        registry.ResolveCalls.Should().BeEmpty();
        classifier.CallCount.Should().Be(0);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeCommittedAsync_WhenExactFetchFailsWithNoRecoveryTools_ShouldRestrictEmpty()
    {
        var tools = NewTools("task");
        var profile = SealProfile(BuildProfile(withAlias: true));
        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .PrepareAsync(
                profile,
                "session-empty-exact-fallback",
                "/alpha run",
                tools,
                ToolContext(),
                CancellationToken.None);

        var materialization = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(new InvalidOperationException("must not classify")),
                new RecordingFetcher(ExactRemoteSkillFetchResult.Failed(
                    ExactRemoteSkillFetchFailureCode.NotFound)))
            .MaterializeCommittedAsync(
                profile,
                preparation.Authority,
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        materialization.ReconcileProposal.AuthorityKind.Should().Be(
            AgentProfileTurnAuthorityKind.RestrictedEmpty);
        materialization.ReconcileProposal.AuthorityCeilingToolNames.Should().BeEmpty();
        materialization.ReconcileProposal.DegradationReasons.Should().Contain(
            AgentProfileTurnDegradationReason.ExactSkillFetchFailed);
    }

    [Fact]
    public async Task MaterializeCommittedAsync_ShouldReturnSameKeyMonotonicReconcileForSuccessAndFailure()
    {
        var tools = NewTools("recovery", "task", "extra");
        var profile = SealProfile(BuildProfile(withAlias: true));
        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .PrepareAsync(
                profile,
                "session-a",
                "/alpha run",
                tools,
                ToolContext(),
                CancellationToken.None);

        var failed = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(new InvalidOperationException("must not classify")),
                new RecordingFetcher(ExactRemoteSkillFetchResult.Success(
                    SkillGuid,
                    SkillVersion,
                    "wrong-name",
                    PublisherId,
                    SkillSha256,
                    SkillMarkdown)))
            .MaterializeCommittedAsync(
                profile,
                preparation.Authority,
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        failed.ReconcileProposal.ReconciliationKey.Should().BeEquivalentTo(
            preparation.Authority.ReconciliationKey);
        failed.ReconcileProposal.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        failed.ReconcileProposal.AuthorityCeilingToolNames.Should().Equal("recovery");
        failed.ReconcileProposal.DegradationReasons.Should().Contain(
            AgentProfileTurnDegradationReason.ExactSkillIdentityMismatch);

        var recoveredBody = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(new InvalidOperationException("must not classify")),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeCommittedAsync(
                profile,
                failed.ReconcileProposal,
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        recoveredBody.Catalog.SelectedSkillPromptLayer!.Content.Should().Be("Selected instructions.");
        recoveredBody.Catalog.FinalAllowedToolNames.Should().Equal("recovery");
        recoveredBody.ReconcileProposal.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        recoveredBody.ReconcileProposal.AuthorityCeilingToolNames.Should().Equal("recovery");
        recoveredBody.ReconcileProposal.DegradationReasons.Should().Contain(
            AgentProfileTurnDegradationReason.ExactSkillIdentityMismatch);

        var authorityOnlyReasons = new[]
        {
            AgentProfileTurnDegradationReason.LegacyAuthorityMissing,
            AgentProfileTurnDegradationReason.MaterializerUnavailable,
            AgentProfileTurnDegradationReason.MaterializationFailed,
        };
        foreach (var reason in authorityOnlyReasons)
        {
            var authorityOnly = preparation.Authority;
            authorityOnly.CandidateRoute = null;
            authorityOnly.SelectedExactSkillRef = null;
            authorityOnly.AuthorityKind = AgentProfileTurnAuthorityKind.RestrictedEmpty;
            authorityOnly.AuthorityCeilingToolNames.Clear();
            authorityOnly.DegradationReasons.Clear();
            authorityOnly.DegradationReasons.Add(reason);
            var preserved = await NewMaterializer(
                    RegistryWithRoute(tools),
                    new RecordingClassifier(new InvalidOperationException("must not classify")),
                    new RecordingFetcher(SuccessfulFetch()))
                .MaterializeCommittedAsync(
                    profile,
                    authorityOnly,
                    "token",
                    tools,
                    ToolContext(),
                    CancellationToken.None);

            preserved.ReconcileProposal.DegradationReasons.Should().Equal(reason);
            preserved.Catalog.Diagnostics.Should().NotContain(diagnostic =>
                diagnostic.Code == AgentProfileTurnDiagnosticCode.ProfileInvalid);
        }
    }

    [Fact]
    public async Task MaterializeAsync_EnforcedAlias_ShouldSelectBodyAndAttenuatedPolicy()
    {
        var tools = NewTools("recovery", "task", "extra");
        var registry = RegistryWithRoute(tools);
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());
        var fetcher = new RecordingFetcher(SuccessfulFetch());
        var profile = SealProfile(BuildProfile(withAlias: true));

        var result = await NewMaterializer(registry, classifier, fetcher)
            .MaterializeWithPreparationAsync(
                profile,
                "/alpha now",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);
        var catalog = result.Catalog;

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery", "task");
        catalog.SelectedIntentId.Should().Be("intent-alpha");
        catalog.CandidateIntentId.Should().Be("intent-alpha");
        catalog.SelectedSkillPromptLayer!.Content.Should().Be("Selected instructions.");
        result.Preparation.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.AliasMatched &&
            diagnostic.Detail == "intent-alpha");
        classifier.CallCount.Should().Be(0);
        fetcher.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task MaterializeAsync_RouteOnlyTool_ShouldFreezeExactObject()
    {
        var routeTool = new TestTool("task");
        var registry = RegistryWithRoute([routeTool]);
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add("task");
        profile.RecoveryToolPolicy.ToolNames.Clear();

        var catalog = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                [],
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().ContainSingle().Which.Should().Be("task");
        catalog.ExactTools.Should().ContainSingle();
        catalog.ExactTools["task"].Should().BeSameAs(routeTool);
    }

    [Fact]
    public async Task MaterializeAsync_RouteAndRegisteredSameNameDifferentReference_ShouldFailClosed()
    {
        var routeTool = new TestTool("task");
        var registeredTool = new TestTool("task");
        var registry = RegistryWithRoute([routeTool]);
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add("task");
        profile.RecoveryToolPolicy.ToolNames.Clear();

        var catalog = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                [registeredTool],
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.ExactTools.Should().BeEmpty();
        catalog.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolNameCollision &&
            diagnostic.Detail == "task");
    }

    [Fact]
    public async Task MaterializeAsync_PolicyToolSetRefs_ShouldOnlyAttenuateFinalAuthority()
    {
        var routeTools = NewTools(
            "recovery-from-set",
            "task-from-set",
            "route-only",
            "visibility-blocked");
        var registeredOnlyTool = new TestTool("registered-only");
        var registeredTools = routeTools
            .Append(registeredOnlyTool)
            .ToArray();
        var registry = new RecordingToolSetRegistry();
        registry.Add("profile.route", new StaticToolSource(routeTools));
        registry.Add("maximum.policy", new StaticToolSource(NewTools(
            "recovery-from-set",
            "task-from-set",
            "registered-only",
            "maximum-only")));
        registry.Add("recovery.policy", new StaticToolSource(NewTools(
            "recovery-from-set",
            "registered-only",
            "recovery-outside-maximum")));
        registry.Add("task.policy", new StaticToolSource(NewTools(
            "task-from-set",
            "registered-only",
            "task-outside-maximum")));
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolSetRefs.Add("maximum.policy");
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.RecoveryToolPolicy.ToolSetRefs.Add("recovery.policy");
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolSetRefs.Add("task.policy");
        var toolContext = ToolContext() with
        {
            ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames(
                ["recovery-from-set", "task-from-set", "route-only", "registered-only"]),
        };

        var catalog = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                registeredTools,
                toolContext,
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery-from-set", "task-from-set");
        catalog.FinalAllowedToolNames.Should().NotContain([
            "route-only",
            "visibility-blocked",
            "registered-only",
            "maximum-only",
            "recovery-outside-maximum",
            "task-outside-maximum",
        ]);
        catalog.ExactTools.Keys.Should().BeEquivalentTo("recovery-from-set", "task-from-set");

        var toolManager = new ToolManager();
        toolManager.Register(registeredTools);
        var runtime = new ChatRuntime(
            providerFactory: static () => throw new InvalidOperationException("Provider is not used."),
            history: new ChatHistory(),
            toolLoop: new ToolCallLoop(toolManager),
            hooks: null,
            requestBuilder: _ => new LLMRequest { Messages = [], Tools = toolManager.GetAll() });
        var request = runtime.CreateStepExecutor(catalog).BuildBaseRequest(null, null, toolContext, null);

        request.Tools.Should().HaveCount(2);
        request.Tools.Should().OnlyContain(tool =>
            tool.Name == "recovery-from-set" || tool.Name == "task-from-set");
        request.Tools.Should().NotContain(tool => ReferenceEquals(tool, registeredOnlyTool));
        registry.ResolveCalls.Should().Equal(
            "profile.route",
            "maximum.policy",
            "recovery.policy",
            "task.policy",
            "profile.route",
            "maximum.policy",
            "recovery.policy");
    }

    [Fact]
    public async Task MaterializeAsync_BroadSelectorAcrossConnections_ShouldRequireClarification()
    {
        IAgentTool[] tools =
        [
            new AdmittedTestTool(
                "nyxop_github_read_alpha",
                CreateReadAdmission(
                    "us-github-alpha",
                    "api-github-alpha",
                    "endpoint-read",
                    "api-github")),
            new AdmittedTestTool(
                "nyxop_github_read_beta",
                CreateReadAdmission(
                    "us-github-beta",
                    "api-github-beta",
                    "endpoint-read",
                    "api-github")),
            new AdmittedTestTool(
                "nyxop_github_write_alpha",
                CreateWriteAdmission(
                    "us-github-alpha",
                    "api-github-alpha",
                    "endpoint-write",
                    "api-github")),
            new AdmittedTestTool(
                "nyxop_slack_read",
                CreateReadAdmission(
                    "us-slack-alpha",
                    "api-slack-alpha",
                    "endpoint-read",
                    "api-slack")),
            new PresentedTestTool("presentation-spoof", "api-github"),
            new TestTool("ask_user"),
        ];
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add("ask_user");
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly,
            AgentToolOperationRiskPayload.Write));
        profile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly));

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().ContainSingle().Which.Should().Be("ask_user");
        catalog.FinalAllowedToolNames.Should().NotContain([
            "nyxop_github_read_alpha",
            "nyxop_github_read_beta",
            "nyxop_github_write_alpha",
            "nyxop_slack_read",
            "presentation-spoof",
        ]);
        catalog.Diagnostics.Should().NotContain(static diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ProfileInvalid);
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation);
        catalog.HasUnresolvedConnectedServiceSelectors.Should().BeFalse();
        catalog.RequiredToolInvocation.Should().BeNull();
    }

    [Fact]
    public async Task MaterializeAsync_ExactEndpointSelector_ShouldAdmitOnlyThatPublishedOperation()
    {
        IAgentTool[] tools =
        [
            new AdmittedTestTool(
                "nyxop_github_read_alpha",
                CreateReadAdmission(
                    "us-github-alpha",
                    "api-github-alpha",
                    "endpoint-read-alpha",
                    "api-github")),
            new AdmittedTestTool(
                "nyxop_github_read_beta",
                CreateReadAdmission(
                    "us-github-beta",
                    "api-github-beta",
                    "endpoint-read-beta",
                    "api-github")),
        ];
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly));
        var taskSelector = ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly);
        taskSelector.EndpointId = "endpoint-read-beta";
        profile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(taskSelector);

        var connectedSelector = new RecordingConnectedOperationSelector(
            AgentProfileConnectedOperationSelectionResult.NoMatch());
        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()),
                connectedOperationSelector: connectedSelector)
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().ContainSingle()
            .Which.Should().Be("nyxop_github_read_beta");
        var owner = catalog.ExactTools.Values.Should().ContainSingle().Subject
            .Should().BeAssignableTo<IAgentToolOperationAdmissionOwner>().Subject;
        owner.OperationAdmission.Identity.Should().Be(
            new AgentToolOperationIdentity.PublishedEndpoint("endpoint-read-beta"));
        connectedSelector.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_ConnectedSelectorOverLimit_ShouldExposeOnlyClarificationTool()
    {
        var connectedTools = Enumerable.Range(1, 4)
            .Select(index => (IAgentTool)new AdmittedTestTool(
                $"nyxop_github_read_{index}",
                CreateReadAdmission(
                    $"us-github-{index}",
                    $"api-github-{index}",
                    $"endpoint-read-{index}",
                    "api-github")))
            .ToArray();
        IAgentTool[] tools = [.. connectedTools, new TestTool("ask_user")];
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add("ask_user");
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly));
        profile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly));

        var connectedSelector = new RecordingConnectedOperationSelector(
            AgentProfileConnectedOperationSelectionResult.NoMatch());
        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()),
                connectedOperationSelector: connectedSelector)
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().ContainSingle().Which.Should().Be("ask_user");
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation);
        connectedSelector.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task PrepareAsync_BroadConnectedSelector_ShouldCommitOnlyBoundedExactSelection()
    {
        var connectedTools = Enumerable.Range(1, 5)
            .Select(index => (IAgentTool)new AdmittedTestTool(
                $"nyxop_github_read_{index}",
                CreateReadAdmission(
                    "us-github-alpha",
                    "api-github-alpha",
                    $"endpoint-read-{index}",
                    "api-github")))
            .ToArray();
        IAgentTool[] tools = [.. connectedTools, new TestTool("ask_user")];
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add("ask_user");
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly));
        profile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly));
        var connectedSelector = new RecordingConnectedOperationSelector(request =>
            AgentProfileConnectedOperationSelectionResult.Selected(
            [request.Candidates.Single(candidate =>
                candidate.DisplayName == "nyxop_github_read_2").CandidateId]));
        var toolContext = ToolContext() with
        {
            ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames(
                [.. connectedTools.Take(4).Select(static tool => tool.Name), "ask_user"]),
        };
        var materializer = NewMaterializer(
            RegistryWithRoute(tools),
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
            new RecordingFetcher(SuccessfulFetch()),
            connectedOperationSelector: connectedSelector);

        var (catalog, preparation) = await materializer.MaterializeWithPreparationAsync(
            SealProfile(profile),
            "/alpha read repository metadata",
            "token",
            tools,
            toolContext,
            CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().ContainSingle()
            .Which.Should().Be("nyxop_github_read_2");
        preparation.Authority.AuthorityCeilingToolNames.Should().ContainSingle()
            .Which.Should().Be("nyxop_github_read_2");
        connectedSelector.CallCount.Should().Be(1);
        connectedSelector.LastRequest!.Candidates.Should().HaveCount(4);
        connectedSelector.LastRequest.Candidates.Should().OnlyContain(candidate =>
            candidate.CatalogServiceSlug == "api-github" &&
            candidate.ConnectorDisplayName.Length <= 160 &&
            candidate.ConnectionLabel.Length <= 160 &&
            candidate.Description.Length <= 512);
        connectedSelector.LastRequest.Candidates.Should().NotContain(candidate =>
            candidate.DisplayName == "nyxop_github_read_5");
        var admission = ((IAgentToolOperationAdmissionOwner)catalog.ExactTools.Values.Single())
            .OperationAdmission;
        catalog.Proof.ToolDescriptors.Should().ContainSingle().Which.SelectorDigest
            .Should().Be(AgentToolOperationSelector.ComputeDigest(admission));

        var replay = await materializer.MaterializeCommittedAsync(
            SealProfile(profile),
            preparation.Authority,
            "token",
            tools,
            toolContext,
            CancellationToken.None);

        replay.Catalog.FinalAllowedToolNames.Should().Equal(catalog.FinalAllowedToolNames);
        replay.Catalog.Proof.CatalogDigest.Should().Be(catalog.Proof.CatalogDigest);
        connectedSelector.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task PrepareNyxIdChatAsync_WorkflowMemberWithContextSelector_ShouldExposeWorkflowAndSelectedReadContext()
    {
        IAgentTool[] contextTools =
        [
            new AdmittedTestTool(
                "nyxop_profile_preferences_read",
                CreateReadAdmission(
                    "us-profile-alpha",
                    "api-profile-alpha",
                    "endpoint-preferences",
                    "api-profile")),
            new AdmittedTestTool(
                "nyxop_profile_calendar_read",
                CreateReadAdmission(
                    "us-profile-alpha",
                    "api-profile-alpha",
                    "endpoint-calendar",
                    "api-profile")),
            new AdmittedTestTool(
                "nyxop_profile_loyalty_read",
                CreateReadAdmission(
                    "us-profile-alpha",
                    "api-profile-alpha",
                    "endpoint-loyalty",
                    "api-profile")),
            new AdmittedTestTool(
                "nyxop_profile_payment_read",
                CreateReadAdmission(
                    "us-profile-alpha",
                    "api-profile-alpha",
                    "endpoint-payment",
                    "api-profile")),
        ];
        IAgentTool[] tools = [new TestTool("aevatar_start_workflow"), .. contextTools];
        var profile = BuildProfile();
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add("aevatar_start_workflow");
        profile.MaximumToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-profile",
            AgentToolOperationRiskPayload.ReadOnly));
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].RoutingDescription =
            "Start the dinner date workflow after preparing input from relevant user context.";
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Add("aevatar_start_workflow");
        profile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-profile",
            AgentToolOperationRiskPayload.ReadOnly));
        var sealedProfile = SealProfile(profile);
        var classifier = new SequencedClassifier(
            AgentProfileTurnClassificationResult.Matched(
                AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId),
            AgentProfileTurnClassificationResult.Matched("intent-alpha"));
        var connectedSelector = new RecordingConnectedOperationSelector(request =>
            AgentProfileConnectedOperationSelectionResult.Selected(
            [request.Candidates.Single(candidate =>
                candidate.DisplayName == "nyxop_profile_preferences_read").CandidateId]));
        var materializer = NewMaterializer(
            RegistryWithRoute(tools),
            classifier,
            new RecordingFetcher(SuccessfulFetch()),
            connectedOperationSelector: connectedSelector);

        var preparation = await materializer.PrepareNyxIdChatAsync(
            sealedProfile,
            "session-dinner-context-workflow",
            "I want to book dinner on Tuesday",
            tools,
            ToolContext(),
            llmControl: null,
            CancellationToken.None);
        var materialization = await materializer.MaterializeCommittedAsync(
            sealedProfile,
            preparation.Authority,
            "token",
            tools,
            ToolContext(),
            CancellationToken.None);

        materialization.Catalog.FinalAllowedToolNames.Should().BeEquivalentTo(
            "aevatar_start_workflow",
            "nyxop_profile_preferences_read");
        materialization.Catalog.FinalAllowedToolNames.Should().NotContain([
            "nyxop_profile_calendar_read",
            "nyxop_profile_loyalty_read",
            "nyxop_profile_payment_read",
        ]);
        materialization.Catalog.ExactTools.Keys.Should().BeEquivalentTo(
            "aevatar_start_workflow",
            "nyxop_profile_preferences_read");
        preparation.Authority.AuthorityCeilingToolNames.Should().BeEquivalentTo(
            "aevatar_start_workflow",
            "nyxop_profile_preferences_read");
        preparation.Authority.CandidateRoute!.IntentId.Should().Be("intent-alpha");
        classifier.Requests.Should().HaveCount(2);
        classifier.Requests[0].Candidates.Should().ContainSingle(candidate =>
            candidate.IntentId == AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId);
        classifier.Requests[1].Candidates.Should().ContainSingle(candidate =>
            candidate.IntentId == "intent-alpha" &&
            candidate.RoutingDescription.Contains("dinner date workflow"));
        connectedSelector.CallCount.Should().Be(1);
        connectedSelector.LastRequest!.UserMessage.Should().Be("I want to book dinner on Tuesday");
        connectedSelector.LastRequest.Candidates.Should().HaveCount(4);
        connectedSelector.LastRequest.Candidates.Should().OnlyContain(candidate =>
            candidate.CatalogServiceSlug == "api-profile" &&
            candidate.Risk == AgentToolOperationRisk.ReadOnly);
    }

    [Fact]
    public async Task PrepareNyxIdChatAsync_WorkflowMemberWithDynamicContextReads_ShouldNotRequireCatalogSlug()
    {
        IAgentTool[] contextTools =
        [
            new AdmittedTestTool(
                "nyxop_current_user_dining_context_read",
                CreateReadAdmission(
                    "us-context-current",
                    "current-user-context",
                    "readDiningProfileContext")),
            new AdmittedTestTool(
                "nyxop_other_user_dining_context_read",
                CreateReadAdmission(
                    "us-context-other",
                    "other-user-context",
                    "readDiningProfileContext")),
            new AdmittedTestTool(
                "nyxop_current_user_dining_context_write",
                CreateWriteAdmission(
                    "us-context-current",
                    "current-user-context",
                    "updateDiningProfileContext",
                    "user-context")),
        ];
        IAgentTool[] tools = [new TestTool("aevatar_start_workflow"), .. contextTools];
        var profile = BuildProfile();
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add("aevatar_start_workflow");
        profile.MaximumToolPolicy.ToolSetRefs.Add("profile.route");
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].RoutingDescription =
            "Start the dinner date workflow after preparing input from relevant user context.";
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Add("aevatar_start_workflow");
        profile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(
            ConnectedServiceSelector(string.Empty, AgentToolOperationRiskPayload.ReadOnly));
        var sealedProfile = SealProfile(profile);
        var classifier = new SequencedClassifier(
            AgentProfileTurnClassificationResult.Matched(
                AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId),
            AgentProfileTurnClassificationResult.Matched("intent-alpha"));
        var connectedSelector = new RecordingConnectedOperationSelector(request =>
            AgentProfileConnectedOperationSelectionResult.Selected(
            [request.Candidates.Single(candidate =>
                candidate.DisplayName == "nyxop_current_user_dining_context_read").CandidateId]));
        var materializer = NewMaterializer(
            RegistryWithRoute(tools),
            classifier,
            new RecordingFetcher(SuccessfulFetch()),
            connectedOperationSelector: connectedSelector);

        var preparation = await materializer.PrepareNyxIdChatAsync(
            sealedProfile,
            "session-dinner-dynamic-context-workflow",
            "Use my saved dining profile and book dinner tonight at 7pm for 2 people.",
            tools,
            ToolContext(),
            llmControl: null,
            CancellationToken.None);
        var materialization = await materializer.MaterializeCommittedAsync(
            sealedProfile,
            preparation.Authority,
            "token",
            tools,
            ToolContext(),
            CancellationToken.None);

        materialization.Catalog.FinalAllowedToolNames.Should().BeEquivalentTo(
            "aevatar_start_workflow",
            "nyxop_current_user_dining_context_read");
        materialization.Catalog.FinalAllowedToolNames.Should().NotContain([
            "nyxop_other_user_dining_context_read",
            "nyxop_current_user_dining_context_write",
        ]);
        preparation.Authority.AuthorityCeilingToolNames.Should().BeEquivalentTo(
            "aevatar_start_workflow",
            "nyxop_current_user_dining_context_read");
        connectedSelector.CallCount.Should().Be(1);
        connectedSelector.LastRequest!.MaximumReadSelections.Should().Be(3);
        connectedSelector.LastRequest.MaximumWriteSelections.Should().Be(0);
        connectedSelector.LastRequest.Candidates.Should().HaveCount(2);
        connectedSelector.LastRequest.Candidates.Should().OnlyContain(candidate =>
            candidate.Risk == AgentToolOperationRisk.ReadOnly);
        connectedSelector.LastRequest.Candidates.Should().Contain(candidate =>
            candidate.CatalogServiceSlug.Length == 0 &&
            candidate.DisplayName == "nyxop_current_user_dining_context_read");
    }

    [Fact]
    public async Task PrepareNyxIdChatAsync_DynamicContextReadSelectorFailure_ShouldRestrictEmpty()
    {
        IAgentTool[] tools =
        [
            new TestTool("aevatar_start_workflow"),
            new AdmittedTestTool(
                "nyxop_current_user_dining_context_read",
                CreateReadAdmission(
                    "us-context-current",
                    "current-user-context",
                    "readDiningProfileContext")),
        ];
        var profile = BuildProfile();
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add("aevatar_start_workflow");
        profile.MaximumToolPolicy.ToolSetRefs.Add("profile.route");
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Add("aevatar_start_workflow");
        profile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(
            ConnectedServiceSelector(string.Empty, AgentToolOperationRiskPayload.ReadOnly));
        var connectedSelector = new RecordingConnectedOperationSelector(
            AgentProfileConnectedOperationSelectionResult.Failed("selector_timeout"));
        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                new SequencedClassifier(
                    AgentProfileTurnClassificationResult.Matched(
                        AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId),
                    AgentProfileTurnClassificationResult.Matched("intent-alpha")),
                new RecordingFetcher(SuccessfulFetch()),
                connectedOperationSelector: connectedSelector)
            .PrepareNyxIdChatAsync(
                SealProfile(profile),
                "session-dinner-dynamic-context-failure",
                "Use my saved dining profile and book dinner tonight at 7pm for 2 people.",
                tools,
                ToolContext(),
                llmControl: null,
                CancellationToken.None);

        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.RestrictedEmpty);
        preparation.Authority.AuthorityCeilingToolNames.Should().BeEmpty();
        preparation.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation);
    }

    [Fact]
    public async Task PrepareNyxIdChatAsync_DynamicAndConcreteContextReads_ShouldShareReadBudget()
    {
        var concreteReads = Enumerable.Range(1, 3)
            .Select(index => (IAgentTool)new AdmittedTestTool(
                $"nyxop_api_profile_read_{index}",
                CreateReadAdmission(
                    "us-profile-current",
                    "current-user-profile",
                    $"readProfile{index}",
                    "api-profile")))
            .ToArray();
        var dynamicReads = Enumerable.Range(1, 3)
            .Select(index => (IAgentTool)new AdmittedTestTool(
                $"nyxop_current_user_dining_context_read_{index}",
                CreateReadAdmission(
                    "us-context-current",
                    "current-user-context",
                    $"readDiningProfileContext{index}")))
            .ToArray();
        IAgentTool[] tools = [new TestTool("aevatar_start_workflow"), .. concreteReads, .. dynamicReads];
        var profile = BuildProfile();
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add("aevatar_start_workflow");
        profile.MaximumToolPolicy.ToolSetRefs.Add("profile.route");
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Add("aevatar_start_workflow");
        profile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-profile",
            AgentToolOperationRiskPayload.ReadOnly));
        profile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(
            ConnectedServiceSelector(string.Empty, AgentToolOperationRiskPayload.ReadOnly));
        var connectedSelector = new RecordingConnectedOperationSelector(request =>
            AgentProfileConnectedOperationSelectionResult.Selected(
                request.Candidates.Take(request.MaximumReadSelections)
                    .Select(static candidate => candidate.CandidateId)
                    .ToArray()));
        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                new SequencedClassifier(
                    AgentProfileTurnClassificationResult.Matched(
                        AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId),
                    AgentProfileTurnClassificationResult.Matched("intent-alpha")),
                new RecordingFetcher(SuccessfulFetch()),
                connectedOperationSelector: connectedSelector)
            .PrepareNyxIdChatAsync(
                SealProfile(profile),
                "session-dinner-dynamic-context-budget",
                "Use my saved dining profile and book dinner tonight at 7pm for 2 people.",
                tools,
                ToolContext(),
                llmControl: null,
                CancellationToken.None);

        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Selected);
        CountReadTools(preparation.Authority.AuthorityCeilingToolNames, tools).Should().Be(
            AgentTurnToolCatalogBudget.ConnectedOperations.MaximumConnectedReadToolCount);
        connectedSelector.CallCount.Should().Be(1);
        connectedSelector.LastRequest!.MaximumReadSelections.Should().Be(
            AgentTurnToolCatalogBudget.ConnectedOperations.MaximumConnectedReadToolCount);
    }

    [Fact]
    public async Task VerifiedAuthorizationContinuation_BroadProfile_ShouldSelectInsideExactVerifiedService()
    {
        var verifiedServiceTools = Enumerable.Range(1, 5)
            .Select(index => (IAgentTool)new AdmittedTestTool(
                $"nyxop_github_read_{index}",
                CreateReadAdmission(
                    "us-github-alpha",
                    "api-github-alpha",
                    $"endpoint-read-{index}",
                    "api-github")))
            .ToArray();
        IAgentTool[] tools =
        [
            .. verifiedServiceTools,
            new AdmittedTestTool(
                "nyxop_github_other_connection",
                CreateReadAdmission(
                    "us-github-other",
                    "api-github-other",
                    "endpoint-read-other",
                    "api-github")),
            new TestTool("ask_user"),
        ];
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add("ask_user");
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly));
        profile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly));
        var sealedProfile = SealProfile(profile);
        var authority = new AgentProfileTurnAuthorityState
        {
            CandidateRoute = new AgentProfileTurnCandidateRouteIdentity
            {
                ProfileId = sealedProfile.ProfileId,
                ProfileVersion = sealedProfile.ProfileVersion,
                PolicyRevision = sealedProfile.PolicyRevision,
                IntentId = "intent-alpha",
            },
            SelectedExactSkillRef = new ExactRemoteSkillRef
            {
                Guid = SkillGuid,
                LiteralVersion = SkillVersion,
            },
            AuthorityKind = AgentProfileTurnAuthorityKind.Selected,
            AuthorityCeilingToolNames = { "nyxid_require_service" },
        };
        var connectedSelector = new RecordingConnectedOperationSelector(request =>
            AgentProfileConnectedOperationSelectionResult.Selected(
            [request.Candidates.Single(candidate =>
                candidate.DisplayName == "nyxop_github_read_2").CandidateId]));

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()),
                connectedOperationSelector: connectedSelector)
            .MaterializeVerifiedAuthorizationContinuationAsync(
                sealedProfile,
                authority,
                new NyxIdChatVerifiedAuthorizationContinuation
                {
                    OriginTurnId = "turn-origin-alpha",
                    ServiceSlug = "api-github-alpha",
                    VerifiedResource = new NyxIdChatSafeResourceRef
                    {
                        UserService = new NyxIdChatUserServiceRef
                        {
                            UserServiceId = "us-github-alpha",
                        },
                    },
                },
                "Read repository metadata from my GitHub connection.",
                llmControl: null,
                toolContext: ToolContext(),
                ct: CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().ContainSingle()
            .Which.Should().Be("nyxop_github_read_2");
        connectedSelector.CallCount.Should().Be(1);
        connectedSelector.LastRequest!.Candidates.Should().HaveCount(5);
        connectedSelector.LastRequest.Candidates.Should().NotContain(candidate =>
            candidate.DisplayName == "nyxop_github_other_connection");
    }

    [Fact]
    public async Task VerifiedAuthorizationContinuation_ProfileMemberWithoutSkill_ShouldKeepCommittedTaskPolicy()
    {
        var connectedTools = Enumerable.Range(1, 5)
            .Select(index => (IAgentTool)new AdmittedTestTool(
                $"nyxop_github_read_{index}",
                CreateReadAdmission(
                    "us-github-alpha",
                    "api-github-alpha",
                    $"endpoint-read-{index}",
                    "api-github")))
            .ToArray();
        var profile = BuildProfile(withAlias: true);
        profile.Members[0].SkillRef = null;
        profile.Members[0].ExpectedSkillName = string.Empty;
        profile.Members[0].ReviewedPublisherId = string.Empty;
        profile.Members[0].SealedSkillSha256 = ByteString.Empty;
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly));
        var exactTaskSelector = ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly);
        exactTaskSelector.EndpointId = "endpoint-read-4";
        profile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(exactTaskSelector);
        var sealedProfile = SealProfile(profile);
        var authority = new AgentProfileTurnAuthorityState
        {
            CandidateRoute = new AgentProfileTurnCandidateRouteIdentity
            {
                ProfileId = sealedProfile.ProfileId,
                ProfileVersion = sealedProfile.ProfileVersion,
                PolicyRevision = sealedProfile.PolicyRevision,
                IntentId = "intent-alpha",
            },
            AuthorityKind = AgentProfileTurnAuthorityKind.Selected,
            AuthorityCeilingToolNames = { "nyxid_require_service" },
        };
        var connectedSelector = new RecordingConnectedOperationSelector(
            AgentProfileConnectedOperationSelectionResult.Failed("must_not_select"));

        var catalog = await NewMaterializer(
                RegistryWithRoute(connectedTools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()),
                connectedOperationSelector: connectedSelector)
            .MaterializeVerifiedAuthorizationContinuationAsync(
                sealedProfile,
                authority,
                new NyxIdChatVerifiedAuthorizationContinuation
                {
                    OriginTurnId = "turn-origin-alpha",
                    ServiceSlug = "api-github-alpha",
                    VerifiedResource = new NyxIdChatSafeResourceRef
                    {
                        UserService = new NyxIdChatUserServiceRef
                        {
                            UserServiceId = "us-github-alpha",
                        },
                    },
                },
                "Read the fourth repository operation.",
                llmControl: null,
                toolContext: ToolContext(),
                ct: CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().ContainSingle()
            .Which.Should().Be("nyxop_github_read_4");
        connectedSelector.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_BroadWriteSelector_ShouldAdmitExactlyOneSelectedWrite()
    {
        IAgentTool[] tools =
        [
            .. Enumerable.Range(1, 4)
                .Select(index => (IAgentTool)new AdmittedTestTool(
                    $"nyxop_github_read_{index}",
                    CreateReadAdmission(
                        "us-github-alpha",
                        "api-github-alpha",
                        $"endpoint-read-{index}",
                        "api-github"))),
            new AdmittedTestTool(
                "nyxop_github_write",
                CreateWriteAdmission(
                    "us-github-alpha",
                    "api-github-alpha",
                    "endpoint-write",
                    "api-github")),
            new TestTool("ask_user"),
        ];
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add("ask_user");
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly,
            AgentToolOperationRiskPayload.Write));
        profile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly,
            AgentToolOperationRiskPayload.Write));
        var connectedSelector = new RecordingConnectedOperationSelector(request =>
            AgentProfileConnectedOperationSelectionResult.Selected(
            [request.Candidates.Single(candidate =>
                candidate.DisplayName == "nyxop_github_write").CandidateId]));

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()),
                connectedOperationSelector: connectedSelector)
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha update the repository",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().ContainSingle()
            .Which.Should().Be("nyxop_github_write");
        connectedSelector.LastRequest!.MaximumReadSelections.Should().Be(3);
        connectedSelector.LastRequest.MaximumWriteSelections.Should().Be(1);
    }

    [Fact]
    public async Task MaterializeAsync_MixedBroadSelector_ShouldSelectReadDespiteMultipleWrites()
    {
        IAgentTool[] tools =
        [
            .. Enumerable.Range(1, 4)
                .Select(index => (IAgentTool)new AdmittedTestTool(
                    $"nyxop_github_read_{index}",
                    CreateReadAdmission(
                        "us-github-alpha",
                        "api-github-alpha",
                        $"endpoint-read-{index}",
                        "api-github"))),
            .. Enumerable.Range(1, 2)
                .Select(index => (IAgentTool)new AdmittedTestTool(
                    $"nyxop_github_write_{index}",
                    CreateWriteAdmission(
                        "us-github-alpha",
                        "api-github-alpha",
                        $"endpoint-write-{index}",
                        "api-github"))),
            new TestTool("ask_user"),
        ];
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add("ask_user");
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly,
            AgentToolOperationRiskPayload.Write));
        profile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly,
            AgentToolOperationRiskPayload.Write));
        var connectedSelector = new RecordingConnectedOperationSelector(request =>
            AgentProfileConnectedOperationSelectionResult.Selected(
            [request.Candidates.Single(candidate =>
                candidate.DisplayName == "nyxop_github_read_2").CandidateId]));

        var (catalog, preparation) = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()),
                connectedOperationSelector: connectedSelector)
            .MaterializeWithPreparationAsync(
                SealProfile(profile),
                "/alpha read the repository metadata",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().ContainSingle()
            .Which.Should().Be("nyxop_github_read_2");
        preparation.Authority.AuthorityCeilingToolNames.Should().ContainSingle()
            .Which.Should().Be("nyxop_github_read_2");
        connectedSelector.CallCount.Should().Be(1);
        connectedSelector.LastRequest!.Candidates.Should().HaveCount(6);
        connectedSelector.LastRequest.Candidates.Count(candidate =>
            candidate.Risk == AgentToolOperationRisk.Write).Should().Be(2);
    }

    [Fact]
    public async Task MaterializeAsync_InvalidConnectedSelection_ShouldExposeOnlyClarificationTool()
    {
        IAgentTool[] tools = Enumerable.Range(1, 4)
            .Select(index => (IAgentTool)new AdmittedTestTool(
                $"nyxop_github_read_{index}",
                CreateReadAdmission(
                    "us-github-alpha",
                    "api-github-alpha",
                    $"endpoint-read-{index}",
                    "api-github")))
            .Append(new TestTool("ask_user"))
            .ToArray();
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add("ask_user");
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly));
        profile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly));
        var connectedSelector = new RecordingConnectedOperationSelector(request =>
            AgentProfileConnectedOperationSelectionResult.Selected(
                request.Candidates.Select(static candidate => candidate.CandidateId).ToArray()));

        var (catalog, preparation) = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()),
                connectedOperationSelector: connectedSelector)
            .MaterializeWithPreparationAsync(
                SealProfile(profile),
                "/alpha update the repository",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().ContainSingle().Which.Should().Be("ask_user");
        preparation.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation &&
            diagnostic.Detail == "connected_operation_selector_output_invalid");
    }

    [Fact]
    public async Task MaterializeAsync_MultipleBroadWrites_ShouldRequireClarificationWithoutSelector()
    {
        IAgentTool[] tools = Enumerable.Range(1, 2)
            .Select(index => (IAgentTool)new AdmittedTestTool(
                $"nyxop_github_write_{index}",
                CreateWriteAdmission(
                    "us-github-alpha",
                    "api-github-alpha",
                    $"endpoint-write-{index}",
                    "api-github")))
            .Append(new TestTool("ask_user"))
            .ToArray();
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add("ask_user");
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.Write));
        profile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.Write));
        var connectedSelector = new RecordingConnectedOperationSelector(
            AgentProfileConnectedOperationSelectionResult.NoMatch());

        var result = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()),
                connectedOperationSelector: connectedSelector)
            .MaterializeWithPreparationAsync(
                SealProfile(profile),
                "/alpha update the repository",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        result.Catalog.FinalAllowedToolNames.Should().ContainSingle()
            .Which.Should().Be("ask_user");
        result.Preparation.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation &&
            diagnostic.Detail == "connected_service_write_ambiguous");
        connectedSelector.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_LiteralMaximum_ShouldDiagnoseFilteredConnectedServiceToolsByKind()
    {
        IAgentTool[] tools =
        [
            new TestTool("task"),
            new AdmittedTestTool(
                "nyxop_github_read",
                CreateReadAdmission(
                    "us-github-alpha",
                    "api-github-alpha",
                    "endpoint-read",
                    "api-github")),
        ];
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add("task");
        profile.RecoveryToolPolicy.ToolNames.Clear();

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().ContainSingle().Which.Should().Be("task");
        catalog.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.MaximumPolicyFilteredTools &&
            diagnostic.Detail == "removed=1;nyx_id_operation=1");
    }

    [Fact]
    public async Task MaterializeAsync_UnmatchedConnectedServiceSelector_ShouldKeepOtherAllowedTools()
    {
        var tools = NewTools("task");
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add("task");
        profile.MaximumToolPolicy.ConnectedServiceSelectors.Add(ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly));
        profile.RecoveryToolPolicy.ToolNames.Clear();

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().ContainSingle().Which.Should().Be("task");
        catalog.Diagnostics.Should().NotContain(static diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ProfileInvalid);
    }

    [Fact]
    public async Task MaterializeAsync_UnmatchedSealedReadinessSelector_ShouldRequireBoundedToolWithoutLlmChoice()
    {
        IAgentTool[] tools = [new NyxIdBuiltInTestTool("nyxid_require_service")];
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        var maximumSelector = ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly);
        maximumSelector.Readiness = new AgentProfileConnectedServiceReadiness
        {
            RequestedScopes = { "read:user", "repo" },
        };
        var taskSelector = maximumSelector.Clone();
        profile.MaximumToolPolicy.ConnectedServiceSelectors.Add(maximumSelector);
        profile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(taskSelector);

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().ContainSingle()
            .Which.Should().Be("nyxid_require_service");
        catalog.HasUnresolvedConnectedServiceSelectors.Should().BeTrue();
        catalog.RequiredToolInvocation.Should().NotBeNull();
        catalog.RequiredToolInvocation!.ToolName.Should().Be("nyxid_require_service");
        using var arguments = System.Text.Json.JsonDocument.Parse(
            catalog.RequiredToolInvocation.ArgumentsJson);
        arguments.RootElement.GetProperty("service_slug").GetString().Should().Be("api-github");
        arguments.RootElement.GetProperty("requested_scopes").EnumerateArray()
            .Select(static scope => scope.GetString())
            .Should().Equal("read:user", "repo");
    }

    [Fact]
    public async Task MaterializeAsync_AuthorityFilteredExistingOperation_ShouldNotRequestConnectionAgain()
    {
        IAgentTool[] tools =
        [
            new AdmittedTestTool(
                "nyxop_github_read",
                CreateReadAdmission(
                    "us-github-alpha",
                    "api-github-alpha",
                    "endpoint-read",
                    "api-github")),
            new NyxIdBuiltInTestTool("nyxid_require_service"),
        ];
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add("nyxid_require_service");
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        var taskSelector = ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly);
        taskSelector.Readiness = new AgentProfileConnectedServiceReadiness
        {
            RequestedScopes = { "read:user" },
        };
        profile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(taskSelector);

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.HasUnresolvedConnectedServiceSelectors.Should().BeFalse();
        catalog.RequiredToolInvocation.Should().BeNull();
    }

    [Fact]
    public async Task MaterializeAsync_EmptySealedReadinessScopes_ShouldReturnRestrictedEmpty()
    {
        IAgentTool[] tools = [new NyxIdBuiltInTestTool("nyxid_require_service")];
        var profile = BuildProfile(withAlias: true);
        var selector = ConnectedServiceSelector(
            "api-github",
            AgentToolOperationRiskPayload.ReadOnly);
        selector.Readiness = new AgentProfileConnectedServiceReadiness();
        profile.MaximumToolPolicy.ConnectedServiceSelectors.Add(selector);

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.RequiredToolInvocation.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ProfileInvalid);
    }

    [Fact]
    public async Task MaterializeAsync_DuplicateAlias_ShouldRequireDisambiguationWithoutFetching()
    {
        var tools = NewTools("recovery", "task", "extra");
        var profile = BuildProfile(withAlias: true);
        var collidingMember = profile.Members[0].Clone();
        collidingMember.IntentId = "intent-beta";
        collidingMember.RoutingDescription = "Route beta requests.";
        profile.Members.Add(collidingMember);
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var result = await NewMaterializer(RegistryWithRoute(tools), classifier, fetcher)
            .MaterializeWithPreparationAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);
        var catalog = result.Catalog;

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().BeNull();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        result.Preparation.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation &&
            diagnostic.Detail == "alias_collision");
        result.Preparation.Authority.DegradationReasons.Should().Equal(
            AgentProfileTurnDegradationReason.CatalogNeedsDisambiguation);
        classifier.CallCount.Should().Be(0);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_AliasPrefixWithoutBoundary_ShouldUseClassifierAndRestrictOnNoMatch()
    {
        var tools = NewTools("recovery", "task", "extra");
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(RegistryWithRoute(tools), classifier, fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alphabet",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().BeNull();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierNoMatch);
        classifier.CallCount.Should().Be(1);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_BlankMessage_ShouldUseClassifierAndRestrictWithoutFetching()
    {
        var tools = NewTools("recovery", "task", "extra");
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(RegistryWithRoute(tools), classifier, fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                " \t\n",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().BeNull();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierNoMatch);
        classifier.CallCount.Should().Be(1);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_ClassifierMatch_ShouldSelectExactMember()
    {
        var tools = NewTools("recovery", "task", "extra");
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha"));
        var fetcher = new RecordingFetcher(SuccessfulFetch());
        var profile = BuildProfile();
        profile.Members.Single().SideEffectClass = AgentProfileSideEffectClass.ExternalHandoff;

        var result = await NewMaterializer(RegistryWithRoute(tools), classifier, fetcher)
            .MaterializeWithPreparationAsync(
                SealProfile(profile),
                "classify me",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);
        var catalog = result.Catalog;

        catalog.SelectedIntentId.Should().Be("intent-alpha");
        result.Preparation.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierMatched &&
            diagnostic.Detail == "intent-alpha");
        classifier.CallCount.Should().Be(1);
        classifier.LastRequest!.Candidates.Should().ContainSingle(candidate =>
            candidate.IntentId == "intent-alpha" &&
            candidate.RoutingDescription == "Route alpha requests." &&
            candidate.SideEffectClass == AgentProfileSideEffectClass.ExternalHandoff);
    }

    [Fact]
    public async Task MaterializeAsync_ClassifierNoMatch_ShouldUseRestrictedEmptyCatalog()
    {
        var tools = NewTools("recovery", "task", "extra");
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "no route",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.SelectedIntentId.Should().BeNull();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierNoMatch);
        fetcher.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(true, 600)]
    [InlineData(false, 0)]
    public async Task MaterializeAsync_ClassifierNotConfigured_ShouldUseRecoveryWithoutClassifierOrFetch(
        bool removeMembers,
        int classifierTimeoutMs)
    {
        var tools = NewTools("recovery", "task", "extra");
        var profile = BuildProfile();
        profile.ClassifierTimeoutMs = classifierTimeoutMs;
        if (removeMembers)
            profile.Members.Clear();
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha"));
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var result = await NewMaterializer(RegistryWithRoute(tools), classifier, fetcher)
            .MaterializeWithPreparationAsync(
                SealProfile(profile),
                "classify",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);
        var catalog = result.Catalog;

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().BeNull();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        result.Preparation.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierFailed &&
            diagnostic.Detail == "classifier_not_configured");
        result.Preparation.Authority.DegradationReasons.Should().Equal(
            AgentProfileTurnDegradationReason.ClassifierFailed);
        classifier.CallCount.Should().Be(0);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_CallerCancellationDuringClassification_ShouldPropagate()
    {
        var tools = NewTools("recovery", "task", "extra");
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();
        var classifier = new CancellationAwareClassifier();

        var act = async () => await NewMaterializer(
                RegistryWithRoute(tools),
                classifier,
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "classify",
                "token",
                tools,
                ToolContext(),
                callerCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        classifier.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task MaterializeAsync_CallerCancellationDuringToolDiscovery_ShouldPropagate()
    {
        var tools = NewTools("recovery", "task", "extra");
        var registry = new RecordingToolSetRegistry();
        registry.Add("profile.route", new CancellationAwareToolSource());
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());
        var fetcher = new RecordingFetcher(SuccessfulFetch());
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();

        var act = async () => await NewMaterializer(registry, classifier, fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "classify",
                "token",
                tools,
                ToolContext(),
                callerCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        classifier.CallCount.Should().Be(0);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_ClassifierException_ShouldFailClosedToRecovery()
    {
        var tools = NewTools("recovery", "task", "extra");
        var classifier = new RecordingClassifier(new InvalidOperationException("classifier failed"));

        var result = await NewMaterializer(
                RegistryWithRoute(tools),
                classifier,
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeWithPreparationAsync(
                SealProfile(BuildProfile()),
                "classify",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);
        var catalog = result.Catalog;

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        result.Preparation.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierFailed &&
            diagnostic.Detail == "classifier_exception");
        result.Preparation.Authority.DegradationReasons.Should().Equal(
            AgentProfileTurnDegradationReason.ClassifierFailed);
    }

    [Fact]
    public async Task MaterializeAsync_ClassifierReturnsUnknownIntent_ShouldUseRecoveryWithoutFetching()
    {
        var tools = NewTools("recovery", "task", "extra");
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var result = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-outside-profile")),
                fetcher)
            .MaterializeWithPreparationAsync(
                SealProfile(BuildProfile()),
                "classify",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);
        var catalog = result.Catalog;

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.FinalAllowedToolNames.Should().NotContain("task");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().BeNull();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        result.Preparation.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierFailed &&
            diagnostic.Detail == "unknown_intent");
        result.Preparation.Authority.DegradationReasons.Should().Equal(
            AgentProfileTurnDegradationReason.ClassifierFailed);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_Shadow_ShouldObserveCandidateProofWithoutChangingExecutionCatalog()
    {
        var tools = NewTools("recovery", "task", "extra");
        var registry = RegistryWithRoute(tools);
        var profile = BuildProfile(withAlias: true);
        profile.ActivationMode = AgentProfileActivationMode.Shadow;
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var result = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher)
            .MaterializeWithPreparationAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);
        var catalog = result.Catalog;

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.CandidateIntentId.Should().Be("intent-alpha");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ShadowCandidate);
        result.Preparation.ShadowCandidateProof.Should().NotBeNull();
        result.Preparation.ShadowCandidateProof!.ToolDescriptors.Select(static descriptor => descriptor.Name)
            .Should().Equal("recovery", "task");
        result.Preparation.ShadowCandidateProof.ToolCount.Should().Be(2);
        result.Preparation.ShadowCandidateProof.CatalogDigest.Should().StartWith("sha256:");
        fetcher.CallCount.Should().Be(0);
        registry.ResolveCalls.Should().Equal(
            ["profile.route", "profile.route"],
            "the test materializes the unchanged recovery catalog after observing the candidate proof");
    }

    [Fact]
    public async Task MaterializeAsync_ExactFetchIdentityOrBodyFailure_ShouldUseRecoveryOnly()
    {
        var tools = NewTools("recovery", "task", "extra");
        var failures = new[]
        {
            ExactRemoteSkillFetchResult.Failed(ExactRemoteSkillFetchFailureCode.NotFound),
            ExactRemoteSkillFetchResult.Success(
                SkillGuid, SkillVersion, "wrong-name", PublisherId, SkillSha256, SkillMarkdown),
            ExactRemoteSkillFetchResult.Success(
                SkillGuid, SkillVersion, SkillName, PublisherId, SkillSha256, new string('x', 300)),
        };

        foreach (var failure in failures)
        {
            var catalog = await NewMaterializer(
                    RegistryWithRoute(tools),
                    new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha")),
                    new RecordingFetcher(failure))
                .MaterializeAsync(
                    SealProfile(BuildProfile()),
                    "select",
                    "token",
                    tools,
                    ToolContext(),
                    CancellationToken.None);

            catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
            catalog.SelectedSkillPromptLayer.Should().BeNull();
            catalog.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed ||
                diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillIdentityMismatch ||
                diagnostic.Code == AgentProfileTurnDiagnosticCode.SelectedSkillBodyInvalid);
        }
    }

    [Theory]
    [InlineData("22222222-2222-2222-2222-222222222222", SkillVersion, PublisherId, false)]
    [InlineData(SkillGuid, "9.9", PublisherId, false)]
    [InlineData(SkillGuid, SkillVersion, "publisher-beta", false)]
    [InlineData(SkillGuid, SkillVersion, PublisherId, true)]
    public async Task MaterializeAsync_ExactFetchIdentityMismatch_ShouldUseRecoveryOnly(
        string fetchedGuid,
        string fetchedVersion,
        string fetchedPublisherId,
        bool missingSkillHash)
    {
        var tools = NewTools("recovery", "task", "extra");
        var fetcher = new RecordingFetcher(ExactRemoteSkillFetchResult.Success(
            fetchedGuid,
            fetchedVersion,
            SkillName,
            fetchedPublisherId,
            missingSkillHash ? ByteString.Empty : SkillSha256,
            SkillMarkdown));

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha")),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "select",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.FinalAllowedToolNames.Should().NotContain("task");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().Be("intent-alpha");
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillIdentityMismatch);
        fetcher.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task MaterializeAsync_FetchedHashMismatchShouldUseRecoveryOnly()
    {
        var tools = NewTools("recovery", "task", "extra");
        var fetcher = new RecordingFetcher(ExactRemoteSkillFetchResult.Success(
            SkillGuid,
            SkillVersion,
            SkillName,
            PublisherId,
            ByteString.CopyFrom(Enumerable.Repeat((byte)0xff, 32).ToArray()),
            SkillMarkdown));

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha")),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "select",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillIdentityMismatch);
    }

    [Fact]
    public async Task MaterializeAsync_EmptySelectedSkillBody_ShouldUseRecoveryWithoutPromptInjection()
    {
        var tools = NewTools("recovery", "task", "extra");
        var fetcher = new RecordingFetcher(SuccessfulFetch("---\nname: skill-alpha\n---\n   "));

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().Be("intent-alpha");
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.SelectedSkillBodyInvalid &&
            diagnostic.Detail == "frontmatter_identity_invalid");
        fetcher.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task MaterializeAsync_SelectedSkillFrontmatterNameMismatch_ShouldUseRecoveryWithoutPromptInjection()
    {
        var tools = NewTools("recovery", "task", "extra");
        var fetcher = new RecordingFetcher(SuccessfulFetch(
            "---\nname: skill-beta\n---\nSelected instructions."));

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().Be("intent-alpha");
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.SelectedSkillBodyInvalid &&
            diagnostic.Detail == "frontmatter_identity_invalid");
        fetcher.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task MaterializeAsync_ExactFetcherUnavailable_ShouldUseRecoveryOnly()
    {
        var tools = NewTools("recovery", "task", "extra");

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher: null)
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed &&
            diagnostic.Detail == "exact_fetch_unavailable");
    }

    [Fact]
    public async Task MaterializeAsync_ExactFetchTimeout_ShouldUseRecoveryOnly()
    {
        var tools = NewTools("recovery", "task", "extra");
        var profile = BuildProfile(withAlias: true);
        profile.ExactSkillFetchTimeoutMs = 1_000;
        var timeProvider = new ManualDeadlineTimeProvider();
        var fetcher = new CancellationBlockingFetcher();

        var materialization = NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher,
                timeProvider)
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);
        await fetcher.Started;

        timeProvider.Advance(TimeSpan.FromMilliseconds(profile.ExactSkillFetchTimeoutMs));
        var catalog = await materialization;

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed &&
            diagnostic.Detail == "timeout");
        fetcher.CancellationObserved.Should().BeTrue();
    }

    [Fact]
    public async Task MaterializeAsync_ExactFetcherException_ShouldUseRecoveryOnly()
    {
        var tools = NewTools("recovery", "task", "extra");

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new ThrowingFetcher(new InvalidOperationException("fetch failed")))
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed &&
            diagnostic.Detail == "fetch_exception");
    }

    [Fact]
    public async Task MaterializeAsync_CallerCancellationDuringExactFetch_ShouldPropagate()
    {
        var tools = NewTools("recovery", "task", "extra");
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();

        var act = async () => await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new CancellationBlockingFetcher())
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                callerCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task MaterializeAsync_InvalidSnapshot_ShouldReturnRestrictedEmpty()
    {
        var tools = NewTools("recovery", "task", "extra");
        var profile = SealProfile(BuildProfile());
        profile.PolicyRevision = "tampered";

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha")),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(profile, "select", "token", tools, ToolContext(), CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.ToolVisibility.IsRestricted.Should().BeTrue();
        catalog.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ProfileInvalid);
    }

    [Fact]
    public async Task MaterializeAsync_UnknownRouteToolSet_ShouldReturnRestrictedEmpty()
    {
        var tools = NewTools("recovery", "task", "extra");
        var registry = new RecordingToolSetRegistry();

        var catalog = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "no route",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.RouteToolSetUnavailable);
    }

    [Fact]
    public async Task MaterializeAsync_RouteToolSetResolveThrows_ShouldReturnRestrictedEmptyWithoutFetching()
    {
        var tools = NewTools("recovery", "task", "extra");
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha"));
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                new ThrowingToolSetRegistry(),
                classifier,
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "classify",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.ToolVisibility.IsRestricted.Should().BeTrue();
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().BeNull();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.RouteToolSetUnavailable &&
            diagnostic.Detail == "profile.route");
        classifier.CallCount.Should().Be(0);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_RouteDiscoveryFailure_ShouldReturnRestrictedEmpty()
    {
        var tools = NewTools("recovery", "task", "extra");
        var registry = new RecordingToolSetRegistry();
        registry.Add("profile.route", new ThrowingToolSource());
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha")),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "no route",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolDiscoveryFailed);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_CollisionAndCapabilityRejection_ShouldNotGrantThoseNames()
    {
        var duplicateA = new TestTool("recovery");
        var duplicateB = new TestTool("recovery");
        var blocked = new CapabilityTool(
            "task",
            [AgentToolCapabilities.ExcludeFromDirectChannelChat]);
        var routeTools = new IAgentTool[] { duplicateA, duplicateB, blocked };
        var registry = RegistryWithRoute(routeTools);
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha")),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "no route",
                "token",
                routeTools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolNameCollision &&
            diagnostic.Detail == "recovery");
        catalog.Diagnostics.Should().NotContain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolCapabilityRejected);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_HumanSessionToolWithoutToken_ShouldBeRejected()
    {
        var humanSessionTool = new CapabilityTool(
            "task",
            [AgentToolCapabilities.RequiresHumanSession]);
        var tools = new IAgentTool[] { humanSessionTool };
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alpha",
                accessToken: null,
                tools,
                ToolContext(accessToken: null),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolCapabilityRejected &&
            diagnostic.Detail == "task");
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_HumanSessionToolWithToken_ShouldBeAdmittedWhenPoliciesAllow()
    {
        var humanSessionTool = new CapabilityTool(
            "task",
            [AgentToolCapabilities.RequiresHumanSession]);
        var tools = new IAgentTool[] { humanSessionTool };
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("task");
        catalog.SelectedIntentId.Should().Be("intent-alpha");
        catalog.Diagnostics.Should().NotContain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolCapabilityRejected);
        fetcher.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task MaterializeAsync_HumanSessionToolWithRawProxyDelegation_ShouldBeRejected()
    {
        var humanSessionTool = new CapabilityTool(
            "task",
            [AgentToolCapabilities.RequiresHumanSession]);
        var tools = new IAgentTool[] { humanSessionTool };
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alpha",
                "proxy-delegation",
                tools,
                ToolContext(
                    "proxy-delegation",
                    AgentToolNyxIdCredentialKind.ProxyDelegation),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.ExactTools.Should().BeEmpty();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolCapabilityRejected &&
            diagnostic.Detail == "task");
        // Capability ineligibility degrades the one tool instead of failing
        // the whole materialization, so the selected-skill layer still pins.
        fetcher.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task MaterializeAsync_NyxIdChatProfile_ShouldHideRawProxyWithoutLosingTypedTools()
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var rawProxy = new NyxIdProxyTool(new NyxIdApiClient(options, new HttpClient()));
        var requireService = new TestTool("nyxid_require_service");
        var typedInventory = new TestTool("nyxid_service_inventory");
        IAgentTool[] tools = [rawProxy, requireService, typedInventory];
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolNames.Add(tools.Select(static tool => tool.Name));
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.RecoveryToolPolicy.ToolNames.Add(requireService.Name);
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolNames.Add([rawProxy.Name, typedInventory.Name]);

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should()
            .BeEquivalentTo("nyxid_require_service", "nyxid_service_inventory");
        catalog.ExactTools.Keys.Should()
            .BeEquivalentTo("nyxid_require_service", "nyxid_service_inventory");
    }

    [Fact]
    public async Task MaterializeAsync_TaskToolSetFailure_ShouldKeepRecoveryCeilingAndAllowRequestLocalBody()
    {
        var tools = NewTools("recovery", "task", "extra");
        var registry = RegistryWithRoute(tools);
        var profile = BuildProfile(withAlias: true);
        profile.Members[0].TaskToolPolicy.ToolSetRefs.Add("missing.task");

        var catalog = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedIntentId.Should().Be("intent-alpha");
        catalog.CandidateIntentId.Should().Be("intent-alpha");
        catalog.SelectedSkillPromptLayer!.Content.Should().Be("Selected instructions.");
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolSetUnavailable);
    }

    [Fact]
    public void NarrowToVerifiedUserService_ShouldKeepOnlyExactAdmittedOperations()
    {
        IAgentTool[] tools =
        [
            new AdmittedTestTool(
                "operation-alpha-read",
                CreateReadAdmission("us-alpha", "service-alpha", "endpoint-read")),
            new AdmittedTestTool(
                "operation-alpha-list",
                CreateReadAdmission("us-alpha", "service-alpha", "endpoint-list")),
            new AdmittedTestTool(
                "operation-beta-read",
                CreateReadAdmission("us-beta", "service-beta", "endpoint-read")),
            new TestTool("global-fallback"),
        ];
        var catalog = new AgentTurnToolCatalog(
            tools.Select(static tool => tool.Name),
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            selectedIntentId: "general_nyxid_assistant",
            candidateIntentId: "general_nyxid_assistant",
            exactTools: tools);

        var narrowed = AgentTurnToolCatalogMaterializer.NarrowToVerifiedUserService(
            catalog,
            VerifiedAuthorization("us-alpha", "service-alpha"));

        narrowed.FinalAllowedToolNames.Should().BeEquivalentTo(
            "operation-alpha-read",
            "operation-alpha-list");
        narrowed.ExactTools.Keys.Should().BeEquivalentTo(
            "operation-alpha-read",
            "operation-alpha-list");
        narrowed.FinalAllowedToolNames.Should().NotContain("global-fallback");
    }

    [Fact]
    public void NarrowToVerifiedUserService_ClarificationCatalog_ShouldPreserveAskUser()
    {
        IAgentTool askUser = new TestTool("ask_user");
        var diagnostics = new[]
        {
            new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
                "connected_service_write_ambiguous"),
        };
        var catalog = new AgentTurnToolCatalog(
            [askUser.Name],
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            selectedIntentId: "general_nyxid_assistant",
            candidateIntentId: "general_nyxid_assistant",
            diagnostics,
            exactTools: [askUser]);

        var narrowed = AgentTurnToolCatalogMaterializer.NarrowToVerifiedUserService(
            catalog,
            VerifiedAuthorization("us-alpha", "service-alpha"));

        narrowed.Should().BeSameAs(catalog);
        narrowed.FinalAllowedToolNames.Should().ContainSingle().Which.Should().Be("ask_user");
    }

    [Theory]
    [InlineData("us-other", "service-alpha")]
    [InlineData("us-alpha", "service-other")]
    public void NarrowToVerifiedUserService_WhenTypedIdentityDiffers_ShouldReturnRestrictedEmpty(
        string userServiceId,
        string serviceSlug)
    {
        IAgentTool[] tools =
        [
            new AdmittedTestTool(
                "operation-alpha-read",
                CreateReadAdmission("us-alpha", "service-alpha", "endpoint-read")),
            new TestTool("global-fallback"),
        ];
        var catalog = new AgentTurnToolCatalog(
            tools.Select(static tool => tool.Name),
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            selectedIntentId: "general_nyxid_assistant",
            candidateIntentId: "general_nyxid_assistant",
            exactTools: tools);

        var narrowed = AgentTurnToolCatalogMaterializer.NarrowToVerifiedUserService(
            catalog,
            VerifiedAuthorization(userServiceId, serviceSlug));

        narrowed.FinalAllowedToolNames.Should().BeEmpty();
        narrowed.ToolVisibility.IsRestricted.Should().BeTrue();
        narrowed.ExactTools.Should().BeEmpty();
    }

    private static NyxIdChatVerifiedAuthorizationContinuation VerifiedAuthorization(
        string userServiceId,
        string serviceSlug) =>
        new()
        {
            ActionRequestId = "action-alpha",
            OriginTurnId = "turn-alpha",
            SourceToolStepId = "step-tool-alpha",
            PostconditionStepId = "step-postcondition-alpha",
            VerifiedResource = new NyxIdChatSafeResourceRef
            {
                UserService = new NyxIdChatUserServiceRef
                {
                    UserServiceId = userServiceId,
                },
            },
            ServiceSlug = serviceSlug,
            ResumeRequirement =
                NyxIdChatAuthorizationResumeRequirement.CompleteOriginalServiceRequest,
        };

    private static AgentToolOperationAdmission CreateReadAdmission(
        string userServiceId,
        string serviceSlug,
        string endpointId,
        string catalogServiceSlug = "") =>
        new(
            userServiceId,
            serviceSlug,
            new AgentToolOperationIdentity.PublishedEndpoint(endpointId),
            AgentToolOperationAuthorizationBasis.PublishedContract,
            "GET",
            "/items",
            "contract-digest-alpha",
            [],
            null,
            AgentToolOperationResponsePolicy.TextOnly,
            new AgentToolOperationExecutionPolicy(
                AgentToolOperationRisk.ReadOnly,
                AgentToolOperationApproval.None,
                AgentToolOperationEnforcementOwner.Aevatar,
                [AgentToolOperationExecutionMode.Interactive]),
            "catalog-digest-alpha",
            ReadBack: null,
            CatalogServiceSlug: catalogServiceSlug);

    private static AgentToolOperationAdmission CreateWriteAdmission(
        string userServiceId,
        string serviceSlug,
        string endpointId,
        string catalogServiceSlug) =>
        new(
            userServiceId,
            serviceSlug,
            new AgentToolOperationIdentity.PublishedEndpoint(endpointId),
            AgentToolOperationAuthorizationBasis.PublishedContract,
            "POST",
            "/items",
            "contract-digest-alpha",
            [],
            null,
            AgentToolOperationResponsePolicy.TextOnly,
            new AgentToolOperationExecutionPolicy(
                AgentToolOperationRisk.Write,
                AgentToolOperationApproval.Required,
                AgentToolOperationEnforcementOwner.Aevatar,
                [AgentToolOperationExecutionMode.Interactive]),
            "catalog-digest-alpha",
            ReadBack: null,
            CatalogServiceSlug: catalogServiceSlug);

    private static AgentProfileConnectedServiceSelector ConnectedServiceSelector(
        string catalogServiceSlug,
        params AgentToolOperationRiskPayload[] allowedRisks) => new()
    {
        CatalogServiceSlug = catalogServiceSlug,
        AllowedRisks = { allowedRisks },
    };

    private static AgentTurnToolCatalogMaterializer NewMaterializer(
        IToolSetRegistry registry,
        IAgentProfileTurnClassifier classifier,
        IExactRemoteSkillFetcher? fetcher,
        TimeProvider? timeProvider = null,
        IAgentProfileConnectedOperationSelector? connectedOperationSelector = null) =>
        new(
            registry,
            classifier,
            fetcher,
            timeProvider: timeProvider,
            connectedOperationSelector: connectedOperationSelector);

    private static AgentProfileSnapshot BuildProfile(bool withAlias = false)
    {
        var member = new AgentProfileSkillMember
        {
            IntentId = "intent-alpha",
            RoutingDescription = "Route alpha requests.",
            SkillRef = new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = SkillVersion },
            TaskToolPolicy = new AgentProfileToolPolicy(),
            SideEffectClass = AgentProfileSideEffectClass.ReadOnly,
            ExpectedSkillName = SkillName,
            ReviewedPublisherId = PublisherId,
            SealedSkillSha256 = SkillSha256,
        };
        member.TaskToolPolicy.ToolNames.Add("task");
        if (withAlias)
            member.ExplicitTriggerAliases.Add("/alpha");

        var profile = new AgentProfileSnapshot
        {
            ProfileId = "profile-alpha",
            ProfileVersion = "profile-v1",
            AgentKind = "nyxid.chat",
            PolicyRevision = "policy-v1",
            RouteToolSetRef = "profile.route",
            MaximumToolPolicy = new AgentProfileToolPolicy(),
            RecoveryToolPolicy = new AgentProfileToolPolicy(),
            ClassifierTimeoutMs = 600,
            ExactSkillFetchTimeoutMs = 1_500,
            MaxSelectedSkillBytes = 256,
            ActivationMode = AgentProfileActivationMode.Enforced,
        };
        profile.MaximumToolPolicy.ToolNames.Add(["recovery", "task", "extra"]);
        profile.RecoveryToolPolicy.ToolNames.Add("recovery");
        profile.Members.Add(member);
        return profile;
    }

    private static AgentProfileSnapshot SealProfile(AgentProfileSnapshot profile) =>
        AgentProfileSnapshotCodec.Seal(profile);

    private static ExactRemoteSkillFetchResult SuccessfulFetch(string skillMarkdown = SkillMarkdown) =>
        ExactRemoteSkillFetchResult.Success(
            SkillGuid,
            SkillVersion,
            SkillName,
            PublisherId,
            SkillSha256,
            skillMarkdown);

    private static AgentToolExecutionContext ToolContext(
        string? accessToken = "token",
        AgentToolNyxIdCredentialKind credentialKind =
            AgentToolNyxIdCredentialKind.SourceReadableUserBearer) =>
        AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(accessToken, null, null, credentialKind),
        };

    private static IReadOnlyList<IAgentTool> NewTools(params string[] names) =>
        names.Select(static name => (IAgentTool)new TestTool(name)).ToArray();

    private static int CountReadTools(
        IEnumerable<string> names,
        IReadOnlyList<IAgentTool> tools)
    {
        var byName = tools.ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);
        return names.Count(name => byName.TryGetValue(name, out var tool) &&
                                   tool is IAgentToolOperationAdmissionOwner owner &&
                                   owner.OperationAdmission.ExecutionPolicy.Risk == AgentToolOperationRisk.ReadOnly);
    }

    private static RecordingToolSetRegistry RegistryWithRoute(IReadOnlyList<IAgentTool> tools)
    {
        var registry = new RecordingToolSetRegistry();
        registry.Add("profile.route", new StaticToolSource(tools));
        return registry;
    }

    private sealed class RecordingClassifier : IAgentProfileTurnClassifier
    {
        private readonly AgentProfileTurnClassificationResult? _result;
        private readonly Exception? _exception;

        public RecordingClassifier(AgentProfileTurnClassificationResult result) => _result = result;
        public RecordingClassifier(Exception exception) => _exception = exception;

        public int CallCount { get; private set; }
        public AgentProfileTurnClassificationRequest? LastRequest { get; private set; }

        public Task<AgentProfileTurnClassificationResult> ClassifyAsync(
            AgentProfileTurnClassificationRequest request,
            CancellationToken ct = default)
        {
            CallCount++;
            LastRequest = request;
            return _exception is not null
                ? Task.FromException<AgentProfileTurnClassificationResult>(_exception)
                : Task.FromResult(_result!);
        }
    }

    private sealed class SequencedClassifier(
        params AgentProfileTurnClassificationResult[] results) : IAgentProfileTurnClassifier
    {
        private readonly Queue<AgentProfileTurnClassificationResult> _results = new(results);

        public List<AgentProfileTurnClassificationRequest> Requests { get; } = [];

        public Task<AgentProfileTurnClassificationResult> ClassifyAsync(
            AgentProfileTurnClassificationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class RecordingConnectedOperationSelector :
        IAgentProfileConnectedOperationSelector
    {
        private readonly Func<
            AgentProfileConnectedOperationSelectionRequest,
            AgentProfileConnectedOperationSelectionResult> _select;

        public RecordingConnectedOperationSelector(
            AgentProfileConnectedOperationSelectionResult result)
            : this(_ => result)
        {
        }

        public RecordingConnectedOperationSelector(Func<
            AgentProfileConnectedOperationSelectionRequest,
            AgentProfileConnectedOperationSelectionResult> select) =>
            _select = select;

        public int CallCount { get; private set; }
        public AgentProfileConnectedOperationSelectionRequest? LastRequest { get; private set; }

        public Task<AgentProfileConnectedOperationSelectionResult> SelectAsync(
            AgentProfileConnectedOperationSelectionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            return Task.FromResult(_select(request));
        }
    }

    private sealed class CancellationAwareClassifier : IAgentProfileTurnClassifier
    {
        public int CallCount { get; private set; }

        public Task<AgentProfileTurnClassificationResult> ClassifyAsync(
            AgentProfileTurnClassificationRequest request,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromCanceled<AgentProfileTurnClassificationResult>(ct);
        }
    }

    private sealed class RecordingFetcher(ExactRemoteSkillFetchResult result) : IExactRemoteSkillFetcher
    {
        public int CallCount { get; private set; }

        public Task<ExactRemoteSkillFetchResult> FetchAsync(
            string accessToken,
            ExactRemoteSkillRef skillRef,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class CancellationBlockingFetcher : IExactRemoteSkillFetcher
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
        public bool CancellationObserved { get; private set; }

        public async Task<ExactRemoteSkillFetchResult> FetchAsync(
            string accessToken,
            ExactRemoteSkillRef skillRef,
            CancellationToken ct = default)
        {
            _started.TrySetResult();
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                canceled);
            try
            {
                await canceled.Task;
                return SuccessfulFetch();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }

    private sealed class ThrowingFetcher(Exception exception) : IExactRemoteSkillFetcher
    {
        public Task<ExactRemoteSkillFetchResult> FetchAsync(
            string accessToken,
            ExactRemoteSkillRef skillRef,
            CancellationToken ct = default) =>
            Task.FromException<ExactRemoteSkillFetchResult>(exception);
    }

    private sealed class RecordingToolSetRegistry : IToolSetRegistry
    {
        private readonly Dictionary<string, IReadOnlyList<IAgentToolSource>> _sources =
            new(StringComparer.Ordinal);

        public List<string> ResolveCalls { get; } = [];

        public void Add(string name, params IAgentToolSource[] sources) =>
            _sources.Add(name, sources);

        public IReadOnlyList<string> GetRegisteredNames() => _sources.Keys.ToArray();

        public ToolSetResolveResult Resolve(string? name)
        {
            name ??= string.Empty;
            ResolveCalls.Add(name);
            return _sources.TryGetValue(name, out var sources)
                ? ToolSetResolveResult.Success(name, sources)
                : ToolSetResolveResult.Failure(new ToolSetResolveError(
                    ToolSetResolveError.UnknownNameCode,
                    name,
                    "missing",
                    GetRegisteredNames()));
        }
    }

    private sealed class ThrowingToolSetRegistry : IToolSetRegistry
    {
        public IReadOnlyList<string> GetRegisteredNames() => [];

        public ToolSetResolveResult Resolve(string? name) =>
            throw new InvalidOperationException("resolve failed");
    }

    private sealed class StaticToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class TokenBoundToolSource(
        string requiredToken,
        IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public List<string?> ObservedTokens { get; } = [];

        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ObservedTokens.Add(AgentToolRequestContext.NyxIdAccessToken);
            return Task.FromResult<IReadOnlyList<IAgentTool>>(
                string.Equals(AgentToolRequestContext.NyxIdAccessToken, requiredToken, StringComparison.Ordinal)
                    ? tools
                    : []);
        }
    }

    private sealed class ThrowingToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<IAgentTool>>(new InvalidOperationException("discovery failed"));
    }

    private sealed class CancellationAwareToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromCanceled<IReadOnlyList<IAgentTool>>(ct);
    }

    private class TestTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";
        public virtual bool IsReadOnly => false;
        public virtual ToolPresentationDescriptor Presentation =>
            ToolPresentationDescriptors.Generic(name, name);
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }

    private sealed class NyxIdBuiltInTestTool(string name) : TestTool(name), INyxIdBuiltInTool;

    private sealed class ReadOnlyTestTool(string name) : TestTool(name)
    {
        public override bool IsReadOnly => true;
    }

    private sealed class AdmittedTestTool(
        string name,
        AgentToolOperationAdmission operationAdmission) :
        TestTool(name),
        IAgentToolOperationAdmissionOwner
    {
        public AgentToolOperationAdmission OperationAdmission { get; } = operationAdmission;

        public override ToolPresentationDescriptor Presentation { get; } = new()
        {
            InvocationName = name,
            DisplayName = name,
            Description = name,
            Kind = ToolPresentationKind.NyxIdOperation,
            Availability = ToolAvailability.Available,
            NyxIdOperation = new NyxIdOperationRef
            {
                ConnectedServiceId = operationAdmission.ServiceInstanceId,
                ServiceSlug = operationAdmission.ServiceSlug,
                CatalogServiceSlug = operationAdmission.CatalogServiceSlug,
                OperationId = operationAdmission.Identity is AgentToolOperationIdentity.PublishedEndpoint endpoint
                    ? endpoint.EndpointId
                    : string.Empty,
                HttpMethod = operationAdmission.HttpMethod,
                PathTemplate = operationAdmission.PathTemplate,
            },
        };
    }

    private sealed class PresentedTestTool(string name, string catalogServiceSlug) : TestTool(name)
    {
        public override ToolPresentationDescriptor Presentation { get; } = new()
        {
            InvocationName = name,
            DisplayName = name,
            Description = name,
            Kind = ToolPresentationKind.NyxIdOperation,
            Availability = ToolAvailability.Available,
            NyxIdOperation = new NyxIdOperationRef
            {
                CatalogServiceSlug = catalogServiceSlug,
            },
        };
    }

    private sealed class CapabilityTool(string name, IReadOnlyCollection<string> capabilities)
        : TestTool(name), IAgentToolCapabilityDescriptor
    {
        public IReadOnlyCollection<string> Capabilities { get; } = capabilities;
    }
}

internal static class AgentTurnToolCatalogMaterializerTestExtensions
{
    public static async Task<AgentTurnToolCatalog> MaterializeAsync(
        this AgentTurnToolCatalogMaterializer materializer,
        AgentProfileSnapshot profile,
        string userMessage,
        string? accessToken,
        IReadOnlyList<IAgentTool> registeredTools,
        AgentToolExecutionContext toolContext,
        CancellationToken ct = default)
    {
        var result = await materializer.MaterializeWithPreparationAsync(
            profile,
            userMessage,
            accessToken,
            registeredTools,
            toolContext,
            ct);
        return result.Catalog;
    }

    public static async Task<(
        AgentTurnToolCatalog Catalog,
        AgentProfileTurnAuthorityPreparation Preparation)> MaterializeWithPreparationAsync(
        this AgentTurnToolCatalogMaterializer materializer,
        AgentProfileSnapshot profile,
        string userMessage,
        string? accessToken,
        IReadOnlyList<IAgentTool> registeredTools,
        AgentToolExecutionContext toolContext,
        CancellationToken ct = default)
    {
        var preparation = await materializer.PrepareAsync(
            profile,
            "materializer-test-session",
            userMessage,
            registeredTools,
            toolContext,
            ct);
        var materialization = await materializer.MaterializeCommittedAsync(
            profile,
            preparation.Authority,
            accessToken,
            registeredTools,
            toolContext,
            ct);
        return (materialization.Catalog, preparation);
    }
}
