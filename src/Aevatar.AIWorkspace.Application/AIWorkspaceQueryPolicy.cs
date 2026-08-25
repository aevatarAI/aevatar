using Aevatar.AIWorkspace.Application.Abstractions;

namespace Aevatar.AIWorkspace.Application;

internal static class AIWorkspaceQueryPolicy
{
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 100;

    public static bool IsValidPageSize(int value) => value is >= 1 and <= MaximumPageSize;

    public static AIWorkspaceQueryResult<T> InvalidPageSize<T>() =>
        AIWorkspaceQueryResult<T>.Fail(
            AIWorkspaceQueryFailureKind.InvalidInput,
            "INVALID_PAGE_SIZE",
            $"Page size must be between 1 and {MaximumPageSize}.");
}
