using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Interop.A2A.Hosting;

public static class A2AServiceCollectionExtensions
{
    /// <summary>
    /// 注册 A2A 协议适配层所需的服务。
    /// 前置条件：宿主必须已注册 <c>IActorDispatchPort</c>（由 Foundation Runtime 提供）。
    /// </summary>
    public static IServiceCollection AddA2AAdapter(this IServiceCollection services)
    {
        services.TryAddSingleton<IA2ATaskStore, InMemoryA2ATaskStore>();
        services.TryAddScoped<IA2AAdapterService, A2AAdapterService>();
        return services;
    }
}
