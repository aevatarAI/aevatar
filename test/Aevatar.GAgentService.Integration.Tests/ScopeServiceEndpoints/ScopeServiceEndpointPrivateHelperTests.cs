using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.ScopeScripts;
using Aevatar.GAgentService.Application.Bindings;
using Aevatar.GAgentService.Application.Services;
using Aevatar.Workflow.Abstractions;
using WorkflowCallerCredential = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.AGUI.Contracts;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Integration.Tests;

[Collection(ScopeServiceEndpointCollection.Name)]
public sealed class ScopeServiceEndpointPrivateHelperTests : ScopeServiceEndpointTestKit
{
    [Fact]
    public void ScopeServiceEndpointHelpers_ShouldParseKinds_AndRejectUnsupportedValues()
    {
        InvokePrivateStatic<ScopeBindingImplementationKind>("ParseScopeBindingImplementationKind", "workflow")
            .Should().Be(ScopeBindingImplementationKind.Workflow);
        InvokePrivateStatic<ScopeBindingImplementationKind>("ParseScopeBindingImplementationKind", "script")
            .Should().Be(ScopeBindingImplementationKind.Scripting);
        InvokePrivateStatic<ScopeBindingImplementationKind>("ParseScopeBindingImplementationKind", "scripting")
            .Should().Be(ScopeBindingImplementationKind.Scripting);
        InvokePrivateStatic<ScopeBindingImplementationKind>("ParseScopeBindingImplementationKind", "gagent")
            .Should().Be(ScopeBindingImplementationKind.GAgent);

        InvokePrivateStatic<ServiceEndpointKind>("ParseEndpointKind", "chat")
            .Should().Be(ServiceEndpointKind.Chat);
        InvokePrivateStatic<ServiceEndpointKind>("ParseEndpointKind", "command")
            .Should().Be(ServiceEndpointKind.Command);
        InvokePrivateStatic<ServiceEndpointKind>("ParseEndpointKind", (object?)null)
            .Should().Be(ServiceEndpointKind.Command);
        InvokePrivateStatic<ServiceEndpointKind>("ParseEndpointKind", string.Empty)
            .Should().Be(ServiceEndpointKind.Command);

        InvokePrivateStatic<ServiceBindingKind>("ParseBindingKind", "service")
            .Should().Be(ServiceBindingKind.Service);
        InvokePrivateStatic<ServiceBindingKind>("ParseBindingKind", "connector")
            .Should().Be(ServiceBindingKind.Connector);
        InvokePrivateStatic<ServiceBindingKind>("ParseBindingKind", "secret")
            .Should().Be(ServiceBindingKind.Secret);

        FluentActions.Invoking(() => InvokePrivateStatic<ScopeBindingImplementationKind>(
                "ParseScopeBindingImplementationKind",
                "unsupported"))
            .Should().Throw<TargetInvocationException>().WithInnerException<InvalidOperationException>();
        FluentActions.Invoking(() => InvokePrivateStatic<ServiceEndpointKind>(
                "ParseEndpointKind",
                "unsupported"))
            .Should().Throw<TargetInvocationException>().WithInnerException<InvalidOperationException>();
        FluentActions.Invoking(() => InvokePrivateStatic<ServiceBindingKind>(
                "ParseBindingKind",
                "unsupported"))
            .Should().Throw<TargetInvocationException>().WithInnerException<InvalidOperationException>();
    }

    [Fact]
    public async Task ScopeServiceEndpointHelpers_ShouldBuildScopedHeaders_AndIgnoreConfigFailures()
    {
        var explicitHeaders = new Dictionary<string, string>
        {
            ["scope_id"] = "old",
            [WorkflowRunCommandMetadataKeys.ScopeId] = "legacy",
            [LLMRequestMetadataKeys.ModelOverride] = "existing-model",
        };
        var successContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IUserConfigQueryPort>(new StubUserConfigStore(
                    new UserConfig(
                        DefaultModel: "user-model",
                        PreferredLlmRoute: "/api/v1/proxy/s/legacy",
                        LlmSelection: new LLMSelection
                        {
                            RouteKind = LLMRouteKind.NyxIdUserService,
                            RouteValue = " /preferred-route ",
                            NyxIdUserServiceId = "us-preferred",
                            ServiceSlugSnapshot = "preferred",
                            ModelSelection = new LLMModelSelection { Kind = LLMModelSelectionKind.ProviderDefault },
                        })))
                .BuildServiceProvider(),
        };
        successContext.Request.Headers.Authorization = "Bearer token-123";

