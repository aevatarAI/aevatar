using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Application.AgentProfiles;
using Aevatar.Mainnet.Host.Api.AI;
using Aevatar.Mainnet.Host.Api.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetAgentProfileEndpointHandlerTests
{
    [Fact]
    public async Task CreateScopeProfile_ShouldReturnCanonicalScopeResourceUrl()
    {
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(actorPort: actors);
        using var request = Request(HttpMethod.Post, "/api/scopes/scope-alpha/agent-profiles", "scope-alpha", "user-alpha");
        request.Headers.Add("Idempotency-Key", "create-alpha");
        request.Content = JsonContent.Create(new { profileSlug = "research", idempotencyKey = "create-alpha" });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("resourceUrl").GetString()
            .Should().Be("/api/scopes/scope-alpha/agent-profiles/research");
        payload.RootElement.GetProperty("actorId").GetString().Should().NotBeNullOrWhiteSpace();
        actors.CreateCommands.Should().ContainSingle(command =>
            command.Owner.Scope.ScopeId == "scope-alpha" && command.ProfileSlug == "research");
    }

    [Fact]
    public async Task CreateAIProfile_ShouldUseCallerScopeAndHideInternalActorIdentity()
    {
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(actorPort: actors);
        using var request = Request(HttpMethod.Post, "/api/ai/agents", "scope-alpha", "user-alpha");
        request.Headers.Add("Idempotency-Key", "create-ai-alpha");
        request.Content = JsonContent.Create(new { profileSlug = "research" });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("resourceUrl").GetString()
            .Should().Be("/api/ai/agents/research");
        payload.RootElement.TryGetProperty("actorId", out _).Should().BeFalse();
        actors.CreateCommands.Should().ContainSingle(command =>
            command.Owner.Scope.ScopeId == "scope-alpha" &&
            command.ProfileSlug == "research");
    }

    [Fact]
    public async Task CreateAIProfile_WithoutCallerScope_ShouldRejectBeforeDispatch()
    {
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(actorPort: actors);
        using var request = Request(HttpMethod.Post, "/api/ai/agents", string.Empty, "user-alpha");
        request.Headers.Add("Idempotency-Key", "create-ai-without-scope");
        request.Content = JsonContent.Create(new { profileSlug = "research" });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorMessageAsync(response)).Should().Be("Authenticated caller access context is required.");
        actors.CreateCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAIDefaultAgent_ShouldHideSystemRolloutControls()
    {
        await using var host = await AgentProfileTestHost.StartAsync();

        var response = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            "/api/ai/agents/default/nyxid.chat",
            "scope-alpha",
            "user-alpha"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.TryGetProperty("enabled", out _).Should().BeFalse();
        payload.RootElement.TryGetProperty("cohortBasisPoints", out _).Should().BeFalse();
        payload.RootElement.GetRawText().Should().NotContain("ownerKind");
        payload.RootElement.GetProperty("authorityStateVersion").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task GetAIAgentDetail_ShouldHideAuthorizationOwnerKind()
    {
        var owner = AgentProfileOwners.ForScope("scope-alpha");
        var management = new RecordingManagementQuery
        {
            Snapshot = Management(owner, "prof-research", "research", authorityVersion: 9),
        };
        var catalog = new RecordingCatalogQuery
        {
            Resolve = _ => Catalog(
                owner,
                authorityVersion: 9,
                Entry("prof-research", "research", AgentProfileProvisioningStatus.Active)),
        };
        await using var host = await AgentProfileTestHost.StartAsync(catalog, management);

        var response = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            "/api/ai/agents/research",
            "scope-alpha",
            "user-alpha"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("profileSlug").GetString().Should().Be("research");
        payload.RootElement.TryGetProperty("ownerKind", out _).Should().BeFalse();
    }

    [Fact]
    public async Task AIEditorOptions_ShouldExposeBackendAuthoringConstraints()
    {
        await using var host = await AgentProfileTestHost.StartAsync();

        var response = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            "/api/ai/agents/editor-options",
            "scope-alpha",
            "user-alpha"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("supportedAgentKinds").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().BeEquivalentTo(
                AgentProfilePolicies.WorkspaceChatAgentKind,
                AgentProfilePolicies.ChannelReplyAgentKind,
                AgentProfilePolicies.NyxIdChatAgentKind);
        payload.RootElement.GetProperty("activationModes").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().BeEquivalentTo("SHADOW", "ENFORCED");
        payload.RootElement.GetProperty("agentSources").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().BeEquivalentTo("my_agents", "system_agents");
        var runtimeParameters = payload.RootElement.GetProperty("runtimeParameters");
        runtimeParameters.GetProperty("classifierTimeoutMs").GetInt32().Should().Be(15_000);
        runtimeParameters.GetProperty("exactSkillFetchTimeoutMs").GetInt32().Should().Be(15_000);
        var json = payload.RootElement.GetRawText().ToLowerInvariant();
        json.Should().NotContain("owner");
        json.Should().NotContain("caller");
        json.Should().NotContain("scope");
    }

    [Fact]
    public async Task ManagementEditorOptions_ShouldPreserveExistingOwnershipContract()
    {
        await using var host = await AgentProfileTestHost.StartAsync();

        var response = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            "/api/agent-profiles/editor-options",
            "scope-alpha",
            "user-alpha"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("referenceOwnerKinds").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().BeEquivalentTo("caller", "system");
        var runtimeParameters = payload.RootElement.GetProperty("runtimeParameters");
        runtimeParameters.GetProperty("classifierTimeoutMs").GetInt32().Should().Be(15_000);
        runtimeParameters.GetProperty("exactSkillFetchTimeoutMs").GetInt32().Should().Be(15_000);
        payload.RootElement.TryGetProperty("agentSources", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SetAIDefaultAgent_ShouldRejectSystemRolloutControls()
    {
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(actorPort: actors);
        using var request = Request(
            HttpMethod.Put,
            "/api/ai/agents/default/nyxid.chat",
            "scope-alpha",
            "user-alpha");
        request.Headers.Add("Idempotency-Key", "set-ai-default");
        request.Content = JsonContent.Create(new
        {
            agentProfile = new { source = "my_agents", profileSlug = "research" },
            enabled = false,
        });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ToLowerInvariant().Should()
            .NotContain("ownerkind");
        actors.BindingCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task SetAIDefaultAgent_ShouldRejectLegacyOwnershipFieldsWithoutEchoingThem()
    {
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(actorPort: actors);
        using var request = Request(
            HttpMethod.Put,
            "/api/ai/agents/default/nyxid.chat",
            "scope-alpha",
            "user-alpha");
        request.Content = JsonContent.Create(new
        {
            agentProfile = new { ownerKind = "caller", profileSlug = "research" },
            expectedVersion = 0,
            idempotencyKey = "set-ai-default",
        });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = (await response.Content.ReadAsStringAsync()).ToLowerInvariant();
        json.Should().NotContain("owner");
        json.Should().NotContain("caller");
        json.Should().NotContain("scope");
        actors.BindingCommands.Should().BeEmpty();
    }

    [Theory]
    [InlineData("my_agents", false)]
    [InlineData("system_agents", true)]
    public async Task SetAIDefaultAgent_ShouldMapProductSourceToInternalReference(
        string source,
        bool expectsSystemTarget)
    {
        var scopeOwner = AgentProfileOwners.ForScope("scope-alpha");
        var targetOwner = expectsSystemTarget ? AgentProfileOwners.ForSystem() : scopeOwner;
        var entry = Entry("prof-research", "research", AgentProfileProvisioningStatus.Active, publishedRevision: 2);
        var catalog = new RecordingCatalogQuery
        {
            Resolve = requestedOwner => requestedOwner.Equals(targetOwner)
                ? Catalog(targetOwner, authorityVersion: 0, entry)
                : requestedOwner.Equals(scopeOwner)
                    ? Catalog(scopeOwner, authorityVersion: 0)
                    : null,
        };
        var execution = new RecordingExecutionQuery
        {
            Resolve = target => Execution(target, "research"),
        };
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(
            catalog: catalog,
            actorPort: actors,
            execution: execution);
        using var request = Request(
            HttpMethod.Put,
            "/api/ai/agents/default/nyxid.chat",
            "scope-alpha",
            "user-alpha");
        request.Content = JsonContent.Create(new
        {
            agentProfile = new { source, profileSlug = "research" },
            expectedVersion = 0,
            idempotencyKey = $"set-ai-{source}",
        });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        actors.BindingCommands.Should().ContainSingle();
        actors.BindingCommands[0].Target.Owner.OwnerCase.Should().Be(
            expectsSystemTarget
                ? AgentProfileOwner.OwnerOneofCase.System
                : AgentProfileOwner.OwnerOneofCase.Scope);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.TryGetProperty("actorId", out _).Should().BeFalse();
        payload.RootElement.GetRawText().ToLowerInvariant().Should().NotContain("ownerkind");
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task AIDefaultAgentErrors_ShouldNotExposeAuthorizationOwnership(string method)
    {
        var catalog = new RecordingCatalogQuery
        {
            Resolve = _ => Catalog(AgentProfileOwners.ForSystem(), authorityVersion: 4),
        };
        await using var host = await AgentProfileTestHost.StartAsync(catalog: catalog);
        using var request = Request(
            new HttpMethod(method),
            "/api/ai/agents/default/nyxid.chat",
            "scope-alpha",
            "user-alpha");
        if (method == "PUT")
        {
            request.Content = JsonContent.Create(new
            {
                agentProfile = new { source = "my_agents", profileSlug = "research" },
                expectedVersion = 4,
                idempotencyKey = "set-ai-default",
            });
        }
        else if (method == "DELETE")
        {
            request.Content = JsonContent.Create(new
            {
                expectedVersion = 4,
                idempotencyKey = "clear-ai-default",
            });
        }

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertStrictAIError(payload.RootElement, "AI_AGENT_UNAVAILABLE");
        var json = payload.RootElement.GetRawText().ToLowerInvariant();
        json.Should().NotContain("owner");
        json.Should().NotContain("caller");
        json.Should().NotContain("scope");
        json.Should().NotContain("authority");
    }

    [Fact]
    public async Task GetAIAgentDetail_WhenMissing_ShouldReturnStrictNeutralError()
    {
        await using var host = await AgentProfileTestHost.StartAsync();

        var response = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            "/api/ai/agents/missing",
            "scope-alpha",
            "user-alpha"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertStrictAIError(payload.RootElement, "AI_AGENT_NOT_FOUND");
    }

    [Fact]
    public async Task CreateAIAgent_WhenRequestIsInvalid_ShouldReturnStrictNeutralError()
    {
        await using var host = await AgentProfileTestHost.StartAsync();
        using var request = Request(
            HttpMethod.Post,
            "/api/ai/agents",
            "scope-alpha",
            "user-alpha");
        request.Content = JsonContent.Create(new
        {
            profileSlug = " ",
            idempotencyKey = "create-invalid",
        });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertStrictAIError(payload.RootElement, "AI_AGENT_INVALID");
    }

    [Fact]
    public async Task CreateScopeProfile_ShouldAcceptBodyIdempotencyKey()
    {
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(actorPort: actors);
        using var request = Request(HttpMethod.Post, "/api/scopes/scope-alpha/agent-profiles", "scope-alpha", "user-alpha");
        request.Content = JsonContent.Create(new { profileSlug = "research", idempotencyKey = "create-body" });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        actors.CreateCommands.Should().ContainSingle();
        actors.CreateCommands[0].ProfileId.Should().Be(
            AgentProfileDeterminism.CreateProfileId(
                AgentProfileOwners.ForScope("scope-alpha"),
                "create-body"));
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("operationId").GetString().Should().Be(
            actors.CreateCommands[0].Operation.OperationId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"profileSlug\":\"research\",\"owner\":\"system\"}")]
    public async Task CreateScopeProfile_ShouldRejectInvalidOrIdentitySpoofingBody(string json)
    {
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(actorPort: actors);
        using var request = Request(
            HttpMethod.Post,
            "/api/scopes/scope-alpha/agent-profiles",
            "scope-alpha",
            "user-alpha");
        request.Headers.Add("Idempotency-Key", "create-alpha");
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actors.CreateCommands.Should().BeEmpty();
    }

    [Theory]
    [InlineData("body-key", "Idempotency-Key header and idempotencyKey body values must agree.")]
    [InlineData(" ", "idempotencyKey body value must not be blank.")]
    public async Task CreateScopeProfile_ShouldRejectInvalidBodyIdempotencyKeyBeforeDispatch(
        string bodyIdempotencyKey,
        string expectedMessage)
    {
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(actorPort: actors);
        using var request = Request(HttpMethod.Post, "/api/scopes/scope-alpha/agent-profiles", "scope-alpha", "user-alpha");
        request.Headers.Add("Idempotency-Key", "header-key");
        request.Content = JsonContent.Create(new { profileSlug = "research", idempotencyKey = bodyIdempotencyKey });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorMessageAsync(response)).Should().Be(expectedMessage);
        actors.CreateCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task SystemPublicEndpoints_ShouldExposeOnlyPublishedActiveSummaries()
    {
        var catalog = new RecordingCatalogQuery
        {
            Resolve = owner => owner.OwnerCase == AgentProfileOwner.OwnerOneofCase.System
                ? Catalog(
                    AgentProfileOwners.ForSystem(),
                    8,
                    Entry("prof-public", "public-profile", AgentProfileProvisioningStatus.Active, publishedRevision: 2),
                    Entry("prof-draft", "draft-profile", AgentProfileProvisioningStatus.Active),
                    Entry("prof-failed", "failed-profile", AgentProfileProvisioningStatus.Failed, publishedRevision: 4))
                : null,
        };
        await using var host = await AgentProfileTestHost.StartAsync(catalog: catalog);

        var listResponse = await host.Client.SendAsync(Request(HttpMethod.Get, "/api/agent-profiles/system", "scope-alpha", "user-alpha"));
        var hiddenResponse = await host.Client.SendAsync(Request(HttpMethod.Get, "/api/agent-profiles/system/draft-profile", "scope-alpha", "user-alpha"));

        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var profile = list.RootElement.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        profile.GetProperty("profileSlug").GetString().Should().Be("public-profile");
        profile.TryGetProperty("draft", out _).Should().BeFalse();
        profile.TryGetProperty("authorityStateVersion", out _).Should().BeFalse();
        hiddenResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SystemPublicList_ShouldPageOnlyPublishedSummaries()
    {
        var catalog = new RecordingCatalogQuery
        {
            Resolve = owner => owner.OwnerCase == AgentProfileOwner.OwnerOneofCase.System
                ? Catalog(
                    AgentProfileOwners.ForSystem(),
                    8,
                    Entry("prof-a", "alpha", AgentProfileProvisioningStatus.Active, publishedRevision: 1),
                    Entry("prof-draft", "draft", AgentProfileProvisioningStatus.Active),
                    Entry("prof-b", "beta", AgentProfileProvisioningStatus.Active, publishedRevision: 2))
                : null,
        };
        await using var host = await AgentProfileTestHost.StartAsync(catalog: catalog);

        var firstResponse = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            "/api/agent-profiles/system?take=1",
            "scope-alpha",
            "user-alpha"));
        using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        var cursor = first.RootElement.GetProperty("nextCursor").GetString();
        var secondResponse = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/agent-profiles/system?take=1&cursor={Uri.EscapeDataString(cursor!)}",
            "scope-alpha",
            "user-alpha"));
        using var second = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        first.RootElement.GetProperty("items")[0].GetProperty("profileSlug").GetString().Should().Be("alpha");
        second.RootElement.GetProperty("items")[0].GetProperty("profileSlug").GetString().Should().Be("beta");
        second.RootElement.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ScopeRoute_ShouldRejectDifferentAuthenticatedScopeBeforeQueryingCatalog()
    {
        var catalog = new RecordingCatalogQuery();
        await using var host = await AgentProfileTestHost.StartAsync(catalog: catalog);

        var response = await host.Client.SendAsync(Request(HttpMethod.Get, "/api/scopes/scope-beta/agent-profiles", "scope-alpha", "user-alpha"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        catalog.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScopeMutation_ShouldRequireAuditSubjectBeforeDispatching()
    {
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(actorPort: actors);
        using var request = Request(HttpMethod.Post, "/api/scopes/scope-alpha/agent-profiles", "scope-alpha", subject: null);
        request.Headers.Add("Idempotency-Key", "create-without-subject");
        request.Content = JsonContent.Create(new { profileSlug = "research" });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        actors.CreateCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBinding_ShouldReturnEmptyBindingWithAuthorityVersionZeroWhenCatalogIsAbsent()
    {
        await using var host = await AgentProfileTestHost.StartAsync();

        var response = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            "/api/scopes/scope-alpha/agent-profile-bindings/nyxid.chat",
            "scope-alpha",
            "user-alpha"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.Should().Be("\"agent-profile-binding-v0\"");
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("target").ValueKind.Should().Be(JsonValueKind.Null);
        payload.RootElement.GetProperty("authorityStateVersion").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task ReadEndpoints_ShouldExposeCommittedMutationForReceiptReconciliation()
    {
        var owner = AgentProfileOwners.ForScope("scope-alpha");
        var mutation = Mutation("op-profile-alpha", "PROFILE_PUBLISHED", authorityVersion: 9);
        var entry = Entry("prof-research", "research", AgentProfileProvisioningStatus.Active, publishedRevision: 2);
        var catalog = new RecordingCatalogQuery
        {
            Resolve = _ => CatalogWithMutation(owner, 9, mutation, entry),
        };
        var management = new RecordingManagementQuery
        {
            Snapshot = Management(owner, entry.ProfileId, entry.ProfileSlug, authorityVersion: 9, mutation),
        };
        await using var host = await AgentProfileTestHost.StartAsync(catalog, management);

        var listResponse = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            "/api/scopes/scope-alpha/agent-profiles",
            "scope-alpha",
            "user-alpha"));
        var detailResponse = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            "/api/scopes/scope-alpha/agent-profiles/research",
            "scope-alpha",
            "user-alpha"));
        var bindingResponse = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            "/api/scopes/scope-alpha/agent-profile-bindings/nyxid.chat",
            "scope-alpha",
            "user-alpha"));

        foreach (var response in new[] { listResponse, detailResponse, bindingResponse })
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            payload.RootElement.GetProperty("lastMutation").GetProperty("operationId").GetString()
                .Should().Be("op-profile-alpha");
            payload.RootElement.GetProperty("lastMutation").GetProperty("code").GetString()
                .Should().Be("PROFILE_PUBLISHED");
        }

        using var detailPayload = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        detailPayload.RootElement.GetProperty("executionAvailable").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetDetail_ShouldSerializeMultiwordEnumsAsCanonicalSnakeCase()
    {
        var owner = AgentProfileOwners.ForScope("scope-alpha");
        var mutation = Mutation("op-profile-alpha", "DRAFT_UNCHANGED", authorityVersion: 9);
        mutation.Status = AgentProfileMutationStatus.NoChange;
        var snapshot = Management(owner, "prof-research", "research", authorityVersion: 9, mutation);
        snapshot.Draft!.RuntimeProfile!.Members.Add(new AgentProfileSkillMember
        {
            IntentId = "operate",
            SideEffectClass = AgentProfileSideEffectClass.ServiceCall,
        });
        var catalog = new RecordingCatalogQuery
        {
            Resolve = _ => Catalog(
                owner,
                9,
                Entry("prof-research", "research", AgentProfileProvisioningStatus.Active)),
        };
        var management = new RecordingManagementQuery { Snapshot = snapshot };
        await using var host = await AgentProfileTestHost.StartAsync(catalog, management);

        var response = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            "/api/scopes/scope-alpha/agent-profiles/research",
            "scope-alpha",
            "user-alpha"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("draft").GetProperty("runtimeProfile")
            .GetProperty("members")[0].GetProperty("sideEffectClass").GetString()
            .Should().Be("SERVICE_CALL");
        payload.RootElement.GetProperty("lastMutation").GetProperty("status").GetString()
            .Should().Be("NO_CHANGE");
    }

    [Theory]
    [InlineData(null, HttpStatusCode.PreconditionRequired)]
    [InlineData("not-an-etag", HttpStatusCode.BadRequest)]
    [InlineData("\"agent-profile-v7\"", HttpStatusCode.PreconditionFailed)]
    public async Task UpdateDraft_ShouldRejectMissingMalformedOrStaleIfMatchBeforeDispatch(
        string? ifMatch,
        HttpStatusCode expectedStatus)
    {
        var owner = AgentProfileOwners.ForScope("scope-alpha");
        var catalog = new RecordingCatalogQuery
        {
            Resolve = _ => Catalog(owner, 4, Entry("prof-research", "research", AgentProfileProvisioningStatus.Active)),
        };
        var management = new RecordingManagementQuery
        {
            Snapshot = Management(owner, "prof-research", "research", authorityVersion: 9),
        };
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(catalog, management, actors);
        using var request = Request(HttpMethod.Put, "/api/scopes/scope-alpha/agent-profiles/research/draft", "scope-alpha", "user-alpha");
        request.Headers.Add("Idempotency-Key", "update-alpha");
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        request.Content = JsonContent.Create(new { draft = DraftInput() });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(expectedStatus);
        actors.DraftCommands.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateDraft_ShouldAcceptBodyMutationPreconditions(bool includeMatchingHeaders)
    {
        var owner = AgentProfileOwners.ForScope("scope-alpha");
        var catalog = new RecordingCatalogQuery
        {
            Resolve = _ => Catalog(owner, 9, Entry("prof-research", "research", AgentProfileProvisioningStatus.Active)),
        };
        var management = new RecordingManagementQuery
        {
            Snapshot = Management(owner, "prof-research", "research", authorityVersion: 9),
        };
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(catalog, management, actors);
        using var request = Request(HttpMethod.Put, "/api/scopes/scope-alpha/agent-profiles/research/draft", "scope-alpha", "user-alpha");
        if (includeMatchingHeaders)
        {
            request.Headers.Add("Idempotency-Key", "update-body");
            request.Headers.TryAddWithoutValidation("If-Match", "\"agent-profile-v9\"");
        }
        request.Content = JsonContent.Create(new
        {
            draft = DraftInput(),
            expectedVersion = 9,
            idempotencyKey = "update-body",
        });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        actors.DraftCommands.Should().ContainSingle();
        actors.DraftCommands[0].ExpectedAuthorityStateVersion.Should().Be(9);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("operationId").GetString().Should().Be(
            actors.DraftCommands[0].Operation.OperationId);
    }

    [Fact]
    public async Task UpdateDraft_ShouldMapTypedConnectedServiceSelectors()
    {
        var owner = AgentProfileOwners.ForScope("scope-alpha");
        var catalog = new RecordingCatalogQuery
        {
            Resolve = _ => Catalog(owner, 9, Entry("prof-research", "research", AgentProfileProvisioningStatus.Active)),
        };
        var management = new RecordingManagementQuery
        {
            Snapshot = Management(owner, "prof-research", "research", authorityVersion: 9),
        };
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(catalog, management, actors);
        using var request = Request(
            HttpMethod.Put,
            "/api/scopes/scope-alpha/agent-profiles/research/draft",
            "scope-alpha",
            "user-alpha");
        request.Content = JsonContent.Create(new
        {
            draft = DraftInput([
                new
                {
                    catalogServiceSlug = "api-github",
                    allowedRisks = new[] { "READ_ONLY", "WRITE" },
                    readiness = new { requestedScopes = new[] { "read:user", "repo" } },
                },
            ]),
            expectedVersion = 9,
            idempotencyKey = "update-connected-service-selector",
        });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var selector = actors.DraftCommands.Should().ContainSingle().Subject.Draft.RuntimeProfile
            .MaximumToolPolicy.ConnectedServiceSelectors.Should().ContainSingle().Subject;
        selector.CatalogServiceSlug.Should().Be("api-github");
        selector.AllowedRisks.Should().Equal(
            AgentToolOperationRiskPayload.ReadOnly,
            AgentToolOperationRiskPayload.Write);
        selector.Readiness.Should().NotBeNull();
        selector.Readiness.RequestedScopes.Should().Equal("read:user", "repo");
    }

    [Fact]
    public async Task GetProfile_ShouldReturnTypedConnectedServiceSelectors()
    {
        var owner = AgentProfileOwners.ForScope("scope-alpha");
        var snapshot = Management(owner, "prof-research", "research", authorityVersion: 9);
        snapshot.Draft!.RuntimeProfile.MaximumToolPolicy = new AgentProfileToolPolicy();
        snapshot.Draft!.RuntimeProfile.MaximumToolPolicy.ConnectedServiceSelectors.Add(
            new AgentProfileConnectedServiceSelector
            {
                CatalogServiceSlug = "api-github",
                AllowedRisks =
                {
                    AgentToolOperationRiskPayload.ReadOnly,
                    AgentToolOperationRiskPayload.Write,
                },
                Readiness = new AgentProfileConnectedServiceReadiness
                {
                    RequestedScopes = { "read:user", "repo" },
                },
            });
        var management = new RecordingManagementQuery { Snapshot = snapshot };
        var catalog = new RecordingCatalogQuery
        {
            Resolve = _ => Catalog(
                owner,
                9,
                Entry("prof-research", "research", AgentProfileProvisioningStatus.Active)),
        };
        await using var host = await AgentProfileTestHost.StartAsync(catalog, management);

        var response = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            "/api/scopes/scope-alpha/agent-profiles/research",
            "scope-alpha",
            "user-alpha"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var selector = payload.RootElement.GetProperty("draft").GetProperty("runtimeProfile")
            .GetProperty("maximumToolPolicy").GetProperty("connectedServiceSelectors")[0];
        selector.GetProperty("catalogServiceSlug").GetString().Should().Be("api-github");
        selector.GetProperty("allowedRisks").EnumerateArray()
            .Select(static value => value.GetString())
            .Should().Equal("READ_ONLY", "WRITE");
        selector.GetProperty("readiness").GetProperty("requestedScopes").EnumerateArray()
            .Select(static value => value.GetString())
            .Should().Equal("read:user", "repo");
    }

    [Fact]
    public async Task UpdateDraft_ShouldRejectConflictingExpectedVersionsBeforeDispatch()
    {
        var owner = AgentProfileOwners.ForScope("scope-alpha");
        var catalog = new RecordingCatalogQuery
        {
            Resolve = _ => Catalog(owner, 9, Entry("prof-research", "research", AgentProfileProvisioningStatus.Active)),
        };
        var management = new RecordingManagementQuery
        {
            Snapshot = Management(owner, "prof-research", "research", authorityVersion: 9),
        };
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(catalog, management, actors);
        using var request = Request(HttpMethod.Put, "/api/scopes/scope-alpha/agent-profiles/research/draft", "scope-alpha", "user-alpha");
        request.Headers.Add("Idempotency-Key", "update-body");
        request.Headers.TryAddWithoutValidation("If-Match", "\"agent-profile-v9\"");
        request.Content = JsonContent.Create(new { draft = DraftInput(), expectedVersion = 8 });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorMessageAsync(response)).Should().Be(
            "If-Match header and expectedVersion body values must agree.");
        actors.DraftCommands.Should().BeEmpty();
    }

    [Theory]
    [InlineData(7, HttpStatusCode.PreconditionFailed)]
    [InlineData(-1, HttpStatusCode.BadRequest)]
    public async Task UpdateDraft_ShouldRejectInvalidBodyExpectedVersionBeforeDispatch(
        long expectedVersion,
        HttpStatusCode expectedStatus)
    {
        var owner = AgentProfileOwners.ForScope("scope-alpha");
        var catalog = new RecordingCatalogQuery
        {
            Resolve = _ => Catalog(owner, 9, Entry("prof-research", "research", AgentProfileProvisioningStatus.Active)),
        };
        var management = new RecordingManagementQuery
        {
            Snapshot = Management(owner, "prof-research", "research", authorityVersion: 9),
        };
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(catalog, management, actors);
        using var request = Request(HttpMethod.Put, "/api/scopes/scope-alpha/agent-profiles/research/draft", "scope-alpha", "user-alpha");
        request.Content = JsonContent.Create(new
        {
            draft = DraftInput(),
            expectedVersion,
            idempotencyKey = "update-body",
        });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(expectedStatus);
        (await ErrorMessageAsync(response)).Should().Be(expectedVersion < 0
            ? "expectedVersion must be non-negative."
            : "If-Match or expectedVersion does not match the current resource version.");
        actors.DraftCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task Publish_ShouldAcceptBodyMutationPreconditions()
    {
        var owner = AgentProfileOwners.ForScope("scope-alpha");
        var catalog = new RecordingCatalogQuery
        {
            Resolve = _ => Catalog(owner, 9, Entry("prof-research", "research", AgentProfileProvisioningStatus.Active)),
        };
        var management = new RecordingManagementQuery
        {
            Snapshot = Management(owner, "prof-research", "research", authorityVersion: 9),
        };
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(catalog, management, actors);
        using var request = Request(HttpMethod.Post, "/api/scopes/scope-alpha/agent-profiles/research:publish", "scope-alpha", "user-alpha");
        request.Content = JsonContent.Create(new { expectedVersion = 9, idempotencyKey = "publish-body" });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        actors.PublishCommands.Should().ContainSingle();
        actors.PublishCommands[0].ExpectedAuthorityStateVersion.Should().Be(9);
    }

    [Fact]
    public async Task Publish_ShouldPreserveHeaderOnlyEmptyBodyContract()
    {
        var owner = AgentProfileOwners.ForScope("scope-alpha");
        var catalog = new RecordingCatalogQuery
        {
            Resolve = _ => Catalog(owner, 9, Entry("prof-research", "research", AgentProfileProvisioningStatus.Active)),
        };
        var management = new RecordingManagementQuery
        {
            Snapshot = Management(owner, "prof-research", "research", authorityVersion: 9),
        };
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(catalog, management, actors);
        using var request = Request(HttpMethod.Post, "/api/scopes/scope-alpha/agent-profiles/research:publish", "scope-alpha", "user-alpha");
        request.Headers.Add("Idempotency-Key", "publish-header");
        request.Headers.TryAddWithoutValidation("If-Match", "\"agent-profile-v9\"");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        actors.PublishCommands.Should().ContainSingle();
    }

    [Fact]
    public async Task ClearBinding_ShouldAcceptBodyMutationPreconditions()
    {
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(actorPort: actors);
        using var request = Request(HttpMethod.Delete, "/api/scopes/scope-alpha/agent-profile-bindings/nyxid.chat", "scope-alpha", "user-alpha");
        request.Content = JsonContent.Create(new { expectedVersion = 0, idempotencyKey = "clear-body" });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        actors.ClearBindingCommands.Should().ContainSingle();
        actors.ClearBindingCommands[0].ExpectedAuthorityStateVersion.Should().Be(0);
    }

    [Fact]
    public async Task ClearBinding_ShouldPreserveHeaderOnlyEmptyBodyContract()
    {
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(actorPort: actors);
        using var request = Request(HttpMethod.Delete, "/api/scopes/scope-alpha/agent-profile-bindings/nyxid.chat", "scope-alpha", "user-alpha");
        request.Headers.Add("Idempotency-Key", "clear-header");
        request.Headers.TryAddWithoutValidation("If-Match", "\"agent-profile-binding-v0\"");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        actors.ClearBindingCommands.Should().ContainSingle();
    }

    [Fact]
    public async Task SetBinding_ShouldRejectConflictingExpectedVersionsBeforeDispatch()
    {
        var actors = new RecordingActorPort();
        await using var host = await AgentProfileTestHost.StartAsync(actorPort: actors);
        using var request = Request(HttpMethod.Put, "/api/scopes/scope-alpha/agent-profile-bindings/nyxid.chat", "scope-alpha", "user-alpha");
        request.Headers.TryAddWithoutValidation("If-Match", "\"agent-profile-binding-v0\"");
        request.Content = JsonContent.Create(new
        {
            agentProfile = new { ownerKind = "caller", profileSlug = "research" },
            expectedVersion = 1,
            idempotencyKey = "set-body",
        });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorMessageAsync(response)).Should().Be(
            "If-Match header and expectedVersion body values must agree.");
        actors.BindingCommands.Should().BeEmpty();
    }

    private static async Task<string?> ErrorMessageAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.TryGetProperty("message", out var message)
            ? message.GetString()
            : null;
    }

    private static void AssertStrictAIError(JsonElement error, string expectedCode)
    {
        error.EnumerateObject().Select(static property => property.Name)
            .Should().BeEquivalentTo("code", "message");
        error.GetProperty("code").GetString().Should().Be(expectedCode);
        error.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string scopeId, string? subject)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Test-Scope", scopeId);
        if (subject is not null) request.Headers.Add("X-Test-Subject", subject);
        return request;
    }

    private static object DraftInput(object[]? connectedServiceSelectors = null) => new
    {
        displayName = "Research",
        purpose = "Research evidence",
        instructions = "Use reviewed skills.",
        runtimeProfile = new
        {
            agentKind = AgentProfilePolicies.NyxIdChatAgentKind,
            routeToolSetRef = AgentProfilePolicies.NyxIdChatRouteToolSet,
            activationMode = "SHADOW",
            maximumToolPolicy = new
            {
                toolNames = Array.Empty<string>(),
                toolSetRefs = Array.Empty<string>(),
                connectedServiceSelectors = connectedServiceSelectors ?? [],
            },
            recoveryToolPolicy = new
            {
                toolNames = Array.Empty<string>(),
                toolSetRefs = Array.Empty<string>(),
                connectedServiceSelectors = Array.Empty<object>(),
            },
            members = Array.Empty<object>(),
        },
    };

    private static AgentProfileCatalogSnapshot Catalog(
        AgentProfileOwner owner,
        long authorityVersion,
        params AgentProfileCatalogEntry[] entries) =>
        new(
            "namespace-actor",
            authorityVersion,
            owner.Clone(),
            entries,
            [],
            null,
            DateTimeOffset.Parse("2026-07-30T00:00:00Z"));

    private static AgentProfileCatalogSnapshot CatalogWithMutation(
        AgentProfileOwner owner,
        long authorityVersion,
        AgentProfileMutationOutcome mutation,
        params AgentProfileCatalogEntry[] entries) =>
        new(
            "namespace-actor",
            authorityVersion,
            owner.Clone(),
            entries,
            [],
            mutation.Clone(),
            DateTimeOffset.Parse("2026-07-30T00:00:00Z"));

    private static AgentProfileCatalogEntry Entry(
        string profileId,
        string slug,
        AgentProfileProvisioningStatus status,
        long publishedRevision = 0) =>
        new()
        {
            ProfileId = profileId,
            ProfileSlug = slug,
            ProfileActorId = $"actor-{profileId}",
            DisplayName = $"{slug} display",
            Purpose = $"{slug} purpose",
            Status = status,
            PublishedRevision = publishedRevision,
            SnapshotSha256 = publishedRevision > 0
                ? ByteString.CopyFrom(Enumerable.Repeat((byte)7, 32).ToArray())
                : ByteString.Empty,
        };

    private static AgentProfileManagementSnapshot Management(
        AgentProfileOwner owner,
        string profileId,
        string slug,
        long authorityVersion,
        AgentProfileMutationOutcome? mutation = null)
    {
        var draft = new AgentProfileDraft
        {
            DisplayName = "Research",
            Instructions = "Use reviewed skills.",
            RuntimeProfile = new AgentProfileSnapshot
            {
                AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
                RouteToolSetRef = AgentProfilePolicies.NyxIdChatRouteToolSet,
            },
        };
        return new(
            $"actor-{profileId}",
            authorityVersion,
            new AgentProfileIdentity { Owner = owner.Clone(), ProfileId = profileId, ProfileSlug = slug },
            draft,
            1,
            AgentProfileDeterminism.ComputeDraftDigest(draft),
            "",
            "",
            0,
            ByteString.Empty,
            null,
            mutation?.Clone(),
            DateTimeOffset.UtcNow);
    }

    private static AgentProfileExecutionSnapshot Execution(
        AgentProfileBindingTarget target,
        string profileSlug)
    {
        var identity = new AgentProfileIdentity
        {
            Owner = target.Owner.Clone(),
            ProfileId = target.ProfileId,
            ProfileSlug = profileSlug,
        };
        return new AgentProfileExecutionSnapshot(
            $"actor-{target.ProfileId}",
            target.PublishedRevision,
            identity,
            new AgentProfilePublishedSnapshot
            {
                Identity = identity.Clone(),
                PublishedRevision = target.PublishedRevision,
                SnapshotSha256 = target.SnapshotSha256,
            },
            DateTimeOffset.UtcNow);
    }

    private static AgentProfileMutationOutcome Mutation(
        string operationId,
        string code,
        long authorityVersion) =>
        new()
        {
            Operation = new AgentProfileOperationFact
            {
                OperationId = operationId,
                CommandId = $"cmd-{operationId}",
                CorrelationId = $"corr-{operationId}",
            },
            Status = AgentProfileMutationStatus.Succeeded,
            Code = code,
            AuthorityStateVersion = authorityVersion,
        };

    private sealed class AgentProfileTestHost : IAsyncDisposable
    {
        private AgentProfileTestHost(WebApplication app) { App = app; Client = app.GetTestClient(); }
        public WebApplication App { get; }
        public HttpClient Client { get; }

        public static async Task<AgentProfileTestHost> StartAsync(
            RecordingCatalogQuery? catalog = null,
            RecordingManagementQuery? management = null,
            RecordingActorPort? actorPort = null,
            RecordingExecutionQuery? execution = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Aevatar:Authentication:Enabled"] = "true" });
            builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton<IAgentProfileCatalogQueryPort>(catalog ?? new RecordingCatalogQuery());
            builder.Services.AddSingleton<IAgentProfileManagementQueryPort>(management ?? new RecordingManagementQuery());
            builder.Services.AddSingleton<IAgentProfileExecutionQueryPort>(execution ?? new RecordingExecutionQuery());
            builder.Services.AddSingleton<IAgentProfileActorPort>(actorPort ?? new RecordingActorPort());
            builder.Services.AddSingleton<IAgentProfileSkillSealer>(new StaticSkillSealer());
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<AgentProfileApplicationService>();

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapAgentProfileEndpoints();
            app.MapAIWorkspaceAgentManagementEndpoints();
            await app.StartAsync();
            return new AgentProfileTestHost(app);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim>();
            var scopeId = Request.Headers["X-Test-Scope"].ToString();
            var subject = Request.Headers["X-Test-Subject"].ToString();
            if (!string.IsNullOrWhiteSpace(scopeId)) claims.Add(new Claim("scope_id", scopeId));
            if (!string.IsNullOrWhiteSpace(subject)) claims.Add(new Claim("sub", subject));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class RecordingCatalogQuery : IAgentProfileCatalogQueryPort
    {
        public Func<AgentProfileOwner, AgentProfileCatalogSnapshot?> Resolve { get; init; } = _ => null;
        public List<AgentProfileOwner> Requests { get; } = [];
        public Task<AgentProfileCatalogSnapshot?> GetAsync(AgentProfileOwner owner, CancellationToken ct = default)
        {
            Requests.Add(owner.Clone());
            return Task.FromResult(Resolve(owner));
        }
    }

    private sealed class RecordingManagementQuery : IAgentProfileManagementQueryPort
    {
        public AgentProfileManagementSnapshot? Snapshot { get; init; }
        public Task<AgentProfileManagementSnapshot?> GetAsync(AgentProfileIdentity identity, CancellationToken ct = default) => Task.FromResult(Snapshot);
    }

    private sealed class RecordingExecutionQuery : IAgentProfileExecutionQueryPort
    {
        public Func<AgentProfileBindingTarget, AgentProfileExecutionSnapshot?> Resolve { get; init; } = _ => null;
        public Task<AgentProfileExecutionSnapshot?> GetAsync(AgentProfileBindingTarget target, CancellationToken ct = default) =>
            Task.FromResult(Resolve(target));
    }

    private sealed class RecordingActorPort : IAgentProfileActorPort
    {
        public List<CreateAgentProfileCommand> CreateCommands { get; } = [];
        public List<UpdateAgentProfileDraftCommand> DraftCommands { get; } = [];
        public List<PublishAgentProfileCommand> PublishCommands { get; } = [];
        public List<SetAgentProfileDefaultBindingCommand> BindingCommands { get; } = [];
        public List<ClearAgentProfileDefaultBindingCommand> ClearBindingCommands { get; } = [];
        public Task<DispatchAdmission> DispatchCreateAsync(CreateAgentProfileCommand command, CancellationToken ct = default) { CreateCommands.Add(command.Clone()); return Admission(command.Operation); }
        public Task<DispatchAdmission> DispatchInitializeAsync(string profileActorId, InitializeAgentProfileCommand command, CancellationToken ct = default) => Admission(command.Operation);
        public Task<DispatchAdmission> DispatchUpdateDraftAsync(string profileActorId, UpdateAgentProfileDraftCommand command, CancellationToken ct = default) { DraftCommands.Add(command.Clone()); return Admission(command.Operation); }
        public Task<DispatchAdmission> DispatchPublishAsync(string profileActorId, PublishAgentProfileCommand command, CancellationToken ct = default) { PublishCommands.Add(command.Clone()); return Admission(command.Operation); }
        public Task<DispatchAdmission> DispatchSetDefaultBindingAsync(SetAgentProfileDefaultBindingCommand command, CancellationToken ct = default) { BindingCommands.Add(command.Clone()); return Admission(command.Operation); }
        public Task<DispatchAdmission> DispatchClearDefaultBindingAsync(ClearAgentProfileDefaultBindingCommand command, CancellationToken ct = default) { ClearBindingCommands.Add(command.Clone()); return Admission(command.Operation); }
        private static Task<DispatchAdmission> Admission(AgentProfileOperationFact operation) => Task.FromResult(new DispatchAdmission(true, operation.CommandId, DateTimeOffset.UtcNow, "test-actor", operation.CorrelationId));
    }

    private sealed class StaticSkillSealer : IAgentProfileSkillSealer
    {
        public Task<AgentProfileSealingResult> ResolveAndSealAsync(AgentProfileIdentity identity, AgentProfileDraft draft, AgentProfileSealingContext context, CancellationToken ct = default) =>
            Task.FromResult(AgentProfileSealingResult.Success(AgentProfileDeterminism.BuildPublishedSnapshot(
                identity,
                draft,
                context.CurrentDraftRevision,
                context.NextPublishedRevision,
                context.PublishedAt)));
    }
}
