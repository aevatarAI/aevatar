using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Aevatar.Bootstrap.Connectors;

public interface IConnectorRequestAuthorizationProvider
{
    Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);
}