        var scopedHeaders = InvokePrivateStatic<Dictionary<string, string>>(
            "BuildScopedHeaders",
            explicitHeaders);

        scopedHeaders.Should().NotContainKey("scope_id");
        scopedHeaders.Should().NotContainKey(WorkflowRunCommandMetadataKeys.ScopeId);
        scopedHeaders[LLMRequestMetadataKeys.ModelOverride].Should().Be("existing-model");
        scopedHeaders.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdRoutePreference);
        scopedHeaders.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        scopedHeaders.Should().NotContainKey("connector.http.authorization");

        var scopedControl = await InvokePrivateStaticTask<LLMControlContext?>(
            "BuildScopedLlmControlAsync",
            successContext,
            CancellationToken.None);
        scopedControl.Should().Be(new LLMControlContext(
            NyxIdAccessToken: null,
            NyxIdOrgToken: null,
            SenderNyxIdAccessToken: null,
            ModelOverride: "user-model",
            NyxIdRoutePreference: "/preferred-route",
            MaxToolRoundsOverride: null,
            UserMemoryPrompt: null));

        var failingContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IUserConfigQueryPort>(new ThrowingUserConfigStore())
                .BuildServiceProvider(),
        };
        var failedHeaders = InvokePrivateStatic<Dictionary<string, string>>(
            "BuildScopedHeaders",
            (object?)null);
        failedHeaders.Should().BeEmpty();
        var failedControl = await InvokePrivateStaticTask<LLMControlContext?>(
            "BuildScopedLlmControlAsync",
            failingContext,
            CancellationToken.None);
        failedControl.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildScopedLlmControlAsync_WithoutTypedSelection_ShouldIgnoreCompatibilityRoute(
        bool useUnspecifiedSelection)
    {
        const string prefixedModel = "chrono-llm/gpt-5.5";
        var selection = useUnspecifiedSelection
            ? new LLMSelection
            {
                RouteKind = LLMRouteKind.Unspecified,
                RouteValue = "/api/v1/proxy/s/typed-but-ignored",
                NyxIdUserServiceId = "us-ignored",
                ServiceSlugSnapshot = "ignored",
                ModelSelection = new LLMModelSelection { Kind = LLMModelSelectionKind.Unspecified },
            }
            : null;
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IUserConfigQueryPort>(new StubUserConfigStore(new UserConfig(
                    DefaultModel: prefixedModel,
                    PreferredLlmRoute: "/api/v1/proxy/s/legacy",
                    LlmSelection: selection)))
                .BuildServiceProvider(),
        };

        var control = await InvokePrivateStaticTask<LLMControlContext?>(
            "BuildScopedLlmControlAsync",
            context,
            CancellationToken.None);

        control.Should().NotBeNull();
        control!.ModelOverride.Should().Be(prefixedModel);
        control.NyxIdRoutePreference.Should().BeNull();
    }

    [Fact]
    public void ScopeServiceEndpointHelpers_ShouldBuildBindingSpec_ForEachBindingKind()
    {
        var options = new ScopeWorkflowCapabilityOptions
        {
            DefaultServiceId = "default",
            ServiceAppId = "app-default",
            ServiceNamespace = "ns-default",
        };

        var serviceSpec = InvokePrivateStatic<ServiceBindingSpec>(
            "ToBindingSpec",
            options,
            "scope-a",
            "service-a",
            new ScopeServiceEndpoints.ScopeServiceBindingHttpRequest(
                "binding-1",
                " Service Binding ",
                "service",
                new ScopeServiceEndpoints.BoundScopeServiceHttpRequest("orders", "chat"),
                null,
                null,
                ["policy-a"]),
            "binding-1");
        serviceSpec.BindingKind.Should().Be(ServiceBindingKind.Service);
        serviceSpec.ServiceRef!.Identity.ServiceId.Should().Be("orders");
        serviceSpec.ServiceRef.EndpointId.Should().Be("chat");
        serviceSpec.PolicyIds.Should().ContainSingle("policy-a");

        var connectorSpec = InvokePrivateStatic<ServiceBindingSpec>(
            "ToBindingSpec",
            options,
            "scope-a",
            "service-a",
            new ScopeServiceEndpoints.ScopeServiceBindingHttpRequest(
                "binding-2",
                "Connector Binding",
                "connector",
                null,
                new ScopeServiceEndpoints.BoundConnectorHttpRequest(" github ", " repo-1 "),
                null),
            "binding-2");
        connectorSpec.BindingKind.Should().Be(ServiceBindingKind.Connector);
        connectorSpec.ConnectorRef!.ConnectorType.Should().Be("github");
        connectorSpec.ConnectorRef.ConnectorId.Should().Be("repo-1");

        var secretSpec = InvokePrivateStatic<ServiceBindingSpec>(
            "ToBindingSpec",
            options,
            "scope-a",
            "service-a",
            new ScopeServiceEndpoints.ScopeServiceBindingHttpRequest(
                "binding-3",
                "Secret Binding",
                "secret",
                null,
                null,
                new ScopeServiceEndpoints.BoundSecretHttpRequest(" api-key ")),
            "binding-3");
        secretSpec.BindingKind.Should().Be(ServiceBindingKind.Secret);
        secretSpec.SecretRef!.SecretName.Should().Be("api-key");
    }

    [Fact]
    public void ScopeServiceEndpointHelpers_ShouldBuildBindingSpec_WithNullBindingTargets_AndRejectUnsupportedKind()
    {
        var options = new ScopeWorkflowCapabilityOptions
        {
            DefaultServiceId = "default",
            ServiceAppId = "app-default",
            ServiceNamespace = "ns-default",
        };

        FluentActions.Invoking(() => InvokePrivateStatic<ServiceBindingSpec>(
                "ToBindingSpec",
                options,
                "scope-a",
                "service-a",
                new ScopeServiceEndpoints.ScopeServiceBindingHttpRequest(
                    null,
                    null,
                    "service",
                    null,
                    null,
                    null,
                    null),
                (string?)null))
            .Should()
            .Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .Which.Message.Should().Contain("serviceId is required.");

        var connectorSpec = InvokePrivateStatic<ServiceBindingSpec>(
            "ToBindingSpec",
            options,
            "scope-a",
            "service-a",
            new ScopeServiceEndpoints.ScopeServiceBindingHttpRequest(
                "binding-connector-null",
                null,
                "connector",
                null,
                null,
                null,
                null),
            "binding-connector-null");
        connectorSpec.BindingId.Should().Be("binding-connector-null");
        connectorSpec.DisplayName.Should().BeEmpty();
        connectorSpec.PolicyIds.Should().BeEmpty();
        connectorSpec.ConnectorRef.Should().NotBeNull();
        connectorSpec.ConnectorRef!.ConnectorType.Should().BeEmpty();
        connectorSpec.ConnectorRef.ConnectorId.Should().BeEmpty();

        var secretSpec = InvokePrivateStatic<ServiceBindingSpec>(
            "ToBindingSpec",
            options,
            "scope-a",
            "service-a",
            new ScopeServiceEndpoints.ScopeServiceBindingHttpRequest(
                "binding-secret-null",
                null,
                "secret",
                null,
                null,
                null,
                null),
            "binding-secret-null");
        secretSpec.SecretRef.Should().NotBeNull();
        secretSpec.SecretRef!.SecretName.Should().BeEmpty();

        FluentActions.Invoking(() => InvokePrivateStatic<ServiceBindingSpec>(
                "ToBindingSpec",
                options,
                "scope-a",
                "service-a",
                new ScopeServiceEndpoints.ScopeServiceBindingHttpRequest(
                    "binding-invalid",
                    "Invalid",
                    "unsupported",
                    null,
                    null,
                    null,
                    null),
                "binding-invalid"))
            .Should()
            .Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .Which.Message.Should().Contain("Unsupported binding kind");
    }

    [Fact]
    public async Task ScopeServiceEndpointHelpers_ShouldMapInvocationErrors_AndNormalizeUtilities()
    {
        var formatResult = InvokePrivateStatic<IResult>("CreateScopeInvokeFailureResult", new FormatException("bad"));
        (await ExecutePrivateResultAsync(formatResult)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var notFoundResult = InvokePrivateStatic<IResult>(
            "CreateScopeInvokeFailureResult",
            new InvalidOperationException("Endpoint 'chat' was not found."));
        (await ExecutePrivateResultAsync(notFoundResult)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var unavailableResult = InvokePrivateStatic<IResult>(
            "CreateScopeInvokeFailureResult",
            new InvalidOperationException("No active serving targets are available."));
        (await ExecutePrivateResultAsync(unavailableResult)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var genericResult = InvokePrivateStatic<IResult>(
            "CreateScopeInvokeFailureResult",
            new InvalidOperationException("generic failure"));
        (await ExecutePrivateResultAsync(genericResult)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        InvokePrivateStatic<string?>("NormalizeOptional", "  hello  ").Should().Be("hello");
        InvokePrivateStatic<string?>("NormalizeOptional", " ").Should().BeNull();
        InvokePrivateStatic<string>("BuildScopeServiceNotFoundMessage", "scope-a", "orders")
            .Should().Contain("orders");
        InvokePrivateStatic<string>("BuildScopeServiceRunNotFoundMessage", "scope-a", "orders", "run-1")
            .Should().Contain("run-1");
    }

    [Fact]
    public void ScopeServiceEndpointHelpers_ShouldPreserveTypedFileRefInput()
    {
        var request = JsonSerializer.Deserialize<ScopeServiceEndpoints.StreamScopeServiceHttpRequest>(
            """
            {
              "prompt": "inspect the sanitized attachment",
              "inputParts": [
                {
                  "type": "file",
                  "fileRef": {
                    "fileId": "file-alpha",
                    "artifactId": "workflow-file://file-alpha",
                    "sourceKind": "chat_input",
                    "fileName": "probe.pdf",
                    "mediaType": "application/pdf",
                    "ownerScopeId": "scope-alpha"
                  }
                }
              ]
            }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var mappedParts = InvokePrivateStatic<IReadOnlyList<ChatInputContentPart>?>(
            "MapInputParts",
            request!.InputParts);

        var fileRef = mappedParts.Should().ContainSingle().Which.FileRef;
        fileRef.Should().NotBeNull();
        fileRef!.FileId.Should().Be("file-alpha");
        fileRef.ArtifactId.Should().Be("workflow-file://file-alpha");
        fileRef.OwnerScopeId.Should().Be("scope-alpha");
    }

    [Fact]
    public void ScopeServiceEndpointHelpers_ShouldMapInputParts_AndBuildStreamInvocationRequest()
    {
        var mappedParts = InvokePrivateStatic<IReadOnlyList<ChatInputContentPart>?>(
            "MapInputParts",
            new List<ScopeServiceEndpoints.StreamContentPartHttpRequest?>
            {
                new("text", Text: "hello"),
                null,
                new("image", Uri: "https://example.com/image.png", Name: "img"),
            });
        mappedParts.Should().NotBeNull();
        mappedParts!.Should().HaveCount(2);
        mappedParts[0].Text.Should().Be("hello");
        mappedParts[1].Uri.Should().Be("https://example.com/image.png");
        InvokePrivateStatic<IReadOnlyList<ChatInputContentPart>?>("MapInputParts", (object?)null).Should().BeNull();

        var options = new ScopeWorkflowCapabilityOptions
        {
            DefaultServiceId = "default",
            ServiceAppId = "app-default",
            ServiceNamespace = "ns-default",
        };

        var invocation = InvokePrivateStatic<ServiceInvocationRequest>(
            "BuildStreamInvocationRequest",
            options,
            "scope-a",
            "orders",
            " chat ",
            "prompt",
            new Dictionary<string, string> { ["trace-id"] = "abc" },
            new WorkflowCallerCredential(
                "delegation-alpha",
                Kind: Aevatar.Workflow.Abstractions.NyxIdCallerCredentialKind.ProxyDelegation,
                SourceReadableUserBearerToken: "source-alpha"),
            " rev-1 ",
            " app-x ");
        invocation.Identity.AppId.Should().Be("app-x");
        invocation.Identity.ServiceId.Should().Be("orders");
        invocation.EndpointId.Should().Be("chat");
        invocation.RevisionId.Should().Be("rev-1");
        var payload = invocation.Payload!.Unpack<ChatRequestEvent>();
        payload.Metadata["trace-id"].Should().Be("abc");
        payload.ConnectorHttpAuthorization.Should().Be("Bearer delegation-alpha");
        payload.CallerNyxIdCredentialKind.Should().Be(
            AgentToolNyxIdCredentialKindPayload.ProxyDelegation);
        payload.CallerSourceReadableNyxIdBearerToken.Should().Be("source-alpha");
        payload.LlmControl.Should().BeNull();
        InvokePrivateStatic<AgentToolNyxIdCredentialKindPayload>(
                "ToAgentToolNyxIdCredentialKind",
                Aevatar.Workflow.Abstractions.NyxIdCallerCredentialKind.AgentKey)
            .Should().Be(AgentToolNyxIdCredentialKindPayload.AgentKey);

        InvokePrivateStatic<string>("ResolveDefaultScopeServiceId", options).Should().Be("default");
    }

    [Fact]
    public void ScopeServiceEndpointHelpers_ShouldBuildServingTargetIndex_PreferActiveTargets()
    {
        var servingSet = new ServiceServingSetSnapshot(
            "scope-a:default:default:orders",
            1,
            string.Empty,
            [
                new ServiceServingTargetSnapshot("dep-paused", "rev-1", "actor-paused", 90, "Paused", []),
                new ServiceServingTargetSnapshot("dep-active", "rev-1", "actor-active", 10, "Active", []),
                new ServiceServingTargetSnapshot("dep-disabled", "rev-2", "actor-disabled", 100, "Disabled", []),
            ],
            DateTimeOffset.UtcNow);

        var index = InvokePrivateStatic<IReadOnlyDictionary<string, ServiceServingTargetSnapshot>>(
            "BuildServingTargetIndex",
            servingSet);

        index["rev-1"].DeploymentId.Should().Be("dep-active");
        index["rev-2"].DeploymentId.Should().Be("dep-disabled");
        InvokePrivateStatic<IReadOnlyDictionary<string, ServiceServingTargetSnapshot>>("BuildServingTargetIndex", (object?)null)
            .Should().BeEmpty();
    }

    [Fact]
    public void ScopeServiceEndpointHelpers_ShouldResolveRunDeployment_AndRankServingStates()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var updatedAt = createdAt.AddMinutes(1);
        var service = BuildService("scope-a", "orders", "def-primary");

        var matchedBinding = new WorkflowActorBinding(
            WorkflowActorKind.Run,
            "run-actor-1",
            "def-match",
            "run-1",
            "main",
            "yaml",
            new Dictionary<string, string>(StringComparer.Ordinal),
            ExternalCapabilityExecutionMode.Durable,
            "scope-a");
        var deployments = new ServiceDeploymentCatalogSnapshot(
            "scope-a:default:default:orders",
            [
                new ServiceDeploymentSnapshot("dep-match", "rev-2", "def-match", "Active", createdAt, updatedAt),
                new ServiceDeploymentSnapshot("dep-other", "rev-1", "def-other", "Inactive", createdAt.AddMinutes(-1), updatedAt),
            ],
            updatedAt);

        InvokePrivateStatic<ServiceDeploymentSnapshot?>("ResolveRunDeployment", matchedBinding, service, deployments)!
            .DeploymentId.Should().Be("dep-match");

        var fallbackBinding = new WorkflowActorBinding(
            WorkflowActorKind.Run,
            "run-actor-2",
            "def-primary",
            "run-2",
            "main",
            "yaml",
            new Dictionary<string, string>(StringComparer.Ordinal),
            ExternalCapabilityExecutionMode.Durable,
            "scope-a");
        var fallbackDeployment = InvokePrivateStatic<ServiceDeploymentSnapshot?>(
            "ResolveRunDeployment",
            fallbackBinding,
            service,
            (object?)null);
        fallbackDeployment.Should().NotBeNull();
        fallbackDeployment!.DeploymentId.Should().Be(service.DeploymentId);

        var missingBinding = new WorkflowActorBinding(
            WorkflowActorKind.Run,
            "run-actor-3",
            "def-missing",
            "run-3",
            "main",
            "yaml",
            new Dictionary<string, string>(StringComparer.Ordinal),
            ExternalCapabilityExecutionMode.Durable,
            "scope-a");
        InvokePrivateStatic<ServiceDeploymentSnapshot?>("ResolveRunDeployment", missingBinding, service, deployments)
            .Should().BeNull();

        InvokePrivateStatic<int>(
            "GetServingStateSummaryPriority",
            new ServiceServingTargetSnapshot("dep-active", "rev-1", "actor-active", 100, "Active", []))
            .Should().Be(5);
        InvokePrivateStatic<int>(
            "GetServingStateSummaryPriority",
            new ServiceServingTargetSnapshot("dep-paused", "rev-1", "actor-paused", 80, "Paused", []))
            .Should().Be(4);
        InvokePrivateStatic<int>(
            "GetServingStateSummaryPriority",
            new ServiceServingTargetSnapshot("dep-draining", "rev-1", "actor-draining", 60, "Draining", []))
            .Should().Be(3);
        InvokePrivateStatic<int>(
            "GetServingStateSummaryPriority",
            new ServiceServingTargetSnapshot("dep-disabled", "rev-1", "actor-disabled", 40, "Disabled", []))
            .Should().Be(2);
        InvokePrivateStatic<int>(
            "GetServingStateSummaryPriority",
            new ServiceServingTargetSnapshot("dep-unspecified", "rev-1", "actor-unspecified", 20, "Unspecified", []))
            .Should().Be(1);
        InvokePrivateStatic<int>(
            "GetServingStateSummaryPriority",
            new ServiceServingTargetSnapshot("dep-unknown", "rev-1", "actor-unknown", 0, "mystery", []))
            .Should().Be(0);
    }

    [Fact]
    public void ScopeServiceEndpointHelpers_ShouldBuildBindingAndRevisionCatalogResponses()
    {
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var updatedAt = createdAt.AddMinutes(5);
        var service = BuildService("scope-a", "orders", "def-workflow");

        var emptyStatus = InvokePrivateStatic<ScopeServiceEndpoints.ScopeBindingStatusHttpResponse>(
            "BuildScopeBindingStatusResponse",
            "scope-a",
            service,
            (object?)null,
            (object?)null);
        emptyStatus.CatalogStateVersion.Should().Be(0);
        emptyStatus.CatalogLastEventId.Should().BeEmpty();
        emptyStatus.Revisions.Should().BeEmpty();

        var revisions = new ServiceRevisionCatalogSnapshot(
            service.ServiceKey,
            [
                new ServiceRevisionSnapshot(
                    "rev-1",
                    "workflow",
                    "Published",
                    "hash-1",
                    string.Empty,
                    [],
                    createdAt,
                    createdAt,
                    updatedAt,
                    null,
                    new ServiceRevisionImplementationSnapshot(
                        Workflow: new ServiceRevisionWorkflowSnapshot("order-flow", "def-workflow", 2))),
            ],
            updatedAt,
            7,
            "evt-7");
        var servingSet = new ServiceServingSetSnapshot(
            service.ServiceKey,
            1,
            string.Empty,
            [
                new ServiceServingTargetSnapshot("dep-1", "rev-1", "def-workflow", 100, "Active", []),
            ],
            updatedAt);

        var status = InvokePrivateStatic<ScopeServiceEndpoints.ScopeBindingStatusHttpResponse>(
            "BuildScopeBindingStatusResponse",
            "scope-a",
            service,
            revisions,
            servingSet);
        status.CatalogStateVersion.Should().Be(7);
        status.CatalogLastEventId.Should().Be("evt-7");
        status.Revisions.Should().ContainSingle();
        status.Revisions[0].IsDefaultServing.Should().BeTrue();
        status.Revisions[0].IsActiveServing.Should().BeTrue();
        status.Revisions[0].IsServingTarget.Should().BeTrue();
        status.Revisions[0].AllocationWeight.Should().Be(100);
        status.Revisions[0].ServingState.Should().Be("Active");
        status.Revisions[0].WorkflowName.Should().Be("order-flow");
        status.Revisions[0].WorkflowDefinitionActorId.Should().Be("def-workflow");
        status.Revisions[0].InlineWorkflowCount.Should().Be(2);

        var catalog = InvokePrivateStatic<ScopeServiceEndpoints.ScopeServiceRevisionCatalogHttpResponse>(
            "BuildScopeServiceRevisionCatalogResponse",
            "scope-a",
            service,
            revisions,
            servingSet);
        catalog.CatalogStateVersion.Should().Be(7);
        catalog.CatalogLastEventId.Should().Be("evt-7");
        catalog.UpdatedAt.Should().Be(updatedAt);
        catalog.Revisions.Should().ContainSingle();
        catalog.Revisions[0].DeploymentId.Should().Be("dep-1");
    }

    [Fact]
    public void ScopeServiceEndpointHelpers_ShouldMatchRunsBoundToScopeService()
    {
        var service = BuildService("scope-a", "orders", "def-service");
        var deployments = new ServiceDeploymentCatalogSnapshot(
            service.ServiceKey,
            [
                new ServiceDeploymentSnapshot(
                    "dep-2",
                    "rev-2",
                    "def-deployment",
                    "Active",
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    DateTimeOffset.UtcNow),
            ],
            DateTimeOffset.UtcNow);

        InvokePrivateStatic<bool>(
            "IsRunBoundToScopeService",
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-1",
                "def-deployment",
                "run-1",
                "main",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
            "scope-a",
            service,
            deployments).Should().BeTrue();

        InvokePrivateStatic<bool>(
            "IsRunBoundToScopeService",
            new WorkflowActorBinding(
                WorkflowActorKind.Definition,
                "run-actor-2",
                "def-deployment",
                "run-2",
                "main",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
            "scope-a",
            service,
            deployments).Should().BeFalse();

        InvokePrivateStatic<bool>(
            "IsRunBoundToScopeService",
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                string.Empty,
                "def-deployment",
                "run-3",
                "main",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
            "scope-a",
            service,
            deployments).Should().BeFalse();

        InvokePrivateStatic<bool>(
            "IsRunBoundToScopeService",
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-4",
                string.Empty,
                "run-4",
                "main",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
            "scope-a",
            service,
            deployments).Should().BeFalse();

        InvokePrivateStatic<bool>(
            "IsRunBoundToScopeService",
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-5",
                "def-deployment",
                "run-5",
                "main",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-b"),
            "scope-a",
            service,
            deployments).Should().BeFalse();

        InvokePrivateStatic<bool>(
            "IsRunBoundToScopeService",
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-6",
                "def-missing",
                "run-6",
                "main",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
            "scope-a",
            service,
            deployments).Should().BeFalse();
    }
}
