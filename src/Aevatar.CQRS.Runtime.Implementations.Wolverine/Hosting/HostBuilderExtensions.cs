using Aevatar.CQRS.Runtime.Abstractions.Commands;
using Aevatar.CQRS.Runtime.Implementations.Wolverine.Handlers;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace Aevatar.CQRS.Runtime.Implementations.Wolverine.Hosting;

public static class HostBuilderExtensions
{
    public static IHostBuilder UseAevatarCqrsWolverine(this IHostBuilder hostBuilder)
    {
        return hostBuilder.UseWolverine(options =>
        {
            options.Discovery.IncludeAssembly(typeof(WolverineQueuedCommandHandler).Assembly);
            options.LocalQueue("cqrs-commands");
            options.PublishMessage<QueuedCommandMessage>().ToLocalQueue("cqrs-commands");
        });
    }
}
