using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Aevatar.App.Host.Api.Tests;

public sealed class UserIntegrationTests : IClassFixture<AppTestFixture>
{
    private readonly AppTestFixture _fx;

    public UserIntegrationTests(AppTestFixture fx) => _fx = fx;

    [Fact]
    public async Task SoftDelete_AnonymizesData()
    {
        using var client = _fx.CreateAuthenticatedClient("del-soft", "del-soft@test.com");

        await client.PostAsJsonAsync("/api/sync", new
        {
            syncId = "d1", clientRevision = 0,
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

        var delRes = await client.DeleteAsync("/api/users/me");
        delRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await delRes.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("mode").GetString().Should().Be("soft");
    }

    [Fact]
    public async Task HardDelete_ClearsAllData()
    {
        using var client = _fx.CreateAuthenticatedClient("del-hard", "del-hard@test.com");

        await client.PostAsJsonAsync("/api/sync", new
        {
            syncId = "d1", clientRevision = 0,
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

        var delRes = await client.DeleteAsync("/api/users/me?hard=true");
        delRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await delRes.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("mode").GetString().Should().Be("hard");
    }

    [Fact]
    public async Task CreateProfile_ReturnsCreated()
    {
        using var client = _fx.CreateAuthenticatedClient("profile-create", "profile@test.com");

        var res = await client.PostAsJsonAsync("/api/users/me/profile", new
        {
            firstName = "Test",
            lastName = "User",
            timezone = "America/New_York",
            interests = new[] { "meditation", "nature" },
            purposeStr = "growth",
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("firstName").GetString().Should().Be("Test");
        json.GetProperty("timezone").GetString().Should().Be("America/New_York");
    }

    [Fact]
    public async Task CreateProfile_Duplicate_Returns409()
    {
        using var client = _fx.CreateAuthenticatedClient("profile-dup", "profile-dup@test.com");

        var body = new
        {
            firstName = "First",
            lastName = "User",
            timezone = "UTC",
        };

        await client.PostAsJsonAsync("/api/users/me/profile", body);
        var res = await client.PostAsJsonAsync("/api/users/me/profile", body);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task UpdateProfile_ReturnsUpdatedData()
    {
        using var client = _fx.CreateAuthenticatedClient("profile-update", "update@test.com");

        await client.PostAsJsonAsync("/api/users/me/profile", new
        {
            firstName = "Original",
            lastName = "Name",
            timezone = "UTC",
        });

        var res = await client.PatchAsJsonAsync("/api/users/me/profile", new
        {
            firstName = "Updated",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("firstName").GetString().Should().Be("Updated");
    }
}
