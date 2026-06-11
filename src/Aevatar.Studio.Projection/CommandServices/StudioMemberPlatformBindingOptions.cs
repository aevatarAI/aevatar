namespace Aevatar.Studio.Projection.CommandServices;

internal sealed class StudioMemberPlatformBindingOptions
{
    public const string SectionName = "Studio:MemberPlatformBinding";

    public TimeSpan BindingReadinessTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan BindingReadinessPollInterval { get; set; } = TimeSpan.FromMilliseconds(50);
}
