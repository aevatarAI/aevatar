using System.Text.Json;
using Aevatar.Workflow.Sdk.Contracts;
using FluentAssertions;

namespace Aevatar.Workflow.Sdk.Tests.Session;

public sealed class WorkflowCustomEventParserTests
{
    [Fact]
    public void TryParseRunContext_ShouldParseCamelCaseAndPascalCase()
    {
        var camel = new WorkflowOutputFrame
        {
            Type = WorkflowEventTypes.Custom,
            Name = WorkflowCustomEventNames.RunContext,
            Value = ParseObject("""{"actorId":"actor-c","workflowName":"auto","commandId":"cmd-c"}"""),
        };
        var pascal = new WorkflowOutputFrame
        {
            Type = WorkflowEventTypes.Custom,
            Name = WorkflowCustomEventNames.RunContext,
            Value = ParseObject("""{"ActorId":"actor-p","WorkflowName":"manual","CommandId":"cmd-p"}"""),
        };

        WorkflowCustomEventParser.TryParseRunContext(camel, out var camelData).Should().BeTrue();
        WorkflowCustomEventParser.TryParseRunContext(pascal, out var pascalData).Should().BeTrue();

        camelData.ActorId.Should().Be("actor-c");
        camelData.WorkflowName.Should().Be("auto");
        camelData.CommandId.Should().Be("cmd-c");
        pascalData.ActorId.Should().Be("actor-p");
        pascalData.WorkflowName.Should().Be("manual");
        pascalData.CommandId.Should().Be("cmd-p");
    }

    [Fact]
    public void TryParseWaitingSignal_ShouldReturnTypedPayload()
    {
        var frame = new WorkflowOutputFrame
        {
            Type = WorkflowEventTypes.Custom,
            Name = WorkflowCustomEventNames.WaitingSignal,
            Value = ParseObject("""{"runId":"run-1","stepId":"wait-1","signalName":"continue","timeoutMs":30000}"""),
        };

        var ok = WorkflowCustomEventParser.TryParseWaitingSignal(frame, out var data);

        ok.Should().BeTrue();
        data.RunId.Should().Be("run-1");
        data.StepId.Should().Be("wait-1");
        data.SignalName.Should().Be("continue");
        data.TimeoutMs.Should().Be(30000);
    }

    [Fact]
    public void TryParseHumanInputRequest_WhenEventNameMismatch_ShouldReturnFalse()
    {
        var frame = new WorkflowOutputFrame
        {
            Type = WorkflowEventTypes.Custom,
            Name = WorkflowCustomEventNames.StepRequest,
            Value = ParseObject("""{"runId":"run-1","stepId":"s1"}"""),
        };

        var ok = WorkflowCustomEventParser.TryParseHumanInputRequest(frame, out _);

        ok.Should().BeFalse();
    }

    private static JsonElement ParseObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
