namespace Aevatar.Studio.Application.Provisioning;

public static class StudioMemberAutomationAuditContract
{
    public const string Category = "Aevatar.Studio.MemberAutomation";

    public const int CreateAcceptedEventId = 6201;

    public const string CreateAcceptedEventName = "StudioMemberAutomationCreateAccepted";

    public const int RevocationCompletedEventId = 6202;

    public const string RevocationCompletedEventName = "StudioMemberAutomationRevocationCompleted";

    public const string CompletedRevocationStatus = "Completed";
}
