using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class NyxIdExternalWorkflowCapabilitySourceTests
{
    private const string AdmittedSpec = """
        {
          "openapi": "3.1.0",
          "paths": {
            "/states/{entity_id}": {
              "get": {
                "operationId": "get-state",
                "x-aevatar-tool": { "enabled": true, "readOnly": true },
                "parameters": [
                  { "name": "entity_id", "in": "path", "required": true, "schema": { "type": "string" } }
                ]
              }
            }
          }
        }
        """;

    private const string UnmarkedSpec = """
        {
          "openapi": "3.1.0",
          "paths": {
            "/states/{entity_id}": {
              "get": {
                "operationId": "get-state",
                "parameters": [
                  { "name": "entity_id", "in": "path", "required": true, "schema": { "type": "string" } }
                ]
              }
            }
          }
        }
        """;

    private const string ExecutionPolicySpec = """
        {
          "openapi": "3.1.0",
          "paths": {
            "/items": {
              "get": {
                "operationId": "list-items",
                "x-aevatar-tool": true
              },
              "post": {
                "operationId": "create-item",
                "x-aevatar-tool": { "enabled": true, "readOnly": true, "approval": "never" }
              }
            },
            "/items/{item_id}": {
              "delete": {
                "operationId": "delete-item",
                "x-aevatar-tool": { "enabled": true, "readOnly": true, "destructive": false, "approval": "never" },
                "parameters": [
                  { "name": "item_id", "in": "path", "required": true, "schema": { "type": "string" } }
                ]
              }
            }
          }
        }
        """;

    [Fact]
    public void AddNyxIdTools_ShouldRegisterNyxIdCapabilitySource()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options => options.BaseUrl = "https://nyxid.invalid");

        services.Should().Contain(static descriptor =>
            descriptor.ServiceType == typeof(IExternalWorkflowCapabilitySource) &&
            descriptor.ImplementationType == typeof(NyxIdExternalWorkflowCapabilitySource));
    }

    [Fact]
    public async Task ListAsync_ShouldPreserveDuplicateSlugsByExactUserServiceId()
    {
        var handler = new ReadinessHandler
        {
            KeysJson = """
                {
                  "keys": [
                    { "id": "us-home-alpha", "slug": "home-assistant", "label": "Home A", "status": "active", "allowed": true, "credential_source": { "kind": "oauth", "status": "connected", "allowed": true } },
                    { "id": "us-home-beta", "slug": "home-assistant", "label": "Home B", "status": "active", "allowed": true, "credential_source": { "kind": "api_key", "status": "ready", "allowed": true } }
                  ]
                }
                """,
            Specs =
            {
                ["us-home-alpha"] = AdmittedSpec,
                ["us-home-beta"] = AdmittedSpec,
            },
        };
        var source = CreateSource(handler);

        var descriptors = await source.ListAsync(Access(), CancellationToken.None);

        descriptors.Should().HaveCount(2);
        descriptors.Select(static item => item.Selector.NyxIdOperation.UserServiceId)
            .Should().BeEquivalentTo("us-home-alpha", "us-home-beta");
        descriptors.Should().OnlyContain(static item =>
            item.Selector.NyxIdOperation.OperationId == "get-state");
        descriptors.Select(static item => item.Capability).Should().OnlyContain(static capability => capability == null);
        descriptors.Select(static item => item.ToString()).Should()
            .OnlyContain(text => !text.Contains("runtime-caller-credential", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Admission_ShouldFailClosed_ForOperationsWithoutAevatarToolMarker()
    {
        var handler = new ReadinessHandler
        {
            KeysJson = ReadyKeys,
            Specs = { ["us-home-alpha"] = UnmarkedSpec },
        };
        var source = CreateSource(handler);

        (await source.ListAsync(Access(), CancellationToken.None)).Should().BeEmpty();

        var readiness = await source.InspectAsync(
            Access(),
            NyxIdSelector("us-home-alpha", "get-state"),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        readiness.Status.Should().Be(ExternalCapabilityReadinessStatus.OperationSelectionRequired);
        readiness.Blockers.Should().ContainSingle().Which.Code.Should().Be("OPERATION_NOT_ALLOWLISTED");
        readiness.SelectedCapability.Should().BeNull();
    }

    [Fact]
    public async Task Admission_ShouldFailClosed_WhenOperationIdIsMissingOrDuplicated()
    {
        const string missingOperationIdSpec = """
            {
              "openapi": "3.1.0",
              "x-aevatar-tool": { "enabled": true },
              "paths": { "/states": { "get": { } } }
            }
            """;
        const string duplicateOperationIdSpec = """
            {
              "openapi": "3.1.0",
              "x-aevatar-tool": { "enabled": true },
              "paths": {
                "/states": { "get": { "operationId": "get-state" } },
                "/mirror/states": { "get": { "operationId": "get-state" } }
              }
            }
            """;

        var missing = await CreateSource(new ReadinessHandler
        {
            KeysJson = ReadyKeys,
            Specs = { ["us-home-alpha"] = missingOperationIdSpec },
        }).InspectAsync(
            Access(),
            NyxIdSelector("us-home-alpha", "get_/states"),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        missing.Status.Should().Be(ExternalCapabilityReadinessStatus.OperationSelectionRequired);
        missing.Blockers.Should().ContainSingle().Which.Code.Should().Be("OPENAPI_OPERATION_ID_REQUIRED");

        var duplicate = await CreateSource(new ReadinessHandler
        {
            KeysJson = ReadyKeys,
            Specs = { ["us-home-alpha"] = duplicateOperationIdSpec },
        }).InspectAsync(
            Access(),
            NyxIdSelector("us-home-alpha", "get-state"),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        duplicate.Status.Should().Be(ExternalCapabilityReadinessStatus.EndpointContractRequired);
        duplicate.Blockers.Should().ContainSingle().Which.Code.Should().Be("OPENAPI_OPERATION_ID_DUPLICATE");
    }

    [Fact]
    public async Task InspectAsync_ShouldDeriveServerOwnedProof_FromSelectorAlone()
    {
        var handler = new ReadinessHandler
        {
            KeysJson = ReadyKeys,
            Specs = { ["us-home-alpha"] = AdmittedSpec },
        };
        var source = CreateSource(handler);

        var result = await source.InspectAsync(
            Access(),
            NyxIdSelector("us-home-alpha", "get-state"),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        var proof = result.SelectedCapability.NyxIdUserService;
        proof.UserServiceId.Should().Be("us-home-alpha");
        proof.ServiceSlugSnapshot.Should().Be("home-assistant");
        proof.HttpMethod.Should().Be("GET");
        proof.PathTemplate.Should().Be("/states/{entity_id}");
        proof.ContractDigest.Should().NotBeNullOrWhiteSpace();
        proof.Parameters.Should().ContainSingle(parameter =>
            parameter.Name == "entity_id" &&
            parameter.Location == NyxIdOperationParameterLocation.Path &&
            parameter.Required);
        result.SelectedSelector.NyxIdOperation.OperationId.Should().Be("get-state");
    }

    [Theory]
    [InlineData("list-items", NyxIdOperationRisk.ReadOnly, NyxIdOperationApproval.None, true)]
    [InlineData("create-item", NyxIdOperationRisk.Write, NyxIdOperationApproval.Required, false)]
    [InlineData("delete-item", NyxIdOperationRisk.Destructive, NyxIdOperationApproval.Required, false)]
    public async Task InspectAsync_ShouldDeriveConservativeTypedExecutionPolicy(
        string operationId,
        NyxIdOperationRisk expectedRisk,
        NyxIdOperationApproval expectedApproval,
        bool durableAllowed)
    {
        var source = CreateSource(new ReadinessHandler
        {
            KeysJson = ReadyKeys,
            Specs = { ["us-home-alpha"] = ExecutionPolicySpec },
        });

        var result = await source.InspectAsync(
            Access(),
            NyxIdSelector("us-home-alpha", operationId),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        var policy = result.SelectedCapability.NyxIdUserService.ExecutionPolicy;
        policy.Risk.Should().Be(expectedRisk);
        policy.Approval.Should().Be(expectedApproval);
        policy.EnforcementOwner.Should().Be(NyxIdOperationEnforcementOwner.Aevatar);
        policy.AllowedExecutionModes.Should().Contain(ExternalCapabilityExecutionMode.Interactive);
        policy.AllowedExecutionModes.Contains(ExternalCapabilityExecutionMode.Durable)
            .Should().Be(durableAllowed);
        policy.EnforcementOwner.Should().NotBe(NyxIdOperationEnforcementOwner.NyxId,
            "local OpenAPI markers cannot claim trusted NyxID enforcement");
    }

    [Fact]
    public async Task InspectAsync_ShouldChangeContractDigestWhenExecutionPolicyChanges()
    {
        const string readOnlySpec = """
            {
              "openapi": "3.1.0",
              "paths": {
                "/items": {
                  "get": { "operationId": "list-items", "x-aevatar-tool": true }
                }
              }
            }
            """;
        const string writeSpec = """
            {
              "openapi": "3.1.0",
              "paths": {
                "/items": {
                  "get": {
                    "operationId": "list-items",
                    "x-aevatar-tool": { "enabled": true, "readOnly": false }
                  }
                }
              }
            }
            """;

        var readOnly = await CreateSource(new ReadinessHandler
        {
            KeysJson = ReadyKeys,
            Specs = { ["us-home-alpha"] = readOnlySpec },
        }).InspectAsync(
            Access(),
            NyxIdSelector("us-home-alpha", "list-items"),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);
        var write = await CreateSource(new ReadinessHandler
        {
            KeysJson = ReadyKeys,
            Specs = { ["us-home-alpha"] = writeSpec },
        }).InspectAsync(
            Access(),
            NyxIdSelector("us-home-alpha", "list-items"),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        readOnly.SelectedCapability.NyxIdUserService.ExecutionPolicy.Risk.Should()
            .Be(NyxIdOperationRisk.ReadOnly);
        write.SelectedCapability.NyxIdUserService.ExecutionPolicy.Risk.Should()
            .Be(NyxIdOperationRisk.Write);
        write.SelectedCapability.NyxIdUserService.ContractDigest.Should().NotBe(
            readOnly.SelectedCapability.NyxIdUserService.ContractDigest);
    }

    [Fact]
    public async Task InspectAsync_ShouldRejectDurableWriteBeforeAuthorizationCatalogRead()
    {
        var source = CreateSource(new ReadinessHandler
        {
            KeysJson = ReadyKeys,
            Specs = { ["us-home-alpha"] = ExecutionPolicySpec },
        });

        var result = await source.InspectAsync(
            Access(),
            NyxIdSelector("us-home-alpha", "create-item"),
            ExternalCapabilityExecutionMode.Durable,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable);
        result.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("NYXID_OPERATION_DURABLE_EXECUTION_NOT_ALLOWED");
        result.Remediations.Should().ContainSingle().Which.ActionKind.Should()
            .Be(ExternalCapabilityRemediationActionKind.UseInteractiveExecution);
        result.SelectedCapability.NyxIdUserService.ExecutionPolicy.Risk.Should()
            .Be(NyxIdOperationRisk.Write);
    }

    [Fact]
    public async Task InspectAsync_ShouldRequireExactSelection_WhenSelectorOmitsTheUserService()
    {
        var handler = new ReadinessHandler
        {
            KeysJson = """
                { "keys": [
                  { "id": "us-home-alpha", "slug": "home-assistant", "status": "active", "allowed": true },
                  { "id": "us-home-beta", "slug": "home-assistant", "status": "active", "allowed": true }
                ] }
                """,
        };
        var source = CreateSource(handler);

        var result = await source.InspectAsync(
            Access(),
            NyxIdSelector(string.Empty, "get-state"),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.OperationSelectionRequired);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be("NYXID_OPERATION_SELECTION_REQUIRED");
    }

    [Theory]
    [MemberData(nameof(NonReadyCases))]
    public async Task InspectAsync_ShouldMapPublishedFactsToStableTypedStatus(
        string keysJson,
        string? specJson,
        ExternalCapabilityReadinessStatus expectedStatus,
        string expectedCode)
    {
        var handler = new ReadinessHandler { KeysJson = keysJson };
        if (specJson is not null)
            handler.Specs["us-home-alpha"] = specJson;
        var source = CreateSource(handler);

        var result = await source.InspectAsync(
            Access(),
            NyxIdSelector("us-home-alpha", "get-state"),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(expectedStatus);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be(expectedCode);
        result.Remediations.Should().OnlyContain(static item =>
            !item.TrustedLocator.Contains("runtime-caller-credential", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("api_key", false, "", "")]
    [InlineData("oauth", false, "", "")]
    [InlineData("direct", false, "", "")]
    [InlineData("node", true, "node-home-alpha", "online")]
    public async Task InspectAsync_ShouldBeReadyForSupportedInteractiveCapabilityKinds(
        string credentialKind,
        bool requiresNode,
        string nodeId,
        string nodeStatus)
    {
        var handler = new ReadinessHandler
        {
            KeysJson = $$"""
                { "keys": [{
                  "id": "us-home-alpha",
                  "slug": "home-assistant",
                  "status": "active",
                  "is_active": true,
                  "connected": true,
                  "requires_connection": false,
                  "requires_node": {{requiresNode.ToString().ToLowerInvariant()}},
                  "has_node_binding": {{(!string.IsNullOrWhiteSpace(nodeId)).ToString().ToLowerInvariant()}},
                  "node_id": "{{nodeId}}",
                  "node_status": "{{nodeStatus}}",
                  "credential_type": "{{credentialKind}}",
                  "credential_source": { "type": "personal" }
                }] }
                """,
            Specs = { ["us-home-alpha"] = AdmittedSpec },
        };
        var source = CreateSource(handler);
        var descriptor = (await source.ListAsync(Access(), CancellationToken.None)).Single();

        var result = await source.InspectAsync(
            Access(),
            descriptor.Selector,
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        result.Blockers.Should().BeEmpty();
        result.Sources.Should().HaveCount(2);
    }

    [Fact]
    public async Task InspectAsync_ShouldBeReadyForPublishedPersonalServiceShape()
    {
        var handler = new ReadinessHandler
        {
            KeysJson = PublishedPersonalReadyKeys,
            Specs = { ["us-home-alpha"] = AdmittedSpec },
        };
        var source = CreateSource(handler);
        var descriptor = (await source.ListAsync(Access(), CancellationToken.None)).Single();

        var result = await source.InspectAsync(
            Access(),
            descriptor.Selector,
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
    }

    [Fact]
    public async Task InspectAsync_ShouldFailClosed_WhenPublishedReadinessFactsAreIncomplete()
    {
        var handler = new ReadinessHandler
        {
            KeysJson =
                """{ "keys": [{ "id": "us-home-alpha", "slug": "home-assistant", "status": "active", "credential_source": { "type": "personal" } }] }""",
            Specs = { ["us-home-alpha"] = AdmittedSpec },
        };
        var source = CreateSource(handler);
        var descriptor = (await source.ListAsync(Access(), CancellationToken.None)).Single();

        var result = await source.InspectAsync(
            Access(),
            descriptor.Selector,
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.SourceStale);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be("NYXID_SERVICE_FACTS_INCOMPLETE");
    }

    [Theory]
    [InlineData("""{ "unexpected": [] }""")]
    [InlineData("""{ "keys": [{ "slug": "home-assistant" }] }""")]
    public async Task InspectAsync_ShouldReportSourceUnavailable_WhenKeysPayloadIsMalformed(string keysJson)
    {
        var source = CreateSource(new ReadinessHandler { KeysJson = keysJson });

        var result = await source.InspectAsync(
            Access(),
            NyxIdSelector("us-home-alpha", "get-state"),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.SourceStale);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be("NYXID_SOURCE_UNAVAILABLE");
    }

    [Fact]
    public async Task InspectAsync_ShouldReportNodeUnavailable_WhenPublishedNodeStatusIsUnknown()
    {
        var handler = new ReadinessHandler
        {
            KeysJson = """
                { "keys": [{
                  "id": "us-home-alpha",
                  "slug": "home-assistant",
                  "status": "active",
                  "is_active": true,
                  "connected": true,
                  "requires_connection": false,
                  "has_node_binding": true,
                  "node_id": "node-home-alpha",
                  "node_status": "unknown",
                  "credential_source": { "type": "personal" }
                }] }
                """,
            Specs = { ["us-home-alpha"] = AdmittedSpec },
        };
        var source = CreateSource(handler);
        var descriptor = (await source.ListAsync(Access(), CancellationToken.None)).Single();

        var result = await source.InspectAsync(
            Access(),
            descriptor.Selector,
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.NodeUnavailable);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be("NODE_UNAVAILABLE");
    }

    [Theory]
    [InlineData("pending_auth")]
    [InlineData("refresh_failed")]
    public async Task InspectAsync_ShouldReportNotActive_ForPublishedNonActiveServiceStatus(string status)
    {
        var result = await InspectPublishedServiceAsync($$"""
            { "keys": [{
              "id": "us-home-alpha",
              "slug": "home-assistant",
              "status": "{{status}}",
              "is_active": true,
              "connected": true,
              "requires_connection": false,
              "has_node_binding": false,
              "credential_source": { "type": "personal" }
            }] }
            """);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.CredentialConnectionRequired);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be("USER_SERVICE_NOT_ACTIVE");
    }

    [Fact]
    public async Task InspectAsync_ShouldFailClosed_WhenPublishedServiceStatusIsUnknown()
    {
        var result = await InspectPublishedServiceAsync("""
            { "keys": [{
              "id": "us-home-alpha",
              "slug": "home-assistant",
              "status": "warming_up",
              "is_active": true,
              "connected": true,
              "requires_connection": false,
              "has_node_binding": false,
              "credential_source": { "type": "personal" }
            }] }
            """);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.SourceStale);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be("NYXID_SERVICE_STATUS_UNKNOWN");
    }

    [Fact]
    public async Task InspectAsync_ShouldFailClosed_WhenConnectionFactsContradictEachOther()
    {
        var result = await InspectPublishedServiceAsync("""
            { "keys": [{
              "id": "us-home-alpha",
              "slug": "home-assistant",
              "status": "active",
              "is_active": true,
              "connected": false,
              "requires_connection": false,
              "has_node_binding": false,
              "credential_source": { "type": "personal" }
            }] }
            """);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.SourceStale);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be("NYXID_SERVICE_FACTS_INCONSISTENT");
    }

    [Fact]
    public async Task InspectAsync_ShouldReportNodeUnavailable_WhenPublishedNodeStatusIsNotOnline()
    {
        var result = await InspectPublishedServiceAsync("""
            { "keys": [{
              "id": "us-home-alpha",
              "slug": "home-assistant",
              "status": "active",
              "is_active": true,
              "connected": true,
              "requires_connection": false,
              "has_node_binding": true,
              "node_id": "node-home-alpha",
              "node_status": "degraded",
              "credential_source": { "type": "personal" }
            }] }
            """);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.NodeUnavailable);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be("NODE_UNAVAILABLE");
    }

    [Fact]
    public async Task InspectAsync_ShouldUseExactServiceFromHealthySource_WhenOrganizationSourceFails()
    {
        var handler = new ReadinessHandler
        {
            KeysByBearerToken =
            {
                ["runtime-caller-credential"] = PublishedPersonalReadyKeys,
                ["runtime-organization-credential"] = "not-json",
            },
            Specs = { ["us-home-alpha"] = AdmittedSpec },
        };
        var source = CreateSource(handler);
        var descriptor = (await source.ListAsync(AccessWithOrganization(), CancellationToken.None)).Single();

        var result = await source.InspectAsync(
            AccessWithOrganization(),
            descriptor.Selector,
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
    }

    [Fact]
    public async Task InspectAsync_ShouldReportSourceUnavailable_WhenMissingServiceCouldBeInFailedSource()
    {
        var handler = new ReadinessHandler
        {
            KeysByBearerToken =
            {
                ["runtime-caller-credential"] = PublishedOtherPersonalReadyKeys,
                ["runtime-organization-credential"] = "not-json",
            },
        };
        var source = CreateSource(handler);

        var result = await source.InspectAsync(
            AccessWithOrganization(),
            NyxIdSelector("us-home-alpha", "get-state"),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.SourceStale);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be("NYXID_SOURCE_UNAVAILABLE");
    }

    [Fact]
    public async Task InspectAsync_ShouldFailClosedForDurableMode_WhenTopologyProofIsUnavailable()
    {
        var handler = new ReadinessHandler
        {
            KeysJson = ReadyKeys,
            Specs = { ["us-home-alpha"] = AdmittedSpec },
        };
        var source = CreateSource(handler);
        var descriptor = (await source.ListAsync(Access(), CancellationToken.None)).Single();

        var result = await source.InspectAsync(
            Access(),
            descriptor.Selector,
            ExternalCapabilityExecutionMode.Durable,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be("DURABLE_AUTHORIZATION_UNAVAILABLE");
        result.Remediations.Should().ContainSingle().Which.ActionKind
            .Should().Be(ExternalCapabilityRemediationActionKind.UseInteractiveExecution);
    }

    [Fact]
    public async Task InspectAsync_ShouldReturnReadyForDurableMode_WhenCatalogProvesExactGrant()
    {
        var snapshot = ReadyCatalogSnapshot();

        var result = await InspectDurableWithCatalogAsync(snapshot);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        result.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Durable);
        result.SelectedCapability.NyxIdUserService.UserServiceId.Should().Be("us-home-alpha");
        var catalogSource = result.Sources.Should().ContainSingle(sourceStamp =>
            sourceStamp.SourceKind == ExternalCapabilitySourceKind.DurableAuthorizationCatalog).Which;
        catalogSource.SourceVersion.Should().Be(17);
        catalogSource.ContentDigest.Should().Be(snapshot.ContentDigest);
    }

    [Fact]
    public async Task InspectAsync_ShouldFailDurableMode_WhenCatalogContentDigestIsTampered()
    {
        var snapshot = ReadyCatalogSnapshot() with
        {
            ContentDigest = "tampered-content-digest",
        };

        var result = await InspectDurableWithCatalogAsync(snapshot);

        AssertDurableAuthorizationUnavailable(result);
    }

    [Fact]
    public async Task InspectAsync_ShouldFailDurableMode_WhenCatalogIsStale()
    {
        var snapshot = ReadyCatalogSnapshot() with
        {
            FreshUntilUtc = new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero),
        };

        var result = await InspectDurableWithCatalogAsync(snapshot);

        AssertDurableAuthorizationUnavailable(result);
    }

    [Fact]
    public async Task InspectAsync_ShouldFailDurableMode_WhenCatalogOwnerDoesNotMatchCaller()
    {
        var owner = ReadyCatalogSnapshot().Owner.Clone();
        owner.OwnerSubject = "caller-other";
        var snapshot = ReadyCatalogSnapshot() with { Owner = owner };

        var result = await InspectDurableWithCatalogAsync(snapshot);

        AssertDurableAuthorizationUnavailable(result);
    }

    [Fact]
    public async Task InspectAsync_ShouldFailDurableMode_WhenCatalogSlugSnapshotDrifts()
    {
        var snapshot = ReadyCatalogSnapshot();
        snapshot.Services[0].ServiceSlug = "home-assistant-renamed";
        snapshot = WithCanonicalContentDigest(snapshot);

        var result = await InspectDurableWithCatalogAsync(snapshot);

        AssertDurableAuthorizationUnavailable(result);
    }

    [Fact]
    public async Task InspectAsync_ShouldFailDurableMode_WhenCatalogDeniesSelectedService()
    {
        var snapshot = ReadyCatalogSnapshot();
        snapshot.Services[0].Access = NyxIdAuthorizationAccess.Denied;
        snapshot = WithCanonicalContentDigest(snapshot);

        var result = await InspectDurableWithCatalogAsync(snapshot);

        AssertDurableAuthorizationUnavailable(result);
    }

    [Fact]
    public async Task InspectAsync_ShouldFailDurableMode_WhenRequiredNodeIdsAreMissing()
    {
        var snapshot = ReadyCatalogSnapshot();
        snapshot.Services[0].NodeIds.Clear();
        snapshot = WithCanonicalContentDigest(snapshot);

        var result = await InspectDurableWithCatalogAsync(snapshot);

        AssertDurableAuthorizationUnavailable(result);
    }

    [Fact]
    public async Task InspectAsync_ShouldFailDurableMode_WhenResourceOwnerAuthorityIsNotNyxId()
    {
        var snapshot = ReadyCatalogSnapshot();
        snapshot.Services[0].ResourceOwner.Authority = "other-authority";
        snapshot = WithCanonicalContentDigest(snapshot);

        var result = await InspectDurableWithCatalogAsync(snapshot);

        AssertDurableAuthorizationUnavailable(result);
    }

    public static TheoryData<string, string?, ExternalCapabilityReadinessStatus, string> NonReadyCases =>
        new()
        {
            {
                """{ "keys": [] }""",
                AdmittedSpec,
                ExternalCapabilityReadinessStatus.ServiceRegistrationRequired,
                "USER_SERVICE_NOT_VISIBLE"
            },
            {
                """{ "keys": [{ "id": "us-home-alpha", "slug": "home-assistant", "status": "pending_auth", "is_active": false, "connected": false, "requires_connection": true, "has_node_binding": false, "credential_source": { "type": "personal" } }] }""",
                AdmittedSpec,
                ExternalCapabilityReadinessStatus.CredentialConnectionRequired,
                "USER_SERVICE_NOT_ACTIVE"
            },
            {
                """{ "keys": [{ "id": "us-home-alpha", "slug": "home-assistant", "status": "active", "is_active": true, "connected": true, "requires_connection": false, "has_node_binding": false, "credential_source": { "type": "org", "allowed": false } }] }""",
                AdmittedSpec,
                ExternalCapabilityReadinessStatus.ServiceAccessDenied,
                "USER_SERVICE_ACCESS_DENIED"
            },
            {
                """{ "keys": [{ "id": "us-home-alpha", "slug": "home-assistant", "status": "active", "is_active": true, "connected": true, "requires_connection": false, "requires_node": true, "has_node_binding": false, "credential_source": { "type": "personal" } }] }""",
                AdmittedSpec,
                ExternalCapabilityReadinessStatus.NodeBindingRequired,
                "NODE_BINDING_REQUIRED"
            },
            {
                """{ "keys": [{ "id": "us-home-alpha", "slug": "home-assistant", "status": "active", "is_active": true, "connected": true, "requires_connection": false, "requires_node": true, "has_node_binding": true, "node_id": "node-home-alpha", "node_status": "offline", "credential_source": { "type": "personal" } }] }""",
                AdmittedSpec,
                ExternalCapabilityReadinessStatus.NodeUnavailable,
                "NODE_UNAVAILABLE"
            },
            {
                ReadyKeys,
                null,
                ExternalCapabilityReadinessStatus.EndpointContractRequired,
                "OPENAPI_CONTRACT_REQUIRED"
            },
            {
                ReadyKeys,
                """{ "openapi": "3.1.0", "x-aevatar-tool": { "enabled": true }, "paths": { "/states/{entity_id}": { "get": { "operationId": "other-state" } } } }""",
                ExternalCapabilityReadinessStatus.OperationSelectionRequired,
                "OPERATION_NOT_FOUND"
            },
            {
                """{ "fresh_until": "2026-07-21T09:59:00Z", "keys": [{ "id": "us-home-alpha", "slug": "home-assistant", "status": "active", "is_active": true, "connected": true, "requires_connection": false, "has_node_binding": false, "credential_source": { "type": "personal" } }] }""",
                AdmittedSpec,
                ExternalCapabilityReadinessStatus.SourceStale,
                "NYXID_SOURCE_STALE"
            },
        };

    private const string ReadyKeys = """
        { "keys": [{
          "id": "us-home-alpha",
          "slug": "home-assistant",
          "status": "active",
          "is_active": true,
          "connected": true,
          "requires_connection": false,
          "has_node_binding": false,
          "credential_type": "oauth",
          "credential_source": { "type": "personal" }
        }] }
        """;

    private const string PublishedPersonalReadyKeys = """
        { "keys": [{
          "id": "us-home-alpha",
          "slug": "home-assistant",
          "status": "active",
          "is_active": true,
          "connected": true,
          "requires_connection": false,
          "has_node_binding": false,
          "credential_source": { "type": "personal" }
        }] }
        """;

    private const string PublishedOtherPersonalReadyKeys = """
        { "keys": [{
          "id": "us-calendar-beta",
          "slug": "calendar",
          "status": "active",
          "is_active": true,
          "connected": true,
          "requires_connection": false,
          "has_node_binding": false,
          "credential_source": { "type": "personal" }
        }] }
        """;

    private static NyxIdExternalWorkflowCapabilitySource CreateSource(ReadinessHandler handler) =>
        new(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyxid.invalid" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyxid.invalid" },
            new FixedTimeProvider());

    private static ExternalWorkflowCapabilityAccessContext Access() =>
        new("scope-alpha", "caller-alpha", "runtime-caller-credential");

    private static ExternalWorkflowCapabilityAccessContext AccessWithOrganization() =>
        new(
            "scope-alpha",
            "caller-alpha",
            "runtime-caller-credential",
            "runtime-organization-credential");

    private static NyxIdAuthorizationCatalogSnapshot ReadyCatalogSnapshot()
    {
        var owner = new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = "caller-alpha",
        };
        var service = new NyxIdAuthorizationServiceEvidence
        {
            UserServiceId = "us-home-alpha",
            ServiceSlug = "home-assistant",
            DisplayName = "Home Assistant",
            Access = NyxIdAuthorizationAccess.Permitted,
            NodeGrantRequirement = AuthorizationGrantRequirement.Required,
            ResourceOwner = new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Organization,
                OwnerSubject = "org-home-alpha",
            },
        };
        service.NodeIds.Add(["node-home-alpha", "node-home-beta"]);
        NyxIdAuthorizationServiceEvidence[] services = [service];
        return new NyxIdAuthorizationCatalogSnapshot(
            owner,
            17,
            new DateTimeOffset(2026, 7, 21, 9, 59, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 21, 10, 5, 0, TimeSpan.Zero),
            "scope-plan-contract/v1",
            "scope-plan-policy/v1",
            new DateTimeOffset(2026, 7, 21, 9, 59, 0, TimeSpan.Zero),
            NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(owner, services),
            services,
            Activated: true);
    }

    private static NyxIdAuthorizationCatalogSnapshot WithCanonicalContentDigest(
        NyxIdAuthorizationCatalogSnapshot snapshot) =>
        snapshot with
        {
            ContentDigest = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
                snapshot.Owner,
                snapshot.Services),
        };

    private static async Task<ExternalCapabilityReadiness> InspectDurableWithCatalogAsync(
        NyxIdAuthorizationCatalogSnapshot snapshot)
    {
        var handler = new ReadinessHandler
        {
            KeysJson = ReadyKeys,
            Specs = { ["us-home-alpha"] = AdmittedSpec },
        };
        var services = new ServiceCollection();
        services.AddSingleton<INyxIdAuthorizationCatalogQueryPort>(new StubCatalogQueryPort(snapshot));
        services.AddSingleton<TimeProvider>(new FixedTimeProvider());
        services.AddNyxIdTools(options => options.BaseUrl = "https://nyxid.invalid");
        services.AddHttpClient<NyxIdApiClient>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IExternalWorkflowCapabilitySource>()
            .OfType<NyxIdExternalWorkflowCapabilitySource>()
            .Single();
        var descriptor = (await source.ListAsync(Access(), CancellationToken.None)).Single();
        return await source.InspectAsync(
            Access(),
            descriptor.Selector,
            ExternalCapabilityExecutionMode.Durable,
            CancellationToken.None);
    }

    private static void AssertDurableAuthorizationUnavailable(ExternalCapabilityReadiness result)
    {
        result.Status.Should().Be(ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be("DURABLE_AUTHORIZATION_UNAVAILABLE");
    }

    private static async Task<ExternalCapabilityReadiness> InspectPublishedServiceAsync(string keysJson)
    {
        var handler = new ReadinessHandler
        {
            KeysJson = keysJson,
            Specs = { ["us-home-alpha"] = AdmittedSpec },
        };
        var source = CreateSource(handler);
        var descriptor = (await source.ListAsync(Access(), CancellationToken.None)).Single();
        return await source.InspectAsync(
            Access(),
            descriptor.Selector,
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);
    }

    private static ExternalWorkflowCapabilitySelector NyxIdSelector(
        string userServiceId,
        string operationId) =>
        new()
        {
            NyxIdOperation = new NyxIdOperationSelector
            {
                UserServiceId = userServiceId,
                OperationId = operationId,
            },
        };

    private sealed class ReadinessHandler : HttpMessageHandler
    {
        public string KeysJson { get; init; } = """{ "keys": [] }""";

        public Dictionary<string, string> KeysByBearerToken { get; init; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Specs { get; init; } = new(StringComparer.Ordinal);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/api/v1/keys")
            {
                var bearerToken = request.Headers.Authorization?.Parameter ?? string.Empty;
                var response = KeysByBearerToken.TryGetValue(bearerToken, out var keysJson)
                    ? keysJson
                    : KeysJson;
                return Task.FromResult(Json(HttpStatusCode.OK, response));
            }

            const string prefix = "/api/v1/proxy/services/";
            const string suffix = "/openapi.json";
            if (path.StartsWith(prefix, StringComparison.Ordinal) &&
                path.EndsWith(suffix, StringComparison.Ordinal))
            {
                var serviceId = path[prefix.Length..^suffix.Length];
                return Task.FromResult(Specs.TryGetValue(serviceId, out var spec)
                    ? Json(HttpStatusCode.OK, spec)
                    : Json(HttpStatusCode.NotFound, """{ "detail": "not found" }"""));
            }

            return Task.FromResult(Json(HttpStatusCode.NotFound, """{ "detail": "not found" }"""));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }

    private sealed class StubCatalogQueryPort(NyxIdAuthorizationCatalogSnapshot? snapshot)
        : INyxIdAuthorizationCatalogQueryPort
    {
        public Task<NyxIdAuthorizationCatalogSnapshot?> GetAsync(
            AuthorizationOwnerIdentity owner,
            CancellationToken ct = default) => Task.FromResult(snapshot);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 7, 21, 10, 0, 0, TimeSpan.Zero);
    }
}
