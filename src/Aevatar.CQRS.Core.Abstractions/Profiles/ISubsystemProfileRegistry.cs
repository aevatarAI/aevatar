namespace Aevatar.CQRS.Core.Abstractions.Profiles;

public interface ISubsystemProfileRegistry
{
    IReadOnlyList<string> ListNames();

    ISubsystemProfile Resolve(string? preferredName = null);
}
