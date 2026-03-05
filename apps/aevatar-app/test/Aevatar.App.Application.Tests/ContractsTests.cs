using FluentAssertions;
using Aevatar.App.Application.Contracts;
using System.Text.Json;

namespace Aevatar.App.Application.Tests;

public sealed class ContractsTests
{
    [Fact]
    public void SyncRequestDto_Deserializes_FromJson()
    {
        var json = """
        {
            "syncId": "abc-123",
            "clientRevision": 5,
            "entities": {
                "manifestation": {
                    "m_1": {
                        "clientId": "m_1",
                        "entityType": "manifestation",
                        "revision": 0,
                        "position": 0,
                        "bankEligible": true
                    }
                }
            }
        }
        """;

        var dto = JsonSerializer.Deserialize<SyncRequestDto>(json);

        dto.Should().NotBeNull();
        dto!.SyncId.Should().Be("abc-123");
        dto.ClientRevision.Should().Be(5);
        dto.Entities.Should().ContainKey("manifestation");
        dto.Entities["manifestation"].Should().ContainKey("m_1");
    }

    [Fact]
    public void SyncResponseDto_Serializes_ToJson()
    {
        var dto = new SyncResponseDto
        {
            SyncId = "abc-123",
            ServerRevision = 10,
            Accepted = ["m_1"],
            Rejected = [new RejectedEntityDto { ClientId = "m_2", ServerRevision = 3, Reason = "Stale" }]
        };

        var json = JsonSerializer.Serialize(dto);
        json.Should().Contain("\"syncId\":\"abc-123\"");
        json.Should().Contain("\"serverRevision\":10");
    }
}
