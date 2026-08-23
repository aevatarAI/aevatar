using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.DependencyInjection;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProtoWorkflowCallerNyxIdAuthority = Aevatar.Workflow.Abstractions.WorkflowCallerNyxIdAuthority;

namespace Aevatar.Workflow.Host.Api.Tests;

/// <summary>
/// Bindings are scope-owned data: a scope can register, list (secret
/// redacted), and delete its own route keys; a route owned by another scope
/// is untouchable; and the ingress resolves dynamic bindings without the
/// static Enabled flag or any host configuration change.
/// </summary>
public sealed class WorkflowWebhookBindingEndpointsTests
{
    [Fact]
    public async Task PutListDelete_ShouldRoundTripScopeOwnedBinding_WithSecretRedacted()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding()));

        var put = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            new WorkflowWebhookBindingEndpoints.PutWorkflowWebhookBindingRequest(
                WorkflowName: "workflow-alpha",
                SourceId: "nyxid-trigger",
                PromptTemplate: """{"resource_id":"{{payload.resource_id}}","execute":false}""",
                PromptJsonPath: null,
                DeliveryIdHeader: "X-NyxID-Delivery-Id",
                DeliveryIdJsonPath: "event_id",
                HmacSecret: "delivery-signing-secret-at-least-32-bytes",
                HmacSignatureHeader: "X-NyxID-Signature",
                HmacTimestampHeader: "X-NyxID-Timestamp",
                MaxTimestampSkewSeconds: 300,
                DefinitionActorId: "actor-status-handler",
                TargetRevisionId: "rev-7"));
        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)put).StatusCode.Should().Be(StatusCodes.Status200OK);

        var stored = await store.GetAsync("status-event-route");
        stored.Should().NotBeNull();
        stored!.ScopeId.Should().Be("scope-1");
        stored.WorkflowName.Should().Be("workflow-alpha");
        stored.HmacSecret.Should().Be("delivery-signing-secret-at-least-32-bytes");

        var list = await WorkflowWebhookBindingEndpoints.HandleListAsync(http, "scope-1");
        var listJson = System.Text.Json.JsonSerializer.Serialize(
            ((Microsoft.AspNetCore.Http.IValueHttpResult)list).Value);
        listJson.Should().Contain("status-event-route");
        listJson.Should().Contain("hmacSecretSet");
        listJson.Should().NotContain("delivery-signing-secret-at-least-32-bytes");

        var delete = await WorkflowWebhookBindingEndpoints.HandleDeleteAsync(http, "scope-1", "status-event-route");
        delete.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NoContent>();
        (await store.GetAsync("status-event-route")).Should().BeNull();
    }

    [Fact]
    public async Task Put_ShouldRejectRouteOwnedByAnotherScope()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        await SeedAsync(store, BindingRecord("shared-route", "scope-owner"));
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding(scopeId: "scope-intruder")));

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-intruder",
            "shared-route",
            new WorkflowWebhookBindingEndpoints.PutWorkflowWebhookBindingRequest(
                WorkflowName: "wf",
                SourceId: null,
                PromptTemplate: "{}",
                PromptJsonPath: null,
                DeliveryIdHeader: "X-Delivery",
                DeliveryIdJsonPath: "event_id",
                HmacSecret: "scope-intruder-secret-at-least-32-bytes",
                HmacSignatureHeader: null,
                HmacTimestampHeader: null,
                MaxTimestampSkewSeconds: null,
                DefinitionActorId: "actor-status-handler",
                TargetRevisionId: "rev-7"));

        await result.ExecuteAsync(http);
        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        (await store.GetAsync("shared-route"))!.ScopeId.Should().Be("scope-owner");
    }

    [Fact]
    public async Task Put_ShouldRejectOversizedRouteKey()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding()));

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            new string('r', WorkflowWebhookIngressLimits.MaxRouteKeyBytes + 1),
            ExactPutRequest());

        await result.ExecuteAsync(http);
        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(http.Response)).Should().Contain("WEBHOOK_ROUTE_REQUIRED");
    }

    [Fact]
    public async Task Delete_ShouldNotTouchAnotherScopesBinding()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        await SeedAsync(store, BindingRecord("their-route", "scope-owner"));
        var http = CreateHttpContext(store);

        var result = await WorkflowWebhookBindingEndpoints.HandleDeleteAsync(
            http, "scope-intruder", "their-route");

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NotFound>();
        (await store.GetAsync("their-route")).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_WithStaleOwner_ShouldNotRemoveReassignedRoute()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        await SeedAsync(store, BindingRecord("reassigned-route", "scope-old"));
        (await store.TryDeleteOwnedAsync("reassigned-route", "scope-old")).Should().BeTrue();
        await SeedAsync(store, BindingRecord("reassigned-route", "scope-new"));
        var staleHttp = CreateHttpContext(store);

        var result = await WorkflowWebhookBindingEndpoints.HandleDeleteAsync(
            staleHttp,
            "scope-old",
            "reassigned-route");

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NotFound>();
        (await store.GetAsync("reassigned-route"))!.ScopeId.Should().Be("scope-new");
    }

    [Fact]
    public async Task Put_WithDefinitionActorTarget_ShouldValidateScopeOwnershipAndRevision()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var reader = new FakeActorBindingReader(new WorkflowActorBinding(
            WorkflowActorKind.Definition,
            "actor-status-handler",
            "actor-status-handler",
            string.Empty,
            "workflow-alpha",
            "yaml",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Aevatar.Workflow.Abstractions.ExternalCapabilityExecutionMode.Interactive,
            ScopeId: "scope-1",
            RevisionId: "rev-7"));
        var http = CreateHttpContext(store, bindingReader: reader);

        static WorkflowWebhookBindingEndpoints.PutWorkflowWebhookBindingRequest Request(
            string? revision = null) => new(
            WorkflowName: null,
            SourceId: null,
            PromptTemplate: """{"resource_id":"{{resource_id}}","execute":false}""",
            PromptJsonPath: null,
            DeliveryIdHeader: "X-NyxID-Delivery-Id",
            DeliveryIdJsonPath: "event_id",
            HmacSecret: "delivery-signing-secret-at-least-32-bytes",
            HmacSignatureHeader: null,
            HmacTimestampHeader: null,
            MaxTimestampSkewSeconds: null,
            DefinitionActorId: "actor-status-handler",
            TargetRevisionId: revision);

        // Target owned by another scope is rejected outright.
        var foreign = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http, "scope-intruder", "status-event-route", Request());
        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)foreign).StatusCode
            .Should().Be(StatusCodes.Status403Forbidden);

        // A pinned revision that no longer matches the committed target fails.
        var staleRevision = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http, "scope-1", "status-event-route", Request(revision: "rev-6"));
        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)staleRevision).StatusCode
            .Should().Be(StatusCodes.Status409Conflict);

        // Owner scope with the current revision binds; workflow name and the
        // committed revision are taken from the validated target.
        var ok = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http, "scope-1", "status-event-route", Request(revision: "rev-7"));
        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)ok).StatusCode
            .Should().Be(StatusCodes.Status200OK);
        var stored = await store.GetAsync("status-event-route");
        stored!.DefinitionActorId.Should().Be("actor-status-handler");
        stored.TargetRevisionId.Should().Be("rev-7");
        stored.WorkflowName.Should().Be("workflow-alpha");
    }

    [Fact]
    public async Task Put_ShouldRejectRunActorTarget()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var runTarget = DefinitionBinding() with
        {
            ActorKind = WorkflowActorKind.Run,
            RunId = "run-1",
        };
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(runTarget));

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest());

        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode
            .Should().Be(StatusCodes.Status400BadRequest);
        (await store.GetAsync("status-event-route")).Should().BeNull();
    }

    [Fact]
    public async Task Put_ShouldRejectHmacSecretShorterThan32Utf8Bytes()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding()));
        var request = ExactPutRequest() with { HmacSecret = "short-secret" };

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            request);
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(http.Response)).Should().Contain("WEBHOOK_SECRET_TOO_SHORT");
        (await store.GetAsync("status-event-route")).Should().BeNull();
    }

    [Fact]
    public async Task Put_ShouldRejectShortPreviousSecretAndHeaderOnlyDeliveryId()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding()));

        var shortPrevious = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest() with { PreviousHmacSecret = "short" });
        await shortPrevious.ExecuteAsync(http);
        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(http.Response)).Should().Contain("WEBHOOK_PREVIOUS_SECRET_TOO_SHORT");

        http.Response.Body = new MemoryStream();
        var headerOnly = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest() with { DeliveryIdJsonPath = null });
        await headerOnly.ExecuteAsync(http);
        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(http.Response)).Should().Contain("WEBHOOK_DELIVERY_ID_MAPPING_REQUIRED");
    }

    [Fact]
    public async Task Put_ShouldRejectRouteReservedByStaticConfiguration_AfterCanonicalization()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var options = new WorkflowWebhookIngressOptions();
        options.Bindings.Add(new WorkflowWebhookIngressBindingOptions { RouteKey = " STATUS-EVENT-ROUTE " });
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding()),
            ingressOptions: options);

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest());

        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode
            .Should().Be(StatusCodes.Status409Conflict);
        (await store.GetAsync("status-event-route")).Should().BeNull();
    }

    [Fact]
    public async Task Put_WithDirectHumanAndDurableTarget_ShouldPersistRedactedUnattendedAuthorization()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var plan = DurableWritePlan();
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding(plan: plan)),
            authenticationEnabled: true,
            bindingQuery: new FakeBindingQuery("bnd-owner-alpha"));
        ApplyHumanAuthentication(http, "owner-alpha", "scope-1");

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest() with { EnableUnattendedEffects = true });

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status200OK);
        var stored = await store.GetAsync("status-event-route");
        stored.Should().NotBeNull();
        stored!.CallerAuthority.Should().NotBeNull();
        stored.CallerAuthority!.BindingId.Should().Be("bnd-owner-alpha");
        stored.UnattendedEffectAuthorization.Should().NotBeNull();
        stored.UnattendedEffectAuthorization!.DefinitionVersion.Should().Be(7);
        stored.UnattendedEffectAuthorization.Invocations.Should().ContainSingle();

        var viewJson = System.Text.Json.JsonSerializer.Serialize(
            ((IValueHttpResult)result).Value);
        viewJson.Should().Contain("unattendedEffectsEnabled");
        viewJson.Should().NotContain("bnd-owner-alpha");
        viewJson.Should().NotContain("owner-alpha");
        viewJson.Should().NotContain(stored.UnattendedEffectAuthorization.AuthorizationDigest);
    }

    [Fact]
    public async Task Put_WithAuthenticatedCliProxyDelegation_ShouldPersistUnattendedAuthorization()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var tokenProvider = new RecordingCallerAccessTokenProvider("bound-source-readable-token");
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding(plan: DurableWritePlan())),
            authenticationEnabled: true,
            bindingQuery: new FakeBindingQuery("bnd-owner-alpha"),
            callerAccessTokenProvider: tokenProvider);
        ApplyProxyDelegationAuthentication(http, "owner-alpha", "scope-1");
        http.Request.Headers.Authorization = "Bearer forwarded-user-token";

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest() with { EnableUnattendedEffects = true });

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status200OK);
        var stored = await store.GetAsync("status-event-route");
        stored.Should().NotBeNull();
        stored!.CallerAuthority!.ExternalUserId.Should().Be("owner-alpha");
        stored.CallerAuthority.BindingId.Should().Be("bnd-owner-alpha");
        stored.UnattendedEffectAuthorization.Should().NotBeNull();
        tokenProvider.Authority.Should().NotBeNull();
        tokenProvider.Authority!.ExternalUserId.Should().Be("owner-alpha");
        tokenProvider.Authority.BindingId.Should().Be("bnd-owner-alpha");
    }

    [Fact]
    public async Task Put_WithForwardedAgentKey_ShouldMaterializeDedicatedCredentialWithoutPersistingInboundKey()
    {
        const string agentKey = "nyxid_ag_webhook_exact_service_secret";
        var store = new InMemoryWorkflowWebhookBindingStore();
        var vault = new RecordingSecretVault();
        var materializer = new RecordingWebhookAgentKeyMaterializer();
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding(plan: DurableWritePlan())),
            authenticationEnabled: true,
            bindingQuery: new FakeBindingQuery("bnd-owner-alpha"),
            secretVault: vault,
            webhookAgentKeyMaterializer: materializer);
        ApplyProxyDelegationAuthentication(http, "owner-alpha", "scope-1");
        http.Request.Headers.Authorization = $"Bearer {agentKey}";

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest() with { EnableUnattendedEffects = true });

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status200OK);
        vault.PutRequests.Should().BeEmpty();
        materializer.MaterializeCalls.Should().ContainSingle();
        materializer.MaterializeCalls[0].Authority.ExternalUserId.Should().Be("owner-alpha");
        materializer.MaterializeCalls[0].ScopeId.Should().Be("scope-1");
        materializer.MaterializeCalls[0].RouteKey.Should().Be("status-event-route");
        var stored = (await store.GetAsync("status-event-route"))!;
        stored.CallerDurableCredential.Should().NotBeNull();
        stored.CallerDurableCredential!.SourceKind.Should().Be(DurableCallerCredentialSourceKind.WebhookBinding);
        stored.CallerDurableCredential.ProviderCredentialId.Should().NotBeNullOrWhiteSpace();
        stored.CallerDurableCredential.SecretReference.Fingerprint.Should().NotBeNullOrWhiteSpace();
        System.Text.Json.JsonSerializer.Serialize(stored).Should().NotContain(agentKey);
        System.Text.Json.JsonSerializer.Serialize(((IValueHttpResult)result).Value).Should().NotContain(agentKey);
    }

    [Fact]
    public async Task Put_WithOrdinaryOAuthBearer_ShouldAlsoMaterializeDedicatedCredential()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var vault = new RecordingSecretVault();
        var materializer = new RecordingWebhookAgentKeyMaterializer();
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding(plan: DurableWritePlan())),
            authenticationEnabled: true,
            bindingQuery: new FakeBindingQuery("bnd-owner-alpha"),
            secretVault: vault,
            webhookAgentKeyMaterializer: materializer);
        ApplyHumanAuthentication(http, "owner-alpha", "scope-1");

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest() with { EnableUnattendedEffects = true });

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status200OK);
        vault.PutRequests.Should().BeEmpty();
        materializer.MaterializeCalls.Should().ContainSingle();
        (await store.GetAsync("status-event-route"))!.CallerDurableCredential.Should().NotBeNull();
    }

    [Fact]
    public async Task Put_WhenDedicatedCredentialMaterializerIsUnavailable_ShouldFailWithoutMutation()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding(plan: DurableWritePlan())),
            authenticationEnabled: true,
            bindingQuery: new FakeBindingQuery("bnd-owner-alpha"),
            registerDefaultWebhookAgentKeyMaterializer: false);
        ApplyHumanAuthentication(http, "owner-alpha", "scope-1");

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest() with { EnableUnattendedEffects = true });

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        System.Text.Json.JsonSerializer.Serialize(((IValueHttpResult)result).Value)
            .Should().Contain("WEBHOOK_CALLER_CREDENTIAL_ISSUANCE_UNAVAILABLE");
        (await store.GetAsync("status-event-route")).Should().BeNull();
    }

    [Fact]
    public async Task Put_WhenRouteIsOwnedByAnotherScope_ShouldRevokeNewAgentKeyReference()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        await SeedAsync(store, BindingRecord("status-event-route", "scope-other"));
        var materializer = new RecordingWebhookAgentKeyMaterializer();
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding(plan: DurableWritePlan())),
            authenticationEnabled: true,
            bindingQuery: new FakeBindingQuery("bnd-owner-alpha"),
            webhookAgentKeyMaterializer: materializer);
        ApplyProxyDelegationAuthentication(http, "owner-alpha", "scope-1");
        http.Request.Headers.Authorization = "Bearer nyxid_ag_rejected_route_secret";

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest() with { EnableUnattendedEffects = true });

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status409Conflict);
        materializer.MaterializeCalls.Should().ContainSingle();
        materializer.RevokeCalls.Should().ContainSingle();
        materializer.RevokeCalls[0].Credential.Ref.Should()
            .Be(materializer.MaterializedCredentials[0].Ref);
    }

    [Fact]
    public async Task ReplaceAndDelete_ShouldRevokeOnlyTheReplacedAgentKeyReferences()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var materializer = new RecordingWebhookAgentKeyMaterializer();
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding(plan: DurableWritePlan())),
            authenticationEnabled: true,
            bindingQuery: new FakeBindingQuery("bnd-owner-alpha"),
            webhookAgentKeyMaterializer: materializer);
        ApplyProxyDelegationAuthentication(http, "owner-alpha", "scope-1");

        http.Request.Headers.Authorization = "Bearer nyxid_ag_first_secret";
        ((IStatusCodeHttpResult)await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest() with { EnableUnattendedEffects = true })).StatusCode
            .Should().Be(StatusCodes.Status200OK);
        var firstRef = (await store.GetAsync("status-event-route"))!.CallerDurableCredential!.Ref;

        http.Request.Headers.Authorization = "Bearer nyxid_ag_second_secret";
        ((IStatusCodeHttpResult)await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest() with { EnableUnattendedEffects = true })).StatusCode
            .Should().Be(StatusCodes.Status200OK);
        var secondRef = (await store.GetAsync("status-event-route"))!.CallerDurableCredential!.Ref;

        materializer.RevokeCalls.Select(static call => call.Credential.Ref).Should().ContainSingle(firstRef);
        (await WorkflowWebhookBindingEndpoints.HandleDeleteAsync(http, "scope-1", "status-event-route"))
            .Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NoContent>();
        materializer.RevokeCalls.Select(static call => call.Credential.Ref).Should().Equal(firstRef, secondRef);
    }

    [Fact]
    public async Task ReplaceAndDelete_WhenCleanupIsNotCommitted_ShouldReportTheCommittedBindingOutcome()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var materializer = new RecordingWebhookAgentKeyMaterializer { CommitRevocation = false };
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding(plan: DurableWritePlan())),
            authenticationEnabled: true,
            bindingQuery: new FakeBindingQuery("bnd-owner-alpha"),
            webhookAgentKeyMaterializer: materializer);
        ApplyProxyDelegationAuthentication(http, "owner-alpha", "scope-1");

        http.Request.Headers.Authorization = "Bearer nyxid_ag_first_secret";
        ((IStatusCodeHttpResult)await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest() with { EnableUnattendedEffects = true })).StatusCode
            .Should().Be(StatusCodes.Status200OK);
        var firstRef = (await store.GetAsync("status-event-route"))!.CallerDurableCredential!.Ref;

        http.Request.Headers.Authorization = "Bearer nyxid_ag_second_secret";
        var replace = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest() with { EnableUnattendedEffects = true });

        ((IStatusCodeHttpResult)replace).StatusCode.Should().Be(StatusCodes.Status200OK);
        var secondRef = (await store.GetAsync("status-event-route"))!.CallerDurableCredential!.Ref;
        secondRef.Should().NotBe(firstRef);
        materializer.RevokeCalls.Select(static call => call.Credential.Ref).Should().ContainSingle(firstRef);

        var delete = await WorkflowWebhookBindingEndpoints.HandleDeleteAsync(http, "scope-1", "status-event-route");

        delete.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NoContent>();
        (await store.GetAsync("status-event-route")).Should().BeNull();
        materializer.RevokeCalls.Select(static call => call.Credential.Ref).Should().Equal(firstRef, secondRef);
    }

    [Fact]
    public async Task Put_WithOrdinaryProxyDelegationWithoutBoundSourceCredential_ShouldFailWithoutMutation()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding(plan: DurableWritePlan())),
            authenticationEnabled: true,
            bindingQuery: new FakeBindingQuery("bnd-owner-alpha"));
        ApplyProxyDelegationAuthentication(http, "owner-alpha", "scope-1");

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest() with { EnableUnattendedEffects = true });

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        (await store.GetAsync("status-event-route")).Should().BeNull();
    }

    [Theory]
    [InlineData(CallerAccessTokenProviderFailureMode.Empty)]
    [InlineData(CallerAccessTokenProviderFailureMode.Throw)]
    public async Task Put_WhenBoundSourceCredentialIssuanceFails_ShouldFailWithoutMutation(
        CallerAccessTokenProviderFailureMode failureMode)
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var tokenProvider = new FailingCallerAccessTokenProvider(failureMode);
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding(plan: DurableWritePlan())),
            authenticationEnabled: true,
            bindingQuery: new FakeBindingQuery("bnd-owner-alpha"),
            callerAccessTokenProvider: tokenProvider);
        ApplyProxyDelegationAuthentication(http, "owner-alpha", "scope-1");

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest() with { EnableUnattendedEffects = true });

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        (await store.GetAsync("status-event-route")).Should().BeNull();
        tokenProvider.Authority!.BindingId.Should().Be("bnd-owner-alpha");
    }

    [Fact]
    public async Task Put_WhenIssuedCredentialBindingNoLongerMatchesActiveBinding_ShouldFailWithoutMutation()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var tokenProvider = new RecordingCallerAccessTokenProvider("bound-source-readable-token");
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding(plan: DurableWritePlan())),
            authenticationEnabled: true,
            bindingQuery: new SequencedBindingQuery("bnd-owner-alpha", "bnd-owner-beta"),
            callerAccessTokenProvider: tokenProvider);
        ApplyProxyDelegationAuthentication(http, "owner-alpha", "scope-1");

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest() with { EnableUnattendedEffects = true });

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        (await store.GetAsync("status-event-route")).Should().BeNull();
        tokenProvider.Authority.Should().BeNull();
    }

    [Fact]
    public async Task Put_WithConflictingAuthenticatedSubjects_ShouldFailBeforeCredentialIssuance()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var tokenProvider = new RecordingCallerAccessTokenProvider("bound-source-readable-token");
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding(plan: DurableWritePlan())),
            authenticationEnabled: true,
            bindingQuery: new FakeBindingQuery("bnd-owner-alpha"),
            callerAccessTokenProvider: tokenProvider);
        ApplyProxyDelegationAuthentication(http, "owner-alpha", "scope-1");
        ((ClaimsIdentity)http.User.Identity!).AddClaim(new Claim("uid", "owner-beta"));

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest() with { EnableUnattendedEffects = true });

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        (await store.GetAsync("status-event-route")).Should().BeNull();
        tokenProvider.Authority.Should().BeNull();
    }

    [Fact]
    public async Task Put_EnableUnattendedEffectsWithoutAuthenticatedHuman_ShouldFailWithoutMutation()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding(plan: DurableWritePlan())));

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest() with { EnableUnattendedEffects = true });

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status409Conflict);
        (await store.GetAsync("status-event-route")).Should().BeNull();
    }

    [Fact]
    public async Task Put_EnableUnattendedEffectsWithAuthenticationEnabledButNoPrincipal_ShouldFailWithoutMutation()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding(plan: DurableWritePlan())),
            authenticationEnabled: true,
            bindingQuery: new FakeBindingQuery("bnd-owner-alpha"),
            callerAccessTokenProvider: new RecordingCallerAccessTokenProvider("bound-source-readable-token"));

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest() with { EnableUnattendedEffects = true });

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        (await store.GetAsync("status-event-route")).Should().BeNull();
    }

    [Fact]
    public async Task Put_DisablingUnattendedEffects_ShouldAtomicallyClearStoredAuthority()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var plan = DurableWritePlan();
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding(plan: plan)),
            authenticationEnabled: true,
            bindingQuery: new FakeBindingQuery("bnd-owner-alpha"));
        ApplyHumanAuthentication(http, "owner-alpha", "scope-1");
        var enabled = ExactPutRequest() with { EnableUnattendedEffects = true };
        ((IStatusCodeHttpResult)await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            enabled)).StatusCode.Should().Be(StatusCodes.Status200OK);

        ((IStatusCodeHttpResult)await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "status-event-route",
            ExactPutRequest())).StatusCode.Should().Be(StatusCodes.Status200OK);

        var stored = await store.GetAsync("status-event-route");
        stored.Should().NotBeNull();
        stored!.CallerAuthority.Should().BeNull();
        stored.UnattendedEffectAuthorization.Should().BeNull();
    }

    [Fact]
    public async Task InMemoryStore_ShouldCloneMutableAuthorizationStateAtEveryBoundary()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var plan = DurableWritePlan();
        var authority = CallerAuthority();
        var authorization = WorkflowUnattendedEffectAuthorizationIntegrity.Create(
            "actor-status-handler",
            "scope-1",
            "workflow-status-handler",
            "rev-7",
            "status-event-route",
            "owner-alpha",
            7,
            authority,
            plan);
        var source = BindingRecord("status-event-route", "scope-1") with
        {
            CallerAuthority = authority,
            UnattendedEffectAuthorization = authorization,
        };

        (await store.TryPutOwnedAsync(source)).Should().BeTrue();
        source.CallerAuthority!.BindingId = "bnd-mutated-after-put";
        source.UnattendedEffectAuthorization!.RevisionId = "rev-mutated-after-put";

        var first = (await store.GetAsync("status-event-route"))!;
        first.CallerAuthority!.BindingId.Should().Be("bnd-owner-alpha");
        first.UnattendedEffectAuthorization!.RevisionId.Should().Be("rev-7");
        first.CallerAuthority.BindingId = "bnd-mutated-after-get";
        first.UnattendedEffectAuthorization.RevisionId = "rev-mutated-after-get";

        var second = (await store.GetAsync("status-event-route"))!;
        second.CallerAuthority!.BindingId.Should().Be("bnd-owner-alpha");
        second.UnattendedEffectAuthorization!.RevisionId.Should().Be("rev-7");
    }

    [Fact]
    public async Task Ingress_WithLegacyAuthorityOnlyBinding_ShouldDispatchProxyDelegationEnvelope()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var plan = DurableWritePlan();
        var authority = CallerAuthority();
        var authorization = WorkflowUnattendedEffectAuthorizationIntegrity.Create(
            "actor-status-handler",
            "scope-1",
            "workflow-status-handler",
            "rev-7",
            "status-event-route",
            "owner-alpha",
            7,
            authority,
            plan);
        await SeedAsync(store, BindingRecord("status-event-route", "scope-1") with
        {
            WorkflowName = "workflow-alpha",
            DefinitionActorId = "actor-status-handler",
            TargetRevisionId = "rev-7",
            PromptTemplate = """{"resource_id":"{{resource_id}}","execute":true}""",
            HmacSignatureHeader = "X-NyxID-Signature",
            HmacTimestampHeader = "X-NyxID-Timestamp",
            CallerAuthority = authority,
            UnattendedEffectAuthorization = authorization,
        });
        var dispatch = CreateEnvelopeDispatch();
        var replay = new CountingReplayStore();
        var http = CreateHttpContext(
            store,
            replay,
            new FakeActorBindingReader(DefinitionBinding(plan: plan)));
        var body = Encoding.UTF8.GetBytes("""{"event_id":"delivery-1","resource_id":"res-123"}""");
        http.Request.Body = new MemoryStream(body);
        SignNyxId(http, "secret-1", body);

        var options = Options.Create(new WorkflowWebhookIngressOptions { Enabled = false });
        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "status-event-route",
            new WorkflowWebhookIngressRequestBuilder(options),
            dispatch,
            options,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        replay.Requests.Should().ContainSingle();
        dispatch.DispatchAttempts.Should().Be(1);
        var request = dispatch.RunRequests.Should().ContainSingle().Subject;
        request.CallerCredential.Kind.Should().Be(NyxIdCallerCredentialKind.ProxyDelegation);
        request.CallerCredential.NyxIdAuthority.BindingId.Should().Be("bnd-owner-alpha");
        request.CallerCredential.UnattendedEffectAuthorization.AuthorizationDigest
            .Should().Be(authorization.AuthorizationDigest);
        request.CallerCredential.DurableCallerCredential.Should().BeNull();
    }

    [Fact]
    public async Task Ingress_WithValidDurableAgentKey_ShouldDispatchAgentKeyEnvelope()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var plan = DurableWritePlan();
        var authority = CallerAuthority();
        var authorization = WorkflowUnattendedEffectAuthorizationIntegrity.Create(
            "actor-status-handler",
            "scope-1",
            "workflow-status-handler",
            "rev-7",
            "status-event-route",
            "owner-alpha",
            7,
            authority,
            plan);
        var durableCredential = DurableWebhookCredential();
        await SeedAsync(store, BindingRecord("status-event-route", "scope-1") with
        {
            WorkflowName = "workflow-alpha",
            DefinitionActorId = "actor-status-handler",
            TargetRevisionId = "rev-7",
            PromptTemplate = """{"resource_id":"{{resource_id}}","execute":true}""",
            HmacSignatureHeader = "X-NyxID-Signature",
            HmacTimestampHeader = "X-NyxID-Timestamp",
            CallerAuthority = authority,
            UnattendedEffectAuthorization = authorization,
            CallerDurableCredential = durableCredential,
        });
        var dispatch = CreateEnvelopeDispatch();
        var replay = new CountingReplayStore();
        var http = CreateHttpContext(
            store,
            replay,
            new FakeActorBindingReader(DefinitionBinding(plan: plan)));
        var body = Encoding.UTF8.GetBytes("""{"event_id":"delivery-1","resource_id":"res-123"}""");
        http.Request.Body = new MemoryStream(body);
        SignNyxId(http, "secret-1", body);

        var options = Options.Create(new WorkflowWebhookIngressOptions { Enabled = false });
        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "status-event-route",
            new WorkflowWebhookIngressRequestBuilder(options),
            dispatch,
            options,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        replay.Requests.Should().ContainSingle();
        dispatch.DispatchAttempts.Should().Be(1);
        var request = dispatch.RunRequests.Should().ContainSingle().Subject;
        request.CallerCredential.BearerToken.Should().BeEmpty();
        request.CallerCredential.Kind.Should().Be(NyxIdCallerCredentialKind.AgentKey);
        request.CallerCredential.NyxIdAuthority.BindingId.Should().Be("bnd-owner-alpha");
        request.CallerCredential.UnattendedEffectAuthorization.AuthorizationDigest
            .Should().Be(authorization.AuthorizationDigest);
        request.CallerCredential.DurableCallerCredential.Should().BeEquivalentTo(durableCredential);
    }

    [Fact]
    public async Task Ingress_WithMalformedDurableAgentKey_ShouldRejectBeforeDispatch()
    {
        var credential = DurableWebhookCredential();
        credential.OwnerScopeKey = string.Empty;

        await AssertInvalidDurableCredentialRejectedAsync(credential);
    }

    [Fact]
    public async Task Ingress_WithIncompatibleDurableAgentKey_ShouldRejectBeforeDispatch()
    {
        var credential = DurableWebhookCredential();
        credential.Purpose = CredentialSecretPurposes.ChannelNyxIdAgentKey;
        credential.SecretReference.Purpose = CredentialSecretPurposes.ChannelNyxIdAgentKey;

        await AssertInvalidDurableCredentialRejectedAsync(credential);
    }

    [Fact]
    public async Task Ingress_ShouldStartExactlyOneRun_ForDefinitionActorBindingWithDerivedRunDate()
    {
        // A persisted binding receives a generic JSON delivery and starts
        // exactly one run; a redelivered duplicate is acknowledged without a
        // second start. run_date comes from the trusted ingress received_at,
        // while binding_label is a binding constant.
        var store = new InMemoryWorkflowWebhookBindingStore();
        await SeedAsync(store, BindingRecord("status-event-route", "scope-1") with
        {
            WorkflowName = "workflow-alpha",
            DefinitionActorId = "actor-status-handler",
            TargetRevisionId = "rev-7",
            PromptTemplate =
                """{"resource_id":"{{resource_id}}","binding_label":"fixture-binding","run_date":"{{@run_date}}","execute":false}""",
            DeliveryIdHeader = "X-NyxID-Delivery-Id",
            HmacSignatureHeader = "X-NyxID-Signature",
            HmacTimestampHeader = "X-NyxID-Timestamp",
        });

        var dispatch = new RecordingDispatch();
        dispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
            new WorkflowChatRunAcceptedReceipt("actor-1", "workflow-alpha", "cmd-1", "corr-1"));
        var replayStore = new OnceOnlyReplayStore();
        var disabledOptions = Options.Create(new WorkflowWebhookIngressOptions { Enabled = false });

        async Task<int> DeliverAsync()
        {
            var http = CreateHttpContext(
                store,
                replayStore,
                new FakeActorBindingReader(DefinitionBinding()));
            var body = Encoding.UTF8.GetBytes("""{"event_id":"delivery-1","resource_id":"res-123"}""");
            http.Request.Body = new MemoryStream(body);
            http.Request.ContentType = "application/json";
            http.Request.Headers["X-NyxID-Delivery-Id"] = "delivery-1";
            SignNyxId(http, "secret-1", body);
            var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
                http,
                "status-event-route",
                new WorkflowWebhookIngressRequestBuilder(disabledOptions),
                dispatch,
                disabledOptions,
                NullLoggerFactory.Instance,
                CancellationToken.None);
            await result.ExecuteAsync(http);
            return http.Response.StatusCode;
        }

        (await DeliverAsync()).Should().Be(StatusCodes.Status202Accepted);
        (await DeliverAsync()).Should().Be(StatusCodes.Status202Accepted);

        dispatch.Commands.Should().ContainSingle();
        var command = dispatch.Commands[0];
        command.Source.Kind.Should().Be(WorkflowChatSourceKind.DefinitionActor);
        command.Source.ActorId.Should().Be("actor-status-handler");
        command.ScopeId.Should().Be("scope-1");
        command.ResolvedDefinitionBinding.Should().NotBeNull();
        command.ResolvedDefinitionBinding!.RevisionId.Should().Be("rev-7");
        command.Prompt.Should().MatchRegex(
            """^\{"resource_id":"res-123","binding_label":"fixture-binding","run_date":"\d{4}-\d{2}-\d{2}","execute":false\}$""");
    }

    [Fact]
    public async Task Ingress_ShouldAcceptSignatureFromPreviousSecret_DuringRotation()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        await SeedAsync(store, BindingRecord("status-event-route", "scope-1") with
        {
            WorkflowName = "workflow-alpha",
            DefinitionActorId = "actor-status-handler",
            TargetRevisionId = "rev-7",
            HmacSecret = "rotated-new-secret",
            PreviousHmacSecret = "secret-1",
            DeliveryIdHeader = "X-NyxID-Delivery-Id",
            HmacSignatureHeader = "X-NyxID-Signature",
            HmacTimestampHeader = "X-NyxID-Timestamp",
        });

        var dispatch = new RecordingDispatch();
        dispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
            new WorkflowChatRunAcceptedReceipt("actor-1", "wf", "cmd-1", "corr-1"));
        var http = CreateHttpContext(
            store,
            new AcceptingReplayStore(),
            new FakeActorBindingReader(DefinitionBinding()));
        var body = Encoding.UTF8.GetBytes("""{"event_id":"delivery-1","resource_id":"res-123"}""");
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentType = "application/json";
        http.Request.Headers["X-NyxID-Delivery-Id"] = "delivery-1";
        // Sender still signs with the retired secret mid-rotation.
        SignNyxId(http, "secret-1", body);

        var disabledOptions = Options.Create(new WorkflowWebhookIngressOptions { Enabled = false });
        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "status-event-route",
            new WorkflowWebhookIngressRequestBuilder(disabledOptions),
            dispatch,
            disabledOptions,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        dispatch.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task Ingress_ShouldDispatchViaDynamicBinding_WhenStaticIngressDisabled()
    {
        // The whole point of dynamic bindings: no appsettings change, no
        // Enabled flag — a scope-registered binding is live on its own.
        var store = new InMemoryWorkflowWebhookBindingStore();
        await SeedAsync(store, BindingRecord("status-event-route", "scope-1") with
        {
            WorkflowName = "workflow-alpha",
            DefinitionActorId = "actor-status-handler",
            TargetRevisionId = "rev-7",
            PromptTemplate = """{"resource_id":"{{resource_id}}","execute":false}""",
            DeliveryIdHeader = "X-NyxID-Delivery-Id",
            HmacSignatureHeader = "X-NyxID-Signature",
            HmacTimestampHeader = "X-NyxID-Timestamp",
        });

        var dispatch = new RecordingDispatch();
        dispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
            new WorkflowChatRunAcceptedReceipt("actor-1", "workflow-alpha", "cmd-1", "corr-1"));
        var http = CreateHttpContext(
            store,
            new AcceptingReplayStore(),
            new FakeActorBindingReader(DefinitionBinding()));
        var body = Encoding.UTF8.GetBytes("""{"event_id":"delivery-1","resource_id":"res-123"}""");
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentType = "application/json";
        http.Request.Headers["X-NyxID-Delivery-Id"] = "delivery-1";
        SignNyxId(http, "secret-1", body);

        var disabledOptions = Options.Create(new WorkflowWebhookIngressOptions { Enabled = false });
        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "status-event-route",
            new WorkflowWebhookIngressRequestBuilder(disabledOptions),
            dispatch,
            disabledOptions,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        dispatch.Commands.Should().ContainSingle();
        dispatch.Commands[0].Prompt.Should().Be("""{"resource_id":"res-123","execute":false}""");
        dispatch.Commands[0].Source.WorkflowName.Should().Be("workflow-alpha");
        dispatch.Commands[0].ScopeId.Should().Be("scope-1");
    }

    [Fact]
    public async Task Ingress_ShouldFailClosed_WhenPinnedRevisionDrifts()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        await SeedAsync(store, BindingRecord("status-event-route", "scope-1") with
        {
            WorkflowName = "workflow-alpha",
            DefinitionActorId = "actor-status-handler",
            TargetRevisionId = "rev-7",
            PromptJsonPath = "resource_id",
            PromptTemplate = null,
            HmacSignatureHeader = "X-NyxID-Signature",
            HmacTimestampHeader = "X-NyxID-Timestamp",
        });
        var dispatch = new RecordingDispatch();
        var replay = new CountingReplayStore();
        var reader = new FakeActorBindingReader(DefinitionBinding(revisionId: "rev-8"));
        var http = CreateHttpContext(
            store,
            replay,
            reader);
        var body = Encoding.UTF8.GetBytes("""{"event_id":"delivery-1","resource_id":"res-123"}""");
        http.Request.Body = new MemoryStream(body);
        SignNyxId(http, "secret-1", body);

        var disabledOptions = Options.Create(new WorkflowWebhookIngressOptions { Enabled = false });
        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "STATUS-EVENT-ROUTE",
            new WorkflowWebhookIngressRequestBuilder(disabledOptions),
            dispatch,
            disabledOptions,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        (await ReadBodyAsync(http.Response)).Should().Contain("WEBHOOK_TARGET_REVISION_DRIFT");
        reader.ReadCount.Should().Be(1);
        replay.Requests.Should().BeEmpty();
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Ingress_ShouldAuthenticateBeforeReadingPinnedTarget()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        await SeedAsync(store, BindingRecord("status-event-route", "scope-1") with
        {
            WorkflowName = "workflow-alpha",
            DefinitionActorId = "actor-status-handler",
            TargetRevisionId = "rev-7",
            PromptJsonPath = "resource_id",
            PromptTemplate = null,
        });
        var dispatch = new RecordingDispatch();
        var replay = new CountingReplayStore();
        var reader = new FakeActorBindingReader(DefinitionBinding(revisionId: "rev-8"));
        var http = CreateHttpContext(store, replay, reader);
        var body = Encoding.UTF8.GetBytes("""{"event_id":"delivery-1","resource_id":"res-123"}""");
        http.Request.Body = new MemoryStream(body);
        http.Request.Headers["X-Aevatar-Timestamp"] =
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        http.Request.Headers["X-Aevatar-Signature"] = "sha256=invalid";

        var disabledOptions = Options.Create(new WorkflowWebhookIngressOptions { Enabled = false });
        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "status-event-route",
            new WorkflowWebhookIngressRequestBuilder(disabledOptions),
            dispatch,
            disabledOptions,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        reader.ReadCount.Should().Be(0);
        replay.Requests.Should().BeEmpty();
        dispatch.Commands.Should().BeEmpty();
    }

    private static WorkflowWebhookBindingRecord BindingRecord(string routeKey, string scopeId) => new(
        RouteKey: routeKey,
        ScopeId: scopeId,
        WorkflowName: "wf",
        SourceId: "src",
        PromptTemplate: "{}",
        PromptJsonPath: null,
        DeliveryIdHeader: "X-Delivery",
        DeliveryIdJsonPath: "event_id",
        HmacSecret: "secret-1",
        HmacSignatureHeader: null,
        HmacTimestampHeader: null,
        MaxTimestampSkewSeconds: 300,
        UpdatedAtUnixMs: 1);

    private static async Task SeedAsync(
        IWorkflowWebhookBindingStore store,
        WorkflowWebhookBindingRecord record)
    {
        (await store.TryPutOwnedAsync(record)).Should().BeTrue();
    }

    private static WorkflowActorBinding DefinitionBinding(
        string scopeId = "scope-1",
        string revisionId = "rev-7",
        WorkflowCapabilityAdmissionPlan? plan = null) => new(
        WorkflowActorKind.Definition,
        "actor-status-handler",
        "actor-status-handler",
        string.Empty,
        "workflow-alpha",
        "yaml",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        plan is null
            ? ExternalCapabilityExecutionMode.Interactive
            : ExternalCapabilityExecutionMode.Durable,
        ScopeId: scopeId,
        SourceVersion: plan is null ? 0 : 7,
        SourceKind: "service_revision",
        CapabilityAdmissionPlan: plan,
        WorkflowId: "workflow-status-handler",
        RevisionId: revisionId);

    private static ProtoWorkflowCallerNyxIdAuthority CallerAuthority() => new()
    {
        Platform = "nyxid",
        ExternalUserId = "owner-alpha",
        Scope = "proxy",
        BindingId = "bnd-owner-alpha",
    };

    private static EnvelopeCreatingDispatch CreateEnvelopeDispatch()
    {
        var services = new ServiceCollection();
        services.AddWorkflowApplication();
        using var provider = services.BuildServiceProvider();
        return new EnvelopeCreatingDispatch(
            provider.GetRequiredService<ICommandEnvelopeFactory<WorkflowChatRunRequest>>());
    }

    private static async Task AssertInvalidDurableCredentialRejectedAsync(
        DurableCallerCredentialRef credential)
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var plan = DurableWritePlan();
        var authority = CallerAuthority();
        var authorization = WorkflowUnattendedEffectAuthorizationIntegrity.Create(
            "actor-status-handler",
            "scope-1",
            "workflow-status-handler",
            "rev-7",
            "status-event-route",
            "owner-alpha",
            7,
            authority,
            plan);
        await SeedAsync(store, BindingRecord("status-event-route", "scope-1") with
        {
            WorkflowName = "workflow-alpha",
            DefinitionActorId = "actor-status-handler",
            TargetRevisionId = "rev-7",
            PromptTemplate = """{"resource_id":"{{resource_id}}","execute":true}""",
            HmacSignatureHeader = "X-NyxID-Signature",
            HmacTimestampHeader = "X-NyxID-Timestamp",
            CallerAuthority = authority,
            UnattendedEffectAuthorization = authorization,
            CallerDurableCredential = credential,
        });
        var dispatch = CreateEnvelopeDispatch();
        var replay = new CountingReplayStore();
        var http = CreateHttpContext(
            store,
            replay,
            new FakeActorBindingReader(DefinitionBinding(plan: plan)));
        var body = Encoding.UTF8.GetBytes("""{"event_id":"delivery-invalid","resource_id":"res-123"}""");
        http.Request.Body = new MemoryStream(body);
        SignNyxId(http, "secret-1", body);

        var options = Options.Create(new WorkflowWebhookIngressOptions { Enabled = false });
        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "status-event-route",
            new WorkflowWebhookIngressRequestBuilder(options),
            dispatch,
            options,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        (await ReadBodyAsync(http.Response)).Should()
            .Contain("WEBHOOK_DURABLE_CALLER_CREDENTIAL_INVALID");
        replay.Requests.Should().BeEmpty();
        dispatch.DispatchAttempts.Should().Be(0);
        dispatch.RunRequests.Should().BeEmpty();
    }

    private static DurableCallerCredentialRef DurableWebhookCredential()
    {
        var descriptor = new SecretReference
        {
            Ref = "sec-webhook-binding",
            Purpose = CredentialSecretPurposes.WorkflowWebhookBindingAgentKey,
            Fingerprint = "sha256:test",
            Version = 1,
            OwnerScopeKey = "scope-1",
            CreatedAtUnixMs = 1,
        };
        return new DurableCallerCredentialRef
        {
            Ref = descriptor.Ref,
            Purpose = descriptor.Purpose,
            OwnerScopeKey = descriptor.OwnerScopeKey,
            SubjectId = "owner-alpha",
            SourceKind = DurableCallerCredentialSourceKind.WebhookBinding,
            SecretReference = descriptor,
            ProviderCredentialId = "provider-webhook-binding",
        };
    }

    private static WorkflowCapabilityAdmissionPlan DurableWritePlan()
    {
        var request = new NyxIdRequestSelector
        {
            UserServiceId = "service-alpha",
            Method = NyxIdRequestMethod.Post,
            PathTemplate = "/v1/resources",
            BodyMode = NyxIdRequestBodyMode.Json,
            BodyRequired = true,
            ResponseMode = NyxIdRequestResponseMode.Text,
        };
        var policy = new NyxIdOperationExecutionPolicy
        {
            Risk = NyxIdOperationRisk.Write,
            Approval = NyxIdOperationApproval.Required,
            EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
            AllowedExecutionModes =
            {
                ExternalCapabilityExecutionMode.Interactive,
                ExternalCapabilityExecutionMode.Durable,
            },
        };
        var requestDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeNyxIdRequestContractDigest(request);
        var grant = new NyxIdExplicitRequestGrant
        {
            WorkflowId = "workflow-status-handler",
            RevisionId = "rev-7",
            CallSiteId = "workflow-alpha/update_resource",
            RequestContractDigest = requestDigest,
            GrantorAuthority = NyxIdExplicitRequestGrantorAuthority.AevatarWorkflowBinder,
            GrantorOwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
            GrantorOwnerSubject = "owner-alpha",
            Risk = NyxIdOperationRisk.Write,
            AllowedExecutionModes =
            {
                ExternalCapabilityExecutionMode.Interactive,
                ExternalCapabilityExecutionMode.Durable,
            },
        };
        var capability = new NyxIdUserRequestCapabilityRef
        {
            Request = request,
            ServiceSlugSnapshot = "api-resource-service",
            ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                .ComputeNyxIdExplicitRequestProofDigest(requestDigest, "api-resource-service"),
            ExplicitRequestGrantDigest = WorkflowCapabilityAdmissionPlanIntegrity
                .ComputeNyxIdExplicitRequestGrantDigest(grant),
            ExecutionPolicy = policy,
        };
        var codeExecution = new CodeExecutionCapabilityRef
        {
            UserServiceId = "service-chrono-sandbox",
            ServiceSlugSnapshot = "chrono-sandbox",
            CatalogServiceId = "catalog-chrono-sandbox",
        };
        codeExecution.ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeCodeExecutionCapabilityDigest(
                codeExecution.UserServiceId,
                codeExecution.ServiceSlugSnapshot,
                codeExecution.CatalogServiceId);
        codeExecution.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
        codeExecution.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Durable);
        var plan = new WorkflowCapabilityAdmissionPlan
        {
            SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion,
            DefinitionDigest = "sha256:definition",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            DurableAuthorizationOwner = new ExternalCapabilityAuthorizationOwner
            {
                Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
                OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
                OwnerSubject = "owner-alpha",
            },
        };
        plan.InvocationAdmissions.Add(new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = "workflow-alpha/normalize_person",
            Capability = new ExternalWorkflowCapabilityRef { CodeExecution = codeExecution },
        });
        plan.InvocationAdmissions.Add(new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = grant.CallSiteId,
            Capability = new ExternalWorkflowCapabilityRef { NyxIdUserRequest = capability },
            NyxIdExplicitRequestGrant = grant,
        });
        plan.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan);
        return plan;
    }

    private static WorkflowWebhookBindingEndpoints.PutWorkflowWebhookBindingRequest ExactPutRequest() => new(
        WorkflowName: "workflow-alpha",
        SourceId: "nyxid-trigger",
        PromptTemplate: "{}",
        PromptJsonPath: null,
        DeliveryIdHeader: "X-NyxID-Delivery-Id",
        DeliveryIdJsonPath: "event_id",
        HmacSecret: "delivery-signing-secret-at-least-32-bytes",
        HmacSignatureHeader: "X-NyxID-Signature",
        HmacTimestampHeader: "X-NyxID-Timestamp",
        MaxTimestampSkewSeconds: 300,
        DefinitionActorId: "actor-status-handler",
        TargetRevisionId: "rev-7");

    private static DefaultHttpContext CreateHttpContext(
        IWorkflowWebhookBindingStore? bindingStore = null,
        IWorkflowWebhookReplayStore? replayStore = null,
        IWorkflowActorBindingReader? bindingReader = null,
        WorkflowWebhookIngressOptions? ingressOptions = null,
        bool authenticationEnabled = false,
        IExternalIdentityBindingQueryPort? bindingQuery = null,
        IWorkflowCallerAccessTokenProvider? callerAccessTokenProvider = null,
        ISecretVault? secretVault = null,
        IWorkflowWebhookAgentKeyMaterializer? webhookAgentKeyMaterializer = null,
        bool registerDefaultWebhookAgentKeyMaterializer = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = authenticationEnabled ? "true" : "false",
                })
                .Build());
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new DevelopmentHostEnvironment());
        if (bindingStore != null)
            services.AddSingleton(bindingStore);
        if (replayStore != null)
            services.AddSingleton(replayStore);
        if (bindingReader != null)
            services.AddSingleton(bindingReader);
        if (ingressOptions != null)
            services.AddSingleton<IOptions<WorkflowWebhookIngressOptions>>(Options.Create(ingressOptions));
        if (bindingQuery != null)
            services.AddSingleton(bindingQuery);
        if (callerAccessTokenProvider != null)
            services.AddSingleton(callerAccessTokenProvider);
        if (secretVault != null)
            services.AddSingleton(secretVault);
        if (webhookAgentKeyMaterializer != null)
            services.AddSingleton(webhookAgentKeyMaterializer);
        else if (registerDefaultWebhookAgentKeyMaterializer)
            services.AddSingleton<IWorkflowWebhookAgentKeyMaterializer,
                RecordingWebhookAgentKeyMaterializer>();
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static void ApplyHumanAuthentication(
        DefaultHttpContext http,
        string subject,
        string scopeId)
    {
        const string scheme = "Bearer";
        const string token = "owner-access-token";
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", subject),
            new Claim("scope_id", scopeId),
            new Claim("token_type", "access"),
        ], scheme));
        http.User = principal;
        http.Request.Headers.Authorization = $"Bearer {token}";
        http.Features.Set<IAuthenticateResultFeature>(new TestAuthenticateResultFeature
        {
            AuthenticateResult = AuthenticateResult.Success(
                new AuthenticationTicket(principal, scheme)),
        });
    }

    private static void ApplyProxyDelegationAuthentication(
        DefaultHttpContext http,
        string subject,
        string scopeId)
    {
        const string scheme = "NyxIdIdentityAssertion";
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", subject),
            new Claim("scope_id", scopeId),
        ], scheme));
        http.User = principal;
        http.Request.Headers["X-NyxID-Delegation-Token"] = "proxy-delegation-token";
        http.Features.Set<IAuthenticateResultFeature>(new TestAuthenticateResultFeature
        {
            AuthenticateResult = AuthenticateResult.Success(
                new AuthenticationTicket(principal, scheme)),
        });
    }

    private static async Task<string> ReadBodyAsync(HttpResponse response)
    {
        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static void SignNyxId(HttpContext http, string secret, byte[] body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var payload = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
        http.Request.Headers["X-NyxID-Timestamp"] = timestamp;
        http.Request.Headers["X-NyxID-Signature"] =
            "sha256=" + Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant();
    }

    private sealed class RecordingDispatch
        : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public List<WorkflowChatRunRequest> Commands { get; } = [];

        public CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> Result { get; set; } =
            CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Failure(
                WorkflowChatRunStartError.WorkflowNotFound);

        public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(Result);
        }
    }

    private sealed class EnvelopeCreatingDispatch(
        ICommandEnvelopeFactory<WorkflowChatRunRequest> envelopeFactory)
        : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public int DispatchAttempts { get; private set; }
        public List<WorkflowChatRequestEvent> RunRequests { get; } = [];

        public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            DispatchAttempts++;
            var envelope = envelopeFactory.CreateEnvelope(
                command,
                new CommandContext(
                    "cmd-1",
                    "corr-1",
                    "run-actor",
                    new Dictionary<string, string>(StringComparer.Ordinal)));
            RunRequests.Add(envelope.Payload.Unpack<WorkflowChatRequestEvent>());
            return Task.FromResult(
                CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                    new WorkflowChatRunAcceptedReceipt(
                        "run-actor",
                        "workflow-alpha",
                        "cmd-1",
                        "corr-1")));
        }
    }

    private sealed class RecordingSecretVault : ISecretVault
    {
        private readonly InMemorySecretVault _inner = new();

        public List<StoreSecretRequest> PutRequests { get; } = [];
        public List<StoreSecretResult> PutResults { get; } = [];
        public List<RevokeSecretRequest> RevokeRequests { get; } = [];
        public bool CommitRevocation { get; init; } = true;

        public async Task<StoreSecretResult> PutAsync(
            StoreSecretRequest request,
            CancellationToken ct = default)
        {
            PutRequests.Add(request);
            var result = await _inner.PutAsync(request, ct);
            PutResults.Add(result);
            return result;
        }

        public Task<ResolveSecretResult> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken ct = default) => _inner.ResolveAsync(request, ct);

        public Task<RotateSecretResult> RotateAsync(
            RotateSecretRequest request,
            CancellationToken ct = default) => _inner.RotateAsync(request, ct);

        public Task<RevokeSecretResult> RevokeAsync(
            RevokeSecretRequest request,
            CancellationToken ct = default)
        {
            RevokeRequests.Add(request);
            return CommitRevocation
                ? _inner.RevokeAsync(request, ct)
                : Task.FromResult(new RevokeSecretResult(false));
        }
    }

    private sealed class RecordingWebhookAgentKeyMaterializer
        : IWorkflowWebhookAgentKeyMaterializer
    {
        private int _sequence;

        public List<(
            ProtoWorkflowCallerNyxIdAuthority Authority,
            WorkflowCapabilityAdmissionPlan AdmissionPlan,
            string ScopeId,
            string RouteKey)> MaterializeCalls { get; } = [];

        public List<DurableCallerCredentialRef> MaterializedCredentials { get; } = [];

        public List<(
            ProtoWorkflowCallerNyxIdAuthority? Authority,
            DurableCallerCredentialRef Credential,
            string AuditReason)> RevokeCalls { get; } = [];

        public bool CommitRevocation { get; init; } = true;

        public Task<WorkflowWebhookAgentKeyMaterializationResult> MaterializeAsync(
            ProtoWorkflowCallerNyxIdAuthority callerAuthority,
            WorkflowCapabilityAdmissionPlan admissionPlan,
            string scopeId,
            string routeKey,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            MaterializeCalls.Add((callerAuthority.Clone(), admissionPlan.Clone(), scopeId, routeKey));
            var sequence = Interlocked.Increment(ref _sequence);
            var descriptor = new SecretReference
            {
                Ref = $"sec-webhook-binding-{sequence}",
                Purpose = CredentialSecretPurposes.WorkflowWebhookBindingAgentKey,
                Fingerprint = $"sha256:test-{sequence}",
                Version = 1,
                OwnerScopeKey = scopeId,
                CreatedAtUnixMs = sequence,
            };
            var credential = new DurableCallerCredentialRef
            {
                Ref = descriptor.Ref,
                Purpose = descriptor.Purpose,
                OwnerScopeKey = descriptor.OwnerScopeKey,
                SubjectId = callerAuthority.ExternalUserId,
                SourceKind = DurableCallerCredentialSourceKind.WebhookBinding,
                SecretReference = descriptor,
                ProviderCredentialId = $"provider-webhook-binding-{sequence}",
            };
            MaterializedCredentials.Add(credential.Clone());
            return Task.FromResult(
                WorkflowWebhookAgentKeyMaterializationResult.Success(credential));
        }

        public Task<bool> RevokeAsync(
            ProtoWorkflowCallerNyxIdAuthority? callerAuthority,
            DurableCallerCredentialRef credential,
            string auditReason,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RevokeCalls.Add((callerAuthority?.Clone(), credential.Clone(), auditReason));
            return Task.FromResult(CommitRevocation);
        }
    }

    private sealed class FakeActorBindingReader : IWorkflowActorBindingReader
    {
        private readonly WorkflowActorBinding _binding;

        public FakeActorBindingReader(WorkflowActorBinding binding) => _binding = binding;

        public int ReadCount { get; private set; }

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            ReadCount++;
            return Task.FromResult(string.Equals(actorId, _binding.ActorId, StringComparison.Ordinal)
                ? _binding
                : null);
        }
    }

    private sealed class FakeBindingQuery(string bindingId) : IExternalIdentityBindingQueryPort
    {
        public Task<BindingId?> ResolveAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) =>
            Task.FromResult<BindingId?>(new BindingId { Value = bindingId });
    }

    private sealed class SequencedBindingQuery(params string[] bindingIds)
        : IExternalIdentityBindingQueryPort
    {
        private int _index;

        public Task<BindingId?> ResolveAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default)
        {
            var index = Math.Min(_index++, bindingIds.Length - 1);
            return Task.FromResult<BindingId?>(new BindingId { Value = bindingIds[index] });
        }
    }

    private sealed class RecordingCallerAccessTokenProvider(string accessToken)
        : IWorkflowCallerAccessTokenProvider
    {
        public Aevatar.Workflow.Abstractions.WorkflowCallerNyxIdAuthority? Authority { get; private set; }

        public Task<string> IssueAsync(
            Aevatar.Workflow.Abstractions.WorkflowCallerNyxIdAuthority authority,
            CancellationToken ct = default)
        {
            Authority = authority;
            return Task.FromResult(accessToken);
        }
    }

    public enum CallerAccessTokenProviderFailureMode
    {
        Empty,
        Throw,
    }

    private sealed class FailingCallerAccessTokenProvider(CallerAccessTokenProviderFailureMode failureMode)
        : IWorkflowCallerAccessTokenProvider
    {
        public Aevatar.Workflow.Abstractions.WorkflowCallerNyxIdAuthority? Authority { get; private set; }

        public Task<string> IssueAsync(
            Aevatar.Workflow.Abstractions.WorkflowCallerNyxIdAuthority authority,
            CancellationToken ct = default)
        {
            Authority = authority;
            return failureMode == CallerAccessTokenProviderFailureMode.Throw
                ? Task.FromException<string>(new InvalidOperationException("credential exchange failed"))
                : Task.FromResult(string.Empty);
        }
    }

    private sealed class TestAuthenticateResultFeature : IAuthenticateResultFeature
    {
        public AuthenticateResult? AuthenticateResult { get; set; }
    }

    /// <summary>First delivery id is admitted; every replay is a duplicate.</summary>
    private sealed class OnceOnlyReplayStore : IWorkflowWebhookReplayStore
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public ValueTask<WorkflowWebhookReplayAdmission> AdmitAsync(
            WorkflowWebhookReplayAdmissionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_seen.Add(request.DeliveryId)
                ? new WorkflowWebhookReplayAdmission(WorkflowWebhookReplayAdmissionStatus.Admitted)
                : new WorkflowWebhookReplayAdmission(
                    WorkflowWebhookReplayAdmissionStatus.DuplicateCompleted,
                    ExistingCommandId: "cmd-1",
                    ExistingCorrelationId: "corr-1"));

        public ValueTask ReleaseAsync(
            WorkflowWebhookReplayAdmissionRequest request,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class AcceptingReplayStore : IWorkflowWebhookReplayStore
    {
        public ValueTask<WorkflowWebhookReplayAdmission> AdmitAsync(
            WorkflowWebhookReplayAdmissionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new WorkflowWebhookReplayAdmission(
                WorkflowWebhookReplayAdmissionStatus.Admitted));

        public ValueTask ReleaseAsync(
            WorkflowWebhookReplayAdmissionRequest request,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class CountingReplayStore : IWorkflowWebhookReplayStore
    {
        public List<WorkflowWebhookReplayAdmissionRequest> Requests { get; } = [];

        public ValueTask<WorkflowWebhookReplayAdmission> AdmitAsync(
            WorkflowWebhookReplayAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new WorkflowWebhookReplayAdmission(
                WorkflowWebhookReplayAdmissionStatus.Admitted));
        }

        public ValueTask ReleaseAsync(
            WorkflowWebhookReplayAdmissionRequest request,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class DevelopmentHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Microsoft.Extensions.Hosting.Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
