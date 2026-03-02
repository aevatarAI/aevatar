using FluentAssertions;
using Aevatar.Workflow.Core;
using System.Reflection;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public class WorkflowCallLifecycleTests
{
    private static readonly Type WorkflowCallLifecycleType =
        typeof(WorkflowGAgent).Assembly.GetType("Aevatar.Workflow.Core.Primitives.WorkflowCallLifecycle")!;
    private static readonly MethodInfo NormalizeMethod =
        WorkflowCallLifecycleType.GetMethod("Normalize", BindingFlags.Public | BindingFlags.Static)!;
    private static readonly MethodInfo IsSupportedMethod =
        WorkflowCallLifecycleType.GetMethod("IsSupported", BindingFlags.Public | BindingFlags.Static)!;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Normalize_WhenLifecycleMissing_ShouldDefaultToSingleton(string? lifecycle)
    {
        var normalized = Normalize(lifecycle);

        normalized.Should().Be("singleton");
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("isolate")]
    public void IsSupported_WhenLifecycleUnknown_ShouldReturnFalse(string lifecycle)
    {
        var supported = IsSupported(lifecycle);

        supported.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("singleton")]
    [InlineData("SINGLETON")]
    [InlineData("transient")]
    [InlineData("TRANSIENT")]
    [InlineData("scope")]
    [InlineData("SCOPE")]
    public void IsSupported_WhenLifecycleMissingOrKnown_ShouldReturnTrue(string? lifecycle)
    {
        var supported = IsSupported(lifecycle);

        supported.Should().BeTrue();
    }

    [Theory]
    [InlineData("singleton")]
    [InlineData("SINGLETON")]
    [InlineData("Singleton")]
    public void Normalize_WhenSingletonVariants_ShouldReturnSingleton(string lifecycle)
    {
        var normalized = Normalize(lifecycle);

        normalized.Should().Be("singleton");
    }

    [Theory]
    [InlineData("transient")]
    [InlineData("TRANSIENT")]
    [InlineData("Transient")]
    public void Normalize_WhenTransientVariants_ShouldReturnTransient(string lifecycle)
    {
        var normalized = Normalize(lifecycle);

        normalized.Should().Be("transient");
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("SCOPE")]
    [InlineData("Scope")]
    public void Normalize_WhenScopeVariants_ShouldReturnScope(string lifecycle)
    {
        var normalized = Normalize(lifecycle);

        normalized.Should().Be("scope");
    }

    private static string Normalize(string? lifecycle)
    {
        var normalized = NormalizeMethod.Invoke(null, [lifecycle]);
        return normalized.Should().BeOfType<string>().Subject;
    }

    private static bool IsSupported(string? lifecycle)
    {
        var supported = IsSupportedMethod.Invoke(null, [lifecycle]);
        return supported.Should().BeOfType<bool>().Subject;
    }
}
