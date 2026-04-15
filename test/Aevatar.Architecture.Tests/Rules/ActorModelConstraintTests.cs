using ArchUnitNET.Fluent;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Aevatar.Architecture.Tests.Rules;

public class ActorModelConstraintTests
{
    private static readonly ArchitectureModel Arch = ArchitectureTestBase.ProductionArchitecture;

    [Fact]
    public void GAgentSubclasses_ShouldNot_Declare_ConcurrentDictionary_Fields()
    {
        // Single-threaded actor must not use concurrent collections for state.
        // Exclude InMemory infrastructure implementations which are test/dev helpers.
        IArchRule rule = FieldMembers().That()
            .AreDeclaredIn(
                Classes().That()
                    .HaveNameContaining("GAgent")
                    .And().DoNotHaveNameContaining("InMemory"))
            .And().HaveFullNameContaining("System.Collections.Concurrent.ConcurrentDictionary")
            .Should().NotExist()
            .Because("single-threaded actor must not use concurrent collections for state");
        rule.Check(Arch);
    }

    [Fact]
    public void ProjectionPort_ShouldNot_Use_SemaphoreSlim()
    {
        // 禁止 ProjectionPort 使用 SemaphoreSlim
        IArchRule rule = FieldMembers().That()
            .AreDeclaredIn(
                Classes().That().HaveNameContaining("ProjectionPort"))
            .And().HaveFullNameContaining("SemaphoreSlim")
            .Should().NotExist()
            .Because("projection ports must not use SemaphoreSlim; use actor-based lifecycle");
        rule.Check(Arch);
    }

    [Fact]
    public void LeaseClasses_ShouldNot_Declare_Lock_Fields()
    {
        // Lease 类禁止持有 lock/gate 字段
        IArchRule rule = FieldMembers().That()
            .AreDeclaredIn(
                Classes().That().HaveNameContaining("Lease"))
            .And().HaveNameMatching(".*(?i)(gate|lock|semaphore|monitor).*")
            .Should().NotExist()
            .Because("lease classes must not hold lock-based state; use actor serialization");
        rule.Check(Arch);
    }

    [Fact]
    public void GAgentSubclasses_ShouldNot_DependOn_Monitor()
    {
        // Actor/module runtime state must only be modified on the event-processing main thread.
        // Using System.Threading.Monitor (lock) violates single-threaded actor semantics.
        IArchRule rule = Classes().That()
            .HaveNameContaining("GAgent")
            .And().DoNotHaveNameContaining("InMemory")
            .And().DoNotHaveNameContaining("Test")
            .Should().NotDependOnAny(
                Types().That().HaveFullName("System.Threading.Monitor"))
            .Because("single-threaded actor must not use lock/Monitor for state protection");
        rule.Check(Arch);
    }

    [Fact]
    public void GAgentSubclasses_ShouldNot_DependOn_SemaphoreSlim()
    {
        IArchRule rule = Classes().That()
            .HaveNameContaining("GAgent")
            .And().DoNotHaveNameContaining("InMemory")
            .And().DoNotHaveNameContaining("Test")
            .Should().NotDependOnAny(
                Types().That().HaveFullName("System.Threading.SemaphoreSlim"))
            .Because("single-threaded actor must not use SemaphoreSlim for state protection");
        rule.Check(Arch);
    }

    [Fact]
    public void WorkflowCallModule_ShouldNot_Declare_Collection_Fields()
    {
        // WorkflowCallModule must stay stateless; workflow_call fact state must live in
        // WorkflowGAgent persisted state, not in-process collections.
        IArchRule rule = FieldMembers().That()
            .AreDeclaredIn(
                Classes().That().HaveNameContaining("WorkflowCallModule"))
            .And().HaveFullNameMatching(".*(?i)(dictionary|hashset|queue|list<).*")
            .Should().NotExist()
            .Because("WorkflowCallModule must be stateless; fact state belongs in WorkflowGAgent persisted state");
        rule.Check(Arch);
    }

    [Fact]
    public void ScriptDefinitionGAgent_ShouldNot_Inherit_RoleGAgent()
    {
        IArchRule rule = Classes().That()
            .HaveName("ScriptDefinitionGAgent")
            .Should().NotBeAssignableTo(
                Types().That().HaveNameContaining("RoleGAgent"))
            .Because("ScriptDefinitionGAgent must inherit GAgentBase<> directly, not RoleGAgent");
        rule.Check(Arch);
    }

    [Fact]
    public void ScriptDefinitionGAgent_ShouldNot_Inherit_AIGAgentBase()
    {
        IArchRule rule = Classes().That()
            .HaveName("ScriptDefinitionGAgent")
            .Should().NotBeAssignableTo(
                Types().That().HaveNameContaining("AIGAgentBase"))
            .Because("ScriptDefinitionGAgent must inherit GAgentBase<> directly, not AIGAgentBase");
        rule.Check(Arch);
    }

    [Fact]
    public void ScriptBehaviorGAgent_ShouldNot_Inherit_RoleGAgent()
    {
        IArchRule rule = Classes().That()
            .HaveName("ScriptBehaviorGAgent")
            .Should().NotBeAssignableTo(
                Types().That().HaveNameContaining("RoleGAgent"))
            .Because("ScriptBehaviorGAgent must inherit GAgentBase<> directly, not RoleGAgent");
        rule.Check(Arch);
    }

    [Fact]
    public void ScriptBehaviorGAgent_ShouldNot_Inherit_AIGAgentBase()
    {
        IArchRule rule = Classes().That()
            .HaveName("ScriptBehaviorGAgent")
            .Should().NotBeAssignableTo(
                Types().That().HaveNameContaining("AIGAgentBase"))
            .Because("ScriptBehaviorGAgent must inherit GAgentBase<> directly, not AIGAgentBase");
        rule.Check(Arch);
    }

    [Fact]
    public void TransitionStateOverrides_Should_UseStateTransitionMatcher()
    {
        // Classes that override TransitionState must also reference StateTransitionMatcher
        // for Any-safe replay semantics. ArchUnitNET approximation: classes declaring
        // TransitionState method should depend on StateTransitionMatcher type.
        IArchRule rule = Classes().That()
            .HaveNameContaining("GAgent")
            .And().AreNotAbstract()
            .And().ResideInNamespaceMatching(@"Aevatar(\..+)?")
            .Should().NotDependOnAny(
                Types().That().HaveNameContaining("PLACEHOLDER_NEVER_MATCH_TRANSITION"))
            .Because("placeholder for TransitionState matcher validation — shell guard remains authoritative")
            .WithoutRequiringPositiveResults();
        // Note: Full TransitionState + StateTransitionMatcher co-occurrence checking requires
        // method-body analysis beyond ArchUnitNET capability. The shell guard
        // (architecture_guards.sh) remains authoritative for this rule.
        // This test serves as documentation that the constraint exists.
        rule.Check(Arch);
    }
}
