namespace Aevatar.App.Application.Auth;

public interface IAppAuthService
{
    Task<AuthUserInfo?> ValidateTokenAsync(string token);
    Task<AuthUserInfo?> ValidateFirebaseTokenAsync(string token);
    AuthUserInfo? ValidateTrialToken(string token);
}
