using System.Security.Claims;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aevatar.Authentication.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Governance.Hosting.Endpoints;
using Aevatar.GAgentService.Governance.Hosting.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class GovernanceEndpointsTests
{
    [Fact]
    public async Task BindingEndpoints_WhenAuthenticatedBoundServiceOmitsIdentity_ShouldUseClaimIdentityForOwnerAndBoundService()
    {
        await using var host = await GovernanceEndpointTestHost.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/services/orders/bindings")
        {
            Content = JsonContent.Create(new
            {
                tenantId = "tenant-claim",
                appId = "app-claim",
                @namespace = "ns-claim",
                bindingId = "binding-a",
                displayName = "Dependency",
                bindingKind = "service",
                service = new
                {
                    serviceId = "dependency",
                    endpointId = "run",
                },
            }),
        };
        request.Headers.Add("X-Test-Authenticated", "true");
        request.Headers.Add("X-Test-Tenant-Id", "tenant-claim");
        request.Headers.Add("X-Test-App-Id", "app-claim");
        request.Headers.Add("X-Test-Namespace", "ns-claim");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateBindingCommand!.Spec.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "tenant-claim",
            AppId = "app-claim",
            Namespace = "ns-claim",
            ServiceId = "orders",
        });
        host.CommandPort.CreateBindingCommand.Spec.ServiceRef!.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "tenant-claim",
            AppId = "app-claim",
            Namespace = "ns-claim",
            ServiceId = "dependency",
        });
    }

    [Fact]
    public async Task BindingEndpoints_WhenAuthenticatedOwnerIdentityConflictsWithClaims_ShouldReturnBadRequest()
    {
        await using var host = await GovernanceEndpointTestHost.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/services/orders/bindings")
        {
            Content = JsonContent.Create(new
            {
                tenantId = "spoof-tenant",
                appId = "spoof-app",
                @namespace = "spoof-ns",
                bindingId = "binding-a",
                displayName = "Dependency",
                bindingKind = "service",
                service = new
                {
                    serviceId = "dependency",
                    endpointId = "run",
                },
            }),
        };
        AddAuthenticatedClaims(request);

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be("OWNER_SERVICE_IDENTITY_CONFLICT");
        host.CommandPort.CreateBindingCommand.Should().BeNull();
    }

    [Fact]
    public async Task BindingEndpoints_WhenAuthenticatedOwnerIdentityConflictsOnUpdate_ShouldReturnBadRequest()
    {
        await using var host = await GovernanceEndpointTestHost.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/services/orders/bindings/binding-a")
        {
            Content = JsonContent.Create(new
            {
                tenantId = "spoof-tenant",
                appId = "spoof-app",
                @namespace = "spoof-ns",
                bindingId = "binding-a",
                displayName = "Dependency",
                bindingKind = "service",
                service = new
                {
                    serviceId = "dependency",
                    endpointId = "run",
                },
            }),
        };
        AddAuthenticatedClaims(request);

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be("OWNER_SERVICE_IDENTITY_CONFLICT");
        host.CommandPort.UpdateBindingCommand.Should().BeNull();
    }

    [Fact]
    public async Task BindingEndpoints_WhenAuthenticatedOwnerIdentityConflictsOnRetire_ShouldReturnBadRequest()
    {
        await using var host = await GovernanceEndpointTestHost.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/services/orders/bindings/binding-a:retire")
        {
            Content = JsonContent.Create(new
            {
                tenantId = "spoof-tenant",
                appId = "spoof-app",
                @namespace = "spoof-ns",
            }),
        };
        AddAuthenticatedClaims(request);

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be("OWNER_SERVICE_IDENTITY_CONFLICT");
        host.CommandPort.RetireBindingCommand.Should().BeNull();
    }

    [Fact]
    public async Task BindingEndpoints_WhenAuthenticatedOwnerIdentityConflictsWithQuery_ShouldReturnBadRequest()
    {
        await using var host = await GovernanceEndpointTestHost.StartAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/services/orders/bindings?tenantId=spoof-tenant&appId=spoof-app&namespace=spoof-ns");
        AddAuthenticatedClaims(request);

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be("OWNER_SERVICE_IDENTITY_CONFLICT");
        host.QueryPort.LastBindingsIdentity.Should().BeNull();
    }

    [Fact]
    public async Task BindingEndpoints_WhenAuthenticatedBoundServiceIdentityConflictsWithClaims_ShouldReturnBadRequest()
    {
        await using var host = await GovernanceEndpointTestHost.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/services/orders/bindings")
        {
            Content = JsonContent.Create(new
            {
                tenantId = "tenant-claim",
                appId = "app-claim",
                @namespace = "ns-claim",
                bindingId = "binding-a",
                displayName = "Dependency",
                bindingKind = "service",
                service = new
                {
                    tenantId = "other-tenant",
                    appId = "other-app",
                    @namespace = "other-ns",
                    serviceId = "dependency",
                    endpointId = "run",
                },
            }),
        };
        AddAuthenticatedClaims(request);

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be("BOUND_SERVICE_IDENTITY_CONFLICT");
        host.CommandPort.CreateBindingCommand.Should().BeNull();
    }

    private static void AddAuthenticatedClaims(HttpRequestMessage request)
    {
        request.Headers.Add("X-Test-Authenticated", "true");
        request.Headers.Add("X-Test-Tenant-Id", "tenant-claim");
        request.Headers.Add("X-Test-App-Id", "app-claim");
        request.Headers.Add("X-Test-Namespace", "ns-claim");
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Fact]
    public async Task BindingEndpoints_ShouldMapServiceConnectorAndSecretBindings()
    {
        await using var host = await GovernanceEndpointTestHost.StartAsync();
        host.QueryPort.BindingsResult = new ServiceBindingCatalogSnapshot(
            "tenant:app:ns:orders",
            [
                new ServiceBindingSnapshot(
                    "binding-a",
                    "Binding A",
                    ServiceBindingKind.Service,
                    ["policy-a"],
                    false,
                    new BoundServiceReferenceSnapshot(
                        new ServiceIdentity
                        {
                            TenantId = "tenant",
                            AppId = "app",
                            Namespace = "ns",
                            ServiceId = "dependency",
                        },
                        "run"),
                    null,
                    null),
            ],
            DateTimeOffset.Parse("2026-03-14T00:00:00+00:00"));

        var createServiceResponse = await host.Client.PostAsJsonAsync("/api/services/orders/bindings", new
        {
            tenantId = " tenant ",
            appId = " app ",
            @namespace = " ns ",
            bindingId = "binding-a",
            displayName = "Dependency",
            bindingKind = "service",
            service = new
            {
                tenantId = " dep-tenant ",
                appId = " dep-app ",
                @namespace = " dep-ns ",
                serviceId = " dependency ",
                endpointId = " run ",
            },
        });
        var updateConnectorResponse = await host.Client.PutAsJsonAsync("/api/services/orders/bindings/binding-b", new
        {
            tenantId = "tenant",
            appId = "app",
            @namespace = "ns",
            bindingId = "ignored",
            displayName = "Connector",
            bindingKind = "connector",
            connector = new
            {
                connectorType = "mcp",
                connectorId = "connector-1",
            },
            policyIds = new[] { "policy-b" },
        });
        var createSecretResponse = await host.Client.PostAsJsonAsync("/api/services/orders/bindings", new
        {
            tenantId = "tenant",
            appId = "app",
            @namespace = "ns",
            bindingId = "binding-secret",
            displayName = "Secret",
            bindingKind = "secret",
            secret = new
            {
                secretName = "api-key",
            },
        });
        var retireResponse = await host.Client.PostAsJsonAsync("/api/services/orders/bindings/binding-b:retire", new
        {
            tenantId = "tenant",
            appId = "app",
            @namespace = "ns",
        });
        var getResponse = await host.Client.GetFromJsonAsync<ServiceBindingCatalogSnapshot>("/api/services/orders/bindings?tenantId=tenant&appId=app&namespace=ns");

        createServiceResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        updateConnectorResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        createSecretResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        retireResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        getResponse.Should().NotBeNull();

        host.CommandPort.UpdateBindingCommand!.Spec.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "ns",
            ServiceId = "orders",
        });
        host.CommandPort.UpdateBindingCommand.Spec.BindingId.Should().Be("binding-b");
        host.CommandPort.UpdateBindingCommand.Spec.BindingKind.Should().Be(ServiceBindingKind.Connector);
        host.CommandPort.UpdateBindingCommand.Spec.ConnectorRef!.ConnectorType.Should().Be("mcp");
        host.CommandPort.UpdateBindingCommand.Spec.PolicyIds.Should().ContainSingle("policy-b");

        host.CommandPort.CreateBindingCommand!.Spec.BindingId.Should().Be("binding-secret");
        host.CommandPort.CreateBindingCommand.Spec.BindingKind.Should().Be(ServiceBindingKind.Secret);
        host.CommandPort.CreateBindingCommand.Spec.SecretRef!.SecretName.Should().Be("api-key");

        host.CommandPort.RetireBindingCommand!.Identity.ServiceId.Should().Be("orders");
        host.CommandPort.RetireBindingCommand.BindingId.Should().Be("binding-b");
        host.QueryPort.LastBindingsIdentity!.Namespace.Should().Be("ns");
    }

    [Fact]
    public async Task BindingEndpoints_WithUnsupportedBindingKind_ShouldFail()
    {
        await using var host = await GovernanceEndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/bindings", new
        {
            tenantId = "tenant",
            appId = "app",
            @namespace = "ns",
            bindingId = "binding-a",
            displayName = "Invalid",
            bindingKind = "bogus",
        });

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task EndpointCatalogAndPolicyEndpoints_ShouldMapGovernanceRequests()
    {
        await using var host = await GovernanceEndpointTestHost.StartAsync();
        host.QueryPort.EndpointCatalogResult = new ServiceEndpointCatalogSnapshot(
            "tenant:app:ns:orders",
            [
                new ServiceEndpointExposureSnapshot(
                    "chat",
                    "Chat",
                    ServiceEndpointKind.Chat,
                    "type.googleapis.com/demo.ChatRequest",
                    "type.googleapis.com/demo.ChatResponse",
                    "chat",
                    ServiceEndpointExposureKind.Public,
                    ["policy-a"]),
            ],
            DateTimeOffset.Parse("2026-03-14T00:00:00+00:00"));
        host.QueryPort.PoliciesResult = new ServicePolicyCatalogSnapshot(
            "tenant:app:ns:orders",
            [
                new ServicePolicySnapshot(
                    "policy-a",
                    "Policy A",
                    ["binding-a"],
                    ["tenant/app/ns/caller"],
                    true,
                    false),
            ],
            DateTimeOffset.Parse("2026-03-14T00:00:00+00:00"));
        host.CapabilityViewReader.Result = new ActivationCapabilityView
        {
            Identity = new ServiceIdentity
            {
                TenantId = "tenant",
                AppId = "app",
                Namespace = "ns",
                ServiceId = "orders",
            },
            RevisionId = "rev-1",
        };

        var createCatalogResponse = await host.Client.PostAsJsonAsync("/api/services/orders/endpoint-catalog", new
        {
            tenantId = "tenant",
            appId = "app",
            @namespace = "ns",
            endpoints = new object[]
            {
                new
                {
                    endpointId = "chat",
                    displayName = "Chat",
                    kind = "chat",
                    requestTypeUrl = "type.googleapis.com/demo.ChatRequest",
                    responseTypeUrl = "type.googleapis.com/demo.ChatResponse",
                    description = "chat",
                    exposureKind = "public",
                    policyIds = new[] { "policy-a" },
                },
                new
                {
                    endpointId = "disabled",
                    displayName = "Disabled",
                    kind = "command",
                    requestTypeUrl = "type.googleapis.com/demo.Command",
                    responseTypeUrl = "",
                    description = "disabled",
                    exposureKind = "disabled",
                },
            },
        });
        var updateCatalogResponse = await host.Client.PutAsJsonAsync("/api/services/orders/endpoint-catalog", new
        {
            tenantId = "tenant",
            appId = "app",
            @namespace = "ns",
            endpoints = new object[]
            {
                new
                {
                    endpointId = "fallback",
                    displayName = "Fallback",
                    kind = "unexpected",
                    requestTypeUrl = "type.googleapis.com/demo.Command",
                    responseTypeUrl = "",
                    description = "fallback",
                    exposureKind = "unexpected",
                },
            },
        });
        var getCatalogResponse = await host.Client.GetFromJsonAsync<ServiceEndpointCatalogSnapshot>("/api/services/orders/endpoint-catalog?tenantId=tenant&appId=app&namespace=ns");

        var createPolicyResponse = await host.Client.PostAsJsonAsync("/api/services/orders/policies", new
        {
            tenantId = "tenant",
            appId = "app",
            @namespace = "ns",
            policyId = "policy-a",
            displayName = "Policy A",
            activationRequiredBindingIds = new[] { "binding-a" },
            invokeAllowedCallerServiceKeys = new[] { "tenant/app/ns/caller" },
            invokeRequiresActiveDeployment = true,
        });
        var updatePolicyResponse = await host.Client.PutAsJsonAsync("/api/services/orders/policies/policy-a", new
        {
            tenantId = "tenant",
            appId = "app",
            @namespace = "ns",
            policyId = "ignored",
            displayName = "Policy B",
            activationRequiredBindingIds = Array.Empty<string>(),
            invokeAllowedCallerServiceKeys = Array.Empty<string>(),
            invokeRequiresActiveDeployment = false,
        });
        var retirePolicyResponse = await host.Client.PostAsJsonAsync("/api/services/orders/policies/policy-a:retire", new
        {
            tenantId = "tenant",
            appId = "app",
            @namespace = "ns",
        });
        var getPoliciesResponse = await host.Client.GetFromJsonAsync<ServicePolicyCatalogSnapshot>("/api/services/orders/policies?tenantId=tenant&appId=app&namespace=ns");
        var activationResponse = await host.Client.GetFromJsonAsync<ActivationCapabilityView>("/api/services/orders:activation-capability?tenantId=tenant&appId=app&namespace=ns&revisionId=rev-1");

        createCatalogResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        updateCatalogResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        createPolicyResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        updatePolicyResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        retirePolicyResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        host.CommandPort.CreateEndpointCatalogCommand!.Spec.Endpoints.Should().Contain(x =>
            x.EndpointId == "chat" &&
            x.Kind == ServiceEndpointKind.Chat &&
            x.ExposureKind == ServiceEndpointExposureKind.Public);
        host.CommandPort.CreateEndpointCatalogCommand.Spec.Endpoints.Should().Contain(x =>
            x.EndpointId == "disabled" &&
            x.ExposureKind == ServiceEndpointExposureKind.Disabled);
        host.CommandPort.UpdateEndpointCatalogCommand!.Spec.Endpoints.Should().ContainSingle();
        host.CommandPort.UpdateEndpointCatalogCommand.Spec.Endpoints[0].Kind.Should().Be(ServiceEndpointKind.Command);
        host.CommandPort.UpdateEndpointCatalogCommand.Spec.Endpoints[0].ExposureKind.Should().Be(ServiceEndpointExposureKind.Internal);

        host.CommandPort.CreatePolicyCommand!.Spec.PolicyId.Should().Be("policy-a");
        host.CommandPort.CreatePolicyCommand.Spec.InvokeRequiresActiveDeployment.Should().BeTrue();
        host.CommandPort.UpdatePolicyCommand!.Spec.PolicyId.Should().Be("policy-a");
        host.CommandPort.UpdatePolicyCommand.Spec.DisplayName.Should().Be("Policy B");
        host.CommandPort.RetirePolicyCommand!.PolicyId.Should().Be("policy-a");

        getCatalogResponse.Should().NotBeNull();
        getPoliciesResponse.Should().NotBeNull();
        activationResponse.Should().NotBeNull();
        host.QueryPort.LastEndpointCatalogIdentity!.ServiceId.Should().Be("orders");
        host.QueryPort.LastPoliciesIdentity!.Namespace.Should().Be("ns");
        host.CapabilityViewReader.LastIdentity!.TenantId.Should().Be("tenant");
        host.CapabilityViewReader.LastRevisionId.Should().Be("rev-1");
    }

    [Fact]
    public async Task ActivationCapabilityEndpoint_WhenAuthenticatedIdentityConflictsWithQuery_ShouldUseClaimIdentity()
    {
        await using var host = await GovernanceEndpointTestHost.StartAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/services/orders:activation-capability?tenantId=spoof-tenant&appId=spoof-app&namespace=spoof-ns&revisionId=rev-1");
        request.Headers.Add("X-Test-Authenticated", "true");
        request.Headers.Add("X-Test-Tenant-Id", "tenant-claim");
        request.Headers.Add("X-Test-App-Id", "app-claim");
        request.Headers.Add("X-Test-Namespace", "ns-claim");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.CapabilityViewReader.LastIdentity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "tenant-claim",
            AppId = "app-claim",
            Namespace = "ns-claim",
            ServiceId = "orders",
        });
        host.CapabilityViewReader.LastRevisionId.Should().Be("rev-1");
    }

    [Fact]
    public async Task PolicyEndpoints_WhenAuthenticatedIdentityMissingClaims_ShouldReturnForbidden()
    {
        await using var host = await GovernanceEndpointTestHost.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/services/orders/policies")
        {
            Content = JsonContent.Create(new
            {
                tenantId = "tenant",
                appId = "app",
                @namespace = "ns",
                policyId = "policy-a",
                displayName = "Policy A",
                activationRequiredBindingIds = new[] { "binding-a" },
                invokeAllowedCallerServiceKeys = Array.Empty<string>(),
                invokeRequiresActiveDeployment = true,
            }),
        };
        request.Headers.Add("X-Test-Authenticated", "true");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        host.CommandPort.CreatePolicyCommand.Should().BeNull();
    }

    [Fact]
    public async Task BindingEndpoints_ShouldReturnNullBody_WhenNoBindingsExist()
    {
        await using var host = await GovernanceEndpointTestHost.StartAsync();
        // BindingsResult defaults to null

        var response = await host.Client.GetAsync("/api/services/orders/bindings?tenantId=t&appId=a&namespace=n");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("null");
    }

    [Fact]
    public async Task PolicyEndpoints_ShouldReturnNullBody_WhenNoPoliciesExist()
    {
        await using var host = await GovernanceEndpointTestHost.StartAsync();
        // PoliciesResult defaults to null

        var response = await host.Client.GetAsync("/api/services/orders/policies?tenantId=t&appId=a&namespace=n");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("null");
    }

    [Fact]
    public async Task EndpointCatalogEndpoints_ShouldReturnNullBody_WhenNoCatalogExists()
    {
        await using var host = await GovernanceEndpointTestHost.StartAsync();
        // EndpointCatalogResult defaults to null

        var response = await host.Client.GetAsync("/api/services/orders/endpoint-catalog?tenantId=t&appId=a&namespace=n");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("null");
    }

    [Fact]
    public async Task ActivationCapabilityEndpoint_ShouldReturnEmptyView_WhenNoViewConfigured()
    {
        await using var host = await GovernanceEndpointTestHost.StartAsync();
        // CapabilityViewReader.Result defaults to new ActivationCapabilityView()

        var result = await host.Client.GetFromJsonAsync<ActivationCapabilityView>("/api/services/orders:activation-capability?tenantId=t&appId=a&namespace=n");

        result.Should().NotBeNull();
        result!.RevisionId.Should().BeEmpty();
    }

    [Fact]
    public async Task PolicyEndpoints_ShouldCreateAndRetirePoliciesCorrectly()
    {
        await using var host = await GovernanceEndpointTestHost.StartAsync();
        host.QueryPort.PoliciesResult = new ServicePolicyCatalogSnapshot(
            "tenant:app:ns:orders",
            [
                new ServicePolicySnapshot("policy-1", "Test Policy", ["binding-1"], ["caller/key"], true, false),
            ],
            DateTimeOffset.UtcNow);

        var createResponse = await host.Client.PostAsJsonAsync("/api/services/orders/policies", new
        {
            tenantId = "tenant",
            appId = "app",
            @namespace = "ns",
            policyId = "policy-1",
            displayName = "Test Policy",
            activationRequiredBindingIds = new[] { "binding-1" },
            invokeAllowedCallerServiceKeys = new[] { "caller/key" },
            invokeRequiresActiveDeployment = true,
        });
        var retireResponse = await host.Client.PostAsJsonAsync("/api/services/orders/policies/policy-1:retire", new
        {
            tenantId = "tenant",
            appId = "app",
            @namespace = "ns",
        });
        var getResponse = await host.Client.GetAsync("/api/services/orders/policies?tenantId=tenant&appId=app&namespace=ns");

        createResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        retireResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        host.CommandPort.CreatePolicyCommand!.Spec.PolicyId.Should().Be("policy-1");
        host.CommandPort.CreatePolicyCommand.Spec.ActivationRequiredBindingIds.Should().Contain("binding-1");
        host.CommandPort.RetirePolicyCommand!.PolicyId.Should().Be("policy-1");
    }

    private sealed class GovernanceEndpointTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private GovernanceEndpointTestHost(
            WebApplication app,
            HttpClient client,
            RecordingServiceGovernanceCommandPort commandPort,
            RecordingServiceGovernanceQueryPort queryPort,
            RecordingActivationCapabilityViewReader capabilityViewReader)
        {
            _app = app;
            Client = client;
            CommandPort = commandPort;
            QueryPort = queryPort;
            CapabilityViewReader = capabilityViewReader;
        }

        public HttpClient Client { get; }

        public RecordingServiceGovernanceCommandPort CommandPort { get; }

        public RecordingServiceGovernanceQueryPort QueryPort { get; }

        public RecordingActivationCapabilityViewReader CapabilityViewReader { get; }

        public static async Task<GovernanceEndpointTestHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var commandPort = new RecordingServiceGovernanceCommandPort();
            var queryPort = new RecordingServiceGovernanceQueryPort();
            var capabilityViewReader = new RecordingActivationCapabilityViewReader();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<IServiceGovernanceCommandPort>(commandPort);
            builder.Services.AddSingleton<IServiceGovernanceQueryPort>(queryPort);
            builder.Services.AddSingleton<IActivationCapabilityViewReader>(capabilityViewReader);
            builder.Services.AddSingleton<IServiceIdentityContextResolver, DefaultServiceIdentityContextResolver>();

            var app = builder.Build();
            app.Use(async (http, next) =>
            {
                if (http.Request.Headers.TryGetValue("X-Test-Authenticated", out var authenticatedValues) &&
                    bool.TryParse(authenticatedValues, out var authenticated) &&
                    authenticated)
                {
                    var claims = new List<Claim>();
                    AddClaims(http, "X-Test-Tenant-Id", AevatarStandardClaimTypes.TenantId, claims);
                    AddClaims(http, "X-Test-App-Id", AevatarStandardClaimTypes.AppId, claims);
                    AddClaims(http, "X-Test-Namespace", AevatarStandardClaimTypes.Namespace, claims);
                    http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
                }

                await next();
            });
            app.MapGroup("/api/services").MapGAgentServiceGovernanceEndpoints();
            await app.StartAsync();

            var addressFeature = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Server addresses are unavailable.");
            var client = new HttpClient
            {
                BaseAddress = new Uri(addressFeature.Addresses.Single()),
            };

            return new GovernanceEndpointTestHost(app, client, commandPort, queryPort, capabilityViewReader);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }

        private static void AddClaims(HttpContext http, string headerName, string claimType, ICollection<Claim> claims)
        {
            if (!http.Request.Headers.TryGetValue(headerName, out var values))
                return;

            foreach (var value in values.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim(claimType, value));
            }
        }
    }

    private sealed class RecordingServiceGovernanceCommandPort : IServiceGovernanceCommandPort
    {
        public CreateServiceBindingCommand? CreateBindingCommand { get; private set; }

        public UpdateServiceBindingCommand? UpdateBindingCommand { get; private set; }

        public RetireServiceBindingCommand? RetireBindingCommand { get; private set; }

        public CreateServiceEndpointCatalogCommand? CreateEndpointCatalogCommand { get; private set; }

        public UpdateServiceEndpointCatalogCommand? UpdateEndpointCatalogCommand { get; private set; }

        public CreateServicePolicyCommand? CreatePolicyCommand { get; private set; }

        public UpdateServicePolicyCommand? UpdatePolicyCommand { get; private set; }

        public RetireServicePolicyCommand? RetirePolicyCommand { get; private set; }

        public Task<ServiceCommandAcceptedReceipt> CreateBindingAsync(CreateServiceBindingCommand command, CancellationToken ct = default)
        {
            CreateBindingCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("binding-actor", "cmd-create-binding", "corr-create-binding"));
        }

        public Task<ServiceCommandAcceptedReceipt> UpdateBindingAsync(UpdateServiceBindingCommand command, CancellationToken ct = default)
        {
            UpdateBindingCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("binding-actor", "cmd-update-binding", "corr-update-binding"));
        }

        public Task<ServiceCommandAcceptedReceipt> RetireBindingAsync(RetireServiceBindingCommand command, CancellationToken ct = default)
        {
            RetireBindingCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("binding-actor", "cmd-retire-binding", "corr-retire-binding"));
        }

        public Task<ServiceCommandAcceptedReceipt> CreateEndpointCatalogAsync(CreateServiceEndpointCatalogCommand command, CancellationToken ct = default)
        {
            CreateEndpointCatalogCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("endpoint-actor", "cmd-create-endpoint-catalog", "corr-create-endpoint-catalog"));
        }

        public Task<ServiceCommandAcceptedReceipt> UpdateEndpointCatalogAsync(UpdateServiceEndpointCatalogCommand command, CancellationToken ct = default)
        {
            UpdateEndpointCatalogCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("endpoint-actor", "cmd-update-endpoint-catalog", "corr-update-endpoint-catalog"));
        }

        public Task<ServiceCommandAcceptedReceipt> CreatePolicyAsync(CreateServicePolicyCommand command, CancellationToken ct = default)
        {
            CreatePolicyCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("policy-actor", "cmd-create-policy", "corr-create-policy"));
        }

        public Task<ServiceCommandAcceptedReceipt> UpdatePolicyAsync(UpdateServicePolicyCommand command, CancellationToken ct = default)
        {
            UpdatePolicyCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("policy-actor", "cmd-update-policy", "corr-update-policy"));
        }

        public Task<ServiceCommandAcceptedReceipt> RetirePolicyAsync(RetireServicePolicyCommand command, CancellationToken ct = default)
        {
            RetirePolicyCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("policy-actor", "cmd-retire-policy", "corr-retire-policy"));
        }
    }

    private sealed class RecordingServiceGovernanceQueryPort : IServiceGovernanceQueryPort
    {
        public ServiceIdentity? LastBindingsIdentity { get; private set; }

        public ServiceIdentity? LastEndpointCatalogIdentity { get; private set; }

        public ServiceIdentity? LastPoliciesIdentity { get; private set; }

        public ServiceBindingCatalogSnapshot? BindingsResult { get; set; }

        public ServiceEndpointCatalogSnapshot? EndpointCatalogResult { get; set; }

        public ServicePolicyCatalogSnapshot? PoliciesResult { get; set; }

        public Task<ServiceBindingCatalogSnapshot?> GetBindingsAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastBindingsIdentity = identity;
            return Task.FromResult(BindingsResult);
        }

        public Task<ServiceEndpointCatalogSnapshot?> GetEndpointCatalogAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastEndpointCatalogIdentity = identity;
            return Task.FromResult(EndpointCatalogResult);
        }

        public Task<ServicePolicyCatalogSnapshot?> GetPoliciesAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastPoliciesIdentity = identity;
            return Task.FromResult(PoliciesResult);
        }
    }

    private sealed class RecordingActivationCapabilityViewReader : IActivationCapabilityViewReader
    {
        public ServiceIdentity? LastIdentity { get; private set; }

        public string? LastRevisionId { get; private set; }

        public ActivationCapabilityView Result { get; set; } = new();

        public Task<ActivationCapabilityView> GetAsync(ServiceIdentity identity, string revisionId, CancellationToken ct = default)
        {
            LastIdentity = identity;
            LastRevisionId = revisionId;
            return Task.FromResult(Result.Clone());
        }
    }
}
