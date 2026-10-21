using Aevatar.Workflow.Abstractions.Primitives;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowPrimitiveDefinitionTests
{
    [Fact]
    public void CoreModules_ShouldContributeConsistentAuthoringContracts()
    {
        var registrations = new WorkflowCoreModulePack().Modules;

        foreach (var registration in registrations)
        {
            registration.Descriptor.Should().NotBeNull($"{registration.ModuleType.Name} must describe its authoring contract");
            var descriptor = registration.Descriptor!;
            descriptor.Description.Should().NotBeNullOrWhiteSpace();
            descriptor.Parameters.Select(parameter => parameter.Name).Should().OnlyHaveUniqueItems();
            foreach (var parameter in descriptor.Parameters)
            {
                parameter.Name.Should().NotBeNullOrWhiteSpace();
                parameter.Label.Should().NotBeNullOrWhiteSpace();
                Enum.IsDefined(parameter.Type).Should().BeTrue();
                Enum.IsDefined(parameter.Control).Should().BeTrue();
                if (parameter.Minimum is { } minimum && parameter.Maximum is { } maximum)
                    minimum.Should().BeLessThanOrEqualTo(maximum);
                if (parameter.EnumValues is { Count: > 0 } options && parameter.DefaultValue.Length > 0)
                    options.Should().Contain(parameter.DefaultValue);
            }
        }
    }

    [Fact]
    public void WorkflowCallDescriptor_ShouldAdvertiseOnlyRuntimeSupportedLifecycles()
    {
        var lifecycle = WorkflowPrimitiveDefinitions.Get("workflow_call").Parameters.Single(parameter => parameter.Name == "lifecycle");

        lifecycle.EnumValues.Should().BeEquivalentTo(["singleton", "transient", "scope"]);
        lifecycle.EnumValues!.Should().OnlyContain(value => WorkflowCallLifecycle.IsSupported(value));
        lifecycle.DefaultValue.Should().Be(WorkflowCallLifecycle.Normalize(null));
        WorkflowCallLifecycle.IsSupported("sync").Should().BeFalse();
        WorkflowCallLifecycle.IsSupported("async").Should().BeFalse();
        WorkflowCallLifecycle.IsSupported("inline").Should().BeFalse();

        var waiter = WorkflowPrimitiveDefinitions.Get("wait_signal");
        waiter.Parameters.Single(parameter => parameter.Name == "signal_name")
            .Should().Match<WorkflowPrimitiveParameterDefinition>(parameter => !parameter.Required && parameter.DefaultValue == "default");
        waiter.Parameters.Single(parameter => parameter.Name == "timeout_ms").Maximum.Should().Be(86_400_000);
        WorkflowPrimitiveDefinitions.Get("llm_call").Parameters.Select(parameter => parameter.Name)
            .Should().Contain("prompt_prefix").And.NotContain("prompt");
    }
}
