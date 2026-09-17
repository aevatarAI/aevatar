using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Authentication.Abstractions;
using Aevatar.Mainnet.Host.Api.ModelCatalog;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.DependencyInjection;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetLLMModelCatalogEndpointsTests
{
    private const string ScopeId = "scope-alpha";

    [Fact]
    public async Task GetScope_CustomEmpty_ShouldNotFallBackToPlatformSources()
    {
        var query = new StubPolicyQueryPort
        {
            Scope = Snapshot(
                LLMModelCatalogPolicyOwner.ForScope(ScopeId),
                LLMModelCatalogPolicyMode.Custom,
                [],
                lastMutationId: "mutation-observed"),
            Platform = Snapshot(
                LLMModelCatalogPolicyOwner.Platform,
                LLMModelCatalogPolicyMode.Custom,
                [CatalogSource("catalog-platform", "platform-runtime")]),
        };
        await using var app = await CreateAppAsync(query: query);

        var response = await app.GetTestClient().GetAsync($"/api/scopes/{ScopeId}/llm-model-catalog");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("mode").GetString().Should().Be("custom_replace");
        json.RootElement.GetProperty("effectiveSource").GetString().Should().Be("scope");
        json.RootElement.GetProperty("effectiveSources").GetArrayLength().Should().Be(0);
        json.RootElement.GetProperty("lastMutationId").GetString().Should().Be("mutation-observed");
        query.PlatformReadCount.Should().Be(0);
    }

    [Fact]
    public async Task PutScope_ShouldPersistExactUserServiceIdentityAndReturnAcceptedReceipt()
    {
        var commands = new StubPolicyCommandPort();
        await using var app = await CreateAppAsync(commands: commands);
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/scopes/{ScopeId}/llm-model-catalog")
        {
            Content = JsonContent.Create(new
            {
                mode = "custom_replace",
                expectedStateVersion = 4,
                mutationId = "mutation-alpha",
                sources = new[]
                {
                    new
                    {
                        sourceId = "source-alpha",
                        displayName = "Chrono Runtime",
                        serviceSlugSnapshot = "chrono-runtime",
                        userServiceId = "user-chrono",
                        modelSelection = new
                        {
                            mode = "explicit_models",
                            modelIds = new[] { "gpt-5.5", "o3" },
                        },
                    },
                },
            }),
        };

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, body);
        commands.Commands.Should().ContainSingle();
        var command = commands.Commands[0];
        command.Owner.Should().Be(LLMModelCatalogPolicyOwner.ForScope(ScopeId));
        command.ExpectedStateVersion.Should().Be(4);
        command.Sources.Should().ContainSingle();
        command.Sources[0].SourceIdentity.Should().Be(
            new NyxIDUserServiceModelSourceIdentity("user-chrono"));
        command.Sources[0].ModelSelection.Should().BeEquivalentTo(
            new ExplicitLLMModels(["gpt-5.5", "o3"]));
    }

    [Fact]
    public async Task PutScope_WithNegativeExpectedVersion_ShouldRejectBeforeDispatch()
    {
        var commands = new StubPolicyCommandPort();
        await using var app = await CreateAppAsync(commands: commands);

        var response = await app.GetTestClient().PutAsJsonAsync(
            $"/api/scopes/{ScopeId}/llm-model-catalog",
            new
            {
                mode = "custom_replace",
                expectedStateVersion = -1,
                mutationId = "mutation-negative-version",
                sources = Array.Empty<object>(),
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("INVALID_STATE_VERSION");
        commands.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task PutScope_WithNonCanonicalServiceSlug_ShouldRejectBeforeDispatch()
    {
        var commands = new StubPolicyCommandPort();
        await using var app = await CreateAppAsync(commands: commands);

        var response = await app.GetTestClient().PutAsJsonAsync(
            $"/api/scopes/{ScopeId}/llm-model-catalog",
            new
            {
                mode = "custom_replace",
                expectedStateVersion = 0,
                mutationId = "mutation-invalid-slug",
                sources = new[]
                {
                    new
                    {
                        serviceSlugSnapshot = " chrono-runtime ",
                        userServiceId = "user-chrono",
                        modelSelection = new { mode = "explicit_models", modelIds = new[] { "model-a" } },
                    },
                },
            });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("INVALID_SERVICE_SLUG");
        commands.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task PutScope_WithDuplicateServiceSlug_ShouldRejectBeforeDispatch()
    {
        var commands = new StubPolicyCommandPort();
        await using var app = await CreateAppAsync(commands: commands);

        var response = await app.GetTestClient().PutAsJsonAsync(
            $"/api/scopes/{ScopeId}/llm-model-catalog",
            new
            {
                mode = "custom_replace",
                expectedStateVersion = 0,
                mutationId = "mutation-duplicate-slug",
                sources = new[]
                {
                    new
                    {
                        serviceSlugSnapshot = "chrono-runtime",
                        userServiceId = "user-chrono-a",
                        modelSelection = new { mode = "explicit_models", modelIds = new[] { "model-a" } },
                    },
                    new
                    {
                        serviceSlugSnapshot = "chrono-runtime",
                        userServiceId = "user-chrono-b",
                        modelSelection = new { mode = "explicit_models", modelIds = new[] { "model-b" } },
                    },
                },
            });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("DUPLICATE_SERVICE_SLUG");
        commands.Commands.Should().BeEmpty();
    }

    [Theory]
    [InlineData("null", "SOURCES_REQUIRED")]
    [InlineData("[null]", "SOURCE_REQUIRED")]
    public async Task PutScope_WithNullSourceShape_ShouldRejectBeforeDispatch(
        string sourcesJson,
        string expectedError)
    {
        var commands = new StubPolicyCommandPort();
        await using var app = await CreateAppAsync(commands: commands);
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/scopes/{ScopeId}/llm-model-catalog")
        {
            Content = new StringContent(
                $$"""
                {
                  "mode": "custom_replace",
                  "expectedStateVersion": 0,
                  "mutationId": "mutation-null-shape",
                  "sources": {{sourcesJson}}
                }
                """,
                Encoding.UTF8,
                "application/json"),
        };

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain(expectedError);
        commands.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task PutScope_WithOmittedSources_ShouldRejectBeforeDispatch()
    {
        var commands = new StubPolicyCommandPort();
        await using var app = await CreateAppAsync(commands: commands);

        var response = await app.GetTestClient().PutAsJsonAsync(
            $"/api/scopes/{ScopeId}/llm-model-catalog",
            new
            {
                mode = "custom_replace",
                expectedStateVersion = 0,
                mutationId = "mutation-omitted-scope-sources",
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("SOURCES_REQUIRED");
        commands.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task PutPlatform_WithOmittedSources_ShouldRejectBeforeDispatch()
    {
        var commands = new StubPolicyCommandPort();
        await using var app = await CreateAppAsync(commands: commands);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/admin/llm-model-catalog")
        {
            Content = JsonContent.Create(new
            {
                mode = "custom_replace",
                expectedStateVersion = 0,
                mutationId = "mutation-omitted-platform-sources",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("SOURCES_REQUIRED");
        commands.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Mutations_WithOmittedExpectedStateVersion_ShouldRejectBeforeDispatch()
    {
        var commands = new StubPolicyCommandPort();
        await using var app = await CreateAppAsync(commands: commands);
        var client = app.GetTestClient();

        var scopeResponse = await client.PutAsJsonAsync(
            $"/api/scopes/{ScopeId}/llm-model-catalog",
            new
            {
                mode = "custom_replace",
                mutationId = "mutation-omitted-scope-version",
                sources = Array.Empty<object>(),
            });
        using var platformRequest = new HttpRequestMessage(HttpMethod.Put, "/api/admin/llm-model-catalog")
        {
            Content = JsonContent.Create(new
            {
                mode = "custom_replace",
                mutationId = "mutation-omitted-platform-version",
                sources = Array.Empty<object>(),
            }),
        };
        platformRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
        var platformResponse = await client.SendAsync(platformRequest);
        using var resetRequest = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/scopes/{ScopeId}/llm-model-catalog")
        {
            Content = JsonContent.Create(new { mutationId = "mutation-omitted-reset-version" }),
        };
        var resetResponse = await client.SendAsync(resetRequest);

        foreach (var response in new[] { scopeResponse, platformResponse, resetResponse })
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync()).Should().Contain("EXPECTED_STATE_VERSION_REQUIRED");
        }
        commands.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task PutScope_WithOversizedDurableStrings_ShouldRejectBeforeDispatch()
    {
        var commands = new StubPolicyCommandPort();
        await using var app = await CreateAppAsync(commands: commands);
        var client = app.GetTestClient();

        var mutationResponse = await client.PutAsJsonAsync(
            $"/api/scopes/{ScopeId}/llm-model-catalog",
            new
            {
                mode = "custom_replace",
                expectedStateVersion = 0,
                mutationId = new string('m', LLMModelCatalogPolicyLimits.MaxMutationIdUtf8Bytes + 1),
                sources = Array.Empty<object>(),
            });
        var identityResponse = await client.PutAsJsonAsync(
            $"/api/scopes/{ScopeId}/llm-model-catalog",
            new
            {
                mode = "custom_replace",
                expectedStateVersion = 0,
                mutationId = "mutation-identity-limit",
                sources = new[]
                {
                    new
                    {
                        serviceSlugSnapshot = "alpha",
                        userServiceId = new string(
                            'u',
                            LLMModelCatalogPolicyLimits.MaxServiceIdentityUtf8Bytes + 1),
                        modelSelection = new
                        {
                            mode = "explicit_models",
                            modelIds = new[] { "model-a" },
                        },
                    },
                },
            });
        var modelResponse = await client.PutAsJsonAsync(
            $"/api/scopes/{ScopeId}/llm-model-catalog",
            new
            {
                mode = "custom_replace",
                expectedStateVersion = 0,
                mutationId = "mutation-model-limit",
                sources = new[]
                {
                    new
                    {
                        serviceSlugSnapshot = "alpha",
                        userServiceId = "user-alpha",
                        modelSelection = new
                        {
                            mode = "explicit_models",
                            modelIds = new[]
                            {
                                new string('x', LLMSelectionPolicy.MaxModelIdUtf8Bytes + 1),
                            },
                        },
                    },
                },
            });

        mutationResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await mutationResponse.Content.ReadAsStringAsync()).Should().Contain("MUTATION_ID_TOO_LONG");
        identityResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await identityResponse.Content.ReadAsStringAsync()).Should().Contain("SERVICE_ID_TOO_LONG");
        modelResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await modelResponse.Content.ReadAsStringAsync()).Should().Contain("MODEL_ID_TOO_LONG");
        commands.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task PutScope_WithTooManyExplicitModelIds_ShouldRejectBeforeDispatch()
    {
        var commands = new StubPolicyCommandPort();
        await using var app = await CreateAppAsync(commands: commands);

        var response = await app.GetTestClient().PutAsJsonAsync(
            $"/api/scopes/{ScopeId}/llm-model-catalog",
            new
            {
                mode = "custom_replace",
                expectedStateVersion = 0,
                mutationId = "mutation-model-count-limit",
                sources = new[]
                {
                    new
                    {
                        serviceSlugSnapshot = "alpha",
                        userServiceId = "user-alpha",
                        modelSelection = new
                        {
                            mode = "explicit_models",
                            modelIds = Enumerable.Range(
                                    0,
                                    LLMModelCatalogPolicyLimits.MaxExplicitModelsPerSource + 1)
                                .Select(static index => $"model-{index}")
                                .ToArray(),
                        },
                    },
                },
            });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("TOO_MANY_MODEL_IDS");
        commands.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task PutScope_WhenDispatchRejects_ShouldNotReturnAccepted()
    {
        var commands = new StubPolicyCommandPort { Accept = false };
        await using var app = await CreateAppAsync(commands: commands);

        var response = await app.GetTestClient().PutAsJsonAsync(
            $"/api/scopes/{ScopeId}/llm-model-catalog",
            new
            {
                mode = "custom_replace",
                expectedStateVersion = 0,
                mutationId = "mutation-dispatch-rejected",
                sources = Array.Empty<object>(),
            });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("MODEL_CATALOG_DISPATCH_REJECTED");
        commands.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task PutPlatform_ShouldRejectScopeOwnedUserServiceIdentity()
    {
        var commands = new StubPolicyCommandPort();
        await using var app = await CreateAppAsync(commands: commands);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/admin/llm-model-catalog")
        {
            Content = JsonContent.Create(new
            {
                mode = "custom_replace",
                expectedStateVersion = 0,
                mutationId = "mutation-platform",
                sources = new[]
                {
                    new
                    {
                        serviceSlugSnapshot = "chrono-runtime",
                        catalogServiceId = "catalog-chrono",
                        userServiceId = "user-admin-personal",
                        modelSelection = new { mode = "explicit_models", modelIds = new[] { "model-a" } },
                    },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("PLATFORM_USER_SERVICE_FORBIDDEN");
        commands.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task PutScope_ShouldRejectCatalogIdentityAlongsideUserServiceIdentity()
    {
        var commands = new StubPolicyCommandPort();
        await using var app = await CreateAppAsync(commands: commands);
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/scopes/{ScopeId}/llm-model-catalog")
        {
            Content = JsonContent.Create(new
            {
                mode = "custom_replace",
                expectedStateVersion = 0,
                mutationId = "mutation-dual-identity",
                sources = new[]
                {
                    new
                    {
                        serviceSlugSnapshot = "chrono-runtime",
                        catalogServiceId = "catalog-chrono",
                        userServiceId = "user-chrono",
                        modelSelection = new { mode = "explicit_models", modelIds = new[] { "model-a" } },
                    },
                },
            }),
        };

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("SCOPE_CATALOG_SERVICE_FORBIDDEN");
        commands.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteScope_ShouldDispatchExplicitInheritPlatformPolicy()
    {
        var commands = new StubPolicyCommandPort();
        await using var app = await CreateAppAsync(commands: commands);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/scopes/{ScopeId}/llm-model-catalog")
        {
            Content = JsonContent.Create(new
            {
                expectedStateVersion = 7,
                mutationId = "mutation-reset",
            }),
        };

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        commands.Commands.Should().ContainSingle();
        commands.Commands[0].Mode.Should().Be(LLMModelCatalogPolicyMode.InheritPlatform);
        commands.Commands[0].Sources.Should().BeEmpty();
        commands.Commands[0].ExpectedStateVersion.Should().Be(7);
    }

    [Fact]
    public async Task DeleteScope_WithNegativeExpectedVersion_ShouldRejectBeforeDispatch()
    {
        var commands = new StubPolicyCommandPort();
        await using var app = await CreateAppAsync(commands: commands);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/scopes/{ScopeId}/llm-model-catalog")
        {
            Content = JsonContent.Create(new
            {
                expectedStateVersion = -1,
                mutationId = "mutation-negative-reset",
            }),
        };

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("INVALID_STATE_VERSION");
        commands.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteScope_WithOversizedMutationId_ShouldRejectBeforeDispatch()
    {
        var commands = new StubPolicyCommandPort();
        await using var app = await CreateAppAsync(commands: commands);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/scopes/{ScopeId}/llm-model-catalog")
        {
            Content = JsonContent.Create(new
            {
                expectedStateVersion = 0,
                mutationId = new string('m', LLMModelCatalogPolicyLimits.MaxMutationIdUtf8Bytes + 1),
            }),
        };

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("MUTATION_ID_TOO_LONG");
        commands.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task GetScopeCandidates_ShouldReturnExactHttpInventoryWithoutNameFiltering()
    {
        var inventory = new StubInventoryPort
        {
            ScopeServices =
            [
                new NyxIdScopeModelSourceService(
                    "user-chrono",
                    "catalog-chrono",
                    "chrono-runtime",
                    "Chrono Runtime",
                    "Chrono Runtime",
                    true,
                    new NyxIdModelSourceServiceType(NyxIdModelSourceServiceTypeKind.HTTP, "http"),
                    new NyxIdPersonalCredentialSource(),
                    new NyxIdModelSourceCredentialStatus(
                        NyxIdModelSourceCredentialStatusKind.Active,
                        "active"),
                    CredentialMissing: false,
                    new NyxIdModelSourceConnectionStatus(
                        NyxIdModelSourceConnectionStatusKind.NotApplicable,
                        WireValue: null),
                    NodeId: null,
                    new NyxIdModelSourceNodeStatus(
                        NyxIdModelSourceNodeStatusKind.NotApplicable,
                        WireValue: null)),
            ],
        };
        await using var app = await CreateAppAsync(inventory: inventory);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/scopes/{ScopeId}/llm-model-catalog/candidates");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "scope-token");

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("user-chrono");
        body.Should().Contain("chrono-runtime");
        body.Should().Contain("\"serviceType\":\"http\"");
        body.Should().Contain("\"isCallable\":true");
        body.Should().Contain("\"availabilityReason\":\"available\"");
        inventory.LastScopeBearer.Should().Be("scope-token");
        inventory.LastPlatformBearer.Should().BeNull();
    }

    [Fact]
    public async Task GetScopeCandidateModels_ShouldResolveExactIdentityAndUseAuthoritativeSlug()
    {
        var inventory = new StubInventoryPort
        {
            ScopeServices = [ScopeService("user-chrono", "catalog-chrono", "chrono-llm")],
        };
        var discovery = new StubModelDiscoveryPort
        {
            ScopeModels = new NyxIdDiscoveredModels(["gpt-5.4-mini", "gpt-5.5"], "gpt-5.5"),
        };
        await using var app = await CreateAppAsync(inventory: inventory, modelDiscovery: discovery);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/scopes/{ScopeId}/llm-model-catalog/candidates/user-chrono/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "scope-token");

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("sourceIdentity").GetString().Should().Be("user-chrono");
        json.RootElement.GetProperty("serviceSlug").GetString().Should().Be("chrono-llm");
        json.RootElement.GetProperty("modelIds").EnumerateArray()
            .Select(static item => item.GetString()).Should().Equal("gpt-5.4-mini", "gpt-5.5");
        json.RootElement.GetProperty("defaultModelId").GetString().Should().Be("gpt-5.5");
        discovery.ScopeRequests.Should().ContainSingle().Which.Should().Be(
            ("scope-token", "chrono-llm", "user-chrono"));
        discovery.PlatformRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetScopeCandidateModels_WhenExactCandidateIsMissing_ShouldNotProbeProxy()
    {
        var discovery = new StubModelDiscoveryPort();
        await using var app = await CreateAppAsync(
            inventory: new StubInventoryPort
            {
                ScopeServices = [ScopeService("user-other", "catalog-chrono", "chrono-llm")],
            },
            modelDiscovery: discovery);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/scopes/{ScopeId}/llm-model-catalog/candidates/user-forged/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "scope-token");

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        body.Should().Contain("NYXID_MODEL_SOURCE_NOT_FOUND");
        discovery.ScopeRequests.Should().BeEmpty();
        discovery.PlatformRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(NyxIdModelDiscoveryFailureKind.UpstreamRejected, HttpStatusCode.ServiceUnavailable)]
    [InlineData(NyxIdModelDiscoveryFailureKind.ResponseInvalid, HttpStatusCode.ServiceUnavailable)]
    public async Task GetScopeCandidateModels_WhenDiscoveryFails_ShouldMapTypedFailure(
        NyxIdModelDiscoveryFailureKind failureKind,
        HttpStatusCode expectedStatus)
    {
        var discovery = new StubModelDiscoveryPort
        {
            ScopeFailure = new NyxIdModelDiscoveryException(
                failureKind,
                HttpStatusCode.BadGateway,
                "safe discovery failure"),
        };
        await using var app = await CreateAppAsync(
            inventory: new StubInventoryPort
            {
                ScopeServices = [ScopeService("user-chrono", "catalog-chrono", "chrono-llm")],
            },
            modelDiscovery: discovery);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/scopes/{ScopeId}/llm-model-catalog/candidates/user-chrono/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "scope-token");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task GetScopeCandidates_WithUnsupportedTransportOrSlug_ShouldRemainVisibleButUnavailable()
    {
        var inventory = new StubInventoryPort
        {
            ScopeServices =
            [
                new NyxIdScopeModelSourceService(
                    "user-stale",
                    "catalog-missing",
                    "custom-runtime",
                    "Custom Runtime",
                    "Historical Catalog Name",
                    true,
                    new NyxIdModelSourceServiceType(NyxIdModelSourceServiceTypeKind.Unknown, null),
                    new NyxIdPersonalCredentialSource(),
                    new NyxIdModelSourceCredentialStatus(
                        NyxIdModelSourceCredentialStatusKind.Active,
                        "active"),
                    CredentialMissing: false,
                    new NyxIdModelSourceConnectionStatus(
                        NyxIdModelSourceConnectionStatusKind.NotApplicable,
                        WireValue: null),
                    NodeId: null,
                    new NyxIdModelSourceNodeStatus(
                        NyxIdModelSourceNodeStatusKind.NotApplicable,
                        WireValue: null)),
                new NyxIdScopeModelSourceService(
                    "user-incompatible-slug",
                    "catalog-incompatible-slug",
                    "_ssh_legacy",
                    "Legacy SSH Route",
                    "Legacy SSH Route",
                    true,
                    new NyxIdModelSourceServiceType(NyxIdModelSourceServiceTypeKind.HTTP, "http"),
                    new NyxIdPersonalCredentialSource(),
                    new NyxIdModelSourceCredentialStatus(
                        NyxIdModelSourceCredentialStatusKind.Active,
                        "active"),
                    CredentialMissing: false,
                    new NyxIdModelSourceConnectionStatus(
                        NyxIdModelSourceConnectionStatusKind.NotApplicable,
                        WireValue: null),
                    NodeId: null,
                    new NyxIdModelSourceNodeStatus(
                        NyxIdModelSourceNodeStatusKind.NotApplicable,
                        WireValue: null)),
            ],
        };
        await using var app = await CreateAppAsync(inventory: inventory);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/scopes/{ScopeId}/llm-model-catalog/candidates");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "scope-token");

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var services = json.RootElement.GetProperty("services").EnumerateArray().ToArray();
        var unsupportedTransport = services.Single(service =>
            service.GetProperty("userServiceId").GetString() == "user-stale");
        unsupportedTransport.GetProperty("serviceType").GetString().Should().Be("unknown");
        unsupportedTransport.GetProperty("isCallable").GetBoolean().Should().BeFalse();
        unsupportedTransport.GetProperty("availabilityReason").GetString().Should().Be("unsupported_service_type");
        services.Single(service =>
                service.GetProperty("userServiceId").GetString() == "user-incompatible-slug")
            .GetProperty("availabilityReason").GetString().Should().Be("unsupported_service_slug");
    }

    [Theory]
    [InlineData(
        NyxIdModelSourceInventoryFailureKind.AuthenticationRejected,
        HttpStatusCode.Unauthorized,
        "NYXID_AUTHENTICATION_REJECTED")]
    [InlineData(
        NyxIdModelSourceInventoryFailureKind.Forbidden,
        HttpStatusCode.Forbidden,
        "NYXID_INVENTORY_FORBIDDEN")]
    [InlineData(
        NyxIdModelSourceInventoryFailureKind.Unavailable,
        HttpStatusCode.ServiceUnavailable,
        "NYXID_INVENTORY_UNAVAILABLE")]
    public async Task GetScopeCandidates_WhenInventoryFails_ShouldMapTypedFailure(
        NyxIdModelSourceInventoryFailureKind failureKind,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        var inventory = new StubInventoryPort
        {
            ScopeFailure = new NyxIdModelSourceInventoryException(
                failureKind,
                failureKind switch
                {
                    NyxIdModelSourceInventoryFailureKind.AuthenticationRejected =>
                        HttpStatusCode.Unauthorized,
                    NyxIdModelSourceInventoryFailureKind.Forbidden => HttpStatusCode.Forbidden,
                    _ => HttpStatusCode.InternalServerError,
                },
                "safe inventory failure"),
        };
        await using var app = await CreateAppAsync(inventory: inventory);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/scopes/{ScopeId}/llm-model-catalog/candidates");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "scope-token");

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(expectedStatus, body);
        body.Should().Contain(expectedCode);
    }

    [Fact]
    public async Task GetPlatformCandidates_ShouldReturnCompleteInventoryWithTypedSelectability()
    {
        var inventory = new StubInventoryPort
        {
            PlatformServices =
            [
                PlatformService("catalog-public", "public-runtime", NyxIdCatalogServiceVisibilityKind.Public),
                PlatformService("catalog-private", "private-runtime", NyxIdCatalogServiceVisibilityKind.Private),
                PlatformService(
                    "catalog-inactive",
                    "inactive-runtime",
                    NyxIdCatalogServiceVisibilityKind.Public,
                    isActive: false),
                PlatformService(
                    "catalog-ssh",
                    "ssh-runtime",
                    NyxIdCatalogServiceVisibilityKind.Public,
                    serviceType: new NyxIdModelSourceServiceType(
                        NyxIdModelSourceServiceTypeKind.SSH,
                        "ssh")),
                PlatformService(
                    "catalog-incompatible-slug",
                    "_ssh_legacy",
                    NyxIdCatalogServiceVisibilityKind.Public),
                PlatformService(
                    "catalog-provider",
                    "provider-runtime",
                    NyxIdCatalogServiceVisibilityKind.Public,
                    serviceCategory: new NyxIdCatalogServiceCategory(
                        NyxIdCatalogServiceCategoryKind.Provider,
                        "provider")),
                PlatformService(
                    "catalog-personal-credential",
                    "personal-runtime",
                    NyxIdCatalogServiceVisibilityKind.Public,
                    requiresUserCredential: true),
                PlatformService(
                    "catalog-token-exchange",
                    "exchange-runtime",
                    NyxIdCatalogServiceVisibilityKind.Public,
                    authMethod: new NyxIdCatalogServiceAuthMethod(
                        NyxIdCatalogServiceAuthMethodKind.TokenExchange,
                        "token_exchange")),
            ],
        };
        await using var app = await CreateAppAsync(inventory: inventory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/llm-model-catalog/candidates");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var services = json.RootElement.GetProperty("services").EnumerateArray().ToArray();
        services.Should().HaveCount(8);
        services.Single(service => service.GetProperty("catalogServiceId").GetString() == "catalog-public")
            .GetProperty("isSelectable").GetBoolean().Should().BeTrue();
        services.Single(service => service.GetProperty("catalogServiceId").GetString() == "catalog-private")
            .GetProperty("availabilityReason").GetString().Should().Be("not_public");
        services.Single(service => service.GetProperty("catalogServiceId").GetString() == "catalog-inactive")
            .GetProperty("availabilityReason").GetString().Should().Be("service_inactive");
        services.Single(service => service.GetProperty("catalogServiceId").GetString() == "catalog-ssh")
            .GetProperty("availabilityReason").GetString().Should().Be("unsupported_service_type");
        services.Single(service =>
                service.GetProperty("catalogServiceId").GetString() == "catalog-incompatible-slug")
            .GetProperty("availabilityReason").GetString().Should().Be("invalid_service_slug");
        services.Single(service => service.GetProperty("catalogServiceId").GetString() == "catalog-provider")
            .GetProperty("availabilityReason").GetString().Should().Be("provider_service");
        services.Single(service =>
                service.GetProperty("catalogServiceId").GetString() == "catalog-personal-credential")
            .GetProperty("availabilityReason").GetString().Should().Be("user_credential_required");
        var tokenExchange = services.Single(service =>
            service.GetProperty("catalogServiceId").GetString() == "catalog-token-exchange");
        tokenExchange.GetProperty("availabilityReason").GetString().Should().Be("token_exchange_unsupported");
        tokenExchange.GetProperty("authMethod").GetString().Should().Be("token_exchange");
        tokenExchange.GetProperty("serviceCategory").GetString().Should().Be("internal");
        tokenExchange.GetProperty("requiresUserCredential").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetPlatformCandidateModels_ShouldRequireAdminAndUseExactCatalogIdentity()
    {
        var inventory = new StubInventoryPort
        {
            PlatformServices =
            [
                PlatformService(
                    "catalog-chrono-public",
                    "chrono-llm-public",
                    NyxIdCatalogServiceVisibilityKind.Public),
            ],
        };
        var discovery = new StubModelDiscoveryPort
        {
            PlatformModels = new NyxIdDiscoveredModels(["gpt-5.4", "gpt-5.5"], null),
        };
        await using var app = await CreateAppAsync(inventory: inventory, modelDiscovery: discovery);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/admin/llm-model-catalog/candidates/catalog-chrono-public/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("chrono-llm-public");
        discovery.PlatformRequests.Should().ContainSingle().Which.Should().Be(
            ("admin-token", "catalog-chrono-public"));
        inventory.LastPlatformBearer.Should().Be("admin-token");
    }

    [Fact]
    public async Task GetPlatform_WhenCallerIsNotElevated_ShouldReturnForbidden()
    {
        await using var app = await CreateAppAsync(authorizer: new DeniedAuthorizer());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/llm-model-catalog");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "user-token");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static LLMModelCatalogPolicySnapshot Snapshot(
        LLMModelCatalogPolicyOwner owner,
        LLMModelCatalogPolicyMode mode,
        IReadOnlyList<LLMModelCatalogPolicySource> sources,
        string? lastMutationId = null) =>
        new(
            owner,
            mode,
            sources,
            4,
            DateTimeOffset.Parse("2026-08-15T00:00:00Z"),
            lastMutationId);

    private static LLMModelCatalogPolicySource CatalogSource(string id, string slug) =>
        new(new NyxIDCatalogServiceModelSourceIdentity(id), slug, new ExplicitLLMModels(["model-a"]));

    private static NyxIdPlatformModelSourceService PlatformService(
        string id,
        string slug,
        NyxIdCatalogServiceVisibilityKind visibility,
        bool isActive = true,
        NyxIdModelSourceServiceType? serviceType = null,
        NyxIdCatalogServiceAuthMethod? authMethod = null,
        NyxIdCatalogServiceCategory? serviceCategory = null,
        bool requiresUserCredential = false) =>
        new(
            id,
            slug,
            slug,
            isActive,
            serviceType ?? new NyxIdModelSourceServiceType(NyxIdModelSourceServiceTypeKind.HTTP, "http"),
            new NyxIdCatalogServiceVisibility(visibility, visibility.ToString().ToLowerInvariant()),
            authMethod ?? new NyxIdCatalogServiceAuthMethod(NyxIdCatalogServiceAuthMethodKind.None, "none"),
            serviceCategory ?? new NyxIdCatalogServiceCategory(
                NyxIdCatalogServiceCategoryKind.Internal,
                "internal"),
            requiresUserCredential);

    private static NyxIdScopeModelSourceService ScopeService(
        string userServiceId,
        string catalogServiceId,
        string slug) =>
        new(
            userServiceId,
            catalogServiceId,
            slug,
            slug,
            slug,
            true,
            new NyxIdModelSourceServiceType(NyxIdModelSourceServiceTypeKind.HTTP, "http"),
            new NyxIdPersonalCredentialSource(),
            new NyxIdModelSourceCredentialStatus(NyxIdModelSourceCredentialStatusKind.Active, "active"),
            CredentialMissing: false,
            new NyxIdModelSourceConnectionStatus(
                NyxIdModelSourceConnectionStatusKind.NotApplicable,
                WireValue: null),
            NodeId: null,
            new NyxIdModelSourceNodeStatus(
                NyxIdModelSourceNodeStatusKind.NotApplicable,
                WireValue: null));

    private static async Task<WebApplication> CreateAppAsync(
        StubPolicyQueryPort? query = null,
        StubPolicyCommandPort? commands = null,
        StubInventoryPort? inventory = null,
        StubModelDiscoveryPort? modelDiscovery = null,
        IPlatformAdminAuthorizer? authorizer = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Host.UseDefaultServiceProvider(options => options.ValidateOnBuild = false);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aevatar:Authentication:Enabled"] = "false",
        });
        builder.Services.AddSingleton<ILLMModelCatalogPolicyQueryPort>(query ?? new StubPolicyQueryPort());
        builder.Services.AddSingleton<ILLMModelCatalogPolicyCommandPort>(commands ?? new StubPolicyCommandPort());
        builder.Services.AddSingleton<INyxIdModelSourceInventoryPort>(inventory ?? new StubInventoryPort());
        builder.Services.AddSingleton<INyxIdModelDiscoveryPort>(
            modelDiscovery ?? new StubModelDiscoveryPort());
        builder.Services.AddSingleton<IPlatformAdminAuthorizer>(authorizer ?? new ElevatedAuthorizer());
        builder.Services.AddStudioApplication();

        var app = builder.Build();
        app.MapLLMModelCatalogEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class StubPolicyQueryPort : ILLMModelCatalogPolicyQueryPort
    {
        public LLMModelCatalogPolicySnapshot? Scope { get; init; }
        public LLMModelCatalogPolicySnapshot? Platform { get; init; }
        public int PlatformReadCount { get; private set; }

        public Task<LLMModelCatalogPolicySnapshot?> GetAsync(
            LLMModelCatalogPolicyOwner owner,
            CancellationToken ct = default)
        {
            if (owner.Kind == LLMModelCatalogPolicyOwnerKind.Platform)
                PlatformReadCount++;
            return Task.FromResult(owner.Kind == LLMModelCatalogPolicyOwnerKind.Platform ? Platform : Scope);
        }
    }

    private sealed class StubPolicyCommandPort : ILLMModelCatalogPolicyCommandPort
    {
        public bool Accept { get; init; } = true;
        public List<ReplaceLLMModelCatalogPolicy> Commands { get; } = [];

        public Task<UserConfigSaveReceipt> ReplaceAsync(
            ReplaceLLMModelCatalogPolicy command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(new UserConfigSaveReceipt(
                Accept,
                "command-1",
                Accept
                    ? UserConfigCommandAckStage.Accepted
                    : UserConfigCommandAckStage.AdmissionRejected,
                "actor-1",
                "correlation-1",
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class StubInventoryPort : INyxIdModelSourceInventoryPort
    {
        public IReadOnlyList<NyxIdScopeModelSourceService> ScopeServices { get; init; } = [];
        public IReadOnlyList<NyxIdPlatformModelSourceService> PlatformServices { get; init; } = [];
        public Exception? ScopeFailure { get; init; }
        public Exception? PlatformFailure { get; init; }
        public string? LastScopeBearer { get; private set; }
        public string? LastPlatformBearer { get; private set; }

        public Task<NyxIdPlatformModelSourceInventory> GetPlatformCatalogServicesAsync(
            string bearerToken,
            CancellationToken ct)
        {
            LastPlatformBearer = bearerToken;
            if (PlatformFailure is not null)
                return Task.FromException<NyxIdPlatformModelSourceInventory>(PlatformFailure);
            return Task.FromResult(new NyxIdPlatformModelSourceInventory(PlatformServices));
        }

        public Task<NyxIdScopeModelSourceInventory> GetScopeModelSourcesAsync(
            string bearerToken,
            CancellationToken ct)
        {
            LastScopeBearer = bearerToken;
            if (ScopeFailure is not null)
                return Task.FromException<NyxIdScopeModelSourceInventory>(ScopeFailure);
            return Task.FromResult(new NyxIdScopeModelSourceInventory(ScopeServices));
        }
    }

    private sealed class StubModelDiscoveryPort : INyxIdModelDiscoveryPort
    {
        public NyxIdDiscoveredModels ScopeModels { get; init; } = new([], null);
        public NyxIdDiscoveredModels PlatformModels { get; init; } = new([], null);
        public Exception? ScopeFailure { get; init; }
        public Exception? PlatformFailure { get; init; }
        public List<(string BearerToken, string ServiceSlug, string UserServiceId)> ScopeRequests { get; } = [];
        public List<(string BearerToken, string CatalogServiceId)> PlatformRequests { get; } = [];

        public Task<NyxIdDiscoveredModels> GetScopeModelsAsync(
            string bearerToken,
            string serviceSlug,
            string userServiceId,
            CancellationToken ct)
        {
            ScopeRequests.Add((bearerToken, serviceSlug, userServiceId));
            return ScopeFailure is null
                ? Task.FromResult(ScopeModels)
                : Task.FromException<NyxIdDiscoveredModels>(ScopeFailure);
        }

        public Task<NyxIdDiscoveredModels> GetPlatformModelsAsync(
            string bearerToken,
            string catalogServiceId,
            CancellationToken ct)
        {
            PlatformRequests.Add((bearerToken, catalogServiceId));
            return PlatformFailure is null
                ? Task.FromResult(PlatformModels)
                : Task.FromException<NyxIdDiscoveredModels>(PlatformFailure);
        }
    }

    private sealed class ElevatedAuthorizer : IPlatformAdminAuthorizer
    {
        public Task<PlatformCaller> ResolveCallerAsync(
            string bearerToken,
            CancellationToken ct = default) =>
            Task.FromResult(new PlatformCaller(
                true,
                "admin",
                "admin@example.test",
                "admin-user",
                PlatformAdminGrantSources.AllowedUserId));
    }

    private sealed class DeniedAuthorizer : IPlatformAdminAuthorizer
    {
        public Task<PlatformCaller> ResolveCallerAsync(
            string bearerToken,
            CancellationToken ct = default) =>
            Task.FromResult(new PlatformCaller(
                false,
                "user",
                "user@example.test",
                "user-id",
                string.Empty));
    }
}
