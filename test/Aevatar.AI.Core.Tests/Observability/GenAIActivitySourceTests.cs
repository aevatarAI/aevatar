using System.Diagnostics;
using Aevatar.AI.Core.Observability;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Observability;

public class GenAIActivitySourceTests
{
    [Fact]
    public void Source_HasCorrectName()
    {
        GenAIActivitySource.Source.Name.Should().Be("Aevatar.GenAI");
        GenAIActivitySource.Source.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void StartInvokeAgent_SetsCorrectTags()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "Aevatar.GenAI",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = GenAIActivitySource.StartInvokeAgent("agent-1", "TestAgent");
        activity.Should().NotBeNull();
        activity!.GetTagItem("gen_ai.operation.name").Should().Be("invoke_agent");
        activity.GetTagItem("gen_ai.agent.id").Should().Be("agent-1");
        activity.GetTagItem("gen_ai.agent.name").Should().Be("TestAgent");
    }

    [Fact]
    public void StartChat_SetsModelTag()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "Aevatar.GenAI",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = GenAIActivitySource.StartChat("gpt-4");
        activity.Should().NotBeNull();
        activity!.GetTagItem("gen_ai.operation.name").Should().Be("chat");
        activity.GetTagItem("gen_ai.request.model").Should().Be("gpt-4");
    }

    [Fact]
    public void StartExecuteTool_SetsToolTags()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "Aevatar.GenAI",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = GenAIActivitySource.StartExecuteTool("search", "call-42");
        activity.Should().NotBeNull();
        activity!.GetTagItem("gen_ai.operation.name").Should().Be("execute_tool");
        activity.GetTagItem("gen_ai.tool.name").Should().Be("search");
        activity.GetTagItem("gen_ai.tool.call_id").Should().Be("call-42");
    }

    [Fact]
    public void EnableSensitiveData_DefaultsFalse()
    {
        GenAIActivitySource.EnableSensitiveData.Should().BeFalse();
    }
}
