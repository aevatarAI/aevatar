using Aevatar.Foundation.Abstractions;
using NSubstitute.Core;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

internal static class ActorDispatchPortTestSupport
{
    public static Task<DispatchAdmission> AcceptAsync(CallInfo call)
    {
        var actorId = call.ArgAt<string>(0);
        var envelope = call.ArgAt<EventEnvelope>(1);
        return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
    }
}
