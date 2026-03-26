namespace Aevatar.AppPlatform.Abstractions.Access;

public interface IAppAccessAuthorizer
{
    Task<AppAccessDecision> AuthorizeAsync(
        AppAccessRequest request,
        CancellationToken ct = default);
}
