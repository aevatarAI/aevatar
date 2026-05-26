using Aevatar.Foundation.Abstractions;

namespace Aevatar.ChatRouting.Core;

public interface IChatRoutePolicyQueryPort
{
    Task<ChatRoutePolicySnapshot?> LookupForCallerAsync(
        OwnerScope callerScope,
        CancellationToken ct = default);
}
