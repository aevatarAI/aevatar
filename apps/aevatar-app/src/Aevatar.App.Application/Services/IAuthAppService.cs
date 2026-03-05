namespace Aevatar.App.Application.Services;

public interface IAuthAppService
{
    Task<TrialRegisterResult> RegisterTrialAsync(string email, string trialTokenSecret);
}
