namespace Aevatar.AI.ToolProviders.Ornn;

public sealed partial class OrnnSkillClient
{
    public Task<OrnnExactSkillReadResult<OrnnPublisherIdentity>> GetPublisherIdentityAsync(string token, CancellationToken ct = default) =>
        GetExactAsync<OrnnPublisherIdentity>(token, "/api/v1/me", "publisher", ct);

    public Task<OrnnExactSkillReadResult<OrnnExactSkillDetail>> GetSkillDetailAsync(string token, string skillId, CancellationToken ct = default)
    {
        ValidateExactReference(skillId, "0.0", nameof(skillId));
        return GetExactAsync<OrnnExactSkillDetail>(token, $"/api/v1/skills/{Uri.EscapeDataString(skillId)}", skillId, ct);
    }

    public Task<OrnnExactSkillReadResult<OrnnSkillVersions>> GetSkillVersionsAsync(string token, string skillId, CancellationToken ct = default)
    {
        ValidateExactReference(skillId, "0.0", nameof(skillId));
        return GetExactAsync<OrnnSkillVersions>(token, $"/api/v1/skills/{Uri.EscapeDataString(skillId)}/versions", skillId, ct);
    }
}

public sealed class OrnnPublisherIdentity
{
    public string? UserId { get; set; }
    public List<string> Permissions { get; set; } = [];
}

public sealed class OrnnSkillVersions
{
    public List<OrnnSkillVersion> Items { get; set; } = [];
}

public sealed class OrnnSkillVersion
{
    public string? Version { get; set; }
    public string? SkillHash { get; set; }
    public string? CreatedBy { get; set; }
    public string? CreatedByEmail { get; set; }
    public string? CreatedByDisplayName { get; set; }
    public string? CreatedOn { get; set; }
    public bool? IsDeprecated { get; set; }
    public string? DeprecationNote { get; set; }
    public string? ReleaseNotes { get; set; }
}
