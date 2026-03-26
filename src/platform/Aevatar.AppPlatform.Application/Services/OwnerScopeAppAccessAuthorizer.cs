using Aevatar.AppPlatform.Abstractions.Access;

namespace Aevatar.AppPlatform.Application.Services;

public sealed class OwnerScopeAppAccessAuthorizer : IAppAccessAuthorizer
{
    public Task<AppAccessDecision> AuthorizeAsync(AppAccessRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Resource);

        ct.ThrowIfCancellationRequested();

        var subjectScopeId = Normalize(request.SubjectScopeId);
        var ownerScopeId = Normalize(request.Resource.OwnerScopeId);
        var action = Normalize(request.Action);

        if (ownerScopeId.Length == 0)
            return Task.FromResult(Deny("Resource owner scope is missing."));

        return Task.FromResult(action switch
        {
            AppAccessActions.Read => AuthorizeRead(subjectScopeId, ownerScopeId, request.Resource.IsPublic),
            AppAccessActions.Manage => AuthorizeManage(subjectScopeId, ownerScopeId),
            _ => Deny($"Unsupported app access action '{request.Action}'."),
        });
    }

    private static AppAccessDecision AuthorizeRead(
        string subjectScopeId,
        string ownerScopeId,
        bool isPublic)
    {
        if (isPublic)
            return Allow("Public app.");

        if (subjectScopeId.Length == 0)
            return Deny("Private app requires authenticated scope.");

        return string.Equals(subjectScopeId, ownerScopeId, StringComparison.Ordinal)
            ? Allow("Owner scope matched.")
            : Deny("Private app can only be accessed by its owner scope.");
    }

    private static AppAccessDecision AuthorizeManage(string subjectScopeId, string ownerScopeId)
    {
        if (subjectScopeId.Length == 0)
            return Deny("App management requires authenticated scope.");

        return string.Equals(subjectScopeId, ownerScopeId, StringComparison.Ordinal)
            ? Allow("Owner scope matched.")
            : Deny("App management is restricted to the owner scope.");
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static AppAccessDecision Allow(string reason) => new(true, reason);

    private static AppAccessDecision Deny(string reason) => new(false, reason);
}
