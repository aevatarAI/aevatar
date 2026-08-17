using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting.NyxId;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class NyxIdModelSourceInventoryParserTests
{
    [Fact]
    public void ParsePlatformCatalogServices_ShouldPreserveTypedProxyEligibilityFacts()
    {
        const string json = """
            {
              "services": [
                {
                  "id": "cat-chrono-public",
                  "name": "Chrono LLM Public",
                  "slug": "chrono-llm-public",
                  "service_type": "http",
                  "visibility": "public",
                  "auth_method": "header",
                  "service_category": "internal",
                  "requires_user_credential": false,
                  "is_active": true,
                  "base_url": "https://example.invalid",
                  "future_field": { "ignored": true }
                },
                {
                  "id": "cat-future",
                  "name": "Future Transport",
                  "slug": "future-transport",
                  "service_type": "grpc",
                  "visibility": "private",
                  "auth_method": "token_exchange",
                  "service_category": "connection",
                  "requires_user_credential": true,
                  "is_active": false
                }
              ],
              "next_page": null
            }
            """;

        var result = NyxIdModelSourceInventoryParser.ParsePlatformCatalogServices(json);

        result.Services.Should().Equal(
            new NyxIdPlatformModelSourceService(
                "cat-chrono-public",
                "chrono-llm-public",
                "Chrono LLM Public",
                true,
                new NyxIdModelSourceServiceType(NyxIdModelSourceServiceTypeKind.HTTP, "http"),
                new NyxIdCatalogServiceVisibility(NyxIdCatalogServiceVisibilityKind.Public, "public"),
                new NyxIdCatalogServiceAuthMethod(NyxIdCatalogServiceAuthMethodKind.Header, "header"),
                new NyxIdCatalogServiceCategory(NyxIdCatalogServiceCategoryKind.Internal, "internal"),
                RequiresUserCredential: false),
            new NyxIdPlatformModelSourceService(
                "cat-future",
                "future-transport",
                "Future Transport",
                false,
                new NyxIdModelSourceServiceType(NyxIdModelSourceServiceTypeKind.Unknown, "grpc"),
                new NyxIdCatalogServiceVisibility(NyxIdCatalogServiceVisibilityKind.Private, "private"),
                new NyxIdCatalogServiceAuthMethod(
                    NyxIdCatalogServiceAuthMethodKind.TokenExchange,
                    "token_exchange"),
                new NyxIdCatalogServiceCategory(
                    NyxIdCatalogServiceCategoryKind.Connection,
                    "connection"),
                RequiresUserCredential: true));
    }

    [Fact]
    public void PlatformModelSourceAvailability_ShouldFailClosedForNyxIdProxyConstraints()
    {
        var available = new NyxIdPlatformModelSourceService(
            "catalog-alpha",
            "alpha",
            "Alpha",
            true,
            new NyxIdModelSourceServiceType(NyxIdModelSourceServiceTypeKind.HTTP, "http"),
            new NyxIdCatalogServiceVisibility(NyxIdCatalogServiceVisibilityKind.Public, "public"),
            new NyxIdCatalogServiceAuthMethod(NyxIdCatalogServiceAuthMethodKind.Header, "header"),
            new NyxIdCatalogServiceCategory(NyxIdCatalogServiceCategoryKind.Internal, "internal"),
            RequiresUserCredential: false);

        available.IsSelectable.Should().BeTrue();
        var unavailable = new (
            NyxIdPlatformModelSourceService Service,
            NyxIdPlatformModelSourceAvailabilityReason Reason)[]
        {
            (available with { Slug = "legacy--slug" },
                NyxIdPlatformModelSourceAvailabilityReason.InvalidServiceSlug),
            (available with
            {
                ServiceCategory = new NyxIdCatalogServiceCategory(
                    NyxIdCatalogServiceCategoryKind.Provider,
                    "provider"),
            }, NyxIdPlatformModelSourceAvailabilityReason.ProviderService),
            (available with { RequiresUserCredential = true },
                NyxIdPlatformModelSourceAvailabilityReason.UserCredentialRequired),
            (available with
            {
                AuthMethod = new NyxIdCatalogServiceAuthMethod(
                    NyxIdCatalogServiceAuthMethodKind.TokenExchange,
                    "token_exchange"),
            }, NyxIdPlatformModelSourceAvailabilityReason.TokenExchangeUnsupported),
            (available with
            {
                ServiceCategory = new NyxIdCatalogServiceCategory(
                    NyxIdCatalogServiceCategoryKind.Unknown,
                    "future"),
            }, NyxIdPlatformModelSourceAvailabilityReason.UnsupportedServiceCategory),
            (available with
            {
                AuthMethod = new NyxIdCatalogServiceAuthMethod(
                    NyxIdCatalogServiceAuthMethodKind.Unknown,
                    "future"),
            }, NyxIdPlatformModelSourceAvailabilityReason.UnsupportedAuthMethod),
        };

        unavailable.Should().OnlyContain(static item =>
            !item.Service.IsSelectable && item.Service.AvailabilityReason == item.Reason);
    }

    [Fact]
    public void ParseScopeKeys_ShouldKeepUserAndCatalogIdentitiesAndAvailabilityDistinct()
    {
        const string json = """
            {
              "keys": [
                {
                  "id": "user-service-personal",
                  "catalog_service_id": "cat-chrono-public",
                  "slug": "my-chrono-route",
                  "label": "My Chrono Route",
                  "catalog_service_name": "Chrono LLM Public",
                  "is_active": true,
                  "service_type": "http",
                  "status": "active",
                  "credential_missing": false,
                  "connection_status": null,
                  "node_id": null,
                  "node_status": null,
                  "credential_source": { "type": "personal" },
                  "future_field": "ignored"
                },
                {
                  "id": "user-service-org",
                  "catalog_service_id": null,
                  "slug": "team-route",
                  "label": "Team Route",
                  "catalog_service_name": null,
                  "is_active": false,
                  "service_type": "ssh",
                  "status": "expired",
                  "credential_missing": true,
                  "connection_status": "expired",
                  "node_id": "node-alpha",
                  "node_status": "offline",
                  "credential_source": {
                    "type": "org",
                    "org_id": "org-alpha",
                    "org_name": "Alpha",
                    "avatar_url": "https://example.invalid/avatar.png",
                    "role": "viewer",
                    "allowed": false
                  }
                }
              ]
            }
            """;

        var result = NyxIdModelSourceInventoryParser.ParseScopeKeys(json);

        result.Services.Should().Equal(
            new NyxIdScopeModelSourceService(
                "user-service-personal",
                "cat-chrono-public",
                "my-chrono-route",
                "My Chrono Route",
                "Chrono LLM Public",
                true,
                new NyxIdModelSourceServiceType(NyxIdModelSourceServiceTypeKind.HTTP, "http"),
                new NyxIdPersonalCredentialSource(),
                new NyxIdModelSourceCredentialStatus(
                    NyxIdModelSourceCredentialStatusKind.Active,
                    "active"),
                false,
                new NyxIdModelSourceConnectionStatus(
                    NyxIdModelSourceConnectionStatusKind.NotApplicable,
                    null),
                null,
                new NyxIdModelSourceNodeStatus(
                    NyxIdModelSourceNodeStatusKind.NotApplicable,
                    null)),
            new NyxIdScopeModelSourceService(
                "user-service-org",
                null,
                "team-route",
                "Team Route",
                null,
                false,
                new NyxIdModelSourceServiceType(NyxIdModelSourceServiceTypeKind.SSH, "ssh"),
                new NyxIdOrganizationCredentialSource(
                    "org-alpha",
                    "Alpha",
                    "https://example.invalid/avatar.png",
                    NyxIdScopeOrganizationRole.Viewer,
                    false),
                new NyxIdModelSourceCredentialStatus(
                    NyxIdModelSourceCredentialStatusKind.Expired,
                    "expired"),
                true,
                new NyxIdModelSourceConnectionStatus(
                    NyxIdModelSourceConnectionStatusKind.Expired,
                    "expired"),
                "node-alpha",
                new NyxIdModelSourceNodeStatus(
                    NyxIdModelSourceNodeStatusKind.Offline,
                    "offline")));
        result.Services[0].IsCallable.Should().BeTrue();
        result.Services[1].IsCallable.Should().BeFalse();
    }

    [Fact]
    public void ScopeModelSourceAvailability_ShouldFailClosedForEveryAuthoritativeReadinessFact()
    {
        var available = new NyxIdScopeModelSourceService(
            "user-service-alpha",
            "catalog-alpha",
            "alpha",
            "Alpha",
            "Alpha",
            true,
            new NyxIdModelSourceServiceType(NyxIdModelSourceServiceTypeKind.HTTP, "http"),
            new NyxIdPersonalCredentialSource(),
            new NyxIdModelSourceCredentialStatus(NyxIdModelSourceCredentialStatusKind.Active, "active"),
            false,
            new NyxIdModelSourceConnectionStatus(
                NyxIdModelSourceConnectionStatusKind.NotApplicable,
                null),
            null,
            new NyxIdModelSourceNodeStatus(NyxIdModelSourceNodeStatusKind.NotApplicable, null));

        available.IsCallable.Should().BeTrue();
        var unavailable = new (NyxIdScopeModelSourceService Service, NyxIdModelSourceAvailabilityReason Reason)[]
        {
            (available with
            {
                ServiceType = new NyxIdModelSourceServiceType(
                    NyxIdModelSourceServiceTypeKind.SSH,
                    "ssh"),
            }, NyxIdModelSourceAvailabilityReason.UnsupportedServiceType),
            (available with { IsActive = false }, NyxIdModelSourceAvailabilityReason.ServiceInactive),
            (available with { CredentialMissing = true }, NyxIdModelSourceAvailabilityReason.CredentialMissing),
            (available with
            {
                CredentialStatus = new NyxIdModelSourceCredentialStatus(
                    NyxIdModelSourceCredentialStatusKind.PendingAuth,
                    "pending_auth"),
            }, NyxIdModelSourceAvailabilityReason.CredentialInactive),
            (available with
            {
                ConnectionStatus = new NyxIdModelSourceConnectionStatus(
                    NyxIdModelSourceConnectionStatusKind.Expired,
                    "expired"),
            }, NyxIdModelSourceAvailabilityReason.ConnectionExpired),
            (available with
            {
                ConnectionStatus = new NyxIdModelSourceConnectionStatus(
                    NyxIdModelSourceConnectionStatusKind.Unknown,
                    "future_state"),
            }, NyxIdModelSourceAvailabilityReason.ConnectionUnavailable),
            (available with
            {
                CredentialSource = new NyxIdOrganizationCredentialSource(
                    "org-alpha",
                    "Alpha",
                    null,
                    NyxIdScopeOrganizationRole.Viewer,
                    false),
            }, NyxIdModelSourceAvailabilityReason.OrganizationAccessDenied),
            (available with
            {
                NodeId = "node-alpha",
                NodeStatus = new NyxIdModelSourceNodeStatus(
                    NyxIdModelSourceNodeStatusKind.Offline,
                    "offline"),
            }, NyxIdModelSourceAvailabilityReason.NodeUnavailable),
        };

        unavailable.Should().OnlyContain(static item =>
            !item.Service.IsCallable && item.Service.AvailabilityReason == item.Reason);
    }

    [Fact]
    public void NodeRoutedScopeModelSource_ShouldUseNodeReadinessInsteadOfServerCredentialStatus()
    {
        var service = new NyxIdScopeModelSourceService(
            "user-service-node",
            "catalog-node",
            "chrono-node",
            "Chrono Node",
            "Chrono Node",
            true,
            new NyxIdModelSourceServiceType(NyxIdModelSourceServiceTypeKind.HTTP, "http"),
            new NyxIdPersonalCredentialSource(),
            new NyxIdModelSourceCredentialStatus(
                NyxIdModelSourceCredentialStatusKind.Expired,
                "expired"),
            CredentialMissing: true,
            new NyxIdModelSourceConnectionStatus(
                NyxIdModelSourceConnectionStatusKind.Expired,
                "expired"),
            "node-alpha",
            new NyxIdModelSourceNodeStatus(
                NyxIdModelSourceNodeStatusKind.Online,
                "online"));

        service.IsCallable.Should().BeTrue();
        service.AvailabilityReason.Should().Be(NyxIdModelSourceAvailabilityReason.Available);

        var offline = service with
        {
            NodeStatus = new NyxIdModelSourceNodeStatus(
                NyxIdModelSourceNodeStatusKind.Offline,
                "offline"),
        };
        offline.IsCallable.Should().BeFalse();
        offline.AvailabilityReason.Should().Be(NyxIdModelSourceAvailabilityReason.NodeUnavailable);
    }

    [Fact]
    public void ParsePlatformCatalogServices_WithDuplicateCatalogIdentity_ShouldRejectResponse()
    {
        const string json = """
            {
              "services": [
                { "id": "cat-alpha", "name": "Alpha", "slug": "alpha", "service_type": "http", "visibility": "public", "auth_method": "none", "service_category": "internal", "requires_user_credential": false, "is_active": true },
                { "id": "cat-alpha", "name": "Beta", "slug": "beta", "service_type": "http", "visibility": "public", "auth_method": "none", "service_category": "internal", "requires_user_credential": false, "is_active": true }
              ]
            }
            """;

        var act = () => NyxIdModelSourceInventoryParser.ParsePlatformCatalogServices(json);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*duplicates catalog service*");
    }

    [Fact]
    public void ParseScopeKeys_WithDuplicateUserServiceIdentity_ShouldRejectResponse()
    {
        const string json = """
            {
              "keys": [
                { "id": "us-alpha", "slug": "alpha", "is_active": true, "service_type": "http", "status": "active", "credential_missing": false, "credential_source": { "type": "personal" } },
                { "id": "us-alpha", "slug": "beta", "is_active": true, "service_type": "http", "status": "active", "credential_missing": false, "credential_source": { "type": "personal" } }
              ]
            }
            """;

        var act = () => NyxIdModelSourceInventoryParser.ParseScopeKeys(json);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*duplicates user service*");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"services\":[{\"name\":\"Missing identity\",\"slug\":\"missing-id\",\"service_type\":\"http\",\"is_active\":true}]}")]
    [InlineData("{\"services\":[{\"id\":\"cat-alpha\",\"name\":\"Alpha\",\"slug\":\"alpha\",\"is_active\":true}]}")]
    public void ParsePlatformCatalogServices_WithMalformedRequiredField_ShouldRejectResponse(string json)
    {
        var act = () => NyxIdModelSourceInventoryParser.ParsePlatformCatalogServices(json);

        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"keys\":[{\"id\":\"us-alpha\",\"slug\":\"alpha\",\"is_active\":true}]}")]
    [InlineData("{\"keys\":[{\"id\":\"us-alpha\",\"slug\":\"alpha\",\"is_active\":true,\"service_type\":\"http\",\"status\":\"active\",\"credential_missing\":false,\"credential_source\":{\"type\":\"org\",\"org_id\":\"org-alpha\",\"org_name\":\"Alpha\",\"role\":\"owner\",\"allowed\":true}}]}")]
    public void ParseScopeKeys_WithMalformedRequiredField_ShouldRejectResponse(string json)
    {
        var act = () => NyxIdModelSourceInventoryParser.ParseScopeKeys(json);

        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(" Chrono ")]
    [InlineData("Chrono")]
    [InlineData("chrono--runtime")]
    [InlineData("chrono/runtime")]
    public void ParseInventories_WithNonCanonicalServiceSlug_ShouldKeepUnavailableCandidates(string slug)
    {
        var platformJson = $$"""
            {
              "services": [
                {
                  "id": "cat-alpha",
                  "name": "Alpha",
                  "slug": "{{slug}}",
                  "service_type": "http",
                  "visibility": "public",
                  "auth_method": "none",
                  "service_category": "internal",
                  "requires_user_credential": false,
                  "is_active": true
                }
              ]
            }
            """;
        var scopeJson = $$"""
            {
              "keys": [
                {
                  "id": "us-valid",
                  "slug": "valid-runtime",
                  "is_active": true,
                  "service_type": "http",
                  "status": "active",
                  "credential_missing": false,
                  "credential_source": { "type": "personal" }
                },
                {
                  "id": "us-alpha",
                  "slug": "{{slug}}",
                  "is_active": true,
                  "service_type": "http",
                  "status": "active",
                  "credential_missing": false,
                  "credential_source": { "type": "personal" }
                }
              ]
            }
            """;

        var platform = NyxIdModelSourceInventoryParser.ParsePlatformCatalogServices(platformJson);
        var scope = NyxIdModelSourceInventoryParser.ParseScopeKeys(scopeJson);

        platform.Services.Should().ContainSingle().Which.Should().Match<NyxIdPlatformModelSourceService>(service =>
            service.Slug == slug &&
            !service.IsSelectable &&
            service.AvailabilityReason == NyxIdPlatformModelSourceAvailabilityReason.InvalidServiceSlug);
        scope.Services.Should().HaveCount(2);
        scope.Services.Single(service => service.UserServiceId == "us-valid").IsCallable.Should().BeTrue();
        scope.Services.Single(service => service.UserServiceId == "us-alpha")
            .Should().Match<NyxIdScopeModelSourceService>(service =>
                service.Slug == slug &&
                !service.IsCallable &&
                service.AvailabilityReason == NyxIdModelSourceAvailabilityReason.UnsupportedServiceSlug);
    }

    [Fact]
    public void ParsePlatformCatalogServices_WithDuplicateJsonProperty_ShouldRejectResponse()
    {
        const string json = """
            { "services": [], "services": [] }
            """;

        var act = () => NyxIdModelSourceInventoryParser.ParsePlatformCatalogServices(json);

        act.Should().Throw<InvalidDataException>();
    }
}
