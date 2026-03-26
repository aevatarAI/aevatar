using System.Text.Json.Nodes;

namespace Aevatar.AppPlatform.Hosting.OpenApi;

internal interface IAppOpenApiDocumentPort
{
    JsonObject BuildDocument(string serverUrl);
}
