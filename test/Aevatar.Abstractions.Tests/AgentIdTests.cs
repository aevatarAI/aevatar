// ─── AgentId utility tests ───

using Aevatar.Helpers;
using Shouldly;

namespace Aevatar.Abstractions.Tests;

public class AgentIdTests
{
    [Fact]
    public void Normalize_StripsAgentSuffix()
    {
        // Simulate a type ending with Agent suffix
        var id = AgentId.Normalize(typeof(FakeAgent), "abc123");
        id.ShouldBe("Fake:abc123");
    }

    [Fact]
    public void Normalize_StripsGAgentSuffix()
    {
        var id = AgentId.Normalize(typeof(FakeGAgent), "xyz");
        id.ShouldBe("Fake:xyz");
    }

    [Fact]
    public void Normalize_KeepsNameWithoutSuffix()
    {
        var id = AgentId.Normalize(typeof(Worker), "001");
        id.ShouldBe("Worker:001");
    }

    [Fact]
    public void New_GeneratesValidFormat()
    {
        var id = AgentId.New(typeof(FakeAgent));
        id.ShouldStartWith("Fake:");
        id.Length.ShouldBeGreaterThan("Fake:".Length);
    }

    [Fact]
    public void GetRawId_ExtractsAfterColon()
    {
        AgentId.GetRawId("Counter:abc123").ShouldBe("abc123");
    }

    [Fact]
    public void GetRawId_ReturnsOriginalWhenNoColon()
    {
        AgentId.GetRawId("noprefix").ShouldBe("noprefix");
    }

    // Fake types for testing only
    private sealed class FakeAgent;

    private sealed class FakeGAgent;

    private sealed class Worker;
}