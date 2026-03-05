using Aevatar.Foundation.Abstractions;
using Google.Protobuf;

namespace Aevatar.App.Application.Services;

public interface IActorAccessAppService
{
    Task SendCommandAsync<TAgent>(string id, IMessage command, CancellationToken ct = default)
        where TAgent : class, IAgent;

    string ResolveActorId<TAgent>(string id)
        where TAgent : class, IAgent;
}
