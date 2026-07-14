using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowPrimitiveCatalogTests
{
    [Fact]
    public void BuiltInCanonicalTypes_ShouldIncludeLeaseAndCanonicalizeMutex()
    {
        WorkflowPrimitiveCatalog.ToCanonicalType("mutex").Should().Be("lease");
        WorkflowPrimitiveCatalog.BuiltInCanonicalTypes.Should().Contain("lease");
    }

    [Fact]
    public void ToCanonicalType_ShouldResolveScheduleWorkflowAlias()
    {
        WorkflowPrimitiveCatalog.ToCanonicalType("schedule_workflow").Should().Be("self_reschedule");
    }

    [Fact]
    public void ToCanonicalType_ShouldResolveExecuteCodexAsToolCall()
    {
        WorkflowPrimitiveCatalog.ToCanonicalType("execute_codex").Should().Be("tool_call");
        WorkflowPrimitiveCatalog.IsSideEffectingPrimitive("execute_codex").Should().BeTrue();
        new WorkflowCoreModulePack().Modules
            .Single(registration => registration.ModuleType.Name == "ToolCallModule")
            .Names.Should().Contain("execute_codex");
    }

    [Fact]
    public void BuiltInCanonicalTypes_ShouldIncludeNotifyWithoutEmitOrPublishAlias()
    {
        WorkflowPrimitiveCatalog.ToCanonicalType("notify").Should().Be("notify");
        WorkflowPrimitiveCatalog.ToCanonicalType("emit").Should().Be("emit");
        WorkflowPrimitiveCatalog.ToCanonicalType("publish").Should().Be("emit");
        WorkflowPrimitiveCatalog.BuiltInCanonicalTypes.Should().Contain("notify");
    }

    [Fact]
    public void IsSideEffectingPrimitive_ShouldOnlyIncludeSagaV11ExternalDispatchPrimitives()
    {
        new[] { "tool_call", "connector_call", "secure_connector_call", "bridge_call", "http_post" }
            .Should()
            .OnlyContain(stepType => WorkflowPrimitiveCatalog.IsSideEffectingPrimitive(stepType));

        new[]
            {
                "llm_call", "evaluate", "reflect", "emit", "notify", "workflow_call",
                "dynamic_workflow", "lease", "human_input", "human_approval", "wait_signal",
                "transform", "assign", "retrieve_facts", "cache",
            }
            .Should()
            .OnlyContain(stepType => !WorkflowPrimitiveCatalog.IsSideEffectingPrimitive(stepType));
    }
}
