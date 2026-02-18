namespace Aevatar.CQRS.Core.Abstractions.Profiles;

public interface ISubsystemProfile
{
    string Name { get; }

    IServiceCollection Register(IServiceCollection services, IConfiguration configuration);
}
