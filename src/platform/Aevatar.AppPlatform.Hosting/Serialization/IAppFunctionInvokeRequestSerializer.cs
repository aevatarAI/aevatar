using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Hosting.Endpoints;

namespace Aevatar.AppPlatform.Hosting.Serialization;

internal interface IAppFunctionInvokeRequestSerializer
{
    AppFunctionInvokeRequest Deserialize(AppPlatformEndpointModels.FunctionInvokeHttpRequest request);
}
