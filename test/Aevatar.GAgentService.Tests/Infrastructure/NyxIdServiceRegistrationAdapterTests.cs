using System.Reflection;
using System.Text.Json;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.Models;
using Aevatar.GAgentService.Infrastructure.Adapters;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class NyxIdServiceRegistrationAdapterTests
{
    [Fact]
    public void BuildServiceBody_ShouldSendScopeCredentialAndDisableAccessTokenForwarding()
    {
        var request = new NyxIdServiceRegistrationRequest(
            new ServiceIdentity
            {
                TenantId = "scope-a",
                AppId = "default",
                Namespace = "default",
                ServiceId = "orders-service",
            },
            "Orders",
            "https://api.example.com/openapi.json",
            "hash-1",
            "owner-token",
            "scope-jwt",
            "kid-1");

        var method = typeof(NyxIdServiceRegistrationAdapter).GetMethod(
            "BuildServiceBody",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var body = (string)method!.Invoke(null, [request])!;
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        root.GetProperty("credential").GetString().Should().Be("scope-jwt");
        root.GetProperty("forward_access_token").GetBoolean().Should().BeFalse();
        root.GetProperty("service_slug").GetString().Should().Be("orders-service");
        root.GetProperty("aevatar_desired_spec_hash").GetString().Should().Be("hash-1");
        root.EnumerateObject()
            .Select(x => x.Name)
            .Should()
            .NotContain(["jwks_url", "jwks_uri", "jwks_path"]);
    }
}
