namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionGraphOwnerIdentityResolver : IProjectionGraphOwnerIdentityResolver
{
    public static ProjectionGraphOwnerIdentityResolver Instance { get; } = new();

    public ProjectionGraphOwnerIdentity Resolve(Type readModelType, string readModelId)
    {
        ArgumentNullException.ThrowIfNull(readModelType);
        var normalizedReadModelId = NormalizeToken(readModelId);
        if (normalizedReadModelId.Length == 0)
        {
            throw new InvalidOperationException(
                $"Graph read model '{readModelType.FullName}' requires a non-empty Id for owner lifecycle management.");
        }

        var readModelTypeName = NormalizeToken(readModelType.FullName);
        return new ProjectionGraphOwnerIdentity(
            readModelTypeName.Length == 0
                ? normalizedReadModelId
                : $"{readModelTypeName}:{normalizedReadModelId}");
    }

    private static string NormalizeToken(string? value) => value?.Trim() ?? string.Empty;
}
