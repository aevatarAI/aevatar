using Aevatar.Workflow.Core.Modules;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class BoundedTemplateRendererTests
{
    [Fact]
    public void Render_ShouldSerializeDataScalarNestedInArray()
    {
        var output = BoundedTemplateRenderer.Render(
            """{"d":{"a":"X"}}""",
            "{{~ v = [data.d.a] ~}} {{ json({ v: v }) }}",
            CancellationToken.None);

        output.Should().Be("{\"v\":[\"X\"]}");
    }

    [Fact]
    public void Render_ShouldReportTemplateLocationForMissingDataMember()
    {
        var act = () => BoundedTemplateRenderer.Render(
            """{"a":"X"}""",
            "{{~ v = [data.d.a] ~}} {{ json({ v: v }) }}",
            CancellationToken.None);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage(
                "transform template evaluation failed: " +
                "workflow-transform-template(1,15) : error : Cannot get member with name d.");
    }
}
