namespace Aevatar.App.Application.Auth;

public interface IAppAuthContextAccessor
{
    AppAuthContext? AuthContext { get; set; }

    AppAuthContext RequireAuthContext()
        => AuthContext ?? throw new UnauthorizedAccessException("Not authenticated");
}

public sealed class AppAuthContextAccessor : IAppAuthContextAccessor
{
    public AppAuthContext? AuthContext { get; set; }
}
