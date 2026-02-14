// ─── StateGuard tests ───

using Shouldly;

namespace Aevatar.Foundation.Core.Tests;

public class StateGuardTests
{
    [Fact]
    public void IsWritable_DefaultFalse()
    {
        StateGuard.IsWritable.ShouldBeFalse();
    }

    [Fact]
    public void BeginWriteScope_MakesWritable()
    {
        using var scope = StateGuard.BeginWriteScope();
        StateGuard.IsWritable.ShouldBeTrue();
    }

    [Fact]
    public void WriteScope_Dispose_RestoresState()
    {
        StateGuard.IsWritable.ShouldBeFalse();
        {
            using var scope = StateGuard.BeginWriteScope();
            StateGuard.IsWritable.ShouldBeTrue();
        }
        StateGuard.IsWritable.ShouldBeFalse();
    }

    [Fact]
    public void EnsureWritable_ThrowsOutsideScope()
    {
        var act = () => StateGuard.EnsureWritable();
        Should.Throw<InvalidOperationException>(act);
    }

    [Fact]
    public void EnsureWritable_SucceedsInsideScope()
    {
        using var scope = StateGuard.BeginWriteScope();
        var act = () => StateGuard.EnsureWritable();
        Should.NotThrow(act);
    }

    [Fact]
    public void NestedScopes_RestoreCorrectly()
    {
        StateGuard.IsWritable.ShouldBeFalse();
        using (var outer = StateGuard.BeginWriteScope())
        {
            StateGuard.IsWritable.ShouldBeTrue();
            using (var inner = StateGuard.BeginWriteScope())
            {
                StateGuard.IsWritable.ShouldBeTrue();
            }
            // After inner dispose, outer is still writable
            StateGuard.IsWritable.ShouldBeTrue();
        }
        StateGuard.IsWritable.ShouldBeFalse();
    }
}
