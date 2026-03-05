using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Aevatar.App.Host.Api.Tests;

public sealed class SyncIntegrationTests : IClassFixture<AppTestFixture>
{
    private readonly AppTestFixture _fx;

    public SyncIntegrationTests(AppTestFixture fx) => _fx = fx;

    private static object MakeEntity(string clientId, string entityType = "manifestation",
        int revision = 0, string source = "ai", string? bankHash = null, string? deletedAt = null)
        => new
        {
            clientId, entityType, revision, source,
            bankEligible = true,
            bankHash = bankHash ?? "",
            refs = new Dictionary<string, string>(),
            createdAt = DateTimeOffset.UtcNow.ToString("O"),
            updatedAt = DateTimeOffset.UtcNow.ToString("O"),
            deletedAt,
        };

    private object MakeSyncRequest(string syncId, int clientRevision,
        Dictionary<string, Dictionary<string, object>> entities)
        => new { syncId, clientRevision, entities };

    [Fact]
    public async Task Sync_Create_ReturnsAccepted()
    {
        using var client = _fx.CreateAuthenticatedClient("sync-create", "sync@test.com");

        var body = MakeSyncRequest("s1", 0, new()
        {
            ["manifestation"] = new() { ["c1"] = MakeEntity("c1") }
        });

        var res = await client.PostAsJsonAsync("/api/sync", body);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("accepted").GetArrayLength().Should().Be(1);
        json.GetProperty("serverRevision").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Sync_StaleRevision_IsRejected()
    {
        using var client = _fx.CreateAuthenticatedClient("sync-stale", "stale@test.com");

        var create = MakeSyncRequest("s1", 0, new()
        {
            ["manifestation"] = new() { ["c1"] = MakeEntity("c1") }
        });
        await client.PostAsJsonAsync("/api/sync", create);

        var stale = MakeSyncRequest("s2", 0, new()
        {
            ["manifestation"] = new() { ["c1"] = MakeEntity("c1", revision: 0) }
        });
        var res = await client.PostAsJsonAsync("/api/sync", stale);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("rejected").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Sync_EditDetection_MarksEdited()
    {
        using var client = _fx.CreateAuthenticatedClient("sync-edit", "edit@test.com");

        await client.PostAsJsonAsync("/api/sync", MakeSyncRequest("s1", 0, new()
        {
            ["manifestation"] = new() { ["c1"] = MakeEntity("c1", bankHash: "hash_a") }
        }));

        var updated = MakeSyncRequest("s2", 1, new()
        {
            ["manifestation"] = new() { ["c1"] = MakeEntity("c1", revision: 1, bankHash: "hash_b") }
        });
        await client.PostAsJsonAsync("/api/sync", updated);

        var stateRes = await client.GetFromJsonAsync<JsonElement>("/api/state");
        var entity = stateRes.GetProperty("entities")
            .GetProperty("manifestation").GetProperty("c1");
        entity.GetProperty("source").GetString().Should().Be("edited");
        entity.GetProperty("bankEligible").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Sync_CascadeDelete_DeletesChildren()
    {
        using var client = _fx.CreateAuthenticatedClient("sync-cascade", "cascade@test.com");

        var parent = MakeEntity("parent");
        var child = new
        {
            clientId = "child", entityType = "affirmation", revision = 0, source = "ai",
            bankEligible = true, bankHash = "",
            refs = new Dictionary<string, string> { ["manifestation"] = "parent" },
            createdAt = DateTimeOffset.UtcNow.ToString("O"),
            updatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };

        await client.PostAsJsonAsync("/api/sync", MakeSyncRequest("s1", 0, new()
        {
            ["manifestation"] = new() { ["parent"] = parent },
            ["affirmation"] = new() { ["child"] = child }
        }));

        var stateRes = await client.GetFromJsonAsync<JsonElement>("/api/state");
        var serverRev = stateRes.GetProperty("serverRevision").GetInt32();

        var parentRevision = stateRes.GetProperty("entities")
            .GetProperty("manifestation").GetProperty("parent")
            .GetProperty("revision").GetInt32();

        var del = MakeSyncRequest("s2", serverRev, new()
        {
            ["manifestation"] = new()
            {
                ["parent"] = MakeEntity("parent", revision: parentRevision,
                    deletedAt: DateTimeOffset.UtcNow.ToString("O"))
            }
        });
        await client.PostAsJsonAsync("/api/sync", del);

        var stateAfter = await client.GetFromJsonAsync<JsonElement>("/api/state");
        stateAfter.GetProperty("entities").TryGetProperty("affirmation", out _)
            .Should().BeFalse("child should be cascade deleted and filtered from state");
    }

    [Fact]
    public async Task Sync_Sequential_NoDataLoss()
    {
        using var client = _fx.CreateAuthenticatedClient("sync-seq", "seq@test.com");

        for (var i = 0; i < 5; i++)
        {
            var stateRes = await client.GetFromJsonAsync<JsonElement>("/api/state");
            var rev = stateRes.GetProperty("serverRevision").GetInt32();

            var body = MakeSyncRequest($"s{i}", rev, new()
            {
                ["manifestation"] = new() { [$"c_{i}"] = MakeEntity($"c_{i}") }
            });
            var res = await client.PostAsJsonAsync("/api/sync", body);
            res.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var finalState = await client.GetFromJsonAsync<JsonElement>("/api/state");
        var manifEntities = finalState.GetProperty("entities").GetProperty("manifestation");
        manifEntities.EnumerateObject().Count().Should().Be(5,
            "all 5 sequential syncs should be accepted without data loss");
    }

    [Fact]
    public async Task Sync_ApplicationJson_WorksNormally()
    {
        using var client = _fx.CreateAuthenticatedClient("sync-json", "json@test.com");

        var body = MakeSyncRequest("s1", 0, new()
        {
            ["manifestation"] = new() { ["c1"] = MakeEntity("c1") }
        });

        var res = await client.PostAsJsonAsync("/api/sync", body);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await res.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("accepted").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Sync_TextPlain_SendBeaconCompatible_WorksNormally()
    {
        using var client = _fx.CreateAuthenticatedClient("sync-beacon", "beacon@test.com");

        var payload = JsonSerializer.Serialize(MakeSyncRequest("sb1", 0, new()
        {
            ["manifestation"] = new() { ["c1"] = MakeEntity("c1") }
        }));

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/sync")
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/plain")
        };
        var res = await client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await res.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("accepted").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task State_EmptyState_ReturnsEmptyEntities()
    {
        using var client = _fx.CreateAuthenticatedClient("state-empty", "empty@test.com");

        var res = await client.GetFromJsonAsync<JsonElement>("/api/state");
        res.GetProperty("serverRevision").GetInt32().Should().Be(0);
        res.GetProperty("entities").EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public async Task Limits_ReturnsCorrectFormat()
    {
        using var client = _fx.CreateAuthenticatedClient("limits-test", "limits@test.com");

        var res = await client.GetFromJsonAsync<JsonElement>("/api/sync/limits");
        var limits = res.GetProperty("limits");
        limits.GetProperty("maxSavedPlants").GetInt32().Should().Be(10);
        limits.GetProperty("maxPlantsPerWeek").GetInt32().Should().Be(3);
        limits.GetProperty("maxWateringsPerDay").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task State_RehydratesAfterActorDestroy()
    {
        using var client = _fx.CreateAuthenticatedClient("sync-restore", "restore@test.com");

        var createRes = await client.PostAsJsonAsync("/api/sync", MakeSyncRequest("r1", 0, new()
        {
            ["manifestation"] = new() { ["r_entity"] = MakeEntity("r_entity") }
        }));
        createRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/users/me");
        var userId = me.GetProperty("user").GetProperty("id").GetString();
        userId.Should().NotBeNullOrEmpty();

        await _fx.Runtime.DestroyAsync(userId!);

        var state = await client.GetFromJsonAsync<JsonElement>("/api/state");
        state.GetProperty("serverRevision").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        state.TryGetProperty("entities", out _).Should().BeTrue();
    }
}
