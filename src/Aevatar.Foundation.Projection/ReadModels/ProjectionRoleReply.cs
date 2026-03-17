namespace Aevatar.Foundation.Projection.ReadModels;

public sealed class ProjectionRoleReply
{
    public DateTimeOffset Timestamp { get; set; }
    public string RoleId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int ContentLength { get; set; }
}
