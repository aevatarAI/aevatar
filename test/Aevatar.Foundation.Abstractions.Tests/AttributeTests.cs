// ─── Attribute tests ───

using Aevatar.Foundation.Abstractions.Attributes;
using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public class AttributeTests
{
    [Fact]
    public void EventHandlerAttribute_DefaultValues()
    {
        var attr = new EventHandlerAttribute();
        attr.Priority.ShouldBe(0);
        attr.AllowSelfHandling.ShouldBeFalse();
        attr.OnlySelfHandling.ShouldBeFalse();
    }

    [Fact]
    public void EventHandlerAttribute_SetValues()
    {
        var attr = new EventHandlerAttribute
        {
            Priority = 10,
            AllowSelfHandling = true,
            OnlySelfHandling = true,
        };
        attr.Priority.ShouldBe(10);
        attr.AllowSelfHandling.ShouldBeTrue();
        attr.OnlySelfHandling.ShouldBeTrue();
    }

    [Fact]
    public void AllEventHandlerAttribute_DefaultPriority_IsMaxValue()
    {
        var attr = new AllEventHandlerAttribute();
        attr.Priority.ShouldBe(int.MaxValue);
        attr.AllowSelfHandling.ShouldBeFalse();
    }
}