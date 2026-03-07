using Aevatar.Workflow.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Extensions.Maker;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowMakerExtensions(this IServiceCollection services)
    {
        return services.AddWorkflowPrimitivePack<MakerPrimitivePack>();
    }
}
