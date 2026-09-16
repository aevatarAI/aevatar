namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionGraphOwnerIdentityResolver
{
    ProjectionGraphOwnerIdentity Resolve(Type readModelType, string readModelId);
}

public readonly record struct ProjectionGraphOwnerIdentity(string Value);
