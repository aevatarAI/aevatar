using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.ExternalCapabilities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdCodeExecutionRouteAdmissionPreparerTests
{
    [Fact]
    public async Task AdmitAsync_AutoConnectedPlatformRoute_CreatesPersonalRouteAndCommitsItsExactProof()
    {
        const string yaml = "name: code-workflow\nsteps: []\n";
        var handler = new SequenceHandler(
            AutoConnectedInventory(),
            AutoConnectedKeysInventory(),
            AutoConnectedInventory(),
            AutoConnectedKeysInventory(),
            new SequenceResponse(
                HttpStatusCode.Conflict,
                """{"error":"concurrent create"}"""),
            PersonalExecutionInventory(),
            PersonalExecutionKeysInventory(),
            PersonalExecutionInventory(),
            PersonalExecutionKeysInventory());
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var client = new NyxIdApiClient(options, new HttpClient(handler));
        var factory = new TestClientFactory(client);
        var source = new NyxIdCodeExecutionWorkflowCapabilitySource(
            factory,
            options,
            logger: NullLogger<NyxIdCodeExecutionWorkflowCapabilitySource>.Instance);
        var preparer = new NyxIdCodeExecutionRouteAdmissionPreparer(
            new NyxIdCodeExecutionRoutePolicyReconciler(factory),
            options,
            NullLogger<NyxIdCodeExecutionRouteAdmissionPreparer>.Instance);
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = "code-workflow/run-code",
            ToolName = "code_execute",
            Selector = Selector(),
        });
        var admission = new WorkflowExternalCapabilityAdmissionService(
            new StaticParser(WorkflowYamlParseResult.Success("code-workflow", dependencies)),
            new ExternalWorkflowCapabilityReadinessService([source]),
            preparers: [preparer]);

        var plan = await admission.AdmitAsync(new WorkflowExternalCapabilityAdmissionRequest(
            Access(),
            yaml,
            new Dictionary<string, string>(),
            "test",
            ExternalCapabilityExecutionMode.Interactive,
            workflowId: "wf-alpha",
            revisionId: "rev-alpha"));

        handler.Requests.Select(static request => request.Method)
            .Should().Equal(
                HttpMethod.Get,
                HttpMethod.Get,
                HttpMethod.Get,
                HttpMethod.Get,
                HttpMethod.Post,
                HttpMethod.Get,
                HttpMethod.Get,
                HttpMethod.Get,
                HttpMethod.Get);
        handler.Requests.Should().NotContain(static request => request.Method == HttpMethod.Put);
        handler.Requests[4].Uri.Should().Be("https://nyx.example/api/v1/keys");
        using (var body = JsonDocument.Parse(handler.Requests[4].Body!))
        {
            body.RootElement.GetProperty("service_slug").GetString().Should().Be("chrono-sandbox");
            body.RootElement.GetProperty("slug").GetString().Should().Be("chrono-sandbox-aevatar");
            body.RootElement.GetProperty("label").GetString().Should()
                .Be("Aevatar Code Execution");
            body.RootElement.GetProperty("forward_access_token").GetBoolean().Should().BeTrue();
            body.RootElement.GetProperty("inject_delegation_token").GetBoolean().Should().BeTrue();
            body.RootElement.GetProperty("delegation_token_scope").GetString().Should()
                .Be("proxy:* sandbox:execute");
            body.RootElement.TryGetProperty("node_id", out _).Should().BeFalse();
            body.RootElement.TryGetProperty("credential", out _).Should().BeFalse();
        }
        var proof = plan.InvocationAdmissions.Should().ContainSingle().Which.Capability.CodeExecution;
        proof.UserServiceId.Should().Be("us-code-aevatar");
        proof.ServiceSlugSnapshot.Should().Be("chrono-sandbox-aevatar");
        proof.CatalogServiceId.Should().Be("catalog-chrono-sandbox");
    }

    [Fact]
    public async Task ReconcileAsync_PersonalRouteCreationRejected_PreservesMutationFailure()
    {
        var handler = new SequenceHandler(
            AutoConnectedInventory(),
            AutoConnectedKeysInventory(),
            new SequenceResponse(
                HttpStatusCode.UnprocessableEntity,
                """{"error":"missing field `label`"}"""),
            AutoConnectedInventory(),
            AutoConnectedKeysInventory());
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var reconciler = new NyxIdCodeExecutionRoutePolicyReconciler(
            new TestClientFactory(new NyxIdApiClient(options, new HttpClient(handler))));
        NyxIdUserServiceRouteMutationAuthority.TryCreate(
                NyxIdCallerCredentialSelection.DirectUserBearer("source-readable-alpha"),
                out var mutationAuthority)
            .Should().BeTrue();

        var result = await reconciler.ReconcileAsync(mutationAuthority!);

        result.Attempted.Should().BeTrue();
        result.Verified.Should().BeFalse();
        result.FailureKind.Should().Be(NyxIdCodeExecutionRouteRepairFailureKind.MutationRejected);
        result.HttpStatus.Should().Be(422);
        handler.Requests.Select(static request => request.Method).Should().Equal(
            HttpMethod.Get,
            HttpMethod.Get,
            HttpMethod.Post,
            HttpMethod.Get,
            HttpMethod.Get);
    }

    [Fact]
    public async Task AdmitAsync_AutoConnectedPlatformRouteWithNode_CreatesPersonalRouteUsingNode()
    {
        const string yaml = "name: code-workflow\nsteps: []\n";
        var handler = new SequenceHandler(
            AutoConnectedInventory(),
            AutoConnectedKeysInventory(nodeId: "node-sandbox"),
            AutoConnectedInventory(),
            AutoConnectedKeysInventory(nodeId: "node-sandbox"),
            """{"id":"us-code-aevatar"}""",
            PersonalExecutionInventory(),
            PersonalExecutionKeysInventory(),
            PersonalExecutionInventory(),
            PersonalExecutionKeysInventory());
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var client = new NyxIdApiClient(options, new HttpClient(handler));
        var factory = new TestClientFactory(client);
        var source = new NyxIdCodeExecutionWorkflowCapabilitySource(
            factory,
            options,
            logger: NullLogger<NyxIdCodeExecutionWorkflowCapabilitySource>.Instance);
        var preparer = new NyxIdCodeExecutionRouteAdmissionPreparer(
            new NyxIdCodeExecutionRoutePolicyReconciler(factory),
            options,
            NullLogger<NyxIdCodeExecutionRouteAdmissionPreparer>.Instance);
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = "code-workflow/run-code",
            ToolName = "code_execute",
            Selector = Selector(),
        });
        var admission = new WorkflowExternalCapabilityAdmissionService(
            new StaticParser(WorkflowYamlParseResult.Success("code-workflow", dependencies)),
            new ExternalWorkflowCapabilityReadinessService([source]),
            preparers: [preparer]);

        var plan = await admission.AdmitAsync(new WorkflowExternalCapabilityAdmissionRequest(
            Access(),
            yaml,
            new Dictionary<string, string>(),
            "test",
            ExternalCapabilityExecutionMode.Interactive,
            workflowId: "wf-alpha",
            revisionId: "rev-alpha"));

        handler.Requests.Should().NotContain(static request => request.Method == HttpMethod.Put);
        using var body = JsonDocument.Parse(handler.Requests[4].Body!);
        body.RootElement.GetProperty("label").GetString().Should().Be("Aevatar Code Execution");
        body.RootElement.GetProperty("node_id").GetString().Should().Be("node-sandbox");
        plan.InvocationAdmissions.Should().ContainSingle().Which.Capability.CodeExecution
            .UserServiceId.Should().Be("us-code-aevatar");
    }

    [Fact]
    public async Task ConvergeAsync_RouteMutationRejected_PreservesMutationFailure()
    {
        var handler = new SequenceHandler(
            Inventory("personal", false, true, "proxy:*"),
            KeysInventory("personal"),
            new SequenceResponse(HttpStatusCode.Forbidden, """{"error":"forbidden"}"""),
            Inventory("personal", false, true, "proxy:*"),
            KeysInventory("personal"));
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var converger = new NyxIdUserServiceRouteConverger(
            new TestClientFactory(new NyxIdApiClient(options, new HttpClient(handler))));
        NyxIdUserServiceRouteMutationAuthority.TryCreate(
                NyxIdCallerCredentialSelection.DirectUserBearer("source-readable-alpha"),
                out var mutationAuthority)
            .Should().BeTrue();

        var result = await converger.ConvergeAsync(
            mutationAuthority!,
            "us-code-alpha",
            new NyxIdUserServiceRouteContract(
                NyxIdUserServiceBooleanRequirement.Enabled,
                NyxIdUserServiceBooleanRequirement.Enabled,
                ["proxy:*", "sandbox:execute"]));

        result.Attempted.Should().BeTrue();
        result.Verified.Should().BeFalse();
        result.FailureKind.Should().Be(
            NyxIdUserServiceRouteConvergenceFailureKind.MutationRejected);
        result.HttpStatus.Should().Be(403);
    }

    [Fact]
    public async Task PrepareAsync_PersonalRouteCreationRejected_SurfacesFailureKindAndHttpStatus()
    {
        var handler = new SequenceHandler(
            AutoConnectedInventory(),
            AutoConnectedKeysInventory(),
            new SequenceResponse(
                HttpStatusCode.BadRequest,
                """{"error":"Credential is required for direct routing (or select a node)"}"""),
            AutoConnectedInventory(),
            AutoConnectedKeysInventory());
        var preparer = CreatePreparer(handler);

        var act = () => preparer.PrepareAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        var exception = await act.Should()
            .ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        var blocker = exception.Which.Readiness.Blockers.Should().ContainSingle().Which;
        blocker.Code.Should().Be("CODE_EXECUTION_ROUTE_REPAIR_UNVERIFIED");
        blocker.SafeMessage.Should().Be(
            "The platform code execution route repair could not be verified. failureKind=MutationRejected httpStatus=400");
    }

    [Fact]
    public async Task PrepareAsync_RouteMutationRejected_SurfacesFailureKindAndHttpStatus()
    {
        var handler = new SequenceHandler(
            Inventory("personal", false, true, "proxy:*"),
            KeysInventory("personal"),
            new SequenceResponse(HttpStatusCode.Forbidden, """{"error":"forbidden"}"""),
            Inventory("personal", false, true, "proxy:*"),
            KeysInventory("personal"));
        var preparer = CreatePreparer(handler);

        var act = () => preparer.PrepareAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        var exception = await act.Should()
            .ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        var blocker = exception.Which.Readiness.Blockers.Should().ContainSingle().Which;
        blocker.Code.Should().Be("CODE_EXECUTION_ROUTE_REPAIR_UNVERIFIED");
        blocker.SafeMessage.Should().Be(
            "The platform code execution route repair could not be verified. failureKind=MutationRejected httpStatus=403");
    }

    [Fact]
    public async Task ReconcileAsync_PhantomUserServiceAutoConnected_DoesNotCreateAndRepairsWritableRoute()
    {
        var handler = new SequenceHandler(
            PhantomAutoConnectedInventory(),
            KeysInventory("personal"),
            "{}",
            Inventory("personal", true, true, "proxy:* sandbox:execute"),
            KeysInventory("personal"));
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var reconciler = new NyxIdCodeExecutionRoutePolicyReconciler(
            new TestClientFactory(new NyxIdApiClient(options, new HttpClient(handler))));
        NyxIdUserServiceRouteMutationAuthority.TryCreate(
                NyxIdCallerCredentialSelection.DirectUserBearer("source-readable-alpha"),
                out var mutationAuthority)
            .Should().BeTrue();

        var result = await reconciler.ReconcileAsync(mutationAuthority!);

        result.Attempted.Should().BeTrue();
        result.Verified.Should().BeTrue();
        handler.Requests.Select(static request => request.Method).Should().Equal(
            HttpMethod.Get,
            HttpMethod.Get,
            HttpMethod.Put,
            HttpMethod.Get,
            HttpMethod.Get);
        handler.Requests.Should().NotContain(static request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ReconcileAsync_KeysOmitAutoConnected_DoesNotGuessWritable()
    {
        var handler = new SequenceHandler(
            AutoConnectedInventory(),
            AutoConnectedKeysInventoryOmittingAutoConnected());
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var reconciler = new NyxIdCodeExecutionRoutePolicyReconciler(
            new TestClientFactory(new NyxIdApiClient(options, new HttpClient(handler))));
        NyxIdUserServiceRouteMutationAuthority.TryCreate(
                NyxIdCallerCredentialSelection.DirectUserBearer("source-readable-alpha"),
                out var mutationAuthority)
            .Should().BeTrue();

        var result = await reconciler.ReconcileAsync(mutationAuthority!);

        result.Attempted.Should().BeFalse();
        result.Verified.Should().BeFalse();
        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().OnlyContain(static request => request.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task AdmitAsync_LegacyPersonalRoute_RepairsThenCommitsVerifiedExactProof()
    {
        const string yaml = "name: code-workflow\nsteps: []\n";
        var handler = new SequenceHandler(
            Inventory("personal", false, true, "proxy:*"),
            KeysInventory("personal"),
            Inventory("personal", false, true, "proxy:*"),
            KeysInventory("personal"),
            "{}",
            Inventory("personal", true, true, "proxy:* sandbox:execute"),
            KeysInventory("personal"),
            Inventory("personal", true, true, "proxy:* sandbox:execute"),
            KeysInventory("personal"));
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var client = new NyxIdApiClient(options, new HttpClient(handler));
        var factory = new TestClientFactory(client);
        var source = new NyxIdCodeExecutionWorkflowCapabilitySource(
            factory,
            options,
            logger: NullLogger<NyxIdCodeExecutionWorkflowCapabilitySource>.Instance);
        var preparer = new NyxIdCodeExecutionRouteAdmissionPreparer(
            new NyxIdCodeExecutionRoutePolicyReconciler(factory),
            options,
            NullLogger<NyxIdCodeExecutionRouteAdmissionPreparer>.Instance);
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = "code-workflow/run-code",
            ToolName = "code_execute",
            Selector = Selector(),
        });
        var admission = new WorkflowExternalCapabilityAdmissionService(
            new StaticParser(WorkflowYamlParseResult.Success("code-workflow", dependencies)),
            new ExternalWorkflowCapabilityReadinessService([source]),
            preparers: [preparer]);

        var plan = await admission.AdmitAsync(new WorkflowExternalCapabilityAdmissionRequest(
            Access(),
            yaml,
            new Dictionary<string, string>(),
            "test",
            ExternalCapabilityExecutionMode.Interactive,
            workflowId: "wf-alpha",
            revisionId: "rev-alpha"));

        handler.Requests.Select(static request => request.Method)
            .Should().Equal(
                HttpMethod.Get,
                HttpMethod.Get,
                HttpMethod.Get,
                HttpMethod.Get,
                HttpMethod.Put,
                HttpMethod.Get,
                HttpMethod.Get,
                HttpMethod.Get,
                HttpMethod.Get);
        var proof = plan.InvocationAdmissions.Should().ContainSingle().Which.Capability.CodeExecution;
        proof.UserServiceId.Should().Be("us-code-alpha");
        proof.CatalogServiceId.Should().Be("catalog-chrono-sandbox");
        plan.SourceStamps.Should().ContainSingle().Which.ContentDigest.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PrepareAsync_PersonalLegacyRoute_ReconcilesDualCredentialPolicyAndVerifiesReadBack()
    {
        var handler = new SequenceHandler(
            Inventory("personal", false, true, "proxy:*"),
            KeysInventory("personal"),
            "{}",
            Inventory("personal", true, true, "proxy:* sandbox:execute"),
            KeysInventory("personal"));
        var preparer = CreatePreparer(handler);

        await preparer.PrepareAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        handler.Requests.Should().HaveCount(5);
        handler.Requests.Select(static request => request.Method)
            .Should().Equal(
                HttpMethod.Get,
                HttpMethod.Get,
                HttpMethod.Put,
                HttpMethod.Get,
                HttpMethod.Get);
        handler.Requests.Should().OnlyContain(static request =>
            request.Authorization == "Bearer source-readable-alpha");
        handler.Requests[2].Uri.Should()
            .Be("https://nyx.example/api/v1/user-services/us-code-alpha");
        using var body = JsonDocument.Parse(handler.Requests[2].Body!);
        body.RootElement.GetProperty("forward_access_token").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("inject_delegation_token").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("delegation_token_scope").GetString().Should()
            .Be("proxy:* sandbox:execute");
    }

    [Fact]
    public async Task PrepareAsync_CanonicalRoute_IsReadOnly()
    {
        var handler = new SequenceHandler(
            Inventory("personal", true, true, "proxy:* sandbox:execute"),
            KeysInventory("personal"));
        var preparer = CreatePreparer(handler);

        await preparer.PrepareAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().OnlyContain(static request => request.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task PrepareAsync_AlreadyUsableForwardingRoute_RemainsReadOnly()
    {
        var handler = new SequenceHandler(
            Inventory("personal", true, true, "proxy:* sandbox:execute"),
            KeysInventory("personal"));
        var preparer = CreatePreparer(handler);

        await preparer.PrepareAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().OnlyContain(static request => request.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task PrepareAsync_ReadBackDropsExistingScope_FailsClosed()
    {
        var handler = new SequenceHandler(
            Inventory("personal", false, true, "proxy:*"),
            KeysInventory("personal"),
            "{}",
            Inventory("personal", true, true, "sandbox:execute"),
            KeysInventory("personal"));
        var preparer = CreatePreparer(handler);

        var act = () => preparer.PrepareAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        var exception = await act.Should()
            .ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        var blocker = exception.Which.Readiness.Blockers.Should().ContainSingle().Which;
        blocker.Code.Should().Be("CODE_EXECUTION_ROUTE_REPAIR_UNVERIFIED");
        blocker.SafeMessage.Should().Be(
            "The platform code execution route repair could not be verified. failureKind=PostconditionMismatch");
        handler.Requests.Select(static request => request.Method)
            .Should().Equal(
                HttpMethod.Get,
                HttpMethod.Get,
                HttpMethod.Put,
                HttpMethod.Get,
                HttpMethod.Get);
    }

    [Theory]
    [InlineData("member")]
    [InlineData("viewer")]
    public async Task PrepareAsync_ReadOnlyOrganizationRoute_DoesNotMutateCallerVisibleState(
        string organizationRole)
    {
        var handler = new SequenceHandler(
            Inventory("org", false, true, "proxy:*", organizationRole),
            KeysInventory("org", organizationRole));
        var preparer = CreatePreparer(handler);

        await preparer.PrepareAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().OnlyContain(static request => request.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task PrepareAsync_AllowedOrganizationAdminRoute_ReconcilesAndVerifiesExactRoute()
    {
        var handler = new SequenceHandler(
            Inventory("org", false, true, "proxy:*", "admin"),
            KeysInventory("org", "admin"),
            "{}",
            Inventory("org", true, true, "proxy:* sandbox:execute", "admin"),
            KeysInventory("org", "admin"));
        var preparer = CreatePreparer(handler);

        await preparer.PrepareAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        handler.Requests.Select(static request => request.Method)
            .Should().Equal(
                HttpMethod.Get,
                HttpMethod.Get,
                HttpMethod.Put,
                HttpMethod.Get,
                HttpMethod.Get);
        handler.Requests[2].Uri.Should()
            .Be("https://nyx.example/api/v1/user-services/us-code-alpha");
    }

    [Fact]
    public async Task PrepareAsync_MixedInventory_RepairsOnlyUniquePersonalRoute()
    {
        var handler = new SequenceHandler(
            MixedInventory(personalScope: "proxy:*"),
            MixedKeysInventory(),
            "{}",
            MixedInventory(personalScope: "proxy:* sandbox:execute", personalForward: true),
            MixedKeysInventory());
        var preparer = CreatePreparer(handler);

        await preparer.PrepareAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        handler.Requests.Select(static request => request.Method)
            .Should().Equal(
                HttpMethod.Get,
                HttpMethod.Get,
                HttpMethod.Put,
                HttpMethod.Get,
                HttpMethod.Get);
        handler.Requests[2].Uri.Should()
            .Be("https://nyx.example/api/v1/user-services/us-code-alpha");
    }

    [Fact]
    public async Task PrepareAsync_MultiplePersonalCandidates_DoesNotGuessMutationTarget()
    {
        var handler = new SequenceHandler(
            MultiplePersonalInventory("proxy:*"),
            MultiplePersonalKeysInventory());
        var preparer = CreatePreparer(handler);

        await preparer.PrepareAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().OnlyContain(static request => request.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task ReconcileAsync_ExactUserServiceId_UpdatesOnlyThatJoinedAuthority()
    {
        var handler = new SequenceHandler(
            MultiplePersonalInventory("proxy:*"),
            MultiplePersonalKeysInventory(),
            "{}",
            MultiplePersonalInventory("proxy:* sandbox:execute", forwardAccessToken: true),
            MultiplePersonalKeysInventory());
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var reconciler = new NyxIdCodeExecutionRoutePolicyReconciler(
            new TestClientFactory(new NyxIdApiClient(options, new HttpClient(handler))));
        NyxIdUserServiceRouteMutationAuthority.TryCreate(
                NyxIdCallerCredentialSelection.DirectUserBearer("source-readable-alpha"),
                out var mutationAuthority)
            .Should().BeTrue();

        var result = await reconciler.ReconcileAsync(
            mutationAuthority!,
            exactUserServiceId: "us-code-beta");

        result.Attempted.Should().BeTrue();
        result.Verified.Should().BeTrue();
        result.Resolution.Service!.Id.Should().Be("us-code-beta");
        handler.Requests[2].Uri.Should()
            .Be("https://nyx.example/api/v1/user-services/us-code-beta");
    }

    [Theory]
    [InlineData("forward_access_token")]
    [InlineData("inject_delegation_token")]
    [InlineData("delegation_token_scope")]
    public async Task ConvergeAsync_FreshReadBackChangesContractUndeclaredValue_FailsClosed(
        string changedValue)
    {
        var beforeForward = true;
        var beforeInject = changedValue != "forward_access_token";
        var afterForward = false;
        var afterInject = changedValue == "inject_delegation_token" ? false : true;
        var afterScope = changedValue == "delegation_token_scope"
            ? "proxy:* sandbox:execute account:admin"
            : "proxy:* sandbox:execute";
        var contract = changedValue switch
        {
            "forward_access_token" => new NyxIdUserServiceRouteContract(
                NyxIdUserServiceBooleanRequirement.Unspecified,
                NyxIdUserServiceBooleanRequirement.Enabled,
                ["sandbox:execute"]),
            "inject_delegation_token" => new NyxIdUserServiceRouteContract(
                NyxIdUserServiceBooleanRequirement.Disabled,
                NyxIdUserServiceBooleanRequirement.Unspecified,
                ["sandbox:execute"]),
            "delegation_token_scope" => new NyxIdUserServiceRouteContract(
                NyxIdUserServiceBooleanRequirement.Disabled,
                NyxIdUserServiceBooleanRequirement.Enabled,
                ["sandbox:execute"]),
            _ => throw new ArgumentOutOfRangeException(nameof(changedValue)),
        };
        var handler = new SequenceHandler(
            Inventory("personal", beforeForward, beforeInject, "proxy:*"),
            KeysInventory("personal"),
            "{}",
            Inventory("personal", afterForward, afterInject, afterScope),
            KeysInventory("personal"));
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var converger = new NyxIdUserServiceRouteConverger(
            new TestClientFactory(new NyxIdApiClient(options, new HttpClient(handler))));
        NyxIdUserServiceRouteMutationAuthority.TryCreate(
                NyxIdCallerCredentialSelection.DirectUserBearer("source-readable-alpha"),
                out var mutationAuthority)
            .Should().BeTrue();

        var result = await converger.ConvergeAsync(
            mutationAuthority!,
            "us-code-alpha",
            contract);

        result.Attempted.Should().BeTrue();
        result.Verified.Should().BeFalse();
        result.FailureKind.Should()
            .Be(NyxIdUserServiceRouteConvergenceFailureKind.PostconditionMismatch);
    }

    [Fact]
    public async Task PrepareAsync_RouteWithoutReadyExecutionAuthority_DoesNotMutate()
    {
        var handler = new SequenceHandler(
            Inventory("personal", false, true, "proxy:*"),
            """
            {
              "keys": [{
                "id": "us-code-alpha",
                "slug": "chrono-sandbox",
                "catalog_service_id": "catalog-chrono-sandbox",
                "catalog_service_slug": "chrono-sandbox",
                "is_active": true,
                "status": "expired",
                "connected": true,
                "auto_connected": false,
                "credential_source": { "type": "personal" }
              }]
            }
            """);
        var preparer = CreatePreparer(handler);

        await preparer.PrepareAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().OnlyContain(static request => request.Method == HttpMethod.Get);
    }

    [Fact]
    public void CanConverge_OnlyAcceptsExactCodeRoutePolicyMismatch()
    {
        var preparer = CreatePreparer(new SequenceHandler());
        var readiness = new ExternalCapabilityReadiness
        {
            Status = ExternalCapabilityReadinessStatus.ContractDrift,
        };
        readiness.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = readiness.Status,
            Code = "CODE_EXECUTION_ROUTE_POLICY_MISMATCH",
        });
        readiness.Sources.Add(new ExternalCapabilitySourceStamp
        {
            SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
            SourceId = "nyxid-user-services:caller:caller-alpha",
        });

        preparer.CanConverge(readiness).Should().BeTrue();

        readiness.Sources.Clear();
        preparer.CanConverge(readiness).Should().BeFalse();
        readiness.Sources.Add(new ExternalCapabilitySourceStamp
        {
            SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
            SourceId = "nyxid-user-services:caller:caller-alpha",
        });
        readiness.Blockers[0].Code = "CODE_EXECUTION_ROUTE_INACTIVE";
        preparer.CanConverge(readiness).Should().BeFalse();
        readiness.Blockers[0].Code = "CODE_EXECUTION_ROUTE_POLICY_MISMATCH";
        readiness.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = readiness.Status,
            Code = "ANOTHER_BLOCKER",
        });
        preparer.CanConverge(readiness).Should().BeFalse();
    }

    [Fact]
    public void RouteMutationAuthority_OnlyAcceptsDirectHumanCredential()
    {
        NyxIdUserServiceRouteMutationAuthority.TryCreate(
                NyxIdCallerCredentialSelection.DirectUserBearer("direct-user-alpha"),
                out var direct)
            .Should().BeTrue();
        direct.Should().NotBeNull();
        direct!.ToString().Should().NotContain("direct-user-alpha");

        NyxIdUserServiceRouteMutationAuthority.TryCreate(
                NyxIdCallerCredentialSelection.SourceReadableUserBearer("broker-user-alpha"),
                out var broker)
            .Should().BeFalse();
        broker.Should().BeNull();
        NyxIdUserServiceRouteMutationAuthority.TryCreate(
                NyxIdCallerCredentialSelection.ProxyDelegation("delegation-alpha"),
                out var delegation)
            .Should().BeFalse();
        delegation.Should().BeNull();

        typeof(NyxIdUserServiceRouteConverger).GetMethods()
            .Where(static method => method.Name == nameof(NyxIdUserServiceRouteConverger.ConvergeAsync))
            .Should().ContainSingle()
            .Which.GetParameters()[0].ParameterType.Should()
            .Be<NyxIdUserServiceRouteMutationAuthority>();
    }

    [Fact]
    public async Task PrepareAsync_ProxyDelegationWithoutSourceBearer_DoesNotReadOrWrite()
    {
        var handler = new SequenceHandler();
        var preparer = CreatePreparer(handler);
        var access = new ExternalWorkflowCapabilityAccessContext(
            "scope-alpha",
            "caller-alpha",
            NyxIdCallerCredentialSelection.ProxyDelegation("proxy-delegation-alpha"));

        await preparer.PrepareAsync(
            access,
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_ReadOnlySourceBearer_DoesNotReadOrWrite()
    {
        var handler = new SequenceHandler();
        var preparer = CreatePreparer(handler);
        var access = new ExternalWorkflowCapabilityAccessContext(
            "scope-alpha",
            "caller-alpha",
            NyxIdCallerCredentialSelection.SourceReadableUserBearer("broker-issued-alpha"));

        await preparer.PrepareAsync(
            access,
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void AddRequiredDelegationScopes_PreservesOrderAndRemovesDuplicates()
    {
        NyxIdCodeExecutionRouteResolver.AddRequiredDelegationScopes(
                " proxy:*  proxy:* account:read ")
            .Should().Be("proxy:* account:read sandbox:execute");
    }

    private static NyxIdCodeExecutionRouteAdmissionPreparer CreatePreparer(
        SequenceHandler handler)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var client = new NyxIdApiClient(options, new HttpClient(handler));
        var factory = new TestClientFactory(client);
        return new NyxIdCodeExecutionRouteAdmissionPreparer(
            new NyxIdCodeExecutionRoutePolicyReconciler(factory),
            options,
            NullLogger<NyxIdCodeExecutionRouteAdmissionPreparer>.Instance);
    }

    private static ExternalWorkflowCapabilityAccessContext Access() =>
        new(
            "scope-alpha",
            "caller-alpha",
            NyxIdCallerCredentialSelection.DirectUserBearer("source-readable-alpha"));

    private static ExternalWorkflowCapabilitySelector Selector() =>
        new() { CodeExecution = new CodeExecutionSelector() };

    private static string Inventory(
        string credentialSourceType,
        bool forwardAccessToken,
        bool injectDelegationToken,
        string scope,
        string organizationRole = "member")
    {
        object credentialSource = credentialSourceType == "personal"
            ? new { type = "personal" }
            : new
            {
                type = "org",
                org_id = "org-alpha",
                org_name = "Organization Alpha",
                role = organizationRole,
                allowed = true,
            };
        return JsonSerializer.Serialize(new
        {
            services = new[]
            {
                new
                {
                    id = "us-code-alpha",
                    slug = "chrono-sandbox",
                    catalog_service_id = "catalog-chrono-sandbox",
                    is_active = true,
                    forward_access_token = forwardAccessToken,
                    inject_delegation_token = injectDelegationToken,
                    delegation_token_scope = scope,
                    credential_source = credentialSource,
                },
            },
        });
    }

    private static string KeysInventory(
        string credentialSourceType,
        string organizationRole = "member")
    {
        object credentialSource = credentialSourceType == "personal"
            ? new { type = "personal" }
            : new
            {
                type = "org",
                org_id = "org-alpha",
                org_name = "Organization Alpha",
                role = organizationRole,
                allowed = true,
            };
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    id = "us-code-alpha",
                    slug = "chrono-sandbox",
                    catalog_service_id = "catalog-chrono-sandbox",
                    catalog_service_slug = "chrono-sandbox",
                    is_active = true,
                    status = "active",
                    connected = true,
                    auto_connected = false,
                    credential_source = credentialSource,
                },
            },
        });
    }

    private static string AutoConnectedInventory() =>
        JsonSerializer.Serialize(new
        {
            services = new[]
            {
                new
                {
                    id = "us-code-platform",
                    slug = "chrono-sandbox",
                    catalog_service_id = "catalog-chrono-sandbox",
                    is_active = true,
                    forward_access_token = true,
                    inject_delegation_token = true,
                    delegation_token_scope = "proxy:*",
                    credential_source = new { type = "personal" },
                },
            },
        });

    private static string PhantomAutoConnectedInventory() =>
        JsonSerializer.Serialize(new
        {
            services = new[]
            {
                new
                {
                    id = "us-code-alpha",
                    slug = "chrono-sandbox",
                    catalog_service_id = "catalog-chrono-sandbox",
                    is_active = true,
                    auto_connected = true,
                    forward_access_token = false,
                    inject_delegation_token = true,
                    delegation_token_scope = "proxy:*",
                    credential_source = new { type = "personal" },
                },
            },
        });

    private static string AutoConnectedKeysInventory(string? nodeId = null) =>
        nodeId is null
            ? JsonSerializer.Serialize(new
            {
                keys = new[]
                {
                    new
                    {
                        id = "us-code-platform",
                        slug = "chrono-sandbox",
                        catalog_service_id = "catalog-chrono-sandbox",
                        catalog_service_slug = "chrono-sandbox",
                        is_active = true,
                        status = "active",
                        connected = true,
                        auto_connected = true,
                        credential_source = new { type = "personal" },
                    },
                },
            })
            : JsonSerializer.Serialize(new
            {
                keys = new[]
                {
                    new
                    {
                        id = "us-code-platform",
                        slug = "chrono-sandbox",
                        catalog_service_id = "catalog-chrono-sandbox",
                        catalog_service_slug = "chrono-sandbox",
                        is_active = true,
                        status = "active",
                        connected = true,
                        auto_connected = true,
                        node_id = nodeId,
                        node_status = "online",
                        credential_source = new { type = "personal" },
                    },
                },
            });

    private static string AutoConnectedKeysInventoryOmittingAutoConnected() =>
        JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    id = "us-code-platform",
                    slug = "chrono-sandbox",
                    catalog_service_id = "catalog-chrono-sandbox",
                    catalog_service_slug = "chrono-sandbox",
                    is_active = true,
                    status = "active",
                    connected = true,
                    credential_source = new { type = "personal" },
                },
            },
        });

    private static string PersonalExecutionInventory() =>
        JsonSerializer.Serialize(new
        {
            services = new object[]
            {
                new
                {
                    id = "us-code-platform",
                    slug = "chrono-sandbox",
                    catalog_service_id = "catalog-chrono-sandbox",
                    is_active = true,
                    forward_access_token = true,
                    inject_delegation_token = true,
                    delegation_token_scope = "proxy:*",
                    credential_source = new { type = "personal" },
                },
                new
                {
                    id = "us-code-aevatar",
                    slug = "chrono-sandbox-aevatar",
                    catalog_service_id = "catalog-chrono-sandbox",
                    is_active = true,
                    forward_access_token = true,
                    inject_delegation_token = true,
                    delegation_token_scope = "proxy:* sandbox:execute",
                    credential_source = new { type = "personal" },
                },
            },
        });

    private static string PersonalExecutionKeysInventory() =>
        JsonSerializer.Serialize(new
        {
            keys = new object[]
            {
                new
                {
                    id = "us-code-platform",
                    slug = "chrono-sandbox",
                    catalog_service_id = "catalog-chrono-sandbox",
                    catalog_service_slug = "chrono-sandbox",
                    is_active = true,
                    status = "active",
                    connected = true,
                    auto_connected = true,
                    credential_source = new { type = "personal" },
                },
                new
                {
                    id = "us-code-aevatar",
                    slug = "chrono-sandbox-aevatar",
                    catalog_service_id = "catalog-chrono-sandbox",
                    catalog_service_slug = "chrono-sandbox",
                    is_active = true,
                    status = "active",
                    connected = true,
                    auto_connected = false,
                    credential_source = new { type = "personal" },
                },
            },
        });

    private static string MixedInventory(
        string personalScope,
        bool personalForward = false) =>
        JsonSerializer.Serialize(new
        {
            services = new object[]
            {
                new
                {
                    id = "us-code-alpha",
                    slug = "chrono-sandbox",
                    catalog_service_id = "catalog-chrono-sandbox",
                    is_active = true,
                    forward_access_token = personalForward,
                    inject_delegation_token = true,
                    delegation_token_scope = personalScope,
                    credential_source = new { type = "personal" },
                },
                new
                {
                    id = "us-code-org",
                    slug = "chrono-sandbox",
                    catalog_service_id = "catalog-chrono-sandbox",
                    is_active = true,
                    forward_access_token = false,
                    inject_delegation_token = true,
                    delegation_token_scope = "proxy:*",
                    credential_source = new
                    {
                        type = "org",
                        org_id = "org-alpha",
                        org_name = "Organization Alpha",
                        role = "member",
                        allowed = true,
                    },
                },
            },
        });

    private static string MixedKeysInventory() =>
        JsonSerializer.Serialize(new
        {
            keys = new object[]
            {
                new
                {
                    id = "us-code-alpha",
                    slug = "chrono-sandbox",
                    catalog_service_id = "catalog-chrono-sandbox",
                    catalog_service_slug = "chrono-sandbox",
                    is_active = true,
                    status = "active",
                    connected = true,
                    auto_connected = false,
                    credential_source = new { type = "personal" },
                },
                new
                {
                    id = "us-code-org",
                    slug = "chrono-sandbox",
                    catalog_service_id = "catalog-chrono-sandbox",
                    catalog_service_slug = "chrono-sandbox",
                    is_active = true,
                    status = "active",
                    connected = true,
                    auto_connected = false,
                    credential_source = new
                    {
                        type = "org",
                        org_id = "org-alpha",
                        org_name = "Organization Alpha",
                        role = "member",
                        allowed = true,
                    },
                },
            },
        });

    private static string MultiplePersonalKeysInventory() =>
        JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    id = "us-code-alpha",
                    slug = "chrono-sandbox",
                    catalog_service_id = "catalog-chrono-sandbox",
                    catalog_service_slug = "chrono-sandbox",
                    is_active = true,
                    status = "active",
                    connected = true,
                    auto_connected = false,
                    credential_source = new { type = "personal" },
                },
                new
                {
                    id = "us-code-beta",
                    slug = "chrono-sandbox",
                    catalog_service_id = "catalog-chrono-sandbox",
                    catalog_service_slug = "chrono-sandbox",
                    is_active = true,
                    status = "active",
                    connected = true,
                    auto_connected = false,
                    credential_source = new { type = "personal" },
                },
            },
        });

    private static string MultiplePersonalInventory(
        string betaScope,
        bool forwardAccessToken = false) =>
        JsonSerializer.Serialize(new
        {
            services = new[]
            {
                new
                {
                    id = "us-code-alpha",
                    slug = "chrono-sandbox",
                    catalog_service_id = "catalog-chrono-sandbox",
                    is_active = true,
                    forward_access_token = false,
                    inject_delegation_token = true,
                    delegation_token_scope = "proxy:*",
                    credential_source = new { type = "personal" },
                },
                new
                {
                    id = "us-code-beta",
                    slug = "chrono-sandbox",
                    catalog_service_id = "catalog-chrono-sandbox",
                    is_active = true,
                    forward_access_token = forwardAccessToken,
                    inject_delegation_token = true,
                    delegation_token_scope = betaScope,
                    credential_source = new { type = "personal" },
                },
            },
        });

    private sealed class TestClientFactory(NyxIdApiClient client) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => client;
    }

    private sealed class StaticParser(WorkflowYamlParseResult result) : IWorkflowDefinitionParser
    {
        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default) => Task.FromResult(result);

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) => throw new InvalidOperationException("Unexpected inline bundle parse.");
    }

    private sealed class SequenceHandler(params object[] responses) : HttpMessageHandler
    {
        private readonly Queue<SequenceResponse> _responses = new(responses.Select(static response =>
            response switch
            {
                string body => new SequenceResponse(HttpStatusCode.OK, body),
                SequenceResponse typed => typed,
                _ => throw new ArgumentException("Unsupported response fixture.", nameof(responses)),
            }));

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                body));
            if (_responses.Count == 0)
                throw new InvalidOperationException("Unexpected NyxID request.");

            var response = _responses.Dequeue();
            return new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(
                    response.Body,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed record SequenceResponse(HttpStatusCode StatusCode, string Body);

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Uri,
        string? Authorization,
        string? Body);
}
