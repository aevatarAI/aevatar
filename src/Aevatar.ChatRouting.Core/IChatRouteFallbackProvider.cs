using Aevatar.ChatRouting.Abstractions;

namespace Aevatar.ChatRouting.Core;

public interface IChatRouteFallbackProvider
{
    ChatRouteDecision GetFallbackDecision();
}
