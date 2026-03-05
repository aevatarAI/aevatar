using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Aevatar.App.Host.Api.Tests;

public sealed class AuthIntegrationTests : IClassFixture<AppTestFixture>
{
    private readonly AppTestFixture _fx;

    public AuthIntegrationTests(AppTestFixture fx) => _fx = fx;

    [Fact]
    public async Task RegisterTrial_ValidEmail_ReturnsTokenAndTrialId()
    {
        var res = await _fx.Client.PostAsJsonAsync("/api/auth/register-trial",
            new { email = "trial@test.com" });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("trialId").GetString().Should().StartWith("trial_");
    }

    [Fact]
    public async Task RegisterTrial_SameEmail_ReturnsExistingUser()
    {
        var email = "repeat-trial@test.com";
        var first = await _fx.Client.PostAsJsonAsync("/api/auth/register-trial", new { email });
        var firstJson = await first.Content.ReadFromJsonAsync<JsonElement>();
        var firstTrialId = firstJson.GetProperty("trialId").GetString();

        var second = await _fx.Client.PostAsJsonAsync("/api/auth/register-trial", new { email });
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondJson = await second.Content.ReadFromJsonAsync<JsonElement>();
        var secondTrialId = secondJson.GetProperty("trialId").GetString();

        secondTrialId.Should().Be(firstTrialId);
        secondJson.GetProperty("existing").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task RegisterTrial_InvalidEmail_ReturnsBadRequest()
    {
        var res = await _fx.Client.PostAsJsonAsync("/api/auth/register-trial",
            new { email = "not-an-email" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RegisterTrial_WithTurnstileToken_IsAccepted()
    {
        var res = await _fx.Client.PostAsJsonAsync("/api/auth/register-trial",
            new { email = "turnstile@test.com", turnstileToken = "dummy-token" });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("trialId").GetString().Should().StartWith("trial_");
    }

    [Fact]
    public async Task ProtectedEndpoint_NoAuth_Returns401()
    {
        var res = await _fx.Client.GetAsync("/api/state");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProtectedEndpoint_InvalidToken_Returns401()
    {
        using var client = new HttpClient { BaseAddress = _fx.Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "bad-token");

        var res = await client.GetAsync("/api/state");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProtectedEndpoint_ValidToken_Succeeds()
    {
        using var client = _fx.CreateAuthenticatedClient("auth-ok", "auth-ok@test.com");

        var res = await client.GetAsync("/api/state");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Provisioning_NewUser_AutoCreatesAccount()
    {
        using var client = _fx.CreateAuthenticatedClient("provision-new", "provision-new@test.com");

        var syncRes = await client.PostAsJsonAsync("/api/sync", new
        {
            syncId = "p1",
            clientRevision = 0,
            entities = new Dictionary<string, Dictionary<string, object>>
            {
                ["manifestation"] = new()
                {
                    ["c1"] = new
                    {
                        clientId = "c1", entityType = "manifestation", revision = 0,
                        source = "ai", bankEligible = true, bankHash = "",
                        refs = new Dictionary<string, string>(),
                        createdAt = DateTimeOffset.UtcNow.ToString("O"),
                        updatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    }
                }
            }
        });

        syncRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var stateRes = await client.GetFromJsonAsync<JsonElement>("/api/state");
        stateRes.GetProperty("serverRevision").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Provisioning_SameProvider_ReturnsSameUser()
    {
        var token = _fx.GenerateTrialToken("same-provider", "same-prov@test.com");

        using var c1 = new HttpClient { BaseAddress = _fx.Client.BaseAddress };
        c1.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        await c1.PostAsJsonAsync("/api/sync", new
        {
            syncId = "sp1", clientRevision = 0,
            entities = new Dictionary<string, Dictionary<string, object>>
            {
                ["manifestation"] = new()
                {
                    ["c1"] = new
                    {
                        clientId = "c1", entityType = "manifestation", revision = 0,
                        source = "ai", bankEligible = true, bankHash = "",
                        refs = new Dictionary<string, string>(),
                        createdAt = DateTimeOffset.UtcNow.ToString("O"),
                        updatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    }
                }
            }
        });

        using var c2 = new HttpClient { BaseAddress = _fx.Client.BaseAddress };
        c2.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var state = await c2.GetFromJsonAsync<JsonElement>("/api/state");

        state.GetProperty("serverRevision").GetInt32().Should().BeGreaterThan(0,
            "second request with same token should see the same garden");
    }

    [Fact]
    public async Task HealthEndpoint_NoAuth_ReturnsOk()
    {
        var res = await _fx.Client.GetAsync("/health");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RootEndpoint_ReturnsServiceInfo()
    {
        var res = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/info");
        res.GetProperty("service").GetString().Should().Be("aevatar-app-api");
    }

    [Fact]
    public async Task RemoteConfig_InvalidToken_DoesNotForce401()
    {
        using var client = new HttpClient { BaseAddress = _fx.Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "invalid.token.value");

        var res = await client.GetAsync("/api/remote-config");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
