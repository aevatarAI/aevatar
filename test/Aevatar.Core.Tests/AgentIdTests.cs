// ─── AgentId utility tests ───

using Aevatar.Helpers;
using Shouldly;

namespace Aevatar.Tests;

public class AgentIdTests
{
    [Fact]
    public void Normalize_StripsAgentSuffix()
    {
        var id = AgentId.Normalize(typeof(CounterAgent), "abc123");
        id.ShouldBe("Counter:abc123");
    }

    [Fact]
    public void New_GeneratesValidFormat()
    {
        var id = AgentId.New(typeof(CounterAgent));
        id.ShouldStartWith("Counter:");
        id.Length.ShouldBeGreaterThan("Counter:".Length);
    }

    [Fact]
    public void GetRawId_ExtractsCorrectly()
    {
        AgentId.GetRawId("Counter:abc123").ShouldBe("abc123");
        AgentId.GetRawId("noprefix").ShouldBe("noprefix");
    }
}
