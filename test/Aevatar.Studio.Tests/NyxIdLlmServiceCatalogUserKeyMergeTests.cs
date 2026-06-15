using Aevatar.AI.ToolProviders.NyxId.LlmCatalog;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Verifies how NyxID unified keys (<c>GET /api/v1/keys</c>) are merged into the LLM route catalog.
/// Regression context: <c>/api/v1/proxy/services</c> reports legacy connection state, so an active
/// unified key could previously be misclassified as "not allowed for this user".
/// </summary>
public sealed class NyxIdLlmServiceCatalogUserKeyMergeTests
{
    private static NyxIdLlmService NotConnectedProxyService(string slug = "chrono-llm") => new(
        UserServiceId: "svc-catalog-id",
        ServiceSlug: slug,
        DisplayName: "Chrono LLM",
        RouteValue: $"/api/v1/proxy/s/{slug}",
        DefaultModel: null,
        Models: [],
        Status: "not_connected",
        Source: NyxIdLlmProviderSource.ProxyService,
        Allowed: false,
        Description: "Shared LLM route");

    private static NyxIdLlmServicesResult ResultWith(params NyxIdLlmService[] services) =>
        new(services, null);

    [Fact]
    public void ActiveKeyReplacesNotConnectedProxyEntryAsSelectable()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(NotConnectedProxyService()),
            """
            {
              "keys": [
                {
                  "id": "key-1",
                  "label": "Chrono LLM",
                  "slug": "chrono-llm",
                  "endpoint_url": "https://llm.test/v1",
                  "status": "active",
                  "catalog_service_slug": "chrono-llm",
                  "catalog_service_name": "Chrono LLM",
                  "service_type": "http",
                  "is_active": true
                }
              ]
            }
            """);

        var service = merged.Services.Should().ContainSingle().Subject;
        service.Allowed.Should().BeTrue();
        service.Status.Should().Be("ready");
        service.UserServiceId.Should().Be("key-1");
        service.RouteValue.Should().Be("/api/v1/proxy/s/chrono-llm");
        service.Source.Should().Be(NyxIdLlmProviderSource.UserService);
    }

    [Fact]
    public void CatalogSlugWinsOverPerKeySlug_ForCatalogBackedKeys()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(NotConnectedProxyService()),
            """
            {
              "keys": [
                {
                  "id": "key-1",
                  "label": "Chrono LLM / personal key",
                  "slug": "chrono-llm-personal-key",
                  "endpoint_url": "https://llm.test/v1",
                  "status": "active",
                  "catalog_service_slug": "chrono-llm",
                  "catalog_service_name": "Chrono LLM",
                  "service_type": "http",
                  "is_active": true
                }
              ]
            }
            """);

        var service = merged.Services.Should().ContainSingle().Subject;
        service.ServiceSlug.Should().Be("chrono-llm");
        service.RouteValue.Should().Be("/api/v1/proxy/s/chrono-llm");
        service.UserServiceId.Should().Be("key-1");
    }

    [Fact]
    public void OrgViewerKeyDoesNotMakeServiceSelectable()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(NotConnectedProxyService()),
            """
            {
              "keys": [
                {
                  "id": "key-1",
                  "label": "Chrono LLM",
                  "slug": "chrono-llm",
                  "endpoint_url": "https://llm.test/v1",
                  "status": "active",
                  "catalog_service_slug": "chrono-llm",
                  "catalog_service_name": "Chrono LLM",
                  "service_type": "http",
                  "is_active": true,
                  "credential_source": {
                    "type": "org",
                    "org_id": "org-1",
                    "org_name": "Org",
                    "avatar_url": null,
                    "role": "viewer",
                    "allowed": false
                  }
                }
              ]
            }
            """);

        var service = merged.Services.Should().ContainSingle().Subject;
        service.Source.Should().Be(NyxIdLlmProviderSource.UserService);
        service.Status.Should().Be("ready");
        service.Allowed.Should().BeFalse();
    }

    [Fact]
    public void UserKeyRemainsAuthoritative_WhenProxyAlsoLooksReady()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeProxyRouteCandidates(
            NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
                ResultWith(),
                """
                {
                  "keys": [
                    {
                      "id": "key-1",
                      "label": "Chrono LLM",
                      "slug": "chrono-llm",
                      "endpoint_url": "https://llm.test/v1",
                      "status": "active",
                      "catalog_service_slug": "chrono-llm",
                      "catalog_service_name": "Chrono LLM",
                      "service_type": "http",
                      "is_active": true
                    }
                  ]
                }
                """),
            """
            {
              "services": [
                {
                  "id": "svc-catalog-id",
                  "slug": "chrono-llm",
                  "name": "Chrono LLM",
                  "description": "Shared LLM route",
                  "connected": true,
                  "requires_connection": false,
                  "proxy_url_slug": "https://nyx.test/api/v1/proxy/s/chrono-llm/{path}"
                }
              ]
            }
            """);

        var service = merged.Services.Should().ContainSingle().Subject;
        service.Source.Should().Be(NyxIdLlmProviderSource.UserService);
        service.UserServiceId.Should().Be("key-1");
    }

    [Fact]
    public void InactiveKeyDoesNotMakeServiceSelectable()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(NotConnectedProxyService()),
            """{"keys":[{"id":"key-1","slug":"chrono-llm","status":"active","is_active":false,"service_type":"http"}]}""");

        var service = merged.Services.Should().ContainSingle().Subject;
        service.Allowed.Should().BeFalse();
    }

    [Fact]
    public void PendingAuthKeyStatusPropagatesAsNotSelectable()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(),
            """{"keys":[{"id":"key-1","slug":"my-llm-endpoint","status":"pending_auth","is_active":true,"service_type":"http"}]}""");

        var service = merged.Services.Should().ContainSingle().Subject;
        service.Status.Should().Be("pending_auth");
        service.Allowed.Should().BeFalse();
    }

    [Fact]
    public void KeysWithoutLlmSignalsAreFilteredOut()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(),
            """
            {
              "keys": [
                {"id":"key-lark","slug":"api-lark-bot","label":"Lark Bot","status":"active","is_active":true,"service_type":"http"},
                {"id":"key-storage","slug":"chrono-storage-service","label":"Storage","status":"active","is_active":true,"service_type":"http"}
              ]
            }
            """);

        merged.Services.Should().BeEmpty();
    }

    [Fact]
    public void NonHttpKeysAreFilteredOut()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(),
            """{"keys":[{"id":"key-ssh","slug":"my-llm-box","label":"LLM box over ssh","status":"active","is_active":true,"service_type":"ssh"}]}""");

        merged.Services.Should().BeEmpty();
    }

    [Fact]
    public void StandaloneLlmKeyIsAppendedWhenCatalogHasNoMatchingEntry()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(NotConnectedProxyService("other-llm")),
            """{"keys":[{"id":"key-1","slug":"my-custom-llm","status":"active","is_active":true,"service_type":"http"}]}""");

        merged.Services.Should().HaveCount(2);
        merged.Services.Should().Contain(service =>
            service.ServiceSlug == "my-custom-llm" &&
            service.Allowed &&
            service.RouteValue == "/api/v1/proxy/s/my-custom-llm");
    }

    [Fact]
    public void BareArrayResponseIsTolerated()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(),
            """[{"id":"key-1","slug":"chrono-llm","status":"active","is_active":true,"service_type":"http"}]""");

        merged.Services.Should().ContainSingle(service => service.ServiceSlug == "chrono-llm");
    }

    [Fact]
    public void ErrorEnvelopeThrowsSoCallersCanDegradeSoftly()
    {
        var act = () => NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(),
            """{"error":true,"status":401,"body":"unauthorized"}""");

        act.Should().Throw<InvalidOperationException>();
    }
}
