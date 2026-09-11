using Aevatar.Foundation.Abstractions.Credentials;

namespace Aevatar.AI.Abstractions.ToolProviders;

public sealed record ChannelRegistrationAuthorityAdmissionRequest(
    DurableCallerCredentialRef Credential,
    AgentToolOperationAdmission Operation);

public enum ChannelRegistrationAuthorityAdmissionReason
{
    Unspecified = 0,
    Allowed = 1,
    RegistrationMissing = 2,
    RegistrationAmbiguous = 3,
    CredentialDescriptorMismatch = 4,
    AuthorizationContractInvalid = 5,
    TargetNotGranted = 6,
    TargetNotAuthorized = 7,
    OperationAdmissionMissing = 8,
    AuthorityUnavailable = 9,
}

public sealed record ChannelRegistrationAuthorityAdmissionResult(
    bool Allowed,
    ChannelRegistrationAuthorityAdmissionReason Reason)
{
    public static ChannelRegistrationAuthorityAdmissionResult Allow() =>
        new(true, ChannelRegistrationAuthorityAdmissionReason.Allowed);

    public static ChannelRegistrationAuthorityAdmissionResult Deny(
        ChannelRegistrationAuthorityAdmissionReason reason)
    {
        if (reason is ChannelRegistrationAuthorityAdmissionReason.Unspecified or
            ChannelRegistrationAuthorityAdmissionReason.Allowed)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        return new ChannelRegistrationAuthorityAdmissionResult(false, reason);
    }
}

public interface IChannelRegistrationAuthorityAdmissionPort
{
    Task<ChannelRegistrationAuthorityAdmissionResult> AdmitAsync(
        ChannelRegistrationAuthorityAdmissionRequest request,
        CancellationToken ct = default);
}
