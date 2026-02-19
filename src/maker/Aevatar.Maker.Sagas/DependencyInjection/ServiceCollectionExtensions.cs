using Aevatar.Maker.Sagas.Queries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Maker.Sagas.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMakerExecutionSagas(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISaga, MakerExecutionSaga>());
        services.TryAddSingleton<IMakerExecutionSagaQueryService, MakerExecutionSagaQueryService>();
        return services;
    }
}
