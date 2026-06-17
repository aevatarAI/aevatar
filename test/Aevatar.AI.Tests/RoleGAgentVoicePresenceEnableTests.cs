using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Core;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class RoleGAgentVoicePresenceEnableTests
{
    private static readonly MethodInfo ApplyVoicePresenceEnabledMethod = typeof(RoleGAgent)
        .GetMethod("ApplyVoicePresenceEnabled", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ApplyVoicePresenceEnabled not found.");

    [Fact]
    public void ApplyVoicePresenceEnabled_ShouldMountRuntimeModuleViaEventModules()
    {
        var next = InvokePrivateStatic<RoleGAgentState>(
            ApplyVoicePresenceEnabledMethod,
            new RoleGAgentState(),
            new VoicePresenceEnabledEvent { ModuleName = "voice_presence_openai" });

        // Capability recorded AND the runtime module mounted (EventModules) — without the
        // EventModules append the VoiceModuleSignal lease request matches no handler and is dropped,
        // which times out /ws/voice after 5s.
        next.VoicePresence.Should().ContainKey("voice_presence_openai");
        next.VoicePresence["voice_presence_openai"].Initialized.Should().BeTrue();
        next.EventModules
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Should().Contain("voice_presence_openai");

        // Idempotent: re-enabling voice must not duplicate the module name.
        var again = InvokePrivateStatic<RoleGAgentState>(
            ApplyVoicePresenceEnabledMethod,
            next,
            new VoicePresenceEnabledEvent { ModuleName = "voice_presence_openai" });
        again.EventModules
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(m => m == "voice_presence_openai").Should().Be(1);
    }

    private static T InvokePrivateStatic<T>(MethodInfo method, params object?[] args)
    {
        try
        {
            return (T)method.Invoke(null, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }
}
