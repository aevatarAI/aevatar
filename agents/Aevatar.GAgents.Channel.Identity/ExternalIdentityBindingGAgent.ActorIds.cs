using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Actor id helpers for <see cref="ExternalIdentityBindingGAgent"/>.
/// One actor per <see cref="ExternalSubjectRef"/>; the id format is an
/// implementation detail callers MUST treat as opaque.
/// </summary>
public sealed partial class ExternalIdentityBindingGAgent
{
    /// <summary>
    /// Builds the actor id for the given external subject. Calls
    /// <see cref="ExternalSubjectRefExtensions.ToActorId"/> for the canonical
    /// format; provided here for callers that already have the typed
    /// <see cref="ExternalSubjectRef"/> in hand.
    /// </summary>
    public static string BuildActorId(ExternalSubjectRef externalSubject) =>
        externalSubject.ToActorId();
}
