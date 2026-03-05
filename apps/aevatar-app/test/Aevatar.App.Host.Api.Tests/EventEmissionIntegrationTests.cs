using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Aevatar.App.GAgents;

namespace Aevatar.App.Host.Api.Tests;

public sealed class EventEmissionIntegrationTests : IClassFixture<AppTestFixture>
{
    private readonly AppTestFixture _fx;

    public EventEmissionIntegrationTests(AppTestFixture fx) => _fx = fx;

    [Fact]
    public async Task Sync_Create_PersistsDomainEvents()
    {
        using var client = _fx.CreateAuthenticatedClient("evt-sync", "evt@test.com");

        var res = await client.PostAsJsonAsync("/api/sync", new
        {
            syncId = "es1", clientRevision = 0,
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
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var syncJson = await res.Content.ReadFromJsonAsync<JsonElement>();
        syncJson.GetProperty("accepted").GetArrayLength().Should().Be(1);

        var authContext = syncJson.GetProperty("syncId").GetString();
        authContext.Should().Be("es1");
    }

    [Fact]
    public async Task FullChain_Sync_Generate_State()
    {
        _fx.WorkflowStub.ShouldFail = false;
        _fx.WorkflowStub.NextResponse = """{"mantra":"Test","plantName":"Test Plant","plantDescription":"A test"}""";

        using var client = _fx.CreateAuthenticatedClient("e2e-chain", "e2e@test.com");

        var genRes = await client.PostAsJsonAsync("/api/generate/manifestation",
            new { userGoal = "E2E test" });
        genRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var genJson = await genRes.Content.ReadFromJsonAsync<JsonElement>();
        var plantName = genJson.GetProperty("plantName").GetString();

        var syncRes = await client.PostAsJsonAsync("/api/sync", new
        {
            syncId = "e2e1", clientRevision = 0,
            entities = new Dictionary<string, Dictionary<string, object>>
            {
                ["manifestation"] = new()
                {
                    ["e2e-m1"] = new
                    {
                        clientId = "e2e-m1", entityType = "manifestation", revision = 0,
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
        stateRes.GetProperty("entities").GetProperty("manifestation")
            .GetProperty("e2e-m1").GetProperty("clientId").GetString()
            .Should().Be("e2e-m1");

        var limitsRes = await client.GetFromJsonAsync<JsonElement>("/api/sync/limits");
        limitsRes.GetProperty("limits").GetProperty("maxSavedPlants").GetInt32().Should().Be(10);
    }
}
