using Microsoft.AspNetCore.Http;

namespace Aevatar.BackendConsole.Hosting;

public interface IBackendConsoleAssetService
{
    IResult Serve(BackendConsoleAsset asset);

    string Render(BackendConsoleAsset asset);
}
